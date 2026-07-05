# Screen Capture / Display Streaming — Behavior Baseline

Known-good invariants for screen capture, MJPEG streaming, and optional H264 WebSocket display
streaming. Update only when intended behavior changes.

## Pipeline Invariants

- **At most one capture thread** (`"ScreenCapture"`) runs at any time, regardless of client count. `EnsureCaptureRunning` is guarded by `lock (_frameLock)` and checks `IsAlive` before creating a new thread.
- The capture loop runs **only while `_clientCount > 0`**; it exits and the thread terminates when the count reaches 0.
- HTTP `/stream` clients share exactly one JPEG buffer (`_currentFrame`). The capture thread is
  the only writer; HTTP client threads read under `lock (_frameLock)`.
- `_frameNumber` is monotonically increasing and never reset; clients use it to detect new frames.
- A slow client **skips** intermediate frames and always sends the latest available frame.
- MJPEG response headers are written **synchronously on the HTTP handler thread** before the per-client send thread is queued (prevents http.sys 503).
- `MjpegWriter` only frames pre-encoded JPEG bytes; all encoding happens in `ImageStreamingServer`.
- `DxgiScreenCapture` is scoped to one `RunCaptureSession` (created with `using`, disposed at session end).
- `/ws/display` sends a text format message first, then binary packets with an 8-byte
  little-endian host PTS prefix.
- `/ws/display?renderer=mjpeg` sends JPEG payloads. `/ws/display?renderer=h264` sends complete
  `MFVideoFormat_H264` access units from `H264MediaFoundationEncoder`.
- H264 is only negotiated when `H264MediaFoundationEncoder.IsAvailable` is true; otherwise the
  server falls back to MJPEG for that client.
- `DisplayWebSocket.NeedH264` is checked every capture tick. The native H264 encoder exists only
  while at least one H264 display WebSocket client is connected.

## Capture-Source Rules

- DXGI is preferred. GDI `CopyFromScreen` is used only when `DxgiScreenCapture.IsAvailable` is false at session start.
- DXGI `AcquireNextFrame` uses a 16 ms timeout; a timeout publishes no frame for that tick.
- DXGI `AccessLost`/`AccessDenied` triggers `RecreateDuplication()`; the current frame is skipped.
- The staging texture is lazily created and reused; reallocated only when texture dimensions change.

## Encoding / Pacing Defaults

- JPEG quality is fixed at `60`.
- Frame pacing is best-effort via `Environment.TickCount`; overruns are not compensated across frames.
- Scaling uses fixed `Bilinear` / `HighSpeed` / no-smoothing settings.
- Output is the live screen scaled **uniformly** into the `MaxWidth × MaxHeight` cap box — aspect ratio is
  always preserved and the image is never upscaled (screens ≤ the box stream at native size).
- Output width and height are even numbers so H264 NV12 encoding is valid.
- `MaxHeight` defaults to `AppSettings.DefaultStreamHeight` (1080). Width cap is always `4 × MaxHeight`
  so height is the binding dimension for normal and ultrawide displays.
- `_maxWidth` and `_maxHeight` are `volatile`; they may change between sessions without
  stopping connected clients.
- H264 uses `MFVideoFormat_NV12` input and `MFVideoFormat_H264` output through the native Windows
  Media Foundation H264 encoder MFT; ffmpeg is not used for the live display encoder.
- H264 baseline profile (`eAVEncH264VProfile_Base`) is used. Bitrate is clamped to
  `1_500_000..12_000_000` bps by `H264MediaFoundationEncoder.EstimateBitrate`.
- `H264MediaFoundationEncoder` must not marshal `MFT_OUTPUT_DATA_BUFFER[]` through .NET COM
  interop. `IMFTransform.ProcessOutput` receives an unmanaged `MFT_OUTPUT_DATA_BUFFER` pointer,
  and COM samples/events are released explicitly.

## Session-Restart Rules

- `RunCaptureSession` returns `true` (triggering a restart) when:
  - `_restartEpoch` differs from the value snapshotted at session start (set by `SetMaxResolution` or `RestartCapture`), **or**
  - `Screen.PrimaryScreen.Bounds` no longer matches the `screenSize` captured at session start.
- On a `true` return, `CaptureLoop` immediately calls `RunCaptureSession` again with fresh state.
- DXGI Desktop Duplication cannot survive a display-mode switch; a new `DxgiScreenCapture` is
  constructed for every session.

## Access Control

- `/stream` requires no authentication.
- `/ws/display` requires no authentication.

## Failure Behavior

- DXGI init failure → `IsAvailable = false`, GDI fallback for the whole session.
- Capture/encode error on a frame → logged, frame skipped, loop continues.
- Native H264 encoder unavailable → H264 clients are negotiated as MJPEG.
- Native H264 encode error on a frame → logged; JPEG continues publishing and that H264 payload
  is skipped.
- Client disconnect → write throws, caught in `StreamToClient`; `_clientCount` decremented and the response stream closed in `finally`.
- No new frame within 1000 ms → client re-checks capture-thread liveness; exits if the thread is dead.
- Headless / no active display → DXGI captures nothing (black or last frame); GDI may return a blank 800×600 desktop.
