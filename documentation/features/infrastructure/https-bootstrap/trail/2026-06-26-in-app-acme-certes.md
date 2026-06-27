# In-App ACME (Certes) — replace external win-acme

- Date: 2026-06-26
- Feature: https-bootstrap
- Related code: `TeslaPCInterface/AcmeCertificateManager.cs`, `TeslaPCInterface/Program.cs`, `TeslaPCInterface/TeslaPCInterface.csproj`

## Context

The first cut of the Tesla-trusted-cert work used **win-acme** (external tool + scheduled task +
post-renew rebind script). On the user's request to "integrate this into the app via a library",
we moved certificate issuance and renewal **into TeslaPC itself**.

## Decisions

- Added the **Certes** NuGet (ACME v2) and a new `AcmeCertificateManager`:
  - Loads/creates a Let's Encrypt account (key persisted at `%ProgramData%\TeslaPC\acme-account.pem`).
  - DNS-01 challenge fulfilled via direct **Cloudflare API** calls (find zone, create/delete the
    `_acme-challenge.<host>` TXT), with a Cloudflare-DoH propagation poll before validation.
  - Finalizes, imports the PFX to `LocalMachine\My` (friendly name `TeslaPC LE (<host>)`), and
    `SslCertificateBootstrap` binds it. A 12 h background timer renews when < 30 days remain.
- **Token via environment variable** `TESLAPC_CF_TOKEN` (set once on the host, machine-level);
  never on a command line or in git. Host from `--https-host`; optional `TESLAPC_ACME_EMAIL`,
  `TESLAPC_ACME_STAGING=1`.
- Runs only when host **and** token are present and not `--localhost`; otherwise the self-signed
  path is unchanged.

## Alternatives Considered

- **win-acme (external)** — works, but adds a tool, a scheduled task, a rebind script, and a
  separate secret store. Superseded for a self-contained, distributable app.

## Consequences

- Positive: one binary owns its cert lifecycle; simpler deployment; secret handled via env var.
- Negative: a new dependency (Certes) and ~250 lines to maintain; LE production rate limits apply
  (mitigated by the 30-day renewal gate and a staging option). The earlier win-acme trail/runbook
  are superseded by this approach.
