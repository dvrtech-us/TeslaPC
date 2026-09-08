# Screen Capture & Display Streaming

Captures the primary monitor and streams it to browsers as MJPEG or H264. MJPEG is available
over the legacy `/stream` HTTP route and the `/ws/display` WebSocket route. H264 is available
only over `/ws/display` and is encoded by the native Windows Media Foundation H.264 encoder
MFT (`mfh264enc.dll`). A single shared capture loop feeds all connected clients. Capture
backend chain: **Windows Graphics Capture (WGC)** → **DXGI Desktop Duplication** →
**GDI `CopyFromScreen`**. WGC captures the fully composited DWM output, so hardware-overlay
(MPO) video that renders black under DXGI duplication captures correctly, and the cursor is
included. DRM-protected content (Netflix etc.) is excluded from **every** capture API by the OS
and stays black.

## User Flow

1. The browser loads `index.html`, which contains `#streamImg` for MJPEG and `#streamCanvas`
   for H264.
2. On tap-to-connect, the browser reads `/config` and opens either the legacy `/stream` route
   (`displayTransport=http`) or `/ws/display?renderer=mjpeg|h264` (`displayTransport=websocket`).
3. Each captured frame is JPEG-encoded only when at least one JPEG consumer exists (`/stream`
   or `/ws/display?renderer=mjpeg`). When at least one H264 client is connected, the frame is
   converted to NV12 by `Bgr24ToNv12Converter` and encoded by `H264MediaFoundationEncoder`.
4. When the last client disconnects, the capture loop stops and releases its resources.
5. The Client can select a stream resolution (480p / 720p / 1080p) from a dropdown in the
   main screen or the Settings page. Changes apply immediately without a stream reconnect.

## Technical Flow

### Per-client entry (`ImageStreamingServer.HandleStreamRequest`, `ImageStreamingServer.cs`)

1. `Interlocked.Increment(ref _clientCount)`.
2. `EnsureCaptureRunning()` starts the shared `"ScreenCapture"` background thread if not already alive (guarded by `lock (_frameLock)`).
3. A `MjpegWriter` is created with boundary `"boundary"`; `WriteHeader()` is called **synchronously on the request thread** (prevents http.sys 503).
4. `ThreadPool.QueueUserWorkItem(StreamToClient)` runs this client's send loop.

### WebSocket display entry (`ImageStreamingServer.HandleDisplayWebSocketAsync`, `DisplayWebSocket.cs`)

1. `GetStreamOutputSize()` returns the current even-width/even-height output size (estimated
   from the primary display before the first frame).
2. `DisplayWebSocket.HandleClientAsync` accepts `/ws/display`, reads the `renderer` query
   parameter, and sends a JSON format message:
   `{ type:"format", formatVersion:2, renderer, width, height, fps, streamEpochUs, hostClockHz }`.
3. `renderer=h264` is accepted only when `H264MediaFoundationEncoder.IsAvailable` is true;
   otherwise the server logs a fallback and negotiates `mjpeg`.
4. Binary display messages have an 8-byte little-endian host PTS prefix followed by either a
   JPEG frame or one complete Annex-B H264 access unit.
5. `DisplayWebSocket.NeedH264` becomes true while any connected display client negotiated H264.

### Shared capture loop (`CaptureLoop` -> `RunCaptureSession`)

1. Snapshot `_restartEpoch` and the current `MaxWidth`/`MaxHeight` cap.
2. `using var wgcCapture = new WgcScreenCapture()`; if unavailable,
   `using var dxgiCapture = new DxgiScreenCapture()`; check each `IsAvailable`.
3. Screen size from the active backend's `CaptureSize` (WGC/DXGI) or
   `Screen.PrimaryScreen.Bounds` (GDI); saved as `screenSize` for the session.
4. Output size = the live screen scaled **uniformly** to fit the `MaxWidth × MaxHeight` cap box,
   preserving aspect ratio and never upscaling:
   `scale = min(1, MaxWidth/screenW, MaxHeight/screenH)`. So a 1080p screen with `MaxHeight=720`
   streams 1280×720; the same screen with `MaxHeight=1080` streams at native 1920×1080; a 16:10
   screen at 1200p with `MaxHeight=1080` streams 1728×1080. Width and height are rounded down
   to even values via `MakeEven` because NV12/H264 requires 4:2:0-compatible dimensions.
