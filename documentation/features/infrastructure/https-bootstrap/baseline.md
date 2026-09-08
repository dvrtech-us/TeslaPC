# HTTPS Bootstrap — Behavior Baseline

Known-good invariants. Update only when intended behavior changes.

## Invariants

- **Trusted cert takes precedence.** When `--https-host <host>` (or `TESLAPC_HTTPS_HOST`) is set
  and a non-self-signed cert in `LocalMachine\My` satisfies `MatchesHostname(host)` + valid dates +
  accessible private key, that cert is bound and the self-signed path is skipped. Otherwise the
  self-signed `TeslaPC Dev Cert` is used (first boot / localhost / no host).
- **In-app issuance/renewal.** When `--https-host` **and** `TESLAPC_CF_TOKEN` are both set,
  `AcmeCertificateManager` (Certes + Cloudflare DNS-01) obtains a Let's Encrypt cert at startup and
  renews it when < 30 days remain (12 h background timer), importing to `LocalMachine\My` with
  friendly name `TeslaPC LE (<host>)`. With no token it does nothing and the self-signed path runs.
- **ACME never contacts Let's Encrypt without elevation.** Import requires admin
  (`LocalMachine\My` + machine key set), so a non-elevated run skips the request: it keeps a
  valid-but-expiring cert (logs the days left) or logs that issuance is deferred. This prevents
  one wasted LE issuance per non-admin start.
- **Stale challenge TXT records are swept before create.** Every existing
  `_acme-challenge.<host>` TXT record is deleted (best-effort, logged) before the new challenge
  record is created, so a crashed prior attempt cannot wedge renewals with Cloudflare's
  "identical record already exists" error.
- **The cert-store scan is per-cert fault tolerant.** One unreadable/malformed certificate in
  `LocalMachine\My` is logged and skipped; it must never abort the scan and trigger a re-issue
  while a valid `TeslaPC LE (<host>)` cert is present (`GetBestCertificateDaysLeft`).
- The http.sys AppId is exactly `{A253521A-C31E-457C-AADD-C0E42A87EA0F}` and is identical in `SslCertificateBootstrap.cs` and `bindSSLCert.bat` — the two paths are interchangeable.
- `TeslaPC Dev Cert` is the **sole** lookup key for finding, reusing, and cleaning up certificates; changing it orphans existing certs.
- `RemoveBrokenCertificates()` always runs **before** `GetOrCreateCertificate()` — certs with inaccessible private keys are pruned before reuse is attempted.
- A cert is reused only if it satisfies all of: friendly name matches, `NotAfter > UtcNow`, `HasPrivateKey`, and `GetRSAPrivateKey() != null`.
- `IsCertificateBound()` is checked before binding; if the bound hash already matches, no rebind occurs and it returns `true`.
- The binding is always to `0.0.0.0:8443`.
- Private-key ACLs **must** grant Read to `NETWORK SERVICE`, `SYSTEM`, and `LOCAL SERVICE`; http.sys runs as `NETWORK SERVICE` and cannot load the cert otherwise.
- On bind failure, the cert is removed, regenerated, and the prepare+bind sequence is retried once.

## Certificate Defaults

- Subject `CN=TeslaPC`; SANs `localhost`, `TeslaPC`; store `LocalMachine\My`; 5-year validity; `Exportable` key.
- Generated via `New-SelfSignedCertificate` in PowerShell (`-EncodedCommand`), 30 000 ms timeout; thumbprint matched with `(?i)\b([0-9A-F]{40})\b`.

## Enablement / Privilege

- Runs when `--localhost` is **not** set.
- If http.sys already has an SSL binding on `0.0.0.0:8443` **and** URL ACLs for both
  `http://+:8080/` and `https://+:8443/`, `TryEnsureHttpsReady` returns `true` without elevation
  (reuse path).
- Otherwise provisioning requires administrator; non-admin returns `false` and the server runs
  HTTP-only (no HTTPS prefix bound).

## Known Divergence (documented, not a bug)

- `bindSSLCert.bat` cleans bindings on ports `8443`, `8444`, `8445`; the C# path cleans only `8443` (the extra ports are historical).
- The batch file omits an explicit `-Subject`; the C# path sets `-Subject 'CN=TeslaPC'`.
