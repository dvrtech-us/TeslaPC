# Screen Capture — Behavior Baseline

- One shared frame slot. `/stream` clients send the latest JPEG, waiting at most `1000` ms for a new sequence number.
- Boundary string is `boundary`. Part content type is `image/jpeg`. Quality is `60`. Interval is `33` ms.
- `fit_frame(1920, 1080, 720)` is `1280×720`. `fit_frame(1920, 1200, 1080)` is `1728×1080`. `fit_frame(800, 600, 1080)` stays `800×600` (no upscale).
- Height outside `240`–`2160` becomes `1080`.
- Backend order is WGC, DXGI, GDI. `TESLAPC_NO_WGC=1` and `--session-host` start at DXGI. A black frame or a failed grab (`Err`) selects the next backend.
- "No new frame" (`GrabOutcome::NoFrame`: DXGI wait timeout, WGC null frame) never changes the backend. The stored JPEG is published again at most every `1000` ms. A backend that has not produced a frame since it was selected is failed after `2000` ms of `NoFrame`.
- DXGI `ACCESS_LOST`, `INVALID_CALL`, and `SESSION_DISCONNECTED` reopen the duplication once in place; only a failed reopen is `Err`.
- `[Capture] <backend> frame WxH` prints only at `TESLAPC_LOG_LEVEL=debug`.
- WGC turns cursor capture off and does not require the yellow border when those calls succeed.
- Linux Wayland reads a PipeWire frame after ScreenCast `Start`. Linux X11 copies with XShm, using an XComposite pixmap when the server names one.
- A cap change or a failed session restarts capture without dropping `/stream` clients.
- `--session-host` only: before a grab, the capture thread re-attaches to the input desktop when its name changed (checked at most every 200 ms, at once after a failed grab). A switch drops the DXGI/WGC sessions first, restarts at DXGI, and clears the stored JPEG. A DXGI `Err` before the first frame after a switch is retried 5 times, 200 ms apart, before stepping down to GDI. `ERROR_BUSY` from `SetThreadDesktop` replaces the capture thread (one loop at a time; 250 ms doubling to 5 s between consecutive busy restarts).
- `grab_gdi` takes a new screen DC each grab and therefore captures the desktop its thread is attached to.
- A normal run never re-attaches the capture thread.
