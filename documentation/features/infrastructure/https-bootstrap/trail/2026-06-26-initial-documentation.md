# Initial Documentation of HTTPS Bootstrap

- Date: 2026-06-26
- Feature: https-bootstrap
- Related code: `TeslaPCInterface/SslCertificateBootstrap.cs`, `TeslaPCInterface/bindSSLCert.bat`, `TeslaPCInterface/Program.cs`

## Context

HTTPS on http.sys requires a certificate in the machine store, correct private-key ACLs for
the service account, URL ACL reservations, and an SSL binding — previously a manual `netsh`
chore (`bindSSLCert.bat`). Recent commit history shows this was automated into
`SslCertificateBootstrap`. This entry records the as-built automated flow.

## Decisions

- Documented the full ordered flow: URL ACLs → prune broken certs → get/create cert → binding idempotency check → repair-store + key ACLs → bind → regenerate-and-retry on failure.
- Recorded the fixed http.sys AppId shared between the C# path and the batch file, and that `TeslaPC Dev Cert` is the single lookup key.
- Captured the mandatory private-key ACL grants (NETWORK SERVICE / SYSTEM / LOCAL SERVICE) and the `icacls` fallback.
- Noted the documented divergences between `bindSSLCert.bat` and the C# path (extra port cleanup; missing explicit subject) so they are not mistaken for bugs.

## Alternatives Considered

- **Keeping cert setup manual via the batch file** — superseded by automatic bootstrap so HTTPS "just works" on launch; the batch file is retained as a fallback.
- **Using the .NET `CertificateRequest` API instead of PowerShell** — the implemented path uses `New-SelfSignedCertificate` via `-EncodedCommand`; documented as-is.

## Consequences

- Positive: HTTPS provisioning is self-healing (prunes broken certs, regenerates on bind failure) and idempotent across restarts.
- Negative: it is admin-only and self-signed, so browsers show a trust warning; the divergence between the batch file and C# is a maintenance watch-item recorded in the baseline.
