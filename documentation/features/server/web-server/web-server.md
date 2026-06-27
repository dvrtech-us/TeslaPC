# Web Server

Unified HTTP/HTTPS server that fronts every TeslaPC service. A single `HttpListener`
serves the web UI, the MJPEG video stream, the audio WebSocket, and the input WebSocket,
dispatching by request path. `Program.cs` orchestrates startup of every subsystem around it.

## User Flow

1. The operator launches `TeslaPCInterface.exe` (optionally with `--localhost` or `--no-tesla-bypass`).
2. The app detects screen size, clamps the stream resolution to a max of 1280×720, and starts screen + audio capture.
3. Infrastructure bootstrap runs (firewall rule, HTTPS cert) unless `--localhost` is set.
4. The server begins listening on `8080` (HTTP) and, when HTTPS is ready, `8443` (HTTPS).
5. The console prints the reachable URLs. A browser hitting `/` receives `index.html`.
6. `Ctrl+C` or any keypress triggers graceful shutdown of all subsystems.

## Technical Flow

### Startup (`Program.Main`, `Program.cs:19`)

| Step | Action | Source |
|------|--------|--------|
| 1 | Define `httpPort = 8080`, `httpsPort = 8443` | `Program.cs:21-22` |
| 2 | Read `Screen.PrimaryScreen.Bounds`; clamp to `Size(min(w,1280), min(h,720))` | `Program.cs:24-27` |
| 3 | `new ImageStreamingServer(width, height, 30)` (30 FPS target) | `Program.cs:29` |
| 4 | `new AudioCapture(); audioCapture.StartCapturing()` | `Program.cs:30-31` |
| 5 | Unless `--no-tesla-bypass`: `new TeslaBrowserBypass().Setup(httpPort, httpsPort)`; dispose + null on failure | `Program.cs:34-44` |
| 6 | `localhostOnly = args.Contains("--localhost")` | `Program.cs:51` |
| 7 | If not localhost: `FirewallBootstrap.TryEnsureFirewallOpen(...)` and `enableHttps = SslCertificateBootstrap.TryEnsureHttpsReady(...)` | `Program.cs:53-57` |
| 8 | `new WebServer(imageServer, audioCapture)` | `Program.cs:59` |
| 9 | Start `StartWebServerAsync(...)` as a background task with a `TaskCompletionSource<bool>` start signal | `Program.cs:60-61` |
| 10 | Await the start signal with a 10-second timeout; dispose everything and exit on timeout | `Program.cs:63-74` |
| 11 | Print a connection banner — every usable URL (localhost, each LAN IPv4 via `GetLocalIPv4Addresses`, and the Tesla bypass IP) for HTTP and HTTPS; block until `Ctrl+C` or keypress; then dispose all subsystems | `Program.cs` |

### Listener setup (`WebServer.StartWebServerAsync`, `WebServer.cs:23`)

- A single `HttpListener` (`_Listener`, `WebServer.cs:11`) handles both protocols.
- **Localhost mode**: adds prefix `http://+:{port}/` — the strong wildcard `+` is used even for local-only binding to avoid http.sys returning 503 on host-specific registrations.
- **Network mode**: adds `http://+:{port}/`, and `https://+:{sslPort}/` when `enableHttps` is true. The strong wildcard `+` must match the URL ACL reservations created by `SslCertificateBootstrap` (`http://+:{port}/`, `https://+:{sslPort}/`); a weak wildcard `*` registers a different http.sys URL group than the reservation and causes every request to be rejected with 503 before reaching `GetContext`.
- `_Listener.Start()` is called; on `HttpListenerException` the error and `netsh` remediation hints are logged, the start signal is faulted, and the exception re-throws.
- A dedicated background `Thread` named `"HttpAccept"` runs `AcceptLoop()`. The start signal (`started.TrySetResult(true)`) is set immediately after the thread starts, because http.sys returns 503 until `GetContext()` is actively dequeuing.
- The method then parks on `await Task.Delay(Timeout.Infinite, token)` to stay alive.

