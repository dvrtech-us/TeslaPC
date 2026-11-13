# Plan: keep live audio from falling behind the picture

**Date:** 2026-11-13
**Status:** Revised after two adversarial reviews (Claude Opus, Codex). Awaiting owner sign-off. No code written.
The "Revisions after review" section at the end replaces sections 1-3 and the verification above.

## Owner report

Watching video in a browser on the PC desktop from the car (YouTube, Disney+): audio and
picture start close and drift apart over time. Sound ends up behind the picture.

## Why it happens (from the code)

- Picture: `/stream` is MJPEG into `#streamImg`. Each frame is shown on arrival. No
  timestamps. Latency is capture + encode + network, and it does not accumulate.
- Sound: `/ws/audio` sends raw PCM packets (10 ms from WASAPI) with no timestamps.
  `teslapc/assets/PCMPlayerProcessor.js` appends every packet to one `Float32Array` and plays
  from the front. It starts playing as soon as 128 frames exist and only discards when more
  than **2 seconds** are buffered. Anything that delivers audio late (a network stall, the
  car's browser pausing the page, the PC's audio clock running faster than the car's) adds to
  the buffer, and nothing ever removes it. The sound stays that far behind for the rest of
  the session, up to 2 s.
- Server: `routes.rs` `audio_socket` gives each client a `tokio::sync::mpsc::unbounded_channel`.
  `PcmHub.queue` is capped at 32 packets, but the per-client channel is not. When a client's
  TCP connection is slow, packets queue there without limit and are delivered late, which
  the browser then buffers.
- The fallback path (no AudioWorklet) schedules buffers at `scheduledTime` and only resets
  when it falls behind `currentTime`, so it has the same one-way growth.
- The same player code exists in three places: `PCMPlayerProcessor.js` (shared worklet), the
  inline client in `index.html`, and `audio-client.js` used by `play.html`.

## Goal

On the live desktop, sound stays within about 150 ms of the picture for as long as the
session runs, without audible artifacts in normal conditions. No protocol change. Media
playback through `play.html` must not get worse.

Out of scope: timestamps and true lip sync (the older phased plan
`2026-07-05-av-sync-phased-plan.md`), moving video off MJPEG.

## Changes

### 1. Worklet buffer control (`PCMPlayerProcessor.js`)

Constants (frames at the context rate; ms shown at 48 kHz):

| Name | Value | Meaning |
|------|-------|---------|
| `TARGET_MS` | 80 | buffer level the player steers to |
| `START_MS` | 80 | after an underrun, wait for this much before playing again |
| `TRIM_ABOVE_MS` | 200 | above this, correct by skipping |
| `HARD_MAX_MS` | 500 | above this, drop to target at once |
| `MEASURE_WINDOW_MS` | 2000 | window over which the minimum level is tracked |

- Replace the grow-and-copy `Float32Array` with a fixed ring buffer (1 s capacity). The
  current code allocates a new array on every packet inside the audio thread.
- Track the **minimum** buffered level over the last 2 s. The minimum is the standing
  latency; the level above it is jitter that the buffer is absorbing.
- Small drift: when the windowed minimum is above `TARGET_MS` + 20 ms, play slightly fast by
  dropping one frame in every 200 (0.5%) with linear interpolation across the gap, until the
  minimum is back at target. When the minimum is below `TARGET_MS` - 40 ms and underruns
  occurred in the window, play slightly slow by repeating one frame in 200.
- Large excess: when the windowed minimum is above `TRIM_ABOVE_MS`, skip ahead to
  `TARGET_MS` with a 5 ms crossfade. Above `HARD_MAX_MS` instantaneous, skip at once.
- Underrun: output silence, then wait for `START_MS` before resuming, so one late packet
  does not cause a string of micro-gaps.
- The worklet posts `{ bufferedMs, minMs, underruns, skips }` to the page once a second.

### 2. Page side (`index.html` inline client, `audio-client.js`)

- No behavior change beyond receiving the stats message. With `TESLAPC_LOG_LEVEL=debug`
  (from `/config`) the stats are written with `console.log`.
- Fallback path: when `scheduledTime - currentTime` exceeds 250 ms, reset `scheduledTime`
  to `currentTime + 0.08` (drops the backlog).
- `play.html` uses the same worklet. During media playback ffmpeg produces both streams at
  real-time pace, so the same bounds apply. `TARGET_MS` is passed as a processor option so a
  page can raise it; `play.html` keeps the default.

### 3. Server (`routes.rs`, `state.rs`)

- Replace the per-client unbounded channel with a bounded one (64 packets, about 640 ms).
  When it is full, drop the oldest queued packets for that client down to 8 and count the
  drop. A slow client then receives recent audio instead of an ever-older backlog.
- Pure decision function for the drop rule in `decisions/audio.rs` with tests.
- Remove closed clients from `PcmHub.clients` (today senders are pushed and never removed).

## Verification

- Unit tests for the server drop rule.
- A Node script (not shipped) that loads the worklet class with a stub
  `AudioWorkletProcessor` and feeds it packet schedules: steady, a 1.5 s stall followed by a
  burst, a source clock 0.2% fast for 10 simulated minutes, a source clock 0.2% slow, random
  jitter of +/-30 ms. Assert the buffered level returns to within `TARGET_MS` +/- 40 ms and
  stays there, and count skips and underruns.
- On the Mac in Chrome against the lab laptop with the network throttled for 3 s: stats show
  the buffer rising, then returning to target within a few seconds.
- Owner check in the car over a long watch.

## Risks

| Risk | Handling |
|------|----------|
| Rate correction audible as pitch change | 0.5% is about 9 cents; used only while off target |
| Skips audible | Crossfade; skips only above 200 ms standing latency |
| Tesla browser delivers audio in bursts larger than 200 ms | Stats will show it; raise `TARGET_MS`/`TRIM_ABOVE_MS` |
| Underruns increase with an 80 ms target on a poor link | Slow-down rule and `START_MS` re-buffer; target adjustable |
| Bluetooth or car amplifier delay is outside the page's control | Not addressed; sound may still trail by a fixed amount |

## Docs

`documentation/features/client/web-ui/`, `documentation/features/streaming/audio-capture/`
(feature docs, baselines, trail entries).

## Revisions after review (2026-11-13)

Both reviewers confirmed the diagnosis (one-way growth in the worklet up to 2 s, unbounded
per-client channel, senders never removed, three copies of the player) and that the PC
audio clock against the car's audio clock is the main source of slow growth.

### What the reviews changed

1. **Media playback audio is broken at the source (both reviewers, verified).**
   `playback.rs` `audio_reader` pushes one sample frame (8 bytes) per packet: about 48,000
   pushes per second into `PcmHub.queue`, which keeps 32. Most media audio is likely dropped
   today. Fix first: push frame-aligned chunks of 10 ms (`rate / 100 * frame_bytes`).
   Every queue limit is stated in milliseconds of audio, never in packets.
2. **A bounded `mpsc` cannot drop the oldest packet**, and awaiting a full one would let one
   slow listener stall all of them. Use `tokio::sync::broadcast` (capacity 64 chunks of
   10 ms). A lagging receiver gets `Lagged(n)` and continues from the oldest chunk still
   held; the skip is logged. This also removes the `PcmHub.clients` list and its leak.
3. **The goal is a local-buffer objective, not an end-to-end promise.** Audio also waits in
   the socket send buffer and the browser's event queue, and MJPEG frames already handed to
   TCP cannot be withdrawn. The goal becomes: the worklet's standing buffer stays near the
   target for the whole session, and the remaining offset is what the transport adds.
4. **No per-frame drop or repeat.** One frame in 200 is a 240 Hz pattern and can be heard.
   If rate correction is built (phase 2), it is one fractional-rate reader with linear
   interpolation and phase carried across packets, which also replaces the per-packet
   resampler (that one restarts on every packet and rounds the length).
5. **Trim by the standing excess**, `minMs - TARGET_MS`, not down to the target from the
   current level, so a trim during a burst does not cause an underrun.
6. **Explicit states:** warming (collect `START_MS`), playing, starved. The minimum window
   and underrun count reset on every state change and after every trim.
7. **Startup race (both, verified):** while `addModule` is pending, packets go to the
   fallback player and then play under the worklet. Packets are dropped until the player
   that will be used is ready.
8. **Fallback path:** keep handles to scheduled sources and stop them before resetting the
   schedule; otherwise old and new audio overlap.
9. **Stats must be readable from the car.** The page sends
   `{"type":"stats","bufferedMs","minMs","underruns","trims","contextRate"}` as a text
   frame on `/ws/audio` every 10 s; the server logs it at info level as `[Audio] client ...`.

### Phase 1 (build now)

Server:
- 10 ms frame-aligned chunks from `audio_reader`.
- `broadcast` fan-out with lag skip and log; `PcmHub.queue` cap stated as 320 ms.
- `/ws/audio` accepts the stats text frame and logs it.
- Pure functions and tests for chunk size and the stats line.

Browser (one implementation in `PCMPlayerProcessor.js`; `index.html` and `audio-client.js`
only wire it up):
- Ring buffer of 1 s; states warming / playing / starved.
- `TARGET_MS` 100, `START_MS` 100, `TRIM_ABOVE_MS` 200 (on the 2 s minimum), `HARD_MAX_MS`
  500 (instant). A trim removes `minMs - TARGET_MS` with a 5 ms crossfade.
- Startup race and fallback fixes above. Transfer the packet buffer to the worklet instead
  of copying it.
- No rate correction. At a clock difference of 0.05% this is one short faded skip every
  few minutes.

Verification:
- A committed Node test that loads the worklet with a stub `AudioWorkletProcessor` and
  feeds numbered frames with a different signal per channel: steady, a 1.5 s stall then a
  burst, source 0.2% fast for 10 simulated minutes, source 0.2% slow, +/-30 ms jitter,
  silence then no packets then resume, media-size chunks. Asserts: order kept, channels
  not swapped, standing buffer back within 60-160 ms after each disturbance, trim and
  underrun counts.
- Lab laptop: `/ws/audio` capture during media playback shows 10 ms chunks at 1.00 s of
  audio per second (today it cannot).
- Car: watch for 20 minutes; read `[Audio] client` lines in `teslapc.log` for standing
  buffer, trims, underruns, and the browser's audio rate.

### Phase 2 (only if phase 1's trims are audible, decided from the car's stats)

Fractional-rate reader with gentle rate correction and carried resampling phase; pause and
seek flushing for `play.html`.

The constants are starting values to be tuned from the car's stats, not validated numbers.
