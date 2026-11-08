# Config read and write are pure functions

- Date: 2026-11-07
- Feature: configuration
- Related code: `teslapc/src/decisions/config.rs`, `teslapc/assets/config.html`

## Context

The C# settings page wrote `%ProgramData%\TeslaPC\.env` and never echoed the Cloudflare token.

## Decisions

`apply_config_body` and `public_config_json` are what `POST` and `GET /config` call. The Settings page also turns the Windows hotspot on and off.

## Alternatives Considered

- Return the token to the page so it can be edited in place: rejected.

## Consequences

- Positive: a test can prove the token is absent from the JSON.
- Negative: host, token, and email still need a restart to affect a certificate that is already loaded.
