# AGENTS.md

Operating guide for AI agents and developers working in the TeslaPC repository. Read this
before making changes. For a feature-by-feature reference, start at
[`documentation/README.md`](documentation/README.md).

## What This Project Is

TeslaPC is a Windows and Linux remote-desktop streaming server written in Rust. One binary,
`teslapc`, serves the browser UI, an MJPEG screen stream, system audio, and pointer/keyboard
input. There is no WinForms panel and no authentication. Logs go to stdout. The process can be
installed as a Windows service (`teslapc service install`, started with `--service`) or a
systemd unit of the same command.

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
    ├── win.rs                 # Windows input, hotspot, firewall, service, GDI
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
- `teslapc service install` — Windows `sc.exe create`, or a systemd unit on Linux. A denied install is logged and the server is not required to be registered.

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
- A black or failed capture frame still advances WGC → DXGI → GDI. DRM video can stay black under every backend.
- Linux PipeWire, uinput, and nftables are not executed on a Windows host. The event records, portal messages, and bypass plan are covered by `cargo test`.
- The SQLite file is created in code. There is no `sql/` folder.
