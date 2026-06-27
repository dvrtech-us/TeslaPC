# Runbook: Tesla-trusted HTTPS cert (in-app Let's Encrypt + Cloudflare DNS-01)

TeslaPC obtains and renews a publicly-trusted **Let's Encrypt** certificate for
**`my.thelpers.com`** **in-process** (`AcmeCertificateManager`, Certes + Cloudflare API, DNS-01),
imports it to `LocalMachine\My`, and binds it to `0.0.0.0:8443`. The Tesla browses
`https://my.thelpers.com:8443/` (DNS A → `100.64.0.1` over the hotspot) with no warning. No
external tools (win-acme / scheduled task / rebind script) are involved.

## Prerequisites (done)

- Cloudflare DNS: `my.thelpers.com` **A → 100.64.0.1**, **DNS-only (grey cloud)**.
- Cloudflare API token with **Zone:Read** + **DNS:Edit** on `thelpers.com`.

## One-time setup on the laptop

1. **Create the `.env`** at `%ProgramData%\TeslaPC\.env` (loaded automatically; survives rebuilds;
   gitignored). Copy `TeslaPCInterface/.env.example` and fill in:
   ```
   TESLAPC_HTTPS_HOST=my.thelpers.com
   TESLAPC_CF_TOKEN=<your-cloudflare-token>
   TESLAPC_ACME_EMAIL=you@example.com
   # TESLAPC_ACME_STAGING=1   # optional, while testing
   ```
   (A machine env var of the same name still overrides the file if you prefer that.)
2. **Run TeslaPC with the host**: launcher passes `--https-host my.thelpers.com`
   (`run-full.bat` / desktop `Start-TeslaPC.bat`). On startup, if the cert is missing or has
   < 30 days left, the app runs the ACME DNS-01 flow, imports the cert, and binds it; otherwise
   it no-ops. A background timer re-checks every 12 h and renews near expiry.

## Configuration reference

| Setting | Source | Purpose |
|---------|--------|---------|
| host | `--https-host` or `TESLAPC_HTTPS_HOST` | cert hostname (`my.thelpers.com`) |
| Cloudflare token | `TESLAPC_CF_TOKEN` | DNS-01 TXT record create/delete |
| ACME email | `TESLAPC_ACME_EMAIL` (optional) | Let's Encrypt account contact |
| staging | `TESLAPC_ACME_STAGING=1` (optional) | use LE staging for testing (untrusted) |

State: ACME account key persists at `%ProgramData%\TeslaPC\acme-account.pem`; the issued cert
lives in `LocalMachine\My` (friendly name `TeslaPC LE (<host>)`).

## Verify

```
netsh http show sslcert ipport=0.0.0.0:8443     # thumbprint = the LE cert
curl https://my.thelpers.com:8443/              # 200, no -k, valid chain
```
On the Tesla (on the hotspot): browse `https://my.thelpers.com:8443/` → padlock, no warning.

## Notes

- Renewal needs the laptop to have internet at check time (DNS-01, no inbound). Offline > 90 days
  → cert expires until the next successful renewal; self-signed remains the fallback.
- Test safely with `TESLAPC_ACME_STAGING=1` first to avoid LE production rate limits, then unset it.
- Old Tesla firmware that distrusts Let's Encrypt would need a different ACME CA (e.g. ZeroSSL);
  `AcmeCertificateManager` uses `WellKnownServers.LetsEncryptV2` today.
