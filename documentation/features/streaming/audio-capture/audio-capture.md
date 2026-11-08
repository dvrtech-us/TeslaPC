# Audio Capture

`/ws/audio` sends one text frame from `format_message`, then binary PCM. Fields are `type` (`format`), `sampleRate`, `bitsPerSample`, `channels`, and `sampleFormat` (`float`, `pcm16`, `pcm24`, or `pcm32`).

`should_inject_silence` injects zero PCM when loopback has been idle at least `SILENCE_START_MS` (`250`), stops at `SILENCE_MAX_MS` (`30000`), and the task sleeps `KEEPALIVE_POLL_MS` (`20`). It does not inject during media playback, when the queue is non-empty, when nobody is listening, or before a real buffer size is known.

Windows `win_capture::run_loopback` opens the default render endpoint (`GetDefaultAudioEndpoint(eRender, eConsole)`), initializes it with `AUDCLNT_STREAMFLAGS_LOOPBACK`, sets `capturing`, stores a nominal `typical_bytes` from the mix format, and pushes each packet. A real OS failure is logged and the socket still announces the default format (`48000`, `32`, `2`, `float`). Linux runs `parecord` on the default sink's `.monitor`.

While a file is playing, `pcm.media_mode` suppresses loopback and `playback::audio_reader` pushes the file's PCM on the same queue. Stop clears the flag.

The browser player is `teslapc/assets/PCMPlayerProcessor.js`.
