# Screen Capture — Behavior Baseline

Known-good invariants for screen capture and MJPEG streaming. Update only when intended behavior changes.

## Pipeline Invariants

- **At most one capture thread** (`"ScreenCapture"`) runs at any time, regardless of client count. `EnsureCaptureRunning` is guarded by `lock (_frameLock)` and checks `IsAlive` before creating a new thread.
- The capture loop runs **only while `_clientCount > 0`**; it exits and the thread terminates when the count reaches 0.
- All clients share exactly one JPEG buffer (`_currentFrame`). The capture thread is the only writer; client threads read under `lock (_frameLock)`.
- `_frameNumber` is monotonically increasing and never reset; clients use it to detect new frames.
- A slow client **skips** intermediate frames and always sends the latest available frame.
- MJPEG response headers are written **synchronously on the HTTP handler thread** before the per-client send thread is queued (prevents http.sys 503).
- `MjpegWriter` only frames pre-encoded JPEG bytes; all encoding happens in `ImageStreamingServer`.
- Capture backends (`WgcScreenCapture`/`DxgiScreenCapture`) are scoped to one `RunCaptureSession` (created with `using`, disposed at session end).

## Capture-Source Rules

- Backend priority is **WGC → DXGI → GDI**, probed in order at session start; the first available
  backend is used for the whole session.
- WGC's `TryCapture` drains the frame pool and copies only the **newest** frame; frames whose
  content size no longer matches the session size are skipped (a session restart follows via the
  bounds check).
- `TESLAPC_NO_WGC=1` must disable WGC (constructor reports unavailable without probing the OS).
- WGC includes the cursor; DXGI/GDI do not. DRM-protected regions are black in **all** backends
  (OS-enforced; not a bug).
- GDI `CopyFromScreen` is used only when both WGC and DXGI are unavailable at session start.
- DXGI `AcquireNextFrame` uses a 16 ms timeout; a timeout publishes no frame for that tick.
- DXGI `AccessLost`/`AccessDenied` triggers `RecreateDuplication()`; the current frame is skipped.
- The staging texture is lazily created and reused; reallocated only when texture dimensions change.

## Encoding / Pacing Defaults

- JPEG quality is fixed at `60`.
- Frame pacing is best-effort via `Environment.TickCount`; overruns are not compensated across frames.
- Scaling uses fixed `Bilinear` / `HighSpeed` / no-smoothing settings.
- Output is the live screen scaled **uniformly** into the `MaxWidth × MaxHeight` cap box — aspect ratio is
  always preserved and the image is never upscaled (screens ≤ the box stream at native size).
- `MaxHeight` defaults to `AppSettings.DefaultStreamHeight` (1080). Width cap is always `4 × MaxHeight`
  so height is the binding dimension for normal and ultrawide displays.
- `_maxWidth` and `_maxHeight` are `volatile`; they may change between sessions without
  stopping connected clients.

## Session-Restart Rules

- `RunCaptureSession` returns `true` (triggering a restart) when:
  - `_restartEpoch` differs from the value snapshotted at session start (set by `SetMaxResolution` or `RestartCapture`), **or**
  - `Screen.PrimaryScreen.Bounds` no longer matches the `screenSize` captured at session start, **or**
  - the active WGC backend reports `IsAvailable == false` mid-session (monitor item closed).
- On a `true` return, `CaptureLoop` immediately calls `RunCaptureSession` again with fresh state
  (the backend chain is re-probed).
- Neither WGC nor DXGI duplication survives a display-mode switch; new backend instances are
  constructed for every session.

## Access Control

- `/stream` requires no authentication.

## Failure Behavior

- WGC init failure (pre-1903 OS, no monitor, denied) → `IsAvailable = false`, DXGI is probed next.
- DXGI init failure → `IsAvailable = false`, GDI fallback for the whole session.
- Capture/encode error on a frame → logged, frame skipped, loop continues.
- Client disconnect → write throws, caught in `StreamToClient`; `_clientCount` decremented and the response stream closed in `finally`.
- No new frame within 1000 ms → client re-checks capture-thread liveness; exits if the thread is dead.
- Headless / no active display → DXGI captures nothing (black or last frame); GDI may return a blank 800×600 desktop.
