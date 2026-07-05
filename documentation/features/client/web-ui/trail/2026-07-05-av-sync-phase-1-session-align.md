# A/V sync phase 1 — defer MJPEG until Start playback

**Date:** 2026-07-05  
**Branch:** `feat/av-sync-phase-1-session-align`  
**Plan:** `documentation/planning/Streaming/2026-07-05-av-sync-phased-plan.md`

## Change

`#streamImg` no longer sets `src="/stream"` on page load. `startVideoStream()` runs inside
`startAudioPlayback()` (same user gesture) and cache-busts with `?_=<timestamp>`. Restart
playback reloads both audio WS and MJPEG.

A `#tapstart` `.screen-tap` overlay covers the video area (not the control bar), matching
`play.html` UX: tap the screen to connect. **Start playback** in the bar still works. The
overlay returns if the audio socket drops unexpectedly (not on intentional restart).

## Constraint

Still MJPEG via `<img>` only — no `<video>` element.