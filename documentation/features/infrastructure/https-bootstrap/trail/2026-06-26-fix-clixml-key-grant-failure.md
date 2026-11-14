# Fix Intermittent HTTPS Bootstrap Failure (PowerShell/CLIXML Key Grant)

- Date: 2026-06-26
- Feature: https-bootstrap
- Related code: `TeslaPCInterface/SslCertificateBootstrap.cs` (`GrantHttpSysPrivateKeyAccess`, `PrepareCertificateForHttpSys`, `RemoveBrokenCertificates`)

## Context

During hotspot/Tesla-bypass testing on the laptop, the HTTPS bootstrap failed and the server
fell back to **HTTP-only**:

```
[SSL] Warning: could not grant http.sys access to the certificate private key:  | #< CLIXML ...
[SSL] Certificate was created but could not be loaded from the store.
[SSL] Failed to create or load the TeslaPC development certificate.
Unified server started on port 8080 (HTTP only).
```

Root cause: the previous implementation of `GrantHttpSysPrivateKeyAccess(thumbprint)` granted
the http.sys service accounts read access to the certificate private key by spawning a
**PowerShell** child process that ran `icacls ... | Out-Null`. When that child's streams are
redirected, PowerShell emits CLIXML (e.g. the "Preparing modules for first use" progress
record) into the captured output, which polluted the result and caused the grant — and the
subsequent certificate load — to fail. The failure was intermittent because it depended on
PowerShell module-load timing.

## Decisions

- Replaced the PowerShell-based key grant with direct **.NET `FileSecurity` ACL APIs**
  (`FileInfo.GetAccessControl()` / `SetAccessControl()`), granting `Read` to `NETWORK SERVICE`,
  `SYSTEM`, and `LOCAL SERVICE`. A direct **`icacls`** call (no PowerShell wrapper) remains as
  the fallback, so no CLIXML is ever captured.
- Added `RemoveBrokenCertificates()` (prunes certs with inaccessible private keys before reuse)
  and `PrepareCertificateForHttpSys(cert, thumbprint)` (repair-store + key grant), plus proper
  `X509Certificate2` disposal.

## Validation

- A self-contained build containing this fix was deployed earlier in the same testing session
  and its HTTPS bootstrap **succeeded** (`[SSL] HTTPS ready on port 8443`). The laptop's git
  clone was running the older committed version, which exhibited the failure — confirming the
  diagnosis. This commit promotes the proven working-tree fix.

## Consequences

- Positive: HTTPS bootstrap no longer depends on PowerShell output parsing for the key grant;
  it should come up reliably each run.
- Negative: none identified. The `.NET` ACL path is more direct and avoids a process spawn.
