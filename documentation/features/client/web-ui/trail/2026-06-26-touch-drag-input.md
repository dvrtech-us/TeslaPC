# Touch-Drag Input (Tesla / tablets)

- Date: 2026-06-26
- Feature: web-ui (client) / input-control
- Related code: `TeslaPCInterface/index.html` (`mapPoint`, `sendInput`, touch listeners)

## Context

Input was driven only by mouse events. On a touch screen (the Tesla), a **tap** works via the
browser's synthesized mouse events, but **press-and-drag** does not — and touch gestures scroll/
zoom the page instead of driving the remote cursor.

## Decisions

- Refactored coordinate mapping into `mapPoint(clientX, clientY)` (shared by mouse and touch) and
  `sendInput(type, x, y)`.
- Added touch listeners on the video `<img>`: `touchstart`→`down`, `touchmove`→`move`,
  `touchend`→`up`, using `changedTouches[0]`. This maps a drag to a host left-button press-move-
  release (the server's existing `down`/`move`/`up` handling), so drag-select and swipe work.
- Each touch handler calls `preventDefault()` with `{ passive: false }` so the page doesn't
  scroll/zoom and the browser does **not** also fire synthesized mouse events (avoids double input).
- Touch coordinates use the same letterbox-aware mapping, so they stay correct at any scale.

## Consequences

- Positive: tap = click, and press-drag = click-drag on the Tesla; no accidental page scrolling.
- Note: single-touch only (`changedTouches[0]`); multi-touch gestures (pinch/two-finger) are not
  forwarded. Right-click/scroll-wheel remain unimplemented (separate future work).
