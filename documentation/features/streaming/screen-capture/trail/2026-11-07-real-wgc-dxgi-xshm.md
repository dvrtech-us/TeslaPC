# Real WGC, DXGI, and Linux frame copies

- Date: 2026-11-07
- Feature: screen-capture
- Related code: `teslapc/src/win_capture.rs`, `teslapc/src/linux.rs`, `teslapc/src/decisions/portal.rs`

## Context

The first Rust capture path returned a fixed error for Windows Graphics Capture and DXGI, and the X11 path used `GetImage`. A localhost stream then came only from GDI.

## Decisions

- `grab_wgc` opens a Graphics Capture session on the primary monitor, disables cursor capture, and does not require the yellow border.
- `grab_dxgi` uses Desktop Duplication. `AcquireNextFrame` timeouts are retried; any other HRESULT drops the session.
- Wayland continues past `CreateSession` through `SelectSources` and `Start`, then reads the PipeWire node.
- X11 copies with MIT-SHM `shm_get_image`. XComposite names a pixmap first when the server allows it.
- A failure string is the OS or command error.

## Alternatives Considered

- Leaving GDI as the only Windows source: rejected because the plan requires WGC and DXGI to be attempted for real.
- Root `GetImage` when SHM is missing: rejected. The error names the missing extension.

## Consequences

- Positive: a localhost launch logged `WGC frame 3840x2160, cursor capture off` and `/stream` began `FF D8`.
- Negative: Linux capture still needs a logged-in desktop and was not executed on this Windows host.
