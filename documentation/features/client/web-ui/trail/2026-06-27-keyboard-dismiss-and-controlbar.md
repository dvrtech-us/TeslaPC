# Dismiss on-screen keyboard on tap-out; single-row control bar

- Date: 2026-06-27
- Feature: web-ui
- Related code: `TeslaPCInterface/index.html`, `TeslaPCInterface/style.css`

## Context

Two touch-UX problems surfaced on the Tesla in-car browser (Chromium), whose non-fullscreen
viewport is ~**1180×919**:

1. Tapping the stream or anywhere on the page did **not** blur `#fakeKeyboard`, so the on-screen
   keyboard stayed up. The stream `<img>` touch handlers call `preventDefault()` with
   `{passive:false}`, which suppresses the synthetic-click path Chromium normally uses to blur the
   focused input.
2. The newly added control-bar items (stream-resolution `<select>` + "Fit screen") pushed the bar
   to **two rows** (`.controlbar` was `flex-wrap: wrap`), and the wrapped row could cover/blocked
   the keyboard input.

## Decisions

- **Tap-out blur:** add passive `document` `touchstart` + `pointerdown` listeners that, when
  `document.activeElement === fakeKeyboard` and the target isn't the input, call
  `fakeKeyboard.blur()`. Passive document listeners still fire even though the `<img>` handlers
  `preventDefault`, and they never interfere with the existing tap/drag/long-press input paths.
- **Single-row bar:** `.controlbar` is now `flex-wrap: nowrap` with `overflow-x: auto` as a safety
  net; buttons are compacted (`.controlbar .btn` smaller padding/height); the resolution `<select>`
  no longer uses the wide `.input` styling (was forced to 160px); and the keyboard input
  (`.controlbar .btn.input`) flexes to take the slack so it stays reachable as the left-most
  flexible item. Everything fits one row well within 1180px.

## Consequences

- The on-screen keyboard dismisses when tapping the video or any control, as expected on touch.
- The control bar stays on one row on the Tesla viewport; if it ever exceeds the width it scrolls
  horizontally rather than wrapping, so the keyboard is never hidden behind a second row.
- Physical-keyboard capture and paste handling are unchanged (they already guard on the focused
  element), so blurring the box does not affect click-free typing.
