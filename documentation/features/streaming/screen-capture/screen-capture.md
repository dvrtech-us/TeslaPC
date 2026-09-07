# Screen Capture & MJPEG Streaming

Captures the primary monitor and streams it to browsers as MJPEG over the `/stream` route.
A single shared capture loop feeds all connected clients. Capture backend chain:
**Windows Graphics Capture (WGC)** → **DXGI Desktop Duplication** → **GDI `CopyFromScreen`**.
WGC captures the fully composited DWM output, so hardware-overlay (MPO) video that renders
black under DXGI duplication captures correctly, and the cursor is included. DRM-protected
content (Netflix etc.) is excluded from **every** capture API by the OS and stays black.

## User Flow

1. The browser loads `index.html`, which contains `<img src="/stream">`.
2. The browser opens the MJPEG stream; the server begins (or joins) the shared capture loop.
3. Each captured frame is JPEG-encoded once and pushed to every connected client.
4. When the last client disconnects, the capture loop stops and releases its resources.
5. The Client can select a stream resolution (480p / 720p / 1080p) from a dropdown in the
   main screen or the Settings page. Changes apply immediately without a stream reconnect.

## Technical Flow

### Per-client entry (`ImageStreamingServer.HandleStreamRequest`, `ImageStreamingServer.cs`)

1. `Interlocked.Increment(ref _clientCount)`.
2. `EnsureCaptureRunning()` starts the shared `"ScreenCapture"` background thread if not already alive (guarded by `lock (_frameLock)`).
3. A `MjpegWriter` is created with boundary `"boundary"`; `WriteHeader()` is called **synchronously on the request thread** (prevents http.sys 503).
4. `ThreadPool.QueueUserWorkItem(StreamToClient)` runs this client's send loop.

### Shared capture loop (`CaptureLoop` → `RunCaptureSession`)

1. Snapshot `_restartEpoch` and the current `MaxWidth`/`MaxHeight` cap.
2. `using var wgcCapture = new WgcScreenCapture()`; if unavailable,
   `using var dxgiCapture = new DxgiScreenCapture()`; check each `IsAvailable`.
3. Screen size from the active backend's `CaptureSize` (WGC/DXGI) or
   `Screen.PrimaryScreen.Bounds` (GDI); saved as `screenSize` for the session.
4. Output size = the live screen scaled **uniformly** to fit the `MaxWidth × MaxHeight` cap box,
   preserving aspect ratio and never upscaling:
   `scale = min(1, MaxWidth/screenW, MaxHeight/screenH)`. So a 1080p screen with `MaxHeight=720`
   streams 1280×720; the same screen with `MaxHeight=1080` streams at native 1920×1080; a 16:10
   screen at 1200p with `MaxHeight=1080` streams 1728×1080.
5. Allocate `srcImage` (`Format32bppArgb`) and, if resizing, `scaledImage` (`Format24bppRgb`).
6. Resolve the JPEG codec (`GetJpegCodec`) and set `Encoder.Quality = 60L`.
7. Per frame:
   - `wgcCapture.TryCapture(srcImage)`, `dxgiCapture.TryCapture(srcImage)`, or `srcGraphics.CopyFromScreen(...)`.
   - If resizing: `DrawImage` into `scaledImage`, then `scaledImage.Save(ms, jpegCodec, params)`. Else `srcImage.Save(...)`.
   - `PublishFrame(ms)`: copy bytes to `_currentFrame`, increment `_frameNumber`, `Monitor.PulseAll(_frameLock)`.
   - Pace: if `elapsed < Interval`, `Thread.Sleep(Interval - elapsed)` where `Interval = 1000 / fps`.
8. `RunCaptureSession` returns `true` (restart) when either:
   - `_restartEpoch` has changed (bumped by `SetMaxResolution` or `RestartCapture`), **or**
   - `Screen.PrimaryScreen.Bounds` no longer matches the session's `screenSize` (display
     resolution changed mid-session), **or**
   - the WGC backend became unavailable mid-session (monitor capture item closed).
   A `true` return causes `CaptureLoop` to immediately start a fresh session, re-probing the
   backend chain with correctly-sized buffers. Neither WGC nor DXGI duplication survives a
   display-mode switch, so a new session is required after any resolution change.
