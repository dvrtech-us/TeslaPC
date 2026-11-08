# Screen Capture — Behavior Baseline

- One shared frame slot. `/stream` clients send the latest JPEG, waiting at most `1000` ms for a new sequence number.
- Boundary string is `boundary`. Part content type is `image/jpeg`. Quality is `60`. Interval is `33` ms.
- `fit_frame(1920, 1080, 720)` is `1280×720`. `fit_frame(1920, 1200, 1080)` is `1728×1080`. `fit_frame(800, 600, 1080)` stays `800×600` (no upscale).
- Height outside `240`–`2160` becomes `1080`.
- Backend order is WGC, DXGI, GDI. `TESLAPC_NO_WGC=1` starts at DXGI. A black or failed frame selects the next backend.
- WGC turns cursor capture off and does not require the yellow border when those calls succeed.
- Linux Wayland reads a PipeWire frame after ScreenCast `Start`. Linux X11 copies with XShm, using an XComposite pixmap when the server names one.
- A cap change or a failed session restarts capture without dropping `/stream` clients.
