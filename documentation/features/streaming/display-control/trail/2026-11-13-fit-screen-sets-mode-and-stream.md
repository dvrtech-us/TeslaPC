# Fit screen sets the display mode and the stream height

- Date: 2026-11-13
- Feature: streaming/display-control

## Problem

Owner request: "change the laptop and stream resolution to match the browser". The old rule capped the mode at the stream height (`match_max_height`), so Fit screen could not enlarge the picture, and it never touched the stream height.

## Decision

The page sends the stream area in device pixels. `fit_viewport` prefers the smallest mode that covers the area with the closest aspect (fallback: closest aspect, largest). The stream height becomes the fitted picture height, saved through `save_stream_height` only when the chosen mode is active. `/display/match` adds `streamHeight`. Acts only when Fit screen is tapped. `match_max_height` is removed; `best_for_aspect` stays but is unused by the server. Limits: only driver-listed modes; a larger picture uses more data; nothing restores the previous mode or height. Verified on the lab laptop: 720x480 to 1280x800, stream 1200x750.

## Files

`teslapc/src/decisions/display.rs`, `teslapc/src/routes.rs`, `teslapc/assets/index.html`, `teslapc/assets/config.html`, `teslapc/tests/protocol.rs`
