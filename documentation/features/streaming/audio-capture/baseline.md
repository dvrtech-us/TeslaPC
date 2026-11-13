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
- Windows loopback is the default render endpoint. After `IAudioClient::Start`, `capturing` is true and `typical_bytes` comes from the mix format so silence keepalive can run.