9. When no restart is needed, `RunCaptureSession` returns `false`, and the capture thread exits.

### Resolution cap — mutable at runtime

`_maxWidth` and `_maxHeight` are `volatile` fields on `ImageStreamingServer`. The public
`MaxWidth` and `MaxHeight` properties read them. `SetMaxResolution(int w, int h)` sets both
and increments `_restartEpoch` (via `Interlocked.Increment`), which causes the running session
to detect the change and restart without dropping any connected clients.

`RestartCapture()` increments `_restartEpoch` without changing the dimensions — used by
display-control routes to force a fresh DXGI session after a desktop resolution change.

### Per-client send (`StreamToClient` → `TryWaitForLatestFrame`)

- Blocks on `Monitor.Wait(_frameLock, 1000)` until a newer `_frameNumber` is published.
- If a newer frame arrived while sending, the intermediate frame is **skipped** (slow clients always get the latest image).
- Sends via `MjpegWriter.Write(frame)`.

### WGC capture (`WgcScreenCapture.cs`)

- Public API mirrors `DxgiScreenCapture`: `IsAvailable`, `CaptureSize`, `TryCapture(Bitmap)`, `Dispose()`.
- Init: `GraphicsCaptureSession.IsSupported()` → `D3D11CreateDevice` (hardware, `BgraSupport`) →
  `CreateDirect3D11DeviceFromDXGIDevice` (WinRT `IDirect3DDevice`) →
  `IGraphicsCaptureItemInterop.CreateForMonitor` on the primary monitor (`MonitorFromPoint(0,0)`)
  → free-threaded `Direct3D11CaptureFramePool` (`B8G8R8A8UIntNormalized`, 2 buffers) →
  `CreateCaptureSession` → `StartCapture()`. The yellow capture border is disabled where
  `IsBorderRequired` exists and access allows (best-effort).
- `TryCapture`: **drains** the frame pool to the newest frame (never serves stale frames to the
  30 FPS consumer), skips frames whose `ContentSize` no longer matches `CaptureSize` (mode switch —
  the session restart handles it), then copies GPU texture → cached staging texture → row-by-row
  into the target `Bitmap` (same copy path as DXGI).
- The monitor item's `Closed` event sets `IsAvailable = false`; the capture loop restarts the
  session, which re-probes the chain.
- Requires Windows 10 1903+ (monitor capture). On failure the constructor sets
  `IsAvailable = false` and the session falls back to DXGI. `TESLAPC_NO_WGC=1` skips WGC entirely.
- Includes the mouse cursor (WGC default); the DXGI and GDI paths do not draw the cursor.

### DXGI capture (`DxgiScreenCapture.cs`)

- Public API: `IsAvailable` (bool), `CaptureSize` (Size), `TryCapture(Bitmap)` (bool), `Dispose()`.
- Init: `CreateDXGIFactory1` → find the output whose `DesktopCoordinates` matches the primary screen origin → `D3D11CreateDevice` (`BgraSupport`) → `DuplicateOutput`.
- `TryCapture`: `AcquireNextFrame(16ms)` → on `WaitTimeout` returns false; on `AccessLost`/`AccessDenied` calls `RecreateDuplication()` and returns false; copies the GPU texture to a cached CPU **staging texture**, maps it, and copies row-by-row (respecting `RowPitch`) into the target `Bitmap`.
- On any init failure the constructor sets `IsAvailable = false` and logs that GDI fallback will be used.
- `EnsureStagingTexture` checks that the existing staging texture matches the DXGI output dimensions; if not, it disposes and recreates it. After a display-mode switch the new session creates a fresh `DxgiScreenCapture`, so this check runs fresh.

### MJPEG framing (`MjpegWriter.cs`)

