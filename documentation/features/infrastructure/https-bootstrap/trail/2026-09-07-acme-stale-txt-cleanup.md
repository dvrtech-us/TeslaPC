# ACME: clear stale challenge TXT records before creating

- Date: 2026-09-07
- Feature: https-bootstrap (AcmeCertificateManager)
- Related code: `TeslaPCInterface/AcmeCertificateManager.cs` (`DeleteExistingTxtRecordsAsync`, called in `TryEnsureCertificateAsync`)

## Context

Observed on the laptop after the elevation-gate fix: renewal attempts failed with Cloudflare's
"an identical record already exists" when creating `_acme-challenge.<host>`. Cause: a previous
attempt crashed/exited before its best-effort `DeleteTxtRecordAsync` cleanup ran, leaving the TXT
record behind. Let's Encrypt reuses the pending authorization on the next order, so the token —
and therefore the TXT value — repeats, and Cloudflare rejects the duplicate create. The create
throws, the outer catch aborts the renewal, and every subsequent attempt fails the same way.

## Decisions

- Before creating the challenge record, `DeleteExistingTxtRecordsAsync(zoneId, name, token)` lists
  all TXT records with that exact name (`?type=TXT&name=...&per_page=100`) and deletes each one,
  logging per record. Failures in this cleanup are logged and non-fatal (the create then decides).
- The post-validation cleanup in the `finally` block is unchanged; the pre-create sweep makes the
  flow self-healing when that cleanup was skipped.

## Alternatives Considered

- Treat Cloudflare's "identical record already exists" as success (the needed value is present):
  works for the exact-duplicate case but leaves stale records accumulating and does not handle a
  stale record with a *different* value alongside. Sweeping first is strictly more robust.

## Consequences

- Positive: renewals recover automatically from crashed prior attempts; no manual Cloudflare
  cleanup needed.
- Negative: none material — the record is challenge-only and recreated fresh on each order.
