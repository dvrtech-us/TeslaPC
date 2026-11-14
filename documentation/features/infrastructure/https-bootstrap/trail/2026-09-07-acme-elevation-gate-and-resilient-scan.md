# ACME: elevation gate before issuance + fault-tolerant cert scan

- Date: 2026-09-07
- Feature: https-bootstrap (AcmeCertificateManager)
- Related code: `TeslaPCInterface/AcmeCertificateManager.cs` (`TryEnsureCertificateAsync`, `GetBestCertificateDaysLeft`, `IsElevated`)

## Context

On a machine that restarts the app without admin rights, every start requested a brand-new
Let's Encrypt certificate. Two causes:

1. `TryEnsureCertificateAsync` contacted Let's Encrypt **before** verifying it could store the
   result. Importing to `LocalMachine\My` with `MachineKeySet` requires elevation, so on non-admin
   runs the order completed at LE, the import threw, the cert was discarded, and the next start
   requested again — burning one real issuance per start (LE duplicate-cert rate limit: 5/week).
2. `HasValidCertificate` wrapped the whole `LocalMachine\My` scan in a single try/catch: any one
   foreign cert throwing (e.g. `MatchesHostname` on a malformed extension) aborted the scan,
   returned false, and triggered a re-issue even when a valid cert was present.

## Decisions

- `TryEnsureCertificateAsync` now checks `IsElevated()` **before** any ACME traffic when no
  sufficiently fresh cert exists:
  - not elevated + cert still valid (≤ 30 days left) → keep it, log, return `true`;
  - not elevated + no valid cert → log and return `false` — LE is never contacted.
- `HasValidCertificate(out daysLeft)` was replaced by `GetBestCertificateDaysLeft(host)`
  (0 = none usable). Each certificate is inspected inside its own try/catch; an unreadable entry is
  logged and skipped instead of aborting the scan.
- The pre-request log now distinguishes "renewing (N days left)" from "no valid cert found".

## Alternatives Considered

- Export the issued PFX to `%ProgramData%\TeslaPC` so a later elevated run could import without
  re-requesting: rejected for now — writing a private key to disk adds a secret-handling burden and
  the elevation gate alone stops the repeated issuance.
- Fall back to `CurrentUser\My` when not elevated: rejected — http.sys binding needs the machine
  store (and netsh needs admin anyway), so the cert would be unusable.

## Consequences

- Positive: non-admin restarts no longer hammer Let's Encrypt; a valid-but-expiring cert is kept
  instead of being re-requested and lost.
- Negative: issuance/renewal only happens on elevated runs — an always-non-admin deployment will
  see the expiry warning until it is run elevated once per ~60 days.