5. Allocate `srcImage` (`Format32bppArgb`) and, if resizing, `scaledImage` (`Format24bppRgb`).
6. Resolve the JPEG codec (`GetJpegCodec`) and set `Encoder.Quality = 60L`.
7. Per frame:
   - `wgcCapture.TryCapture(srcImage)`, `dxgiCapture.TryCapture(srcImage)`, or `srcGraphics.CopyFromScreen(...)`.
   - If resizing: `DrawImage` into `scaledImage`.
   - If `Volatile.Read(ref _httpClientCount) > 0 || DisplayWebSocket.NeedMjpeg`, JPEG-encode
     the scaled frame (`scaledImage.Save(...)` or `srcImage.Save(...)`). H264-only sessions
     skip JPEG work entirely.
   - If `DisplayWebSocket.NeedH264` is true for this tick, encode H264 from the capture bitmap:
     when **not** resizing, lock `srcImage` as `Format32bppArgb` and call
     `DisplayWebSocket.EncodeH264Bgra32` (skips the BGR24 intermediate). When resizing, pack
     `scaledImage` to tightly-packed BGR24 via `BitmapToBgr24` and call
     `DisplayWebSocket.EncodeH264`. If no H264 clients are connected, call
     `DisplayWebSocket.StopH264Encoder()` so the native encoder is disposed.
   - `PublishFrame(jpeg, h264Frames)`: copy JPEG bytes to `_currentFrame`, increment
     `_frameNumber`, `Monitor.PulseAll(_frameLock)`, then broadcast JPEG and/or H264 payloads
     to display WebSocket clients. If no JPEG was produced, `_currentFrame` is not changed.
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

### Native H264 encoding (`H264MediaFoundationEncoder.cs`)

- `H264MediaFoundationEncoder.IsAvailable` returns true only on Windows when
  `CoCreateInstance(CLSID_CMSH264EncoderMFT)` succeeds.
- `Start(width, height, fps)` initializes COM/MF on the capture thread, creates the native
  H264 encoder MFT, sets the **output type before input type**, and starts streaming messages.
- Output type attributes:
  - `MF_MT_MAJOR_TYPE = MFMediaType_Video`
  - `MF_MT_SUBTYPE = MFVideoFormat_H264`
  - `MF_MT_AVG_BITRATE` — `TESLAPC_H264_BITRATE` override, else
    `clamp(width * height * fps * 0.10, 500_000, 20_000_000)`
  - `MF_MT_FRAME_RATE = fps/1`
  - `MF_MT_FRAME_SIZE = width/height`
  - `MF_MT_INTERLACE_MODE = MFVideoInterlace_Progressive`
  - `MF_MT_MPEG2_PROFILE = eAVEncH264VProfile_Base`
  - `MF_MT_PIXEL_ASPECT_RATIO = 1/1`
  - `MF_MT_MAX_KEYFRAME_SPACING` — GOP duration in 100-ns units (default one second:
    `gopFrames * 10_000_000 / fps`)
- After `SetOutputType`, `ICodecAPI` low-latency tuning (best-effort; logs and continues on
  unsupported properties):
  - `CODECAPI_AVEncCommonLowLatency = true`
  - `CODECAPI_AVLowLatencyMode = true`
  - `CODECAPI_AVEncMPVDefaultBPictureCount = 0`
  - `CODECAPI_AVEncMPVGOPSize` — `TESLAPC_H264_GOP_FRAMES`, else one second (`fps` frames)
- `RequestKeyframe()` sets `CODECAPI_AVEncVideoForceKeyFrame` before the next `ProcessInput`.
  `DisplayWebSocket` calls this when a new H264 display client connects.
- Input type is `MFVideoFormat_NV12`. `Bgr24ToNv12Converter` performs BT.601 BGR/BGRA → NV12 in
  managed code (scalar 4-pixel unrolled loops; reusable `_nv12Scratch` buffer per encoder).
- `EncodeFrame(byte[] bgr24)` and `EncodeBgra32Frame(IntPtr scan0, int stride)` both convert to
  NV12 then call shared `EncodePreparedNv12()`, which drains any pending output, calls
  `ProcessInput`, then drains output again. Each returned `byte[]` is one complete
  `MFVideoFormat_H264` sample/access unit with start codes and interleaved SPS/PPS.
- Output draining calls `IMFTransform.ProcessOutput` with an unmanaged
  `MFT_OUTPUT_DATA_BUFFER` pointer (`IntPtr`) allocated by `Marshal.AllocHGlobal`. The struct's
  `pSample` field is also an `IntPtr`; `IMFSample` COM objects are resolved explicitly only
  after the call returns. This avoids .NET COM array/typelib marshalling for `IMFSample`.
