# Verify the bound cert before reusing http.sys bindings (rebind after renewal)

- Date: 2026-09-07
- Feature: https-bootstrap
- Related code: `TeslaPCInterface/SslCertificateBootstrap.cs` (`HasHealthySslBinding`, `IsHttpsAlreadyConfigured`)

## Context

Found live on the laptop right after the first successful in-app ACME renewal: HTTPS on 8443
started resetting every TLS handshake. Chain of events:

1. `AcmeCertificateManager.ImportToMachineStore` stores the renewed cert and **removes** the old
   `TeslaPC LE (<host>)` cert (by design, to avoid pile-up).
2. The non-admin-restart reuse fix (`IsHttpsAlreadyConfigured`, 2026-07-05) accepted **any**
   cert hash bound on 8443 and short-circuited before `TryUseTrustedCertificate` could bind the
   new cert.
3. Result: http.sys kept a binding whose thumbprint no longer existed in `LocalMachine\My` —
   handshakes fail with connection reset. Even without the store removal, the binding would have
   silently served the old cert until it expired.

## Decisions

- `HasSslBinding` (any hash present) was replaced by `HasHealthySslBinding(port, trustedHost)`:
  - the bound thumbprint must exist in `LocalMachine\My` and not be expired;
  - when `trustedHost` is set and `GetTrustedCertificate` finds a **newer** cert with a different
    thumbprint, the binding is treated as stale (logged "newer trusted certificate ... rebinding").
  - Store inspection failure keeps the old reuse behavior (log + reuse) rather than forcing an
    admin-only path on machines where the binding is actually fine.
- On an unhealthy binding the flow falls through to the normal bootstrap: elevated runs rebind
  (prepare + `netsh http add sslcert`); non-elevated runs log the admin requirement and return
  false (HTTP-only) — honest failure instead of advertising an HTTPS port that resets.
- The renewal loop already calls `TryEnsureHttpsReady` after a successful renewal; with this check
  it now actually rebinds instead of short-circuiting.

## Alternatives Considered

- Stop `ImportToMachineStore` from deleting the old cert: leaves the stale-binding problem (old
  cert served until expiry) and accumulates certs; rejected.
- Rebind directly inside `AcmeCertificateManager` after import: duplicates binding/ACL logic that
  `SslCertificateBootstrap` already owns; rejected.

## Consequences

- Positive: renewals now propagate to http.sys automatically; a binding pointing at a deleted or
  expired cert self-heals on the next elevated start.
- Negative: a non-admin start with a broken binding now runs HTTP-only (previously it claimed
  HTTPS was configured while handshakes failed) — requires one elevated run to restore HTTPS.
