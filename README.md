# Deriv Accumulator Dashboard — first build

This repository contains a .NET 8 local dashboard scaffold for a Deriv Accumulator strategy. It provides a Deriv OAuth + PKCE sign-in flow, an authentication gate, account listing, account selection, and a basic dashboard. **It does not submit trades.**

## Current status

- No PAT entry field.
- Dashboard account features remain locked until Deriv OAuth succeeds.
- OAuth requests `trade account_manage`.
- Trading remains disabled until the strategy engine is ported and tested.
- This scaffold has not been compiled or verified against a registered Deriv OAuth app yet.

## Local setup

1. Install the .NET 8 SDK.
2. Register an OAuth application with Deriv.
3. Configure the registered redirect URI to match `DerivOAuth:RedirectUri` in `appsettings.json` (default local value: `http://localhost:5180/oauth/callback`, subject to Deriv accepting that URI).
4. Replace `REPLACE_WITH_DERIV_OAUTH_CLIENT_ID` in `appsettings.json` with the client ID.
5. Run `dotnet restore` and `dotnet run` from the project folder.
6. Open `http://localhost:5180` and select **Connect to Deriv**.

Do not expose this local scaffold to the public internet. It uses in-memory session storage and is for local development, not production hosting.

## Strategy requirements to integrate

- Accumulator contract `ACCU`.
- Markets `R_10`, `R_25`, and `R_50`.
- Entry filter: inspect 20 recent movements and require at least 4 calm ticks.
- Barrier distance at least 1.2× the largest recent movement.
- 20-second cooldown and 120-second maximum contract time.
- Stake: 10% of balance above $15; $1 minimum rule at or below $15, including exactly $15.
- Growth rate selectable from 1% through 5%.
- Take-profit equal to 10% of stake.
- Preserve proposal retry/timeout and reconnect/watchdog behavior from the supplied strategy source.
- Remove the session loss limit, consecutive-loss limit, and max-trades-per-session cap as instructed.

The source strategy also had a 20%-of-balance stake ceiling. For balances below $5, a $1 stake exceeds that ceiling. Resolve that conflict explicitly before enabling trading; do not silently override either rule.

## Validation

The initial scaffold was generated in an environment without the .NET SDK, so no build was run. Compile, verify the Deriv OAuth endpoints and account response schema, and test with a demo account before enabling any trading engine.