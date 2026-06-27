# File Browser & In-App ffmpeg Player (VLC Fallback)

A server-side file browser that lists video files on the host, and an in-app ffmpeg decode
pipeline that plays a chosen file by feeding the existing MJPEG `/stream` and `/ws/audio`
endpoints directly — no VLC, no screen capture, host display can be off. Reached from the main
UI via the **Files** button. The file browser (`/list.html`) is unchanged; only the playback
path changed. VLC is retained as an automatic fallback when ffmpeg is absent.

## User Flow

1. From the main page, click **Files** → navigates to `/list.html`.
2. `/list.html` shows the files and folders under `C:\video\` (or a `?path=` folder). Folders
   are links that drill in; an **Up** link goes to the parent; **Back to Screen** returns to `/`.
3. Clicking a file opens `/play.html?FILENAME=<path>`:
   - **ffmpeg present (normal path):** starts in-app playback; the browser shows a player page
     with the MJPEG image (`<img src="/stream">`), a tap-to-start-audio overlay, and a control
     bar (play/pause, seek slider with time labels, Stop → `/`, Files → `/list.html`).
   - **ffmpeg absent (fallback):** kills any running VLC, waits 2 s, then launches VLC
     full-screen on the host — the original behavior.

## Technical Flow

All routing is in `WebServer.HandleHttpAsync`. Media control is in `MediaStreamer.cs`.

### `/list.html` (file browser)

- Query: `?path=<folder>` (defaults to `C:\video\` when absent).
- `returnAllFilesAsHtmlLinks(path)` builds touch markup:
  - Checks `Directory.Exists(path)` first. If the folder does not exist, returns the topbar
    plus a `.empty` message ("This folder doesn't exist … Pick a valid Video folder in
    Settings") with a link to `/config.html` — HTTP 200, not 500.
  - `Directory.GetDirectories` and `Directory.GetFiles` are wrapped in try/catch; on an
    `UnauthorizedAccessException` or other I/O error, `Log.Warn` is called and a "Couldn't
    read this folder" `.empty` message is shown — HTTP 200, not 500.
  - a sticky `.topbar` with **Screen** (`/`), **Up** (parent, omitted at the `C:\video\` root),
    and the current path.
  - a `.grid` of `.tile` cards — **folders first** (`📁`, link to `/list.html?path=<folder>`),
    then files (`🎬`, link to `/play.html?FILENAME=<fullpath>`, with an extension badge).
  - an empty folder shows a `.empty` message.
- Display names are HTML-encoded (`HttpUtility.HtmlEncode`) and query values URL-encoded
  (`HttpUtility.UrlEncode`), so filenames with spaces/special characters work.
- The result is substituted into the `{{GUTS}}` placeholder in `list.html`. Styling is in
  the shared `style.css`.

### `/play.html` (in-app player or VLC fallback)

- Query: `?FILENAME=<fullpath>`.
- If `FILENAME` is absent → page shows "No video file specified".
- If ffmpeg is available (`_media.IsFfmpegAvailable`):
  - Calls `_media.Play(file)` which begins the decode pipeline (see below).
  - Serves the player page (`play.html`) with `{{TITLE}}` replaced by the file's base name.
- If ffmpeg is absent (`LaunchVlcFallbackHtml`):
  - `Process.Start("taskkill", "/F /IM vlc.exe")` → 2 s wait → `Process.Start("C:\\Program
    Files\\VideoLAN\\VLC\\vlc.exe", "-vvv \"<file>\" --fullscreen")`.
  - Serves a small auto-refresh status card (the old behavior). `{{VIDEO}}` substitution
    is no longer used by the primary player; only the VLC fallback card uses a fixed message.

### `MediaStreamer` — in-app ffmpeg decode pipeline

`MediaStreamer` (namespace `Media`, `TeslaPCInterface/MediaStreamer.cs`) owns all playback state
and is constructed by `TeslaPcService` (injected into `WebServer`).

**ffmpeg discovery (`ResolveExe`):**
1. Env var `TESLAPC_FFMPEG` — may be a full path to `ffmpeg.exe` or a directory.
2. `AppContext.BaseDirectory` (alongside the .exe).
3. `PATH`.
`IsFfmpegAvailable` is `true` if ffmpeg was resolved. `ffprobe` is located by the same logic
(used only to probe duration for the seek bar).

**`Play(path)`:**
- Validates the path exists and is under `Root` (`C:\video\` by default).
- Probes duration via `ffprobe` (0 if unknown/error).
- Calls `_img.EnterMediaMode()` (suspends screen-capture loop) and sets
  `_audio.MediaMode = true` (suppresses WASAPI loopback enqueue).
- Starts the decode pipeline from position 0.

**Dual ffmpeg processes (started at the same `-ss` offset, both paced with `-re`):**

| Process | ffmpeg arguments | Consumer |
|---------|-----------------|----------|
| Video | `-hide_banner -loglevel error -re -ss <t> -i "<file>" -an -vf scale='min(1280,iw)':'min(720,ih)':force_original_aspect_ratio=decrease,fps=30 -c:v mjpeg -q:v 6 -f image2pipe pipe:1` | `JpegSplitter` (FF D8…FF D9 boundary scan) → `_img.PublishMediaFrame(jpeg)` |
| Audio | `-hide_banner -loglevel error -re -ss <t> -i "<file>" -vn -ar <deviceRate> -ac <deviceChannels> -f <fmt> pipe:1` | `_audio.EnqueueMediaAudio(pcm)` |

Audio format `<fmt>` is matched to the WASAPI device format announced to clients:
`float→f32le`, `pcm16→s16le`, `pcm24→s24le`, `pcm32→s32le`.

**Controls (`/media/*` endpoints):**

| Endpoint | Action |
|----------|--------|
| `/media/play?path=<fullpath>` | Validate path, probe duration, enter media mode, start pipeline from 0 |
| `/media/pause` | Record position (Stopwatch + `_seekBase`), kill both processes |
| `/media/resume` | Respawn both processes at saved position |
| `/media/seek?t=<seconds>` | Kill + respawn at offset; if paused, just update resume position |
| `/media/stop` | Kill processes, set `_audio.MediaMode=false`, call `_img.ExitMediaMode()` to restore live screen |
| `/media/status` | Return `MediaStatus` JSON |

All `/media/*` responses return `MediaStatus` JSON:
```json
{ "ffmpeg": true, "playing": true, "paused": false, "position": 12.4, "duration": 3600.0, "file": "movie.mkv" }
```
`file` is the base name only. `duration` is 0 when ffprobe could not determine it.

**Position tracking:** `Stopwatch` + `_seekBase` (offset at which the current pipeline started).
A generation counter `_gen` is bumped on every start/kill; reader threads compare against their
captured generation to detect supersession. Natural EOF on the video pipe (generation still
current, playing, not paused) auto-stops playback and calls `ExitMediaMode()` to restore the
live screen.

### `ImageStreamingServer` media mode

`EnterMediaMode()` sets `_mediaMode = true`; the screen-capture loop's `while` condition now
includes `&& !_mediaMode` so it stands down. `EnsureCaptureRunning()` is a no-op while in
media mode. Per-client send threads keep running and stream whatever frame is published.
`TryWaitForLatestFrame` treats the source as alive when `_mediaMode || captureThread alive`.
`PublishMediaFrame(byte[] jpeg)` is ignored if `!_mediaMode` (prevents a late reader
clobbering the live screen after stop). `ExitMediaMode()` clears `_mediaMode` and restarts
screen capture if clients are still connected.

### `AudioCapture` media mode

`MediaMode = true` suppresses `DataAvailable` enqueue from WASAPI loopback. Public getters
`SampleRate`, `Channels`, and `SampleFormat` let `MediaStreamer` match the audio format the
server already announced to audio clients. `EnqueueMediaAudio(byte[] pcm)` feeds PCM into the
same broadcast queue; buffers are dropped when no audio client is connected so the queue cannot
grow unbounded.

### `play.html` (player page)

When served by the ffmpeg path: `<img src="/stream">` renders decoded video; a `.tap-overlay`
(`.tap-inner`, `.tap-title`, `.tap-sub`) covers the page until the Client taps it, at which
point `startTeslaAudio()` is called (browsers require a user gesture for audio context
creation). A `.mediabar` control strip has a play/pause button, a `.seek` slider with `.time`
current/duration labels, a Stop button (→ `/`), and a Files button (→ `/list.html`). JS polls
`/media/status` every second and wires all controls to the `/media/*` endpoints. The placeholder
is `{{TITLE}}` (the file's base name).

### `audio-client.js` (new)

`TeslaPCInterface/audio-client.js` is a self-contained audio client exposing
`window.startTeslaAudio()` / `window.stopTeslaAudio()`. It connects to `/ws/audio`, reads the
format metadata packet, then plays PCM via `PCMPlayerProcessor.js` AudioWorklet with a
manual-scheduling fallback. Used by `play.html`; `index.html` keeps its own inline copy.

### HTML templating (`handleHTMLReplacements`)

Applied to all `text/html` responses: replaces `//LOCALHOST` with the request host, and when the
request port is `8443` swaps `:8080→:8443`, `:8081→:8444`, `:8082→:8445`, `ws://→wss://`,
`http://→https://`.

## Key Classes / Methods

| Member | File | Responsibility |
|--------|------|----------------|
| `MediaStreamer` | `TeslaPCInterface/MediaStreamer.cs` | ffmpeg discovery, playback lifecycle, dual-process pipeline, position tracking |
| `MediaStreamer.Play` | `TeslaPCInterface/MediaStreamer.cs` | Validate path, probe duration, enter media mode, start pipeline |
| `MediaStreamer.Pause` / `Resume` / `Seek` / `Stop` | `TeslaPCInterface/MediaStreamer.cs` | Playback controls; kill/respawn processes |
| `MediaStreamer.Status` | `TeslaPCInterface/MediaStreamer.cs` | Returns `MediaStatus` (ffmpeg, playing, paused, position, duration, file) |
| `JpegSplitter` | `TeslaPCInterface/MediaStreamer.cs` (internal) | Scans raw pipe output for FF D8…FF D9 JPEG boundaries |
| `ImageStreamingServer.EnterMediaMode` / `ExitMediaMode` / `PublishMediaFrame` | `TeslaPCInterface/ImageStreamingServer.cs` | Suspend/restore screen capture; push decoded video frames to clients |
| `AudioCapture.EnqueueMediaAudio` | `TeslaPCInterface/AudioStreamingServer.cs` | Push decoded audio PCM into the broadcast queue |
| `WebServer.HandleMedia` | `TeslaPCInterface/WebServer.cs` | Routes `/media/*` requests to `MediaStreamer` |
| `WebServer.HandleHttpAsync` | `TeslaPCInterface/WebServer.cs` | Serves `list.html`/`play.html`, applies templating, dispatches to `_media.Play` or VLC fallback |
| `WebServer.returnAllFilesAsHtmlLinks` | `TeslaPCInterface/WebServer.cs` | Builds file/folder link markup for the browser |
| `WebServer.handleHTMLReplacements` | `TeslaPCInterface/WebServer.cs` | Host/port/protocol substitution for HTML responses |
| `TeslaPcService` | `TeslaPCInterface/TeslaPcService.cs` | Constructs `MediaStreamer`, passes it to `WebServer`, calls `_media.Stop()` on shutdown |
| `list.html` | `TeslaPCInterface/list.html` | File-browser page template (`{{GUTS}}`) |
| `play.html` | `TeslaPCInterface/play.html` | Player page template (`{{TITLE}}`); VLC fallback uses an inline status card |
| `audio-client.js` | `TeslaPCInterface/audio-client.js` | Self-contained WebSocket audio client for play.html |
| `style.css` | `TeslaPCInterface/style.css` | Shared styles including `.mediabar`, `.seek`, `.time`, `.tap-overlay` |

## Routes and Access Control

| Route | Protocol | Auth | Handler |
|-------|----------|------|---------|
| `/list.html` | HTTP/HTTPS | none | `HandleHttpAsync` (file browser) |
| `/play.html` | HTTP/HTTPS | none | `HandleHttpAsync` (player or VLC fallback) |
| `/media/play` | HTTP/HTTPS | none | `HandleMedia` → `MediaStreamer.Play` |
| `/media/pause` | HTTP/HTTPS | none | `HandleMedia` → `MediaStreamer.Pause` |
| `/media/resume` | HTTP/HTTPS | none | `HandleMedia` → `MediaStreamer.Resume` |
| `/media/seek` | HTTP/HTTPS | none | `HandleMedia` → `MediaStreamer.Seek` |
| `/media/stop` | HTTP/HTTPS | none | `HandleMedia` → `MediaStreamer.Stop` |
| `/media/status` | HTTP/HTTPS | none | `HandleMedia` → `MediaStreamer.Status` |

No authentication. **Anyone who can reach the server can browse the host's `C:\video` tree (or
any `?path=` folder), start in-app playback (or fall back to VLC on the host), and issue
play/pause/seek/stop commands** — a notable capability to keep in mind for network exposure.

## Constants / Environment

| Item | Value |
|------|-------|
| Default browse root (`Root`) | `C:\video\` |
| ffmpeg discovery order | `TESLAPC_FFMPEG` env var (full path or dir) → `AppContext.BaseDirectory` → PATH |
| Video scale cap | `min(1280,iw)` × `min(720,ih)`, aspect-preserving |
| Video frame rate | 30 fps |
| Video JPEG quality | `-q:v 6` |
| Audio format matching | `float→f32le`, `pcm16→s16le`, `pcm24→s24le`, `pcm32→s32le` |
| VLC executable path (fallback) | `C:\Program Files\VideoLAN\VLC\vlc.exe` |
| VLC launch args (fallback) | `-vvv "<file>" --fullscreen` |
| VLC pre-launch kill delay (fallback) | 2000 ms |

## Prerequisites

- **ffmpeg** (and **ffprobe**) must be available — on PATH, in the app directory, or pointed to
  by `TESLAPC_FFMPEG`. Install via `winget install Gyan.FFmpeg` or any standard distribution.
- If ffmpeg is absent, **VLC** must be installed at `C:\Program Files\VideoLAN\VLC\vlc.exe`
  (e.g. `winget install VideoLAN.VLC`) for the fallback path to work.
- A **browse folder** must exist; `C:\video\` by default, or pass `?path=`. If the folder
  does not exist or is unreadable, the file browser returns a friendly HTTP 200 page with an
  error message and a link to `/config.html`; it does not return HTTP 500.
- If ffmpeg is absent and VLC is missing, the VLC `Process.Start` call throws → HTTP 500.
  A missing browse folder no longer causes HTTP 500.

## Database Schema

None.

## SQL Artifacts

None.
