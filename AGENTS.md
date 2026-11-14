# AGENTS.md

Operating guide for AI agents and developers working in the TeslaPC repository. Read this
before making changes. For a feature-by-feature reference, start at
[`documentation/README.md`](documentation/README.md).

## What This Project Is

TeslaPC is a Windows and Linux remote-desktop streaming server written in Rust. One binary,
`teslapc`, serves the browser UI, an MJPEG screen stream, system audio, and pointer/keyboard
input. There is no WinForms panel and no authentication. Logs go to stdout. The process can be
installed as a Windows service (`teslapc service install`) or a systemd unit of the same
command. On Windows the service (`--service`) is a supervisor that starts the server as
`--session-host` in the active console session, and that host follows the input desktop (sign-in, lock, and UAC screens); see
`documentation/features/infrastructure/windows-service/`.

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

**Quality rules:** name exact modules, functions, file paths, and constant values; document
timings, thresholds, and config keys; describe failure/fallback paths; document actual
behavior, not aspirations. SQLite (`rusqlite`) lives at `%ProgramData%\TeslaPC\teslapc.db` on
Windows and `$XDG_DATA_HOME/teslapc/teslapc.db` (default `~/.local/share/teslapc/teslapc.db`)
on Linux. There are still **no `sql/` folders** — the schema is created in `teslapc/src/store.rs`.

### Feature Map (quick index)

| Module | Feature | Primary source |
|--------|---------|----------------|
| server | web-server | `teslapc/src/serve.rs`, `teslapc/src/routes.rs` |
| server | input-control | `teslapc/src/decisions/input.rs`, `teslapc/src/win.rs`, `teslapc/src/linux.rs` |
| streaming | screen-capture | `teslapc/src/capture_loop.rs`, `teslapc/src/win_capture.rs`, `teslapc/src/linux.rs` |
| streaming | audio-capture | `teslapc/src/decisions/audio.rs`, `teslapc/src/win_capture.rs` |
| streaming | display-control | `teslapc/src/decisions/display.rs`, `teslapc/src/win.rs` |
| app | control-panel | `teslapc/src/serve.rs` (stdout log, no WinForms) |
| app | configuration | `teslapc/src/decisions/config.rs`, `teslapc/assets/config.html` |
| media | media-library | `teslapc/src/store.rs`, `teslapc/src/decisions/history.rs` |
| media | file-browser-vlc | `teslapc/src/decisions/listing.rs`, `teslapc/src/playback.rs` |
| client | web-ui | `teslapc/assets/index.html`, `teslapc/assets/PCMPlayerProcessor.js` |
| infrastructure | tesla-browser-bypass | `teslapc/src/decisions/bypass.rs`, `teslapc/src/win.rs`, `teslapc/src/linux.rs` |
| infrastructure | firewall-bootstrap | `teslapc/src/win.rs` |
| infrastructure | https-bootstrap | `teslapc/src/certs.rs` |
| infrastructure | windows-service | `teslapc/src/win_service.rs`, `teslapc/src/win_desktop.rs`, `teslapc/src/decisions/service.rs`, `teslapc/src/decisions/desktop.rs` |

## Source Layout

```
teslapc/
├── Cargo.toml
├── assets/                    # index.html, config.html, play.html, list.html, style.css, JS, teslapc.png
└── src/
    ├── main.rs                # process entry
    ├── serve.rs               # bind 8080/8443, flags, dotenv, service handoff
    ├── routes.rs              # HTTP and WebSocket routes
    ├── decisions/             # pure protocol decisions (tested)
    ├── capture_loop.rs        # shared MJPEG capture loop
    ├── encode.rs              # in-process JPEG quality 60
    ├── playback.rs            # ffmpeg, Windows VLC fallback
    ├── store.rs               # SQLite playback history
    ├── certs.rs               # rustls local cert and DNS-01 renewal
    ├── win.rs                 # Windows input, hotspot, firewall, GDI
    ├── win_service.rs         # Windows service supervisor, session host launch, install/uninstall
    ├── win_desktop.rs         # per-thread input-desktop attachment (session host)
    ├── win_capture.rs         # WGC, DXGI duplication, WASAPI loopback
    └── linux.rs               # Portal/PipeWire, XShm, uinput, nftables, systemd
```

## Build & Run

```bash
cargo build --manifest-path teslapc/Cargo.toml
cargo test --manifest-path teslapc/Cargo.toml
.\build.ps1
.\build.ps1 -Configuration Debug
```

The dev binary is `teslapc\target\debug\teslapc.exe`. Release is `teslapc\target\release\teslapc.exe`.

Runtime flags / env:

