# Web Server — Behavior Baseline

Known-good invariants for the unified HTTP/HTTPS server. Update only when intended behavior changes.

## Flow Invariants

- A **single** `HttpListener` instance serves both HTTP (8080) and HTTPS (8443).
- HTTPS is bound **only** when `enableHttps` is true, which is the return value of `SslCertificateBootstrap.TryEnsureHttpsReady`.
- Listener prefixes always use the strong wildcard: `http://+:{port}/` in localhost mode, `http://*:{port}/` (+ `https://*:{sslPort}/`) in network mode. Host-specific prefixes are never used.
- The HTTP accept loop runs on a dedicated background thread named `"HttpAccept"`; each request is processed on a thread-pool thread.
- The startup `TaskCompletionSource` is signalled immediately after the accept thread starts — before any request is served.
- `Program.Main` aborts startup if the server does not signal ready within **10 seconds**.

## Routing Rules

- Routing is evaluated in this fixed order: (1) `/ws/audio` WebSocket, (2) any other WebSocket → input handler, (3) exact `/stream` → MJPEG, (4) everything else → static files.
- Path matching for `/ws/audio` and `/stream` is `OrdinalIgnoreCase`.
- `/ws/input` is matched by exclusion, not by an explicit path check.
- Static files: only `.html`, `.js`, `.css`, `.png`, `.jpg` are served; all other extensions return `403`; missing files return `404`.
- `/` always resolves to `index.html`.

## Access Control

- No route requires authentication. Network-layer controls (firewall, `--localhost`, bypass IP scope) are the only access boundary.

## Configuration Defaults

- HTTP port: `8080`. HTTPS port: `8443`. Both are hardcoded in `Program.cs`.
- Stream resolution is clamped to a maximum of `1280×720`.
- Target frame rate passed to the image server is `30` FPS.
- `--localhost` restricts binding to local wildcard and **skips** both firewall and HTTPS bootstrap.
- `--no-tesla-bypass` disables only the Tesla bypass; firewall and HTTPS bootstrap still run.

## Failure Behavior

- `HttpListener.Start()` failure faults the start signal and re-throws after logging `netsh` remediation hints.
- A request-handler exception is logged and best-effort returns HTTP `500`.
- The accept loop survives transient errors by sleeping 50 ms and continuing; if it stops without cancellation it logs that new requests will 503 until restart.
- On shutdown, `HttpListenerException` during `Close()` is swallowed.
