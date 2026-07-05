# A/V sync phase 3a — H264 + audio PTS scheduler

**Date:** 2026-07-05  
**Branch:** `feat/av-sync-phase-2-host-pts`  
**Plan:** `documentation/planning/Streaming/2026-07-05-av-sync-phased-plan.md`

## Change

Added `av-scheduler.js` (`TeslaAvScheduler`) with a shared host-PTS anchor, default
`targetDelayMs = 200`, and helpers `hostPtsToPlayWallMs`, `hostPtsToDecoderTimestampUs`, and
`delayUntilPlayMs`.

- **H264:** `display-client.js` forwards `{ h264Data, hostPtsUs, schedulerSync }` to
  `display-h264-worker.js`. The worker uses host PTS for `EncodedVideoChunk.timestamp` and
  delays canvas blit until `hostPtsToPlayWallMs`. Keyframe/backlog drop logic is unchanged.
- **Audio:** `index.html` parses format v2 binary frames as 8-byte little-endian `hostPtsUs` +
  PCM. Chunks are delivered via `setTimeout(delayUntilPlayMs)` to the AudioWorklet (or fallback
  `AudioBufferSourceNode` when worklet is unavailable).
- **MJPEG:** unchanged — still immediate `<img>` blit; no canvas scheduler in this phase.
- **Session reset:** `TeslaAvScheduler.reset()` runs in `startAudioPlayback()`; display worker
  resets on display format handshake / `{ resetScheduler: true }`.

## Constraint

Still no `<video>` element. No phase 4 drift correction or MJPEG canvas scheduler in this step.