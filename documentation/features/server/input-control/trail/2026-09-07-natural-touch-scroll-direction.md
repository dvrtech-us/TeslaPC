# Natural (content-follows-fingers) two-finger scroll direction

- Date: 2026-09-07
- Feature: input-control (client web-ui)
- Related code: `TeslaPCInterface/index.html` (two-finger branch of the `touchmove` handler)

## Context

The initial wheel/two-finger implementation (see `2026-06-30-wheel-and-two-finger-scroll.md`)
mapped dragging fingers **down** to scrolling remote content **down** — scrollbar convention.
Every touch platform (and the Tesla's own browser) uses the opposite, natural convention:
content follows the fingers, so dragging down scrolls content up.

## Decisions

- The two-finger delta is now negated before sending: `wheelDelta = Math.round(-dy * T2_SCROLL_FACTOR)`.
- Desktop `wheel` events are unchanged — `e.deltaY` already carries the correct convention and the
  server still inverts once for Windows `MOUSEEVENTF_WHEEL` semantics.

## Alternatives Considered

- Invert on the server: rejected — the server would then need to know which client gesture produced
  the message; the sign convention on the wire stays "browser `deltaY` semantics" for all senders.
- A user-facing toggle: not needed for v1; revisit only if feedback demands it.

## Consequences

- Positive: two-finger scroll matches what every touch user expects.
- The wire protocol is unchanged; only the client-side sign of touch-generated deltas flipped.
