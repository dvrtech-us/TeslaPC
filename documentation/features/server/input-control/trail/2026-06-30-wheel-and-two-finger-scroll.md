# Wheel scrolling + two-finger touch scroll support

- Date: 2026-06-30
- Feature: input-control (server + client web-ui)
- Related code: `TeslaPCInterface/Program.cs` (InputData + Win32 constants), `TeslaPCInterface/WebServer.cs` (AcceptWebSocketAsync), `TeslaPCInterface/index.html` (sendWheel + wheel + multi-touch handlers)

## Context

Previously only pointer movement, left-click, drag, and right-click (long-press) were supported. There was no way to scroll remote content with a mouse wheel or using natural two-finger gestures on touch devices such as the Tesla browser. The documentation and AGENTS.md explicitly called out "no scroll-wheel yet".

## Goals

- Add first-class mouse wheel support from physical wheels and trackpads.
- Add practical two-finger vertical scroll on touch (the most important gesture for in-car use).
- Keep 100% backward compatibility for existing single-touch tap, drag, and long-press right-click.
- Position the cursor before emitting wheel events when the client supplies coordinates (so scroll affects the element the user is pointing at).
- Follow the existing JSON-over-WebSocket + coordinate scaling pattern.

## Decisions

### Message protocol extension
- New `Type: "wheel"` message:
  ```json
  { "Type": "wheel", "Delta": <number>, "X": ..., "Y": ..., "DisplaySize": ... }
  ```
- `Delta` carries the signed amount (raw `e.deltaY` for wheel events; scaled finger delta for touch).
- X/Y + DisplaySize are optional but recommended; when present the server positions the cursor first.

### Server replay (`WebServer.cs`)
- Added `MOUSEEVENTF_WHEEL = 0x0800` (and `HWHEEL = 0x1000` for future) to `Win32`.
- Extended `InputData` with `public int Delta { get; set; }` and updated `GetAdjusted()` to preserve it.
- In the receive loop: special-case `Type == "wheel"`:
  - Conditionally call `SetCursorPos` only when position data was supplied.
  - Emit `mouse_event(MOUSEEVENTF_WHEEL, 0, 0, -Delta, 0)` (sign inverted so typical browser "scroll down" values scroll remote content in the intuitive direction).
- Non-wheel pointer path left completely unchanged.

### Client (`index.html`)
- Added `sendWheel(delta, clientX, clientY)` helper (re-uses `mapPoint` for letterbox-correct coordinates).
- Attached `wheel` listener on the stream `<img>` (with `preventDefault`).
- Extended the existing touch state machine:
  - `touchstart` / `touchmove` / `touchend` now detect `e.touches.length >= 2` early.
  - When active, two-finger mode computes average Y of the first two touches, scales by `T2_SCROLL_FACTOR` (1.6), and emits `sendWheel`.
  - Single-touch long-press timer and drag state are cleared when multi-touch begins.
  - Added `touchcancel` handler for robustness.
- New state vars: `t2Active`, `t2PrevAvgY`, `T2_SCROLL_FACTOR`.
- Updated comments describing supported gestures.

### Sign convention
Browser `deltaY` positive generally means "user intent to scroll content down".
Windows `mouse_event` WHEEL positive moves wheel "forward" (scrolls content up).
The server applies `-Delta` on the wire.

### Touch vs. native wheel
- Desktop physical wheels use the real `wheel` event.
- On pure touch devices (Tesla) two-finger drag produces synthetic wheel messages. This is reliable even when the browser does not synthesize wheel events from gestures.

## Consequences / Trade-offs

- Positive: Scrolling now works with mouse wheels and natural two-finger gestures on the Tesla and tablets.
- The aggressive `preventDefault` on touch handlers continues (required so the gestures drive the remote desktop instead of scrolling the host page).
- Horizontal scrolling not yet wired (constant prepared, client only does vertical for v1).
- Sensitivity for touch (`T2_SCROLL_FACTOR`) and exact feel may need tuning per user feedback.
- No new configuration knobs yet.

## Files Changed

- `TeslaPCInterface/Program.cs`
- `TeslaPCInterface/WebServer.cs`
- `TeslaPCInterface/index.html`
- `documentation/features/server/input-control/input-control.md`
- `documentation/features/server/input-control/baseline.md`
- `documentation/features/server/input-control/trail/2026-06-30-wheel-and-two-finger-scroll.md` (this file)

## Verification

- `dotnet build` succeeds.
- Manual test (browser + touch device):
  - Mouse wheel over the stream image scrolls the remote view.
  - Two-finger vertical drag on the image scrolls without triggering clicks or drags.
  - Existing single-touch tap, drag, and long-press right-click remain functional.
- Future: test on actual Tesla browser + physical mouse.

## Related

See also web-ui baseline and the original right-click trail for touch state machine history.