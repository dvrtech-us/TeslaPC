# A/V sync phase 1 — defer MJPEG until Start playback

**Date:** 2026-07-05  
**Branch:** `feat/av-sync-phase-1-session-align`  
**Plan:** `documentation/planning/Streaming/2026-07-05-av-sync-phased-plan.md`

## Change

`#streamImg` no longer sets `src="/stream"` on page load. `startVideoStream()` runs inside
`startAudioPlayback()` (same user gesture) and cache-busts with `?_=<timestamp>`. Restart
playback reloads both audio WS and MJPEG.

## Constraint

Still MJPEG via `<img>` only — no `<video>` element.