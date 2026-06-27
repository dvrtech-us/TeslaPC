# Right-click support (desktop contextmenu + touch long-press)

- Date: 2026-06-27
- Feature: input-control (server + client web-ui)
- Related code: `TeslaPCInterface/Program.cs` (`Win32`), `TeslaPCInterface/WebServer.cs` (mouse replay), `TeslaPCInterface/index.html`

## Context

Only the left mouse button was supported, so there was no way to open a context menu on the host.

## Decisions

- Server: added `MOUSEEVENTF_RIGHTDOWN (0x08)` / `MOUSEEVENTF_RIGHTUP (0x10)` to `Win32`, and a new
  input type **`rightclick`** in the WebSocket handler → `SetCursorPos` + RIGHTDOWN + RIGHTUP.
- Client desktop: `contextmenu` event → `sendInput('rightclick', …)` with `preventDefault`.
- Client touch: **long-press (~0.5 s, < ~12 px movement)** → `rightclick`. The touch flow was
  restructured so the left `down` is **deferred**: quick tap = `down`+`up` (left click), drag =
  `down`+`move`…+`up`, hold = `rightclick` only (no stray left press). Coordinates use the existing
  letterbox-aware `mapPoint`.

## Consequences

- Positive: right-click works on both desktop and the Tesla touch screen.
- Note: middle-click and scroll wheel remain unimplemented; multi-touch isn't forwarded.
