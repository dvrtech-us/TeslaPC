# Tesla-Trusted Certificate via Let's Encrypt DNS-01

- Date: 2026-06-26
- Feature: https-bootstrap
- Related code: `TeslaPCInterface/SslCertificateBootstrap.cs` (`TryUseTrustedCertificate`, `GetTrustedCertificate`), `TeslaPCInterface/Program.cs` (`--https-host`)

## Context

The self-signed cert makes the Tesla in-car browser show "Not secure", and the Tesla browser has
**no way to install a custom root CA**. To get a trusted padlock we need a certificate from a CA
the Tesla already trusts. Research established: a public CA cannot issue for a bare IP; but a
public **A record can point a hostname at the CGNAT IP `100.64.0.1`** (reached locally over the
hotspot), and **Let's Encrypt DNS-01** can validate that hostname without any public reachability.
ISRG Root X1 is now near-ubiquitous, so modern Teslas trust Let's Encrypt; the Tesla's lack of
SNI is irrelevant because we bind a single cert per IP:port.

## Decisions

- Hostname **`my.thelpers.com`** on **Cloudflare**, A record → `100.64.0.1` (DNS-only, not proxied).
- Certificate issued/renewed **externally by win-acme** (Let's Encrypt, Cloudflare DNS-01) into
  `LocalMachine\My`; a scheduled task auto-renews (~60/90 days) and a post-renew script rebinds.
- **App change is additive and safe:** `Program.cs` reads `--https-host`/`TESLAPC_HTTPS_HOST`;
  `SslCertificateBootstrap` prefers a matching trusted cert (`MatchesHostname`, valid, has key,
  not the self-signed `TeslaPC Dev Cert`) and binds it, else falls back to self-signed. The app
  never creates or deletes the trusted cert.
- Cloudflare API token lives only in win-acme's encrypted config — never in git.

## Alternatives Considered

- **Custom root CA on the Tesla** — not possible (browser is locked down).
- **traefik.me / public wildcard-for-IP cert** — discontinued / publishes private keys; rejected.
- **HTTP-01 / TLS-ALPN** — need inbound public reachability we don't have; DNS-01 avoids that.

## Consequences

- Positive: trusted HTTPS on the Tesla with auto-renewal; self-signed remains the fallback for
  dev/localhost and first boot before issuance.
- Negative: depends on the Cloudflare token + the laptop having internet at renewal time; if
  offline across a 90-day window the cert expires until the next renewal. Older Tesla firmware
  that distrusts Let's Encrypt would need ZeroSSL/paid (same flow, different ACME CA).

See the runbook: `documentation/planning/Infrastructure/2026-06-26-tesla-trusted-cert-runbook.md`.
