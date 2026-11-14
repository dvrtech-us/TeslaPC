# Configurable stream resolution cap and session-restart-on-cap-change

- Date: 2026-06-27
- Feature: screen-capture
- Related code: `TeslaPCInterface/ImageStreamingServer.cs`, `TeslaPCInterface/AppSettings.cs`,
  `TeslaPCInterface/TeslaPcService.cs`, `TeslaPCInterface/DxgiScreenCapture.cs`,
  `TeslaPCInterface/WebServer.cs`, `TeslaPCInterface/index.html`, `TeslaPCInterface/config.html`

## Context

The MJPEG stream cap was hardcoded to `1280×720` with no way to change it at runtime. Some
Clients wanted 1080p quality and some wanted lower resolution for slower connections. Additionally,
the display-control feature (added in the same batch) changes the Windows desktop resolution at
runtime, which broke the existing `DxgiScreenCapture` session (staging-texture size mismatch).
Two things were therefore needed:

1. Make the max-height cap configurable (env var + live `/config` POST).
2. Detect when the cap or the desktop resolution changes and restart the DXGI session cleanly.

## Decisions

### `AppSettings.StreamHeight` / `TESLAPC_STREAM_HEIGHT`

A new env var `TESLAPC_STREAM_HEIGHT` (constant `AppSettings.StreamHeightKey`) controls the
height cap at startup. `AppSettings.StreamHeight` reads and clamps the value to `[240, 2160]`;
invalid or absent values fall back to `AppSettings.DefaultStreamHeight = 1080`. The width cap
is always `4 × height` so height is the binding dimension for any 4:3, 16:9, 21:9, or wider
display — the stream never exceeds the width of a monitor whose height matches the cap.

### Mutable `MaxWidth` / `MaxHeight` on `ImageStreamingServer`

`_maxWidth` and `_maxHeight` are declared `volatile`. `SetMaxResolution(int w, int h)` writes
them and increments `_restartEpoch` (via `Interlocked`). The running capture session snapshots
the epoch at its start and returns `true` (restart signal) when the epoch changes, which causes
`CaptureLoop` to loop and start a new session with the updated cap. No clients are dropped
during the transition — the new session is set up before the old one tears down, and the shared
frame buffer continues to be published throughout.

### `RestartCapture()` for display-mode changes

`RestartCapture()` increments `_restartEpoch` without changing `MaxWidth`/`MaxHeight`. This is
the hook called by `WebServer.HandleDisplay` after a desktop resolution change. Without it the
running DXGI session would attempt to map a GPU texture of the old size into a staging texture
of the new size (or vice versa), causing `DxgiScreenCapture.EnsureStagingTexture` to throw.

### Session restart on `Screen.PrimaryScreen.Bounds` change

Even without an explicit `RestartCapture()` call (e.g. if the user changes the resolution
outside the app), `RunCaptureSession` compares `Screen.PrimaryScreen.Bounds` to its startup
`screenSize` on every frame. A mismatch returns `true` to trigger a new session. This is a
belt-and-suspenders guard; the explicit `RestartCapture()` from display-control is the primary
path.

### Default raised to 1080p

The hardcoded 720 cap was raised to 1080 as the new default. 1080p is the most common laptop
resolution; streaming at native resolution avoids a lossy downscale + upscale round-trip in the
browser and provides noticeably sharper text. Clients who need lower bandwidth can select 720p
or 480p from the new dropdown.

### `/config` integration for stream height

`GET /config` now includes `streamHeight` (= `_imageStreamer.MaxHeight`). `POST /config`
accepts `streamHeight` (240–2160), persists it to `%ProgramData%\TeslaPC\.env`, and calls
`_imageStreamer.SetMaxResolution(h*4, h)` immediately. The dropdown (480p / 720p / 1080p)
appears in both `index.html` and `config.html` — same control surfaces as other settings.

## Consequences

- **Positive:** stream quality is tunable per-Client per-session from the web UI.
- **Positive:** display-mode switches (from the new display-control feature) no longer crash
  the DXGI session; the capture restarts cleanly within one missed frame.
- **Positive:** even without an explicit call, any unexpected desktop resolution change is
  caught within one frame's worth of the session's steady-state loop.
- **Note:** `TESLAPC_STREAM_HEIGHT` takes effect at the next capture-session restart; it is not
  re-read mid-session. The live `POST /config` path calls `SetMaxResolution` directly and does
  not wait for the env var to be re-read.
- **Note:** headless / asleep displays still produce a black or 800×600 stream; the cap change
  does not affect that underlying constraint.