### Request dispatch

- `AcceptLoop` (`WebServer.cs:~96`) calls `GetContext()` and hands each context to the thread pool via `ThreadPool.QueueUserWorkItem(_ => ProcessRequest(context))`. On a non-cancellation error it logs, sleeps 50 ms, and resumes.
- `ProcessRequest` runs `ProcessRequestAsync(context).GetAwaiter().GetResult()`, then `HandleRequest` routes by path.

### Routing table (`HandleRequest`, `WebServer.cs:147`)

| Priority | Condition | Maps to | Match |
|----------|-----------|---------|-------|
| 1 | WebSocket request **and** path starts with `/ws/audio` | `AudioCapture.HandleClientAsync(context)` | `OrdinalIgnoreCase` prefix |
| 2 | WebSocket request, any other path (incl. `/ws/input`) | `AcceptWebSocketAsync(context)` (input handler) | fallthrough |
| 3 | Non-WebSocket, path equals `/stream` | `ImageStreamingServer.HandleStreamRequest(context)` | `OrdinalIgnoreCase` exact |
| 4 | All other non-WebSocket requests | `HandleHttpAsync(context)` (static files) | fallthrough |

> Note: `/ws/input` is **not** matched explicitly. Any WebSocket whose path is not `/ws/audio` reaches the input handler by exclusion. See [input-control](../input-control/input-control.md).

### Static file serving (`HandleHttpAsync`, `WebServer.cs:172`)

- `/` maps to `index.html`. Other paths resolve to `rootPath + requestPath`.
- `rootPath` is `AppContext.BaseDirectory`, except when a debugger is attached it is resolved to the directory above `bin\` (strips the path at the last `"bin"` segment).
- Allowed extensions: `.html`, `.js`, `.css`, `.png`, `.jpg`. Anything else → `403`. Missing file → `404`.
- Content is read with `File.ReadAllText` (UTF-8) and returned with a content type from `getContentType` (`text/html`, `application/javascript`, `text/css`, `image/png`, `image/jpeg`, else `text/plain`).

## Key Classes

| Class | File | Responsibility |
|-------|------|----------------|
| `Program` | `TeslaPCInterface/Program.cs` | Entry point; orchestrates startup/shutdown of all subsystems |
| `WebServer` | `TeslaPCInterface/WebServer.cs` | Unified HTTP/HTTPS listener; path-based routing; hosts the input WebSocket handler |
| `ImageStreamingServer` | `TeslaPCInterface/ImageStreamingServer.cs` | Handles `/stream` (injected) |
| `AudioCapture` | `TeslaPCInterface/AudioStreamingServer.cs` | Handles `/ws/audio` (injected) |

## Routes and Access Control

| Route | Protocol | Auth | Handler |
|-------|----------|------|---------|
| `/` and static assets | HTTP/HTTPS | none | `HandleHttpAsync` |
| `/stream` | HTTP/HTTPS | none | `ImageStreamingServer.HandleStreamRequest` |
| `/ws/audio` | WebSocket | none | `AudioCapture.HandleClientAsync` |
| `/ws/input` (and other WS paths) | WebSocket | none | `WebServer.AcceptWebSocketAsync` |

**There is no authentication or authorization on any route.** Access is controlled entirely at the network layer (firewall, localhost binding, hotspot bypass scope).

## Integration Points

- Constructs with an `ImageStreamingServer` and `AudioCapture` injected from `Program`.
- Calls `FirewallBootstrap` and `SslCertificateBootstrap` results are passed in as flags (`enableHttps`), decided in `Program.Main` before the server is built.
- Shutdown calls `StopAsync()` (cancels the token, `Stop()` + `Close()` the listener; swallows `HttpListenerException` on close).

## Database Schema

None. TeslaPC has no database.

## SQL Artifacts

None.
