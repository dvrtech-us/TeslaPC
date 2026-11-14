# In-App ffmpeg Player (replacing VLC screen-capture path)

- Date: 2026-06-27
- Feature: file-browser-vlc
- Related code: `TeslaPCInterface/MediaStreamer.cs`, `TeslaPCInterface/WebServer.cs`, `TeslaPCInterface/ImageStreamingServer.cs`, `TeslaPCInterface/AudioStreamingServer.cs`, `TeslaPCInterface/TeslaPcService.cs`, `TeslaPCInterface/play.html`, `TeslaPCInterface/audio-client.js`, `TeslaPCInterface/style.css`

## Context

The previous playback path launched VLC full-screen on the host and relied on the live
screen-capture MJPEG stream to deliver video to the Tesla in-car browser. This worked but had
three problems:

1. **In-motion video lockout.** The Tesla browser blocks native `<video>` decoding while the car
   is moving. TeslaPC already works around this for live screen content by delivering an MJPEG
   image stream and a separate Web-Audio audio feed — neither is subject to the lockout. VLC
   displayed into the OS compositor, which the host GPU then captured via DXGI and MJPEG-encoded
   for the stream. The VLC approach therefore still worked around the browser lockout, but only
   through an unnecessarily long chain.
2. **Host display required.** DXGI Desktop Duplication captures what the GPU is compositing; if
   the display is off or the session is locked, frames degrade or stop.
3. **No real controls.** The browser had no play/pause/seek capability; it was a one-shot launch
   with an auto-refresh status card.

## Decisions

- **Replace VLC+screen-capture with a direct ffmpeg decode pipeline** that feeds `MediaStreamer`
  decoded frames into `ImageStreamingServer.PublishMediaFrame` (video) and
  `AudioCapture.EnqueueMediaAudio` (audio) — injecting directly into the same broadcast paths
  the live-screen stream already uses. This keeps the in-motion-lockout workaround (MJPEG +
  Web-Audio) while eliminating the GPU compositor middleman.
- **Two `-re`-paced ffmpeg processes** per playback, started at the same `-ss` offset: one for
  video (MJPEG pipe, split by `JpegSplitter` on FF D8…FF D9 boundaries) and one for audio
  (raw PCM pipe in the WASAPI device format). Real-time pacing with `-re` keeps A/V broadly
  synchronized without a shared clock between the two processes; brief drift is acceptable given
  the MJPEG transport.
- **`ImageStreamingServer` media mode** (`EnterMediaMode` / `ExitMediaMode`): the screen-capture
  loop stands down while `_mediaMode` is true, and is restarted by `ExitMediaMode` if clients
  are still connected. Per-client send threads remain alive throughout and stream whatever frame
  `PublishMediaFrame` delivers, so there is no reconnect needed on the client side.
- **`AudioCapture` media mode** (`MediaMode = true`): suppresses the WASAPI loopback
  `DataAvailable` enqueue so host system audio does not mix with the decoded file audio.
- **`/media/*` HTTP endpoints** added to `WebServer` (`HandleMedia`) for play, pause, resume,
  seek, stop, and status. All return `MediaStatus` JSON so the browser page can poll state.
- **`play.html` rewritten** from a status card into an actual player page: `<img src="/stream">`
  for decoded video, a tap-to-start-audio overlay (browser requires a user gesture), a control
  bar with play/pause, seek slider with time labels, Stop, and Files. JS polls `/media/status`
  every second.
- **`audio-client.js` extracted** as a self-contained audio client used by `play.html`.
  `index.html` keeps its own inline copy to avoid any coupling.
- **VLC retained as automatic fallback** (`LaunchVlcFallbackHtml`) when `IsFfmpegAvailable` is
  false. This preserves the previous behavior for environments where ffmpeg is not installed and
  VLC is available.
- **ffmpeg discovery order** (`ResolveExe`): `TESLAPC_FFMPEG` env var first (full path or
  directory), then `AppContext.BaseDirectory`, then PATH. This lets a portable ffmpeg binary be
  shipped alongside the .exe without requiring a system install.

## Consequences

- **Positive:** Playback works with the host display off or the session locked. Real play/pause/
  seek control from the Tesla browser. No dependency on the OS compositor or DXGI for playback
  frames. Screen capture is cleanly suspended during media mode and restored on stop/EOF.
  Audio loopback is cleanly suppressed so decoded audio does not compete with host system audio.
- **Quality trade-off:** Video is still MJPEG-encoded (by ffmpeg, at `-q:v 6`, capped
  1280×720/30 fps) for delivery to the browser — the same encoding the live screen stream uses.
  True progressive video codecs are not an option for the in-motion browser lockout workaround.
- **A/V sync:** Two independent `-re`-paced processes are not perfectly synchronized; brief
  drift is possible, particularly after seek. Acceptable for the use case (car infotainment).
- **New prerequisite:** ffmpeg (and ffprobe for seek bar duration) must be available. VLC is
  now only needed as a fallback when ffmpeg is absent.
- **Security note unchanged:** The feature remains unauthenticated; a Client who can reach the
  server can browse `C:\video`, start playback, and issue control commands. The VLC fallback
  additionally allows launching a process on the host.
