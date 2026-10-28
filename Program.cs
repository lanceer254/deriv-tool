using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.Name = ".DerivAccumulator.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.IdleTimeout = TimeSpan.FromMinutes(30);
});
builder.Services.AddHttpClient("deriv", client =>
{
    client.BaseAddress = new Uri("https://api.derivws.com/");
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.Configure<FormOptions>(options => options.ValueLengthLimit = 16 * 1024);

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseSession();

var oauth = app.Configuration.GetSection("DerivOAuth");
string clientId = oauth["ClientId"] ?? "";
string redirectUri = oauth["RedirectUri"] ?? "http://localhost:5180/oauth/callback";
string scopes = oauth["Scopes"] ?? "trade account_manage";

app.MapGet("/oauth/login", (HttpContext context) =>
{
    if (string.IsNullOrWhiteSpace(clientId) || clientId == "REPLACE_WITH_DERIV_OAUTH_CLIENT_ID")
        return Results.Problem(
            "Deriv OAuth is not configured. Register an OAuth application with Deriv, then set DerivOAuth:ClientId and the matching RedirectUri in appsettings.json.",
            statusCode: 503,
            title: "OAuth setup required");

    string verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
    string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    string state = Base64Url(RandomNumberGenerator.GetBytes(32));

    context.Session.SetString("oauth_verifier", verifier);
    context.Session.SetString("oauth_state", state);

    string url = "https://auth.deriv.com/oauth2/auth?" + string.Join("&", new[]
    {
        "response_type=code",
        "client_id=" + Uri.EscapeDataString(clientId),
        "redirect_uri=" + Uri.EscapeDataString(redirectUri),
        "scope=" + Uri.EscapeDataString(scopes),
        "state=" + Uri.EscapeDataString(state),
        "code_challenge=" + Uri.EscapeDataString(challenge),
        "code_challenge_method=S256"
    });
    return Results.Redirect(url);
});

app.MapGet("/oauth/callback", async (HttpContext context, IHttpClientFactory factory) =>
{
    string? error = context.Request.Query["error"].FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(error))
    {
        context.Session.Clear();
        return Results.Content(ErrorPage("Deriv authorization was cancelled or denied: " + error), "text/html");
    }

    string? code = context.Request.Query["code"].FirstOrDefault();
    string? returnedState = context.Request.Query["state"].FirstOrDefault();
    string? expectedState = context.Session.GetString("oauth_state");
    string? verifier = context.Session.GetString("oauth_verifier");

    if (string.IsNullOrWhiteSpace(code) ||
        string.IsNullOrWhiteSpace(returnedState) ||
        string.IsNullOrWhiteSpace(expectedState) ||
        !CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(returnedState),
            Encoding.UTF8.GetBytes(expectedState)) ||
        string.IsNullOrWhiteSpace(verifier))
    {
        context.Session.Clear();
        return Results.Content(ErrorPage("Authorization could not be verified. Please start the connection again."), "text/html", statusCode: 400);
    }

    var client = factory.CreateClient("deriv");
    using var tokenResponse = await client.PostAsync(
        "https://auth.deriv.com/oauth2/token",
        new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri
        }));

    string tokenJson = await tokenResponse.Content.ReadAsStringAsync();
    if (!tokenResponse.IsSuccessStatusCode)
    {
        context.Session.Clear();
        return Results.Content(ErrorPage("Deriv token exchange failed. Check the OAuth client and registered redirect URI, then try again."), "text/html", statusCode: 502);
    }

    using var tokenDoc = JsonDocument.Parse(tokenJson);
    if (!tokenDoc.RootElement.TryGetProperty("access_token", out var tokenElement))
    {
        context.Session.Clear();
        return Results.Content(ErrorPage("Deriv did not return an access token."), "text/html", statusCode: 502);
    }

    string token = tokenElement.GetString() ?? "";
    if (string.IsNullOrWhiteSpace(token))
    {
        context.Session.Clear();
        return Results.Content(ErrorPage("Deriv returned an empty access token."), "text/html", statusCode: 502);
    }

    context.Session.Remove("oauth_state");
    context.Session.Remove("oauth_verifier");
    context.Session.SetString("deriv_access_token", token);
    context.Session.SetString("deriv_connected", "true");

    return Results.Redirect("/");
});

