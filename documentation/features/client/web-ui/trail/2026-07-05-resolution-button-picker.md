# Replace stream-resolution `<select>` with touch button picker

**Date:** 2026-07-05  
**Feature:** web-ui  
**Related code:** `TeslaPCInterface/index.html`, `TeslaPCInterface/config.html`, `TeslaPCInterface/style.css`

## Context

The control-bar `<select id="resSel">` (styled with `appearance: none` via `.btn`) showed no
options when tapped on the Tesla in-car browser — the native popup never appeared.

## Decision

- **`index.html`:** `#resBtn` shows the current value (e.g. `1080p`); tap opens `#resPicker`, a
  fixed overlay with three `.btn.res-choice` targets (480p / 720p / 1080p). Tap outside closes it.
  `pickRes(h)` still posts `streamHeight` to `POST /config`.
- **`config.html`:** replace the `<select>` with the same three-button `.res-choices` row and a
  hidden `streamHeight` input.
- **`style.css`:** `.res-picker`, `.res-choice.active`, `.res-choices` styles.

## Consequences

- Resolution changes work on Tesla without relying on native `<select>` UI.
- Desktop browsers also get larger tap targets; behavior unchanged server-side.