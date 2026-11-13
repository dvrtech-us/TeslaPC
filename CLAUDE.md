# CLAUDE.md

> **Read [`AGENTS.md`](AGENTS.md) first.** It is the primary operating guide for this repo
> (build/run, runtime flags, conventions, and the documentation discipline). Feature-level
> reference docs live under [`documentation/`](documentation/README.md) and **must be kept up
> to date** when you change behavior — see the documentation system rules in `AGENTS.md`.

## Project Overview

**TeslaPC** is a Rust remote-desktop server for Windows and Linux. One binary serves the screen
as MJPEG, system audio as PCM, and mouse, touch, and keyboard input to a browser. The Tesla
in-car browser is reached with a CGNAT address (`100.64.0.1`) on the Windows Mobile Hotspot or,
on Linux, on `TESLAPC_BYPASS_IFACE`.

## Architecture

HTTP is on port `8080`. HTTPS is on port `8443`, terminated with rustls inside the process.
`--localhost` binds `127.0.0.1:8080` only and skips the firewall, ACME, HTTPS, and the bypass.

| Route | Protocol | Purpose |
|-------|----------|---------|
| `/` | HTTP | Web UI (embedded assets, PNG served as bytes) |
| `/stream` | HTTP | Shared MJPEG screen (`boundary`, JPEG quality 60, 30 FPS) |
| `/ws/input` | WebSocket | Pointer, wheel, and keys. Any WebSocket that is not `/ws/audio` |
| `/ws/audio` | WebSocket | One JSON format text frame, then raw PCM |
| `/list.html` | HTTP | Generated folder listing under the video root |
| `/config` | HTTP | `GET`/`POST` settings. The Cloudflare token is never returned |
| `/version` | HTTP | `{ "version": "<crate version>" }` |
| `/media/*` | HTTP | ffmpeg playback into `/stream` and `/ws/audio` |
| `/display/*` | HTTP | Desktop mode info, aspect match, and set |
| `/hotspot` | HTTP | `POST` Mobile Hotspot on/off from the Settings page |

## Source

The crate is `teslapc/`. `teslapc/src/decisions/` holds the tested protocol functions.
`teslapc/src/win.rs`, `teslapc/src/win_capture.rs`, `teslapc/src/win_service.rs`, and `teslapc/src/linux.rs` perform the OS calls. `teslapc/assets/` is the browser UI.

## Build & Run

```bash
cargo build --manifest-path teslapc/Cargo.toml
cargo test --manifest-path teslapc/Cargo.toml
.\build.ps1
teslapc\target\debug\teslapc.exe --localhost
```

`teslapc service install` registers a Windows service or writes a systemd unit;
`teslapc service uninstall` removes the Windows service. On Windows the service runs
`--service`, a supervisor in session 0 that binds no port and starts the server as
`teslapc.exe --session-host --supervisor-pid <pid>` (SYSTEM, active console session, capture
starts at DXGI, logs to `%ProgramData%\TeslaPC\teslapc.log`; supervisor log
`teslapc-service.log`). The host's capture thread and its single `Input` thread follow the
input desktop, so the sign-in, lock, and UAC screens are streamed and take input (mouse path
off `Default`). Compiled on macOS only; not yet run on Windows.

## Testing

`cargo test` calls the shipped decision functions (scaling, wheel sign, SendKeys mapping,
stream fit, MJPEG headers, audio keepalive, config save, watched threshold, display choice,
WebSocket classification, renewal, Linux bypass rules, Linux evdev records, hotspot wait,
display-mode parsers, portal ScreenCast messages, and the Windows service supervisor step,
backoff, log rotation, and argument parsing). There is no separate GUI test.