app.MapGet("/api/status", (HttpContext context) =>
{
    bool connected = context.Session.GetString("deriv_connected") == "true";
    return Results.Json(new
    {
        authenticated = connected,
        tradingEnabled = false,
        message = connected
            ? "Deriv connected. Trading remains disabled until the strategy engine is integrated and verified."
            : "Connect to Deriv to unlock account information."
    });
});

app.MapGet("/api/accounts", async (HttpContext context, IHttpClientFactory factory) =>
{
    string? token = context.Session.GetString("deriv_access_token");
    if (string.IsNullOrWhiteSpace(token))
        return Results.Unauthorized();

    var client = factory.CreateClient("deriv");
    using var request = new HttpRequestMessage(HttpMethod.Get, "trading/v1/options/accounts");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    using var response = await client.SendAsync(request);
    string body = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
        return Results.Problem("Deriv account lookup failed (" + (int)response.StatusCode + "). Reconnect and verify the requested permissions.", statusCode: 502);

    using var doc = JsonDocument.Parse(body);
    JsonElement items;
    if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        items = data;
    else if (doc.RootElement.ValueKind == JsonValueKind.Array)
        items = doc.RootElement;
    else
        return Results.Problem("Deriv returned an unexpected account list.", statusCode: 502);

    var accounts = new List<object>();
    foreach (var item in items.EnumerateArray())
    {
        string id = ReadString(item, "account_id");
        if (string.IsNullOrWhiteSpace(id)) continue;
        accounts.Add(new
        {
            accountId = id,
            accountType = ReadString(item, "account_type", "unknown"),
            currency = ReadString(item, "currency", "USD"),
            balance = ReadNumber(item, "balance")
        });
    }

    return Results.Json(accounts);
});

app.MapPost("/api/disconnect", (HttpContext context) =>
{
    context.Session.Clear();
    return Results.Json(new { ok = true });
});

// Intentionally fail closed: no live trade endpoint is enabled in this first scaffold.
app.MapPost("/api/trading/start", (HttpContext context) =>
{
    if (context.Session.GetString("deriv_connected") != "true")
        return Results.Unauthorized();
    return Results.Conflict(new
    {
        error = "Trading is not enabled in this scaffold. The original strategy engine must be integrated and tested before trading can start."
    });
});

app.MapGet("/health", () => Results.Json(new { status = "ok" }));

app.Run("http://localhost:5180");

static string Base64Url(byte[] bytes) =>
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

static string ReadString(JsonElement element, string name, string fallback = "")
{
    if (!element.TryGetProperty(name, out var value)) return fallback;
    string result = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
    return string.IsNullOrWhiteSpace(result) ? fallback : result;
}

static double ReadNumber(JsonElement element, string name)
{
    if (!element.TryGetProperty(name, out var value)) return 0;
    if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double n)) return n;
    if (value.ValueKind == JsonValueKind.String &&
        double.TryParse(value.GetString(), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out double parsed)) return parsed;
    return 0;
}

static string ErrorPage(string message) =>
    "<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>" +
    "<title>Deriv connection</title><style>body{font:16px system-ui;background:#0b1220;color:#e5e7eb;display:grid;place-items:center;min-height:90vh}.box{max-width:520px;padding:28px;background:#111c30;border:1px solid #29364b;border-radius:16px}a{color:#7dd3fc}</style></head>" +
    "<body><div class='box'><h2>Deriv connection</h2><p>" + System.Net.WebUtility.HtmlEncode(message) +
    "</p><p><a href='/'>Return to dashboard</a></p></div></body></html>";
