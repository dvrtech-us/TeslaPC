# HTTPS — Behavior Baseline

- `8443` uses rustls. http.sys is not used.
- Renewal is required when days remaining are `30` or fewer. `31` does not renew.
- The renewal loop interval is `12` hours.
- `TESLAPC_ACME_STAGING=1` uses the Let's Encrypt staging directory.
- No hostname: a local certificate is generated and `8443` listens (except `--localhost`).
- `--localhost` does not start HTTPS and does not call ACME.
- The token is never returned by `GET /config`.
