# CLAUDE.md

> **Read [`AGENTS.md`](AGENTS.md) first.** It is the primary operating guide for this repo
> (build/run, runtime flags, conventions, and the documentation discipline). Feature-level
> reference docs live under [`documentation/`](documentation/README.md) and **must be kept up
> to date** when you change behavior — see the documentation system rules in `AGENTS.md`.

## Project Overview

**TeslaPC** is a Windows remote desktop streaming application built with .NET 10.0 and C#. It streams screen content, system audio, and accepts mouse/keyboard input through a web-based interface accessible via any browser.

## Architecture

The application runs a single unified HTTP/HTTPS server on ports 8080 (HTTP) and 8443 (HTTPS). All services are routed by path:

| Route | Protocol | Purpose |
|-------|----------|---------|
| `/` | HTTP | Web UI (`index.html`, JS, CSS) |
| `/stream` | HTTP | MJPEG screen capture streaming |
| `/ws/input` | WebSocket | Mouse & keyboard input |
| `/ws/audio` | WebSocket | System audio streaming (WASAPI loopback) |

`TeslaBrowserBypass.cs` optionally adds a CGNAT secondary IP (`100.64.0.1`) to the Windows Mobile Hotspot adapter with port forwarding so the Tesla in-car browser can connect (it blocks RFC 1918 private IPs).

### Source Files

```
TeslaPCInterface/
├── Program.cs                 # WinExe entry: loads .env, runs MainForm
├── MainForm.cs                # WinForms touch control panel (Dashboard + Log tabs)
├── TeslaPcService.cs          # Server lifecycle + status + hotspot-watchdog/ACME-renewal loops
├── WebServer.cs               # Unified HTTP/HTTPS server with path-based routing + input replay
├── ImageStreamingServer.cs    # MJPEG screen capture (30 FPS, max 1280x720)
├── DxgiScreenCapture.cs       # DXGI Desktop Duplication capture (GDI fallback)
├── AudioStreamingServer.cs    # WASAPI loopback audio capture via CSCore
├── TeslaBrowserBypass.cs      # Tesla in-car browser hotspot bypass (CGNAT IP + portproxy)
├── HotspotManager.cs          # Enable/disable Windows Mobile Hotspot (WinRT tethering)
├── FirewallBootstrap.cs       # Inbound firewall rule for 8080/8443
├── SslCertificateBootstrap.cs # Bind trusted/self-signed cert to 8443 (http.sys)
├── AcmeCertificateManager.cs  # In-app Let's Encrypt (Certes + Cloudflare DNS-01)
├── MjpegWriter.cs             # MJPEG multipart boundary encoder
├── index.html                 # Single-page web UI (vanilla JS, Web Audio API)
├── PCMPlayerProcessor.js      # AudioWorklet processor for PCM audio playback
├── .env.example               # Config template (copy to .env; gitignored)
├── TeslaPCInterface.csproj    # Project file (.NET 10.0, WinForms, WinExe)
├── TeslaPCInterface.sln       # Visual Studio solution
└── bindSSLCert.bat            # Binds SSL cert to HTTPS port 8443 (requires admin)
```

## Build & Run

**Prerequisites**: .NET 10.0 SDK, Windows (uses Win32 APIs)

```bash
# Build
dotnet build TeslaPCInterface.sln

# Run
dotnet run --project TeslaPCInterface/TeslaPCInterface.csproj
```

For HTTPS support, run `bindSSLCert.bat` as administrator first. Tesla browser bypass requires administrator privileges.

## Key Dependencies (NuGet)

- **CSCore 1.2.1.2** - WASAPI loopback audio capture
- **NAudio 2.2.1** - Audio processing
- **System.Net.WebSockets 4.3.0** - WebSocket protocol

## Code Conventions

- **Language**: C# with nullable reference types enabled and implicit usings
- **Naming**: PascalCase for classes, methods, and properties (standard C# conventions)
- **Platform**: Windows-only (P/Invoke to `user32.dll` for `SetCursorPos`, `mouse_event`, `keybd_event`)
- **Frontend**: Vanilla JavaScript with no frameworks - HTML5 Canvas, Web Audio API, WebSockets
- **Ports**: Hardcoded in source (8080 HTTP, 8443 HTTPS)
- **No formal linting or formatting configuration**

## Testing

No automated test suite exists. Manual testing by running the application and connecting via browser.

## Git Workflow

- **Main branch**: `main`
- **No CI/CD pipeline** configured
- **No pre-commit hooks**
- Commit messages are short and descriptive