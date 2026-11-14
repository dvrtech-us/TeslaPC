# Fix Network-Mode 503: Strong Wildcard for Listener Prefixes

- Date: 2026-06-26
- Feature: web-server
- Related code: `TeslaPCInterface/WebServer.cs` (`StartWebServerAsync`)

## Context

During cross-device testing on a laptop, every request in **network mode** (no `--localhost`)
returned HTTP **503 Server Unavailable** — including requests from `localhost` on the host
itself. Diagnosis showed the requests never reached the application (`GetContext` never saw
them; no `[HTTP] GET` log lines), so http.sys was rejecting them before dispatch.

Root cause: a **wildcard mismatch**. `SslCertificateBootstrap` reserves the URL ACLs with the
strong wildcard (`http://+:8080/`, `https://+:8443/`), and localhost mode already bound the
strong wildcard. But network mode bound the **weak** wildcard (`http://*:8080/`,
`https://*:8443/`). In http.sys, `+` and `*` are different URL groups, so the listener's
registration did not match the reservation and http.sys returned 503 for every request.

## Decisions

- Changed network-mode prefixes in `StartWebServerAsync` from `http://*:{port}/` /
  `https://*:{sslPort}/` to `http://+:{port}/` / `https://+:{sslPort}/`.
- This makes network mode consistent with both localhost mode and the URL ACL reservations.

## Alternatives Considered

- **Change the URL ACL reservations to `*`** — rejected; the existing localhost workaround and
  the reservations already standardize on `+`, and `+` is the more permissive/correct strong
  wildcard for binding all interfaces.
- **Add a separate `*` reservation** — unnecessary and would leave two divergent code paths.

## Consequences

- Positive: network mode now serves HTTP and HTTPS to remote clients. Verified end-to-end from
  a separate PC over the LAN: `http://<ip>:8080/` → 200, `https://<ip>:8443/` → 200, and
  `/stream` delivered valid MJPEG frames.
- Positive: all listener prefixes now use the strong wildcard, matching the reservations — see
  the updated baseline invariant.
- Negative: none identified. (Binding `+` already required administrator privileges, which
  network mode also requires for firewall and HTTPS bootstrap.)

## Verification Notes

- Screen capture (DXGI desktop duplication, 1080p) only works when the process runs in the
  **interactive console session** (session 1) with elevation; launched in session 0 / non-elevated
  it cannot capture the desktop. This is a Windows session constraint, not an app bug.
