# Web UI — Behavior Baseline

- `/` returns the index document.
- `/teslapc.png` bytes start with `89 50 4E 47` and the content type is `image/png`.
- The main page has no resolution control (`resBtn`, `resPicker`, `toggleResPicker`, `pickRes` are absent). It still loads `inputMode` from `GET /config`.
- Fit screen shows one of three messages in the button label for 4 s, sends one request at a time, and checks `r.ok` before parsing JSON.
- `#streamInfo` is read-only and non-interactive, shows `—` until the stream has a size, and never shows audio format.
- `play.html` control bar is unchanged.
- Pages are served from the binary, not from the C# project directory.
- Audio packets that arrive before the player is ready are dropped, never played by the fallback underneath the worklet.
- The worklet's buffer never exceeds 1 s and never grows per packet; `process()` does not allocate.
- Steady 10 ms delivery: no trims, no underruns, standing buffer about 100 ms (`node teslapc/tests/js/pcm_player.test.js` passes).
- Output keeps source order with only forward gaps; every gap is a trim (5 ms crossfade) or an underrun; channels stay aligned.
- After a stall, burst, or gap the standing buffer is back between 60 and 160 ms within 5 s. Source clock drift moves it at most to `TRIM_ABOVE_MS` (fast) or to an underrun (slow); there is no rate correction.
- The page sends a `stats` text frame on `/ws/audio` every 10 s while the socket is open; the fallback stops queued sources when it is more than 0.25 s ahead.
- `index.html` and `audio-client.js` audio clients behave identically.
