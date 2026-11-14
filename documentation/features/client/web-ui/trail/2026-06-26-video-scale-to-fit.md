# Video Scales to Fit (aspect-ratio preserved) + letterbox-aware input

- Date: 2026-06-26
- Feature: web-ui
- Related code: `TeslaPCInterface/style.css` (`.screen img`), `TeslaPCInterface/index.html` (`sendMouseEvent`)

## Context

The stream `<img>` used `max-width/max-height: 100%`, so it never scaled **above** the frame's
native size (server caps at 1280×720). On the Tesla's wide screen the video sat small with empty
space instead of filling the viewport.

## Decisions

- `.screen img` now uses `width: 100%; height: 100%; object-fit: contain` — the video scales up to
  fill the available area while preserving aspect ratio (letterboxed as needed).
- Because `object-fit: contain` letterboxes inside the element box, `sendMouseEvent` was updated to
  map taps to the **actual video rectangle**: compute the displayed content size from the frame's
  `naturalWidth/naturalHeight` vs the element rect, subtract the letterbox offset, and send that
  content size as `DisplaySize` (the server scales `X/DisplaySize.width × screenWidth`). Taps in the
  black bars are ignored. Falls back to the element rect before the first frame loads.

## Consequences

- Positive: full-bleed video at any screen size with correct aspect ratio; mouse/tap coordinates
  remain accurate at any scale, including the letterboxed regions.
- Negative: none. (Touch-drag still relies on synthesized mouse events; dedicated touch handlers
  remain a possible future enhancement.)
