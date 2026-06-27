# HTTPS Bootstrap

Provisions everything http.sys needs to serve HTTPS on port `8443`: a certificate, private-key
ACLs for the http.sys service accounts, URL ACL reservations, and the SSL certificate binding.
Runs automatically at startup; `bindSSLCert.bat` is the manual equivalent/fallback. The return
value gates whether [web-server](../../server/web-server/web-server.md) binds the HTTPS prefix.

**Two certificate modes:**

- **Trusted (production)** — when a hostname is configured via `--https-host <host>` (or env
  `TESLAPC_HTTPS_HOST`) the app obtains/renews a **Let's Encrypt** certificate **in-process** via
  [`AcmeCertificateManager`](../../../../TeslaPCInterface/AcmeCertificateManager.cs) (Certes,
  DNS-01, Cloudflare API) and imports it to `LocalMachine\My`; the bootstrap then binds it. The
  Tesla browses `https://<host>:8443/` (DNS A record → `100.64.0.1`) and gets **no warning**.
  See the runbook in `documentation/planning/Infrastructure/`.
- **Self-signed (fallback)** — when no host is set, or no trusted cert is present yet (first boot,
  localhost/dev), the bootstrap generates and binds the self-signed `TeslaPC Dev Cert` as before.

## User Flow

Automatic at startup when elevated and not in `--localhost` mode. If it returns `false`
(e.g. not admin), the server runs HTTP-only.

## Technical Flow (`SslCertificateBootstrap`, `SslCertificateBootstrap.cs`, namespace `PrimaryProcess`)

`TryEnsureHttpsReady(int httpsPort, int httpPort, string? trustedHost)`, called from `Program.cs`:

1. **Admin check** — non-admin logs a suggestion to use `--localhost` and returns `false`.
2. **URL ACLs** — `EnsureUrlReservation` for `http://+:8080/` and `https://+:8443/` via `netsh http add urlacl url={url} user=Everyone` (not pre-checked; netsh silently succeeds or fails).
2a. **Trusted cert (if `trustedHost` set)** — `TryUseTrustedCertificate(httpsPort, host)`:
   `GetTrustedCertificate(host)` scans `LocalMachine\My` for the newest cert where
   `cert.MatchesHostname(host)`, `NotAfter > now`, `HasPrivateKey`, accessible key, and that is
   **not** the self-signed `TeslaPC Dev Cert`. If found, bind it (reusing `IsCertificateBound` /
   `PrepareCertificateForHttpSys` / `RemoveSslBinding` / `TryBindCertificate`) and return `true`.
   If not found, log and fall through to the self-signed steps below.
3. **Prune broken certs** — `RemoveBrokenCertificates()`: in `LocalMachine\My`, remove any cert named `TeslaPC Dev Cert` whose `GetRSAPrivateKey()` throws (inaccessible key).
4. **Get or create cert** — `GetOrCreateCertificate()`: reuse a `TeslaPC Dev Cert` only if `NotAfter > UtcNow`, `HasPrivateKey`, and `GetRSAPrivateKey() != null`; otherwise `CreateSelfSignedCertificate()`.
5. **Binding idempotency** — `IsCertificateBound(8443, thumbprint)`: parse `Certificate Hash` from `netsh http show sslcert ipport=0.0.0.0:8443`; if it matches, log "already configured" and return `true`.
6. **Prepare for http.sys** — `certutil -repairstore my {thumbprint}`, then `GrantHttpSysPrivateKeyAccess`.
7. **Bind** — remove any existing binding, then `netsh http add sslcert ipport=0.0.0.0:8443 certhash={thumbprint} appid={A253521A-C31E-457C-AADD-C0E42A87EA0F}`.
8. **Recovery** — if the bind fails, remove the cert, generate a brand-new one, and retry steps 6–7 once.

### Certificate generation (`CreateSelfSignedCertificate`, `:154`)

Runs PowerShell via `-EncodedCommand` (Base64), 30 000 ms timeout:

```powershell
$ErrorActionPreference = 'Stop';
$cert = New-SelfSignedCertificate -Subject 'CN=TeslaPC' -DnsName 'localhost','TeslaPC' `
  -CertStoreLocation 'Cert:\LocalMachine\My' -NotAfter (Get-Date).AddYears(5) `
  -FriendlyName 'TeslaPC Dev Cert' -KeyExportPolicy Exportable;
Write-Output $cert.Thumbprint
```

The thumbprint is parsed with `(?i)\b([0-9A-F]{40})\b`. `TryLoadCertificate` then retries up to 5× (200 ms apart) — by thumbprint, then by friendly name + accessible key.

### Private-key ACLs (`GrantHttpSysPrivateKeyAccess`, `:212`)

- Resolve the key file: CSP → `%ProgramData%\Microsoft\Crypto\RSA\MachineKeys\{UniqueKeyContainerName}`; CNG → `%ProgramData%\Microsoft\Crypto\Keys\{cng.Key.UniqueName}`.
- Grant `Read` to `NetworkService`, `LocalSystem`, `LocalService` via .NET ACL APIs; on failure fall back to `icacls "{key}" /grant "NETWORK SERVICE:R" "NT AUTHORITY\SYSTEM:R" "NT AUTHORITY\LOCAL SERVICE:R"`.

### `bindSSLCert.bat` (manual equivalent)

- Same `APPID` GUID. Runs `New-SelfSignedCertificate` (DnsName + FriendlyName; no explicit `-Subject`), `certutil -repairstore`, `icacls` key grant, writes the thumbprint to `%TEMP%\teslapc_thumbprint.txt`.
- Cleans existing bindings on `8443`, `8444`, `8445` (the C# path cleans only `8443`), then binds `0.0.0.0:8443`.

## Key Classes

| Class | File | Responsibility |
|-------|------|----------------|
| `SslCertificateBootstrap` | `TeslaPCInterface/SslCertificateBootstrap.cs` | Cert lifecycle, key ACLs, URL ACLs, SSL binding |
| `bindSSLCert.bat` | `TeslaPCInterface/bindSSLCert.bat` | Manual cert generation + binding fallback |

## Constants

| Constant | Value |
|----------|-------|
| Cert friendly name | `TeslaPC Dev Cert` |
| Cert subject | `CN=TeslaPC` |
| DNS SANs | `localhost`, `TeslaPC` |
| Cert store | `LocalMachine\My` |
| Validity | 5 years; key `Exportable` |
| http.sys AppId | `{A253521A-C31E-457C-AADD-C0E42A87EA0F}` |
| Binding address:port | `0.0.0.0:8443` |
| URL ACLs | `http://+:8080/`, `https://+:8443/` (user `Everyone`) |
| Key ACL grantees | `NETWORK SERVICE`, `SYSTEM`, `LOCAL SERVICE` (Read) |
| PowerShell timeout | 30 000 ms |

## Access Control / Privileges

- Requires administrator; non-admin returns `false` and the server falls back to HTTP-only.

## Integration Points

- Called from `Program.Main`; its `bool` result becomes `enableHttps`, which controls whether the web server adds the `https://*:8443/` prefix.
- Independent of [tesla-browser-bypass](../tesla-browser-bypass/tesla-browser-bypass.md); both portproxy targets assume HTTPS is available on 8443 when bound.

## Database Schema

None.

## SQL Artifacts

None. (Certificate provisioning is done via PowerShell/netsh, not SQL.)
