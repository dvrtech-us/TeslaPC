# Phased A/V sync plan (MJPEG + WebSocket audio only)

**Date:** 2026-07-05  
**Status:** In progress — one branch per phase; merge only after Tesla/lab test passes.

## Hard constraint

**No HTML5 `<video>`, no MSE, no WebRTC media tracks, no browser-native video decode.**

The Tesla in-car browser blocks native video while driving. All picture output stays on the
existing **MJPEG over HTTP** path (`<img>` today; **canvas** in phase 3 for timed playout).
Audio stays on **`/ws/audio`** + AudioWorklet (or fallback schedulers).

## Problem today

| Path | Starts | Timing |
|------|--------|--------|
| `/stream` (MJPEG) | Page load | Latest-frame display, 30 FPS host pace, no PTS |
| `/ws/audio` (PCM) | **Start playback** tap | Immediate playout into ~2 s worklet buffer, no PTS |

Video can run seconds before audio; neither stream shares a clock.

## Branches (sequential merge)

| Phase | Branch | Scope | Test gate |
|-------|--------|-------|-----------|
| 1 | `feat/av-sync-phase-1-session-align` | Start MJPEG + audio together on user gesture | Lip offset noticeably smaller; no autoplay before tap |
| 2 | `feat/av-sync-phase-2-host-pts` | Host monotonic PTS on MJPEG parts + audio frames | Client logs show stable PTS deltas |
| 3 | `feat/av-sync-phase-3-client-scheduler` | Canvas timed video + scheduled audio (~200 ms target) | ±150 ms lip sync on hotspot |
| 4 | `feat/av-sync-phase-4-clock-drift` | `/sync` ping + slow playout correction | Stable 30+ min session |
| 5 | `feat/av-sync-phase-5-media-pts` | Same PTS path for `MediaStreamer` / `play.html` | File playback lip sync |

**Workflow:** implement on phase branch → test on laptop/Tesla → merge to `Dev` → branch phase N+1 from updated `Dev`.

## Phase details

### Phase 1 — Session align

- `#streamImg` has **no `src`** until **Start playback**.
- `startVideoStream()` sets `img.src = '/stream?_=' + Date.now()` in the same handler as audio WS open.
- Restart playback cache-busts the stream URL again.
- Input WebSocket (`/ws/input`) unchanged (may open on load).

### Phase 2 — Host PTS

- `Stopwatch` (or QPC) origin per unified session.
- MJPEG: `X-TeslaPC-Pts: <hostUs>` custom part header (`MjpegWriter`).
- Audio: `formatVersion: 2`; binary frames = 8-byte little-endian `hostUs` + PCM.
- Format JSON adds `streamEpochUs`, `hostClockHz`.

### Phase 3 — Client scheduler

- Replace `<img>` display with **canvas** blit on PTS schedule (MJPEG still received via fetch/XHR or hidden img — **not** `<video>`).
- AudioWorklet schedules output against host PTS + target delay (default 200 ms).
- UI: ±100 ms sync nudge; optional debug offset readout.

### Phase 4 — Clock drift

- `GET /sync` → `{ hostPtsUs, utcMs }`; client estimates offset; correct ≤ 5 ms/s.

### Phase 5 — Media mode

- ffmpeg PTS mapped into same headers/prefixes in `MediaStreamer`.
- `play.html` uses shared scheduler module (still MJPEG img/canvas + `audio-client.js`).

## Success criteria (final)

- Dialogue/UI sounds within **±150 ms** on Tesla hotspot.
- No `<video>` in any phase.
- Sync feature-flagged (`TESLAPC_AV_SYNC` or `formatVersion`) so legacy behavior remains available.

## Out of scope

- WebRTC, HLS, fMP4 in `<video>`, server-side A/V mux to single container.