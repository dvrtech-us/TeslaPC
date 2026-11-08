# Audio Capture — Behavior Baseline

- First `/ws/audio` frame is JSON text: `type=format`, `sampleRate`, `bitsPerSample`, `channels`, `sampleFormat`.
- Later frames are binary PCM.
- Silence starts at `250` ms idle, stops at `30` s, and is polled every `20` ms.
- Media mode suppresses loopback. Stop restores it.
- A failed loopback device does not stop HTTP.
- Windows loopback is the default render endpoint. After `IAudioClient::Start`, `capturing` is true and `typical_bytes` comes from the mix format so silence keepalive can run.
