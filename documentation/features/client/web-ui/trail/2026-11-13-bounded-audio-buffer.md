# Bounded audio buffer in the worklet

**Date:** 2026-11-13

## Problem

Sound fell behind the picture over a session. `PCMPlayerProcessor.js` appended every packet to
a growing `Float32Array` (a new allocation per packet on the audio thread), started at 128
frames, and discarded only above 2 s. Any late delivery or a PC audio clock faster than the
car's added to the buffer and nothing removed it. While `addModule` was pending, packets went to
the fallback player and kept playing underneath the worklet. The fallback schedule only reset
when it fell behind, so it grew the same way.

## Decision

Phase 1 of `documentation/planning/Streaming/2026-11-13-bounded-audio-latency.md` (browser half):

- One implementation in `PCMPlayerProcessor.js`: a fixed 1 s ring; states warming / playing /
  starved; minimum level over 2 s (8 buckets of 250 ms, measured after each block's read);
  trims by the standing excess (`minMs - TARGET_MS`) and a hard skip to `TARGET_MS` above
  `HARD_MAX_MS`, each with a 5 ms in-place crossfade. The window resets on every state change
  and trim. No rate correction (phase 2, only if the car's stats show audible trims).
- Warm-up waits for `START_MS` plus the largest packet seen (capped at 200 ms), so the trough
  of the sawtooth sits near `START_MS`. With exactly `START_MS`, 100 ms packets underran on
  every packet in simulation.
- An overflowing write keeps the newest `TARGET_MS`, not just enough to fit. Keeping only what
  fits made a 1.5 s burst count about 50 trims, one per packet.
- Pages drop packets until the player in use is ready, transfer the ArrayBuffer to the
  worklet, and send `stats` on `/ws/audio` every 10 s so the numbers can be read from the
  car in the server log.
- Fallback keeps its sources and stops them when it is more than 0.25 s ahead, restarting at
  `currentTime + 0.1`.
- Committed Node test `teslapc/tests/js/pcm_player.test.js`
  (`node teslapc/tests/js/pcm_player.test.js`).

With 0.2% drift (10 min), the standing buffer drifts to 200 ms then trims (11 trims, fast) or
drains to an underrun (12 underruns, slow); the 60-160 ms band holds only for stalls, jitter,
gaps, and packet sizes. Real clock differences are expected near 0.05%.

## Constants

`TARGET_MS` 100, `START_MS` 100, `TRIM_ABOVE_MS` 200, `HARD_MAX_MS` 500,
`MEASURE_WINDOW_MS` 2000 (processor options), ring 1000 ms, crossfade 5 ms, warm-up packet cap
200 ms, worklet stats every 1 s, page stats every 10 s, fallback max lead 0.25 s, reset lead
0.1 s. Starting values, to be tuned from the car's stats.

## Files

- `teslapc/assets/PCMPlayerProcessor.js`
- `teslapc/assets/audio-client.js`
- `teslapc/assets/index.html`
- `teslapc/tests/js/pcm_player.test.js`
