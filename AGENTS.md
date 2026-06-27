# AGENTS.md

Operating guide for AI agents and developers working in the TeslaPC repository. Read this
before making changes. For a feature-by-feature reference, start at
[`documentation/README.md`](documentation/README.md).

## What This Project Is

TeslaPC is a Windows remote-desktop streaming app (.NET 10.0, C#, WinForms). A single unified
HTTP/HTTPS server (ports `8080`/`8443`) streams the screen as MJPEG, streams system audio over
a WebSocket, and replays mouse input — all reachable from any browser, including the Tesla
in-car browser via a CGNAT hotspot bypass.

## Documentation System (read before and after coding)

All feature documentation lives under `documentation/`. The structure is strict:

```
documentation/
  features/<module>/<feature-name>/
    <feature-name>.md   # what the feature does today
    baseline.md         # behavior invariants (regression reference)
    trail/YYYY-MM-DD-<title>.md   # append-only decision history
  planning/<Module>/YYYY-MM-DD-<title>.md
```

**The discipline — after any code change that affects a feature:**

1. **Always** update the feature's `<feature-name>.md` to match current behavior.
2. **If intended behavior changed**, update `baseline.md` invariants.
3. **If a non-trivial decision was made**, add a new `trail/` entry. **Never edit an existing trail entry** — append a new one.
4. Keep the Feature Map in `documentation/README.md` current when features are added or renamed.

**Quality rules:** name exact classes, methods, file paths, and constant values; document
timings, thresholds, and config keys; describe failure/fallback paths; document actual
behavior, not aspirations. The project uses a small SQLite database (`Microsoft.Data.Sqlite`)
at `%ProgramData%\TeslaPC\teslapc.db` (owned by the `media-library` feature); there are
still **no `sql/` folders** — the schema is created in code.

### Feature Map (quick index)

| Module | Feature | Primary source |
|--------|---------|----------------|
| server | web-server | `WebServer.cs`, `Program.cs` |
| server | input-control | `WebServer.cs`, `Program.cs` |
| streaming | screen-capture | `ImageStreamingServer.cs`, `DxgiScreenCapture.cs`, `MjpegWriter.cs` |
| streaming | audio-capture | `AudioStreamingServer.cs`, `PCMPlayerProcessor.js` |
| streaming | display-control | `DisplayManager.cs`, `WebServer.cs` |
| app | control-panel | `Program.cs`, `MainForm.cs`, `TeslaPcService.cs` |
| app | configuration | `AppSettings.cs`, `WebServer.cs`, `MainForm.cs`, `config.html` |
| media | media-library | `MediaLibrary.cs`, `MediaStreamer.cs` |
| client | web-ui | `index.html`, `PCMPlayerProcessor.js` |
| infrastructure | tesla-browser-bypass | `TeslaBrowserBypass.cs` |
| infrastructure | firewall-bootstrap | `FirewallBootstrap.cs` |
| infrastructure | https-bootstrap | `SslCertificateBootstrap.cs`, `bindSSLCert.bat` |

## Source Layout

```
TeslaPCInterface/
├── Program.cs                 # Entry point; startup/shutdown orchestration
├── WebServer.cs               # Unified HTTP/HTTPS server + routing + input WebSocket
├── ImageStreamingServer.cs    # Shared MJPEG capture loop, JPEG encoding, per-client send
├── DxgiScreenCapture.cs       # DXGI Desktop Duplication capture (GDI fallback)
├── MjpegWriter.cs             # multipart/x-mixed-replace framing
├── AudioStreamingServer.cs    # WASAPI loopback capture + /ws/audio broadcast
├── PCMPlayerProcessor.js      # Client AudioWorklet (decode/resample/buffer/play)
├── TeslaBrowserBypass.cs      # CGNAT 100.64.0.1 + portproxy on the hotspot adapter
├── FirewallBootstrap.cs       # Inbound TCP firewall rule (8080/8443)
├── SslCertificateBootstrap.cs # Self-signed cert + key ACLs + URL ACLs + SSL binding
├── index.html                 # Single-page web UI
├── bindSSLCert.bat            # Manual SSL bind fallback
└── restart-server.bat         # Dev restart (passes --no-tesla-bypass)
```

## Build & Run

```bash
dotnet build TeslaPCInterface.sln
dotnet run --project TeslaPCInterface/TeslaPCInterface.csproj
```

Runtime flags / env:
- `--localhost` — bind to local wildcard only; **skips** firewall + HTTPS bootstrap.
- `--no-tesla-bypass` — disable the Tesla CGNAT bypass (firewall + HTTPS bootstrap still run).
- `--https-host <host>` (or `TESLAPC_HTTPS_HOST`) — hostname for a publicly-trusted cert; when set
  with `TESLAPC_CF_TOKEN`, the app issues/renews a Let's Encrypt cert in-process (Certes + Cloudflare
  DNS-01) so the Tesla browser trusts HTTPS. See `documentation/features/infrastructure/https-bootstrap`.
