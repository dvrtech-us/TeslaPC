# File Browser & In-App ffmpeg Player — Behavior Baseline

Known-good invariants. Update only when intended behavior changes.

## Invariants

- `/list.html` lists the contents of `?path=` or, when absent, **`C:\video\`**.
- Files link to `/play.html?FILENAME=<fullpath>`; subfolders link to `/list.html?path=<folder>`.
- At the browse root (`C:\video\`) a **Back to Screen** link is shown; otherwise an **Up** link
  to the parent folder.
- `/play.html` with no `FILENAME` shows "No video file specified" and neither starts ffmpeg
  nor launches VLC.
- `/play.html` **with ffmpeg available** calls `_media.Play(file)` and serves the player page;
  it does **not** launch VLC, does **not** require the host display to be on, and does **not**
  use the screen-capture loop.
- `/play.html` **without ffmpeg** (`IsFfmpegAvailable == false`) falls back to VLC: kills any
  running VLC (`taskkill /F /IM vlc.exe`), waits 2 s, then launches
  `vlc.exe -vvv "<file>" --fullscreen` on the host and serves an auto-refresh status card.

### `/media/*` endpoint invariants

- `Play` rejects paths that do not exist or are outside `Root` (`C:\video\`).
- When `Play` succeeds, `ImageStreamingServer._mediaMode` is set to `true` (screen-capture loop
  stands down) and `AudioCapture.MediaMode` is set to `true` (WASAPI loopback enqueue
  suppressed).
- Two ffmpeg processes are started at the same `-ss` offset, both paced with `-re`.
- The video pipe produces raw concatenated JPEG frames (each bounded by FF D8 … FF D9); each
  frame is delivered to connected MJPEG clients via `PublishMediaFrame`.
- The audio pipe produces frame-aligned PCM in the WASAPI device format (`f32le`, `s16le`,
  `s24le`, or `s32le`) and is enqueued via `EnqueueMediaAudio`; buffers are dropped when no
  audio client is connected.
- `Pause` kills both processes and records the playback position; `Resume` respawns at that
  position.
- `Seek` kills and respawns at the requested offset; if currently paused, it updates the saved
  position without spawning.
- `Stop` kills both processes, sets `_audio.MediaMode = false`, and calls `ExitMediaMode()`
  which clears `_mediaMode` and restarts screen capture if clients remain connected.
- Natural EOF on the video pipe (generation still current, playing, not paused) auto-stops
  playback and restores the live screen (same effect as `Stop`).
- `/media/status` always returns a `MediaStatus` JSON object with fields `ffmpeg` (bool),
  `playing` (bool), `paused` (bool), `position` (seconds), `duration` (seconds, 0 if unknown),
  and `file` (base name only, or null when not playing).
- `PublishMediaFrame` is a no-op when `!_mediaMode`, preventing a late-arriving frame from
  overwriting the live screen after stop.
- `text/html` responses pass through `handleHTMLReplacements` (host substitution; on port 8443,
  port/protocol upgrades).

## Access Control

- No authentication. The Client can traverse the host filesystem via `?path=`, start in-app
  playback, and issue play/pause/seek/stop commands via `/media/*`.

## Failure Behavior

- ffmpeg not found and VLC not installed at `C:\Program Files\VideoLAN\VLC\vlc.exe` →
  `Process.Start` throws → HTTP 500.
- Browse folder missing (`Directory.Exists` returns false) → `returnAllFilesAsHtmlLinks`
  returns a friendly `.empty` page ("This folder doesn't exist … Pick a valid Video folder in
  Settings") with a link to `/config.html` — **HTTP 200, not 500**.
- Browse folder exists but unreadable (access error from `Directory.GetDirectories` or
  `Directory.GetFiles`) → `Log.Warn` is called; a "Couldn't read this folder" `.empty` message
  is shown — **HTTP 200, not 500**.
- Path passed to `Play` does not exist or is outside `Root` → `Play` returns an error status;
  media mode is not entered.
- ffprobe unavailable or unable to determine duration → `duration` is reported as 0; playback
  still proceeds without a seek bar range.
- Audio client not connected → `EnqueueMediaAudio` drops buffers silently; the queue does not
  grow unbounded.

## Prerequisites (environment, not code)

- ffmpeg (and ffprobe) available via `TESLAPC_FFMPEG`, `AppContext.BaseDirectory`, or PATH.
- VLC installed at the standard path (only required when ffmpeg is absent).
- `C:\video\` (or the chosen `?path=`) should exist; a missing or unreadable folder is now
  handled gracefully (HTTP 200 with a friendly message) rather than causing HTTP 500.
