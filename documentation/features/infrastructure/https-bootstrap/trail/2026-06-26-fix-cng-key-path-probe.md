# Fix Private-Key File Lookup for CNG Keys Stored in the CAPI Folder

- Date: 2026-06-26
- Feature: https-bootstrap
- Related code: `TeslaPCInterface/SslCertificateBootstrap.cs` (`TryGetPrivateKeyFilePath`)

## Context

After the CLIXML key-grant fix, a non-fatal warning remained on the laptop when reusing an
existing certificate:

```
[SSL] Warning: could not locate the certificate private key file.
```

Diagnosis on the actual cert: `GetRSAPrivateKey()` returns an **`RSACng`** instance, but the
key file physically lives in the **legacy CAPI** directory
`%ProgramData%\Microsoft\Crypto\RSA\MachineKeys\<UniqueName>`, **not** the CNG directory
`%ProgramData%\Microsoft\Crypto\Keys\<UniqueName>`. The old code mapped key type → directory
(`RSACng` ⇒ `Crypto\Keys` only), so `File.Exists` failed and the http.sys read-ACL grant was
silently skipped. HTTPS still worked only because `SYSTEM` has Full control of the key by
default and http.sys reads the key as `SYSTEM`; `NETWORK SERVICE` was never actually granted.

## Decisions

- `TryGetPrivateKeyFilePath` now resolves the key's `UniqueName` (from `RSACng.Key.UniqueName`
  or `RSACryptoServiceProvider.CspKeyContainerInfo.UniqueKeyContainerName`) and then **probes
  both** the CNG `Crypto\Keys` folder and the CAPI `Crypto\RSA\MachineKeys` folder, returning
  whichever file actually exists.

## Consequences

- Positive: the key file is located regardless of which store backs it, so the `NETWORK SERVICE`
  / `LOCAL SERVICE` read grant applies as intended and the warning no longer appears.
- Negative: none. The grant remains best-effort (a missing file still warns, but only when the
  key truly cannot be found in either location).