- `TESLAPC_CF_TOKEN` — Cloudflare API token (Zone:Read + DNS:Edit) for the DNS-01 challenge.
- `TESLAPC_ACME_EMAIL` (optional) — Let's Encrypt account contact. `TESLAPC_ACME_STAGING=1` — LE staging.

HTTPS, the firewall rule, and the Tesla bypass all require **administrator** privileges. Without
elevation those steps return false and the app degrades gracefully (HTTP-only, no remote rule,
no bypass).

## Conventions

- C# with nullable reference types and implicit usings; PascalCase; Windows-only (P/Invoke to `user32.dll`).
- Frontend is vanilla JS — no frameworks, no build step; inline CSS/JS in `index.html`.
- Ports `8080`/`8443` are hardcoded in `Program.cs`.
- No automated test suite; verify by running the app and connecting via browser.
- No CI/CD, no pre-commit hooks. Main branch is `main`. Commit messages are short and descriptive.

## Versioning & Releases

- The application version is set in `TeslaPCInterface.csproj` via `<Version>1.0.0</Version>`.
- `AppSettings.Version` reads `AssemblyInformationalVersionAttribute` at runtime, strips any
  `+git` suffix, and falls back to the assembly version.
- The version is surfaced in the WinForms tab bar, the `GET /config` JSON (`version` field),
  the `config.html` footer, and the `GET /version` endpoint (`{ "version": "1.0.0" }`).
- **Release convention:** tag git at the released commit as `v<Version>` (e.g. `v1.0.0`).

## Per-Org Engineering Defaults

- Default stack for new work unless the environment dictates otherwise: desktop = C#, web = PHP/Laravel + Bootstrap, mobile = Flutter, Windows scripting = PowerShell.
- Refer to end users/customers as **Clients**.

## Current Known Gaps (documented, not bugs)

- **No authentication** on any route; access is controlled only at the network layer.
- Mouse supports left-click, drag, and right-click (long-press on touch); **no scroll-wheel** yet. Keyboard input is implemented (typing, named keys, and paste, replayed host-side via `SendKeys`); the `keybd_event` P/Invoke remains declared but unused.
- Static files are read via `File.ReadAllText` (UTF-8), so binary assets (e.g. `.png`/`.jpg`) are not served correctly. Video playback does **not** use this path — it streams via the ffmpeg/MJPEG media pipeline.
- `bindSSLCert.bat` and `SslCertificateBootstrap.cs` diverge slightly (extra port cleanup, explicit subject) — see the https-bootstrap baseline.
- **SQLite advisory GHSA-2m69-gcr7-jv3q** on `SQLitePCLRaw.lib.e_sqlite3` (transitive via
  `Microsoft.Data.Sqlite`). No patched release exists as of 2026-06-27. Risk is nil: the DB
  is a local, single-user, parameterized-query store with no untrusted SQL. Pinned at
  `SQLitePCLRaw.bundle_e_sqlite3` 2.1.11 (latest). See
  `documentation/features/media/media-library/media-library.md`.
