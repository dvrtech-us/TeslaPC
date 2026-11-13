# Control bar sized for the car's screen

**Date:** 2026-11-13

## Problem

A photo from the car (portrait Tesla screen, page at `my.thelpers.com:8443`) showed the new
control bar did not fit: the keyboard field was cut to "Keyboar", the stream indicator was
ellipsized to `720x480 · MJP...`, and the Fit screen icon (U+26F6) rendered as an empty box.
The viewport is narrower than the 1180 px the bar was laid out for, and `#fitBtn` was fixed at
`280px` to hold long result messages.

## Decision

- `#fitBtn` is `190px`. Result messages are shortened to `PC set to <w>x<h>`,
  `Unchanged <w>x<h>`, and `Change failed`.
- The keyboard field is `flex: 1 1 150px` with `min-width: 150px`.
- `#streamInfo` no longer shrinks (`flex: 0 0 auto`), so it is never ellipsized.
- The Fit screen icon is inline SVG (four corner brackets, `currentColor`).

- Embedded pages and assets are served with `Cache-Control: no-cache` (`routes.rs`
  `fallback` and `html_response`) so the car loads the pages of the running build.

Checked in headless Chrome at 1200 px and 820 px wide. Not yet confirmed in the car.

## Files

- `teslapc/assets/index.html`
- `teslapc/assets/style.css`
- `teslapc/src/routes.rs`

## Follow-up the same day: more icons without a glyph

A second photo from the car showed the Screen button's home symbol (U+2302) as an empty box.
The home, checkmark, replay, pause, play, and stop symbols are now inline SVG in
`config.html`, `play.html`, and the generated listing (`decisions/listing.rs`). The folder,
gear, keyboard, and film emoji rendered in the car and are unchanged. `.btn-icon` is sized in
`em` so the icons follow the text size.
