# TeslaPC Documentation

Structured documentation for the TeslaPC remote-desktop streaming application. See the
root [`AGENTS.md`](../AGENTS.md) for the documentation conventions every change must follow.

## Documentation System

Each feature has its own directory under `features/<module>/<feature-name>/` containing:

- `<feature-name>.md` — what the feature does **today** (user + technical flow, classes, routes).
- `baseline.md` — behavior invariants (the known-good reference for regression checks).
- `trail/` — dated decision history; entries are append-only and never edited after creation.

`planning/` holds dated planning docs, audits, and proposals per module.

## Feature Map

| Module | Feature | Summary | Primary source |
|--------|---------|---------|----------------|
| server | [web-server](features/server/web-server/web-server.md) | Unified HTTP/HTTPS listener with path-based routing and startup orchestration | `WebServer.cs`, `Program.cs` |
| server | [input-control](features/server/input-control/input-control.md) | Mouse (Win32 P/Invoke) and keyboard (SendKeys) over the `/ws/input` WebSocket | `WebServer.cs`, `Program.cs` |
| streaming | [screen-capture](features/streaming/screen-capture/screen-capture.md) | MJPEG screen streaming with DXGI Desktop Duplication and GDI fallback | `ImageStreamingServer.cs`, `DxgiScreenCapture.cs`, `MjpegWriter.cs` |
| streaming | [audio-capture](features/streaming/audio-capture/audio-capture.md) | WASAPI loopback system-audio streaming over `/ws/audio` | `AudioStreamingServer.cs`, `PCMPlayerProcessor.js` |
| client | [web-ui](features/client/web-ui/web-ui.md) | Single-page browser client (video, audio, input) | `index.html`, `PCMPlayerProcessor.js` |
| media | [file-browser-vlc](features/media/file-browser-vlc/file-browser-vlc.md) | Host file browser that launches videos in VLC full-screen | `WebServer.cs`, `list.html`, `play.html` |
| infrastructure | [tesla-browser-bypass](features/infrastructure/tesla-browser-bypass/tesla-browser-bypass.md) | CGNAT secondary IP + portproxy so the Tesla in-car browser can connect | `TeslaBrowserBypass.cs` |
| infrastructure | [firewall-bootstrap](features/infrastructure/firewall-bootstrap/firewall-bootstrap.md) | Idempotent Windows Firewall inbound rule for ports 8080/8443 | `FirewallBootstrap.cs` |
| infrastructure | [https-bootstrap](features/infrastructure/https-bootstrap/https-bootstrap.md) | Self-signed cert generation, key ACLs, URL ACLs, and SSL binding to port 8443 | `SslCertificateBootstrap.cs`, `bindSSLCert.bat` |

## Conventions

- Document **actual behavior as implemented**, not desired future behavior.
- Name exact classes, methods, file paths, and constant values — no vague references.
- Update the primary doc on every behavior-affecting change; update `baseline.md` only when intended behavior changes; add a `trail/` entry for any non-trivial decision.
- This project has **no database**, so feature directories contain no `sql/` folder.