- `Stop()` sends end-of-stream/end-streaming messages, releases COM objects, calls
  `MFShutdown()`, and uninitializes COM only when this instance initialized COM on the same
  thread.

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
| `ImageStreamingServer` | `TeslaPCInterface/ImageStreamingServer.cs` | Shared capture loop, frame buffer, HTTP MJPEG send threads, WebSocket display publishing, JPEG encoding, H264 BGRA/BGR24 feed; mutable `MaxWidth`/`MaxHeight`; `SetMaxResolution`/`RestartCapture` |
| `WgcScreenCapture` | `TeslaPCInterface/WgcScreenCapture.cs` | Windows Graphics Capture (composited DWM output, incl. MPO video + cursor); signals unavailability for DXGI fallback |
| `DxgiScreenCapture` | `TeslaPCInterface/DxgiScreenCapture.cs` | DXGI Desktop Duplication capture; signals unavailability for GDI fallback |
| `MjpegWriter` | `TeslaPCInterface/MjpegWriter.cs` | multipart/x-mixed-replace header + per-frame boundary framing |
| `DisplayWebSocket` | `TeslaPCInterface/DisplayWebSocket.cs` | `/ws/display` negotiation, PTS-prefixed frame broadcast, H264 client tracking, native encoder lifetime |
| `Bgr24ToNv12Converter` | `TeslaPCInterface/Bgr24ToNv12Converter.cs` | BT.601 BGR24/BGRA32 → NV12 conversion for MF encoder input |
| `H264MediaFoundationEncoder` | `TeslaPCInterface/H264MediaFoundationEncoder.cs` | Native Windows Media Foundation H264 encoder wrapper; NV12 feed; complete access-unit output |

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
| H264 input format | `MFVideoFormat_NV12` | `H264MediaFoundationEncoder.cs` |
| H264 output format | `MFVideoFormat_H264` | `H264MediaFoundationEncoder.cs` |
| H264 profile | `eAVEncH264VProfile_Base` (`66`) | `H264MediaFoundationEncoder.cs` |
| H264 GOP (default) | `fps` frames (~1 s) | `AppSettings.H264GopFrames`, `H264MediaFoundationEncoder.cs` |
| H264 bitrate (default) | `width * height * fps * 0.10`, clamped `500_000`–`20_000_000` bps | `H264MediaFoundationEncoder.EstimateBitrate` |
| H264 GOP override | `TESLAPC_H264_GOP_FRAMES` (`1`–`300`) | `AppSettings.cs` |
| H264 bitrate override | `TESLAPC_H264_BITRATE` (`500_000`–`20_000_000` bps) or web Settings slider (`1.5`–`12` Mbps) | `AppSettings.cs`, `config.html` |
| Scaling quality | `HighSpeed` / `Bilinear` / `SmoothingMode.None` | `ImageStreamingServer.cs` |
| Max resolution (default) | `4320×1080` (width = 4× height), `30` FPS | Constructed in `TeslaPcService` from `AppSettings.StreamHeight` |
| `AppSettings.DefaultStreamHeight` | `1080` | `TeslaPCInterface/AppSettings.cs` |
| Height clamp range | `[240, 2160]` | `AppSettings.StreamHeight` property |

## WebSocket Backpressure

`DisplayWebSocket` serializes sends per client. Each `DisplayWsClient` has one active send loop,
one pending latest packet, and one pending H264 keyframe packet. When a browser is slower than
the capture loop, older unsent display frames are replaced by the newest frame instead of
building an unbounded WebSocket backlog. H264 IDR packets are preserved ahead of delta packets
so a slow client can recover decoder state after frame drops.

## Environment Variables

| Variable | `AppSettings` constant | Default | Apply timing |
|----------|----------------------|---------|--------------|
| `TESLAPC_STREAM_HEIGHT` | `StreamHeightKey` | `1080` | Next capture-session restart (immediate if the server restarts the session) |
| `TESLAPC_DISPLAY_RENDERER` | `DisplayRendererKey` | `mjpeg` | Next browser display connection |
| `TESLAPC_DISPLAY_TRANSPORT` | `DisplayTransportKey` | `websocket` | Next browser display connection |
| `TESLAPC_NO_WGC` | (read directly in `WgcScreenCapture`) | unset | `1` disables Windows Graphics Capture (troubleshooting kill switch; forces DXGI/GDI) |

## Routes and Access Control

| Route | Protocol | Auth | Handler |
|-------|----------|------|---------|
| `/stream` | HTTP/HTTPS | none | `ImageStreamingServer.HandleStreamRequest` |
| `/ws/display` | WebSocket | none | `ImageStreamingServer.HandleDisplayWebSocketAsync` / `DisplayWebSocket.HandleClientAsync` |

## Integration Points

- Invoked by [web-server](../../server/web-server/web-server.md) routing for `/stream` and
  `/ws/display`.
- Consumed by [web-ui](../../client/web-ui/web-ui.md) via `<img>` (MJPEG) or
  `<canvas>` + WebCodecs worker (H264).
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