- `WriteHeader()`: `Content-Type: multipart/x-mixed-replace; boundary=boundary`, `200`, `Cache-Control: no-cache, no-store, must-revalidate`, `Pragma: no-cache`, `SendChunked = true`, `KeepAlive = true`.
- `Write(jpeg)`: writes `\r\n--boundary\r\nContent-Type: image/jpeg\r\nContent-Length: N\r\n\r\n`, then the JPEG bytes, then `\r\n`, then flushes.

## Key Classes

| Class | File | Responsibility |
|-------|------|----------------|
| `ImageStreamingServer` | `TeslaPCInterface/ImageStreamingServer.cs` | Shared capture loop, frame buffer, per-client send threads, JPEG encoding; mutable `MaxWidth`/`MaxHeight`; `SetMaxResolution`/`RestartCapture` |
| `WgcScreenCapture` | `TeslaPCInterface/WgcScreenCapture.cs` | Windows Graphics Capture (composited DWM output, incl. MPO video + cursor); signals unavailability for DXGI fallback |
| `DxgiScreenCapture` | `TeslaPCInterface/DxgiScreenCapture.cs` | DXGI Desktop Duplication capture; signals unavailability for GDI fallback |
| `MjpegWriter` | `TeslaPCInterface/MjpegWriter.cs` | multipart/x-mixed-replace header + per-frame boundary framing |

## Constants

| Constant | Value | Location |
|----------|-------|----------|
| Frame interval | `1000 / fps` ms (33 ms at 30 FPS) | `ImageStreamingServer.cs` |
| JPEG quality | `60L` | `ImageStreamingServer.cs` |
| DXGI acquire timeout | `16` ms | `DxgiScreenCapture.cs` |
| Client wait timeout | `1000` ms | `ImageStreamingServer.cs` |
| Boundary string | `"boundary"` | `ImageStreamingServer.cs` |
| Source pixel format | `Format32bppArgb` | `ImageStreamingServer.cs` |
| Scaled pixel format | `Format24bppRgb` | `ImageStreamingServer.cs` |
| Scaling quality | `HighSpeed` / `Bilinear` / `SmoothingMode.None` | `ImageStreamingServer.cs` |
| Max resolution (default) | `4320×1080` (width = 4× height), `30` FPS | Constructed in `TeslaPcService` from `AppSettings.StreamHeight` |
| `AppSettings.DefaultStreamHeight` | `1080` | `TeslaPCInterface/AppSettings.cs` |
| Height clamp range | `[240, 2160]` | `AppSettings.StreamHeight` property |

## Environment Variables

| Variable | `AppSettings` constant | Default | Apply timing |
|----------|----------------------|---------|--------------|
| `TESLAPC_STREAM_HEIGHT` | `StreamHeightKey` | `1080` | Next capture-session restart (immediate if the server restarts the session) |
| `TESLAPC_NO_WGC` | (read directly in `WgcScreenCapture`) | unset | `1` disables Windows Graphics Capture (troubleshooting kill switch; forces DXGI/GDI) |

## Routes and Access Control

| Route | Protocol | Auth | Handler |
|-------|----------|------|---------|
| `/stream` | HTTP/HTTPS | none | `ImageStreamingServer.HandleStreamRequest` |

## Integration Points

- Invoked by [web-server](../../server/web-server/web-server.md) routing for `/stream`.
- Consumed by [web-ui](../../client/web-ui/web-ui.md) via a plain `<img>` tag.
- Max-resolution cap is readable via `/config` (`streamHeight` field) and writable via
  `POST /config` (`streamHeight`), as documented in
  [configuration](../../app/configuration/configuration.md).
- Display-resolution changes from [display-control](display-control/display-control.md) call
  `RestartCapture()` after applying a new desktop mode.

## Headless / Asleep Display Caveat

DXGI Desktop Duplication requires an active display. On a headless or asleep laptop Windows
falls back to 800×600 and DXGI captures nothing useful. A physical monitor or an HDMI dummy
plug is required for the resolution features to work correctly.

## Database Schema

None.

## SQL Artifacts

None.
