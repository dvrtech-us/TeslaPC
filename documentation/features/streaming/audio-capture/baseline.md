# Audio Capture — Behavior Baseline

- First `/ws/audio` frame is JSON text: `type=format`, `sampleRate`, `bitsPerSample`, `channels`, `sampleFormat`.
- Later frames are binary PCM.
- Keepalive noise (not zero PCM) starts at `250` ms idle, stops after `10` s of silence (`SILENCE_MAX_MS`), and is polled every `20` ms. Peak is `1/4096` (about -72 dBFS).
- Each keepalive buffer covers the real time since the previous tick (`keepalive_bytes`, whole frames, capped at `KEEPALIVE_MAX_CHUNK_MS` = `100`), so noise is continuous. `typical_bytes` only gates injection.
- Noise is never mixed into packets that contain sound.
- `PcmHub::push` replaces an all-zero captured packet with noise while silence is under `10` s and forwards it unchanged after that. This includes ffmpeg playback packets.
- A WASAPI packet flagged `AUDCLNT_BUFFERFLAGS_SILENT` (`0x2`) is pushed as zeros, never as the buffer content.
- A `WAVE_FORMAT_EXTENSIBLE` mix format is float when the SubFormat GUID `data1 == 3`. A 32-bit float mix format is announced as `float`, not `pcm32`.
- Media mode suppresses loopback. Stop restores it.
- A failed loopback device does not stop HTTP.
- Windows loopback retries forever. Waits are `1`, `2`, `4`, `8`, then `10` s (`loopback_backoff_ms`), reset after a run over `30` s. `capturing` is false between attempts. Each attempt re-reads the default render endpoint. A repeated error text is logged once, not per attempt.
- Windows loopback is the default render endpoint. After `IAudioClient::Start`, `capturing` is true and `typical_bytes` comes from the mix format so silence keepalive can run.
- `/ws/audio` clients read from one `tokio::sync::broadcast` channel of 10 ms chunks (`PCM_BROADCAST_CAPACITY` = `64`, about `640` ms). A lagging client skips the oldest chunks, logs `[Audio] client lagged; skipped <n> chunks`, and keeps playing; it never delays other clients and its backlog never exceeds `64` chunks.
- Media audio is pushed in frame-aligned 10 ms chunks (`rate / 100 * frame_bytes`), plus a final whole-frame tail when ffmpeg ends. It is never pushed one sample frame at a time.
- A client text frame `{"type":"stats",...}` up to `512` bytes is logged as `[Audio] client buffered=...`; other client frames are ignored.
- Keepalive injection requires that no captured packet arrived since the previous `20` ms tick.
