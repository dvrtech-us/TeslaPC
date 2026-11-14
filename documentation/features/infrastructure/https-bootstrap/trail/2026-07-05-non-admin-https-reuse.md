# Non-admin HTTPS reuse when http.sys is already configured

**Date:** 2026-07-05

## Problem

`TeslaPCDeployRestart` starts the WinForms app in the interactive user session without
administrator elevation. `TryEnsureHttpsReady` returned `false` immediately on the admin check,
so `WebServer` never added the `https://+:8443/` prefix even though a prior elevated run had
already reserved URL ACLs and bound the SSL certificate.

## Decision

Before requiring elevation, detect an existing http.sys configuration via read-only `netsh`
queries:

- `HasSslBinding(port)` — `netsh http show sslcert ipport=0.0.0.0:{port}` reports a certificate hash
- `HasUrlReservation(url)` — `netsh http show urlacl` lists the reserved URL

When all three checks pass (HTTP + HTTPS URL ACLs and SSL binding), return `true` and skip
provisioning. Elevated setup remains required for first-time bootstrap, cert rotation, and
URL ACL creation.

## Files

- `TeslaPCInterface/SslCertificateBootstrap.cs` — `IsHttpsAlreadyConfigured`, `HasSslBinding`,
  `HasUrlReservation`