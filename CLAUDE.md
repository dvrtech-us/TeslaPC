# CLAUDE.md

## Project Overview

**TeslaPC** is a Windows remote desktop streaming application built with .NET 6.0 and C#. It streams screen content, system audio, and accepts mouse/keyboard input through a web-based interface accessible via any browser.

## Architecture

The application runs three concurrent servers:

| Server | HTTP Port | HTTPS Port | Protocol | Purpose |
|--------|-----------|------------|----------|---------|
| WebServer | 8080 | 8443 | WebSocket | Mouse & keyboard input |
| ImageStreamingServer | 8081 | 8444 | MJPEG over HTTP | Screen capture streaming |
| AudioStreamingServer | 8082 | 8445 | WebSocket | System audio streaming (WASAPI loopback) |

### Source Files

```
TeslaPCInterface/
├── Program.cs                 # Entry point - initializes all three servers
├── WebServer.cs               # WebSocket server for mouse/keyboard control (P/Invoke to user32.dll)
├── ImageStreamingServer.cs    # MJPEG screen capture (30 FPS, max 1280x720)
├── AudioStreamingServer.cs    # WASAPI loopback audio capture via CSCore
├── MjpegWriter.cs             # MJPEG multipart boundary encoder
├── index.html                 # Single-page web UI (vanilla JS, Canvas, Web Audio API)
├── PCMPlayerProcessor.js      # AudioWorklet processor for PCM audio playback
├── TeslaPCInterface.csproj    # Project file (.NET 6.0, WinForms)
├── TeslaPCInterface.sln       # Visual Studio solution
└── bindSSLCert.bat            # Binds SSL certs to HTTPS ports (requires admin)
```

## Build & Run

**Prerequisites**: .NET 6.0 SDK, Windows (uses Win32 APIs)

```bash
# Build
dotnet build TeslaPCInterface.sln

# Run
dotnet run --project TeslaPCInterface/TeslaPCInterface.csproj
```

For HTTPS support, run `bindSSLCert.bat` as administrator first.

## Key Dependencies (NuGet)

- **CSCore 1.2.1.2** - WASAPI loopback audio capture
- **NAudio 2.2.1** - Audio processing
- **System.Net.WebSockets 4.3.0** - WebSocket protocol

## Code Conventions

- **Language**: C# with nullable reference types enabled and implicit usings
- **Naming**: PascalCase for classes, methods, and properties (standard C# conventions)
- **Platform**: Windows-only (P/Invoke to `user32.dll` for `SetCursorPos`, `mouse_event`, `keybd_event`)
- **Frontend**: Vanilla JavaScript with no frameworks - HTML5 Canvas, Web Audio API, WebSockets
- **Ports**: Hardcoded in source (8080-8082 HTTP, 8443-8445 HTTPS)
- **No formal linting or formatting configuration**

## Testing

No automated test suite exists. Manual testing by running the application and connecting via browser.

## Git Workflow

- **Main branch**: `main`
- **No CI/CD pipeline** configured
- **No pre-commit hooks**
- Commit messages are short and descriptive