- `--localhost` — bind `127.0.0.1:8080` only. Skips the firewall, ACME, HTTPS, and the Tesla bypass.
- `--no-tesla-bypass` — skip the `100.64.0.1` address and the forward rules. Firewall and HTTPS still run.
- `--https-host <host>` (or `TESLAPC_HTTPS_HOST`) — hostname for a Let's Encrypt certificate. With `TESLAPC_CF_TOKEN`, issuance and renewal run in-process (DNS-01). Renewal runs when 30 days or fewer remain and is checked every 12 hours. `TESLAPC_ACME_STAGING=1` uses the staging directory.
- `TESLAPC_CF_TOKEN` — Cloudflare API token (Zone:Read + DNS:Edit).
- `TESLAPC_ACME_EMAIL` — optional account contact.
- `TESLAPC_VIDEO_ROOT` — file browser root. Default `C:\video\` on Windows.
- `TESLAPC_STREAM_HEIGHT` — vertical cap, default `1080`, allowed `240`–`2160`. Width cap is `height * 4`.
- `TESLAPC_INPUT_MODE` — `touch` (default) or `mouse`.
- `TESLAPC_LOG_LEVEL` — `error`, `warn`, `info` (default), or `debug`.
- `TESLAPC_NO_WGC=1` — skip Windows Graphics Capture and start at DXGI.
- `TESLAPC_FFMPEG` — ffmpeg executable or directory.
- `TESLAPC_BYPASS_IFACE` — Linux only. Empty leaves the bypass off. A set iface adds `100.64.0.1/10` and nftables forward plus NAT.
- `teslapc service install` — Windows: creates or updates service `TeslaPC` through the SCM API (`"<exe>" --service`, auto start, LocalSystem, restart after 5 s on failure) and warns about scheduled tasks that run `teslapc.exe`. Linux: writes a systemd unit. A denied install is logged, exits non-zero, and the server is not required to be registered.
- `teslapc service uninstall` — Windows: stop (up to 10 s) and delete the service. Other words after `service` print usage and exit `2`.
- `--service` — Windows, started by the SCM: supervisor only (no ports, capture, or audio). It keeps one `--session-host` running as SYSTEM in the active console session, restarting it on exit (backoff 1 s doubling to 30 s, reset after 60 s uptime; 30 s when the host exits `3` = port in use), console session change, or logoff. Log: `%ProgramData%\TeslaPC\teslapc-service.log`. Not started by the SCM (or on Linux): runs the normal server.
- `--session-host [--supervisor-pid <pid>]` — the normal server, plus: stdout/stderr go to `%ProgramData%\TeslaPC\teslapc.log` (rotated to `.1` over 5 MiB), capture starts at DXGI, the capture thread and a dedicated `Input` thread follow the input desktop, it exits when the supervisor pid exits, and it exits `3` if port 8080 cannot be bound. Started by the supervisor; not meant to be run by hand.

HTTPS on `8443` is terminated in-process with rustls. There is no http.sys URL ACL and no certificate-store bind. Without a public hostname, a generated local certificate still makes `8443` listen (unless `--localhost`).

Certificates and `.env` live under `%ProgramData%\TeslaPC\` on Windows and `$XDG_CONFIG_HOME/teslapc/` (default `~/.config/teslapc/`) on Linux.

A missing privilege for firewall, hotspot, input, address, or nftables is logged. HTTP still starts.

## Conventions

- Rust 2021. Protocol decisions are pure functions in `teslapc/src/decisions` and have tests in `teslapc/tests/protocol.rs`.
- Windows and Linux only. No macOS. No Tesla Android source.
- The browser UI is the vanilla JS already in `teslapc/assets`. PNG and other binaries are served as raw bytes.
- Ports `8080` and `8443` are fixed in `serve.rs`.
- `cargo test` covers the protocol decisions. Capture, hotspot, and ACME also depend on the host.

## Versioning

- `teslapc/Cargo.toml` `version` is the string returned by `GET /version` and the `version` field of `GET /config`.
- Tag a release `v<version>`.

## Current Known Gaps

- No authentication on any route.
- A black or failed capture frame still advances WGC → DXGI → GDI. The first backend is tried again after 30 s (doubling to 5 min while it keeps falling back). DRM video can stay black under every backend.
- Linux PipeWire, uinput, and nftables are not executed on a Windows host. The event records, portal messages, and bypass plan are covered by `cargo test`.
- The SQLite file is created in code. There is no `sql/` folder.
- The Windows service host follows the input desktop (`win_desktop.rs`): its capture thread and its single `Input` thread re-attach to `Winlogon` for the sign-in screen, lock screen, and UAC prompts, and back to `Default`. Off `Default` pointer events are mouse events. Ctrl+Alt+Del is not sent. Desktop following was written and compiled on macOS only and has not yet run on Windows; still to confirm there: DXGI duplication on `Winlogon` as SYSTEM (else GDI after five retries), whether the capture thread can call `SetThreadDesktop` without `ERROR_BUSY` (restarts are logged), that `SendInput` reaches the sign-in and UAC screens, and touch on `Default` after a trip to `Winlogon`.
