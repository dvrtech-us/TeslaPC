# Desktop Control Panel (WinForms)

TeslaPC is a **WinForms app** (`OutputType WinExe`), not a console app. On launch it shows a
borderless, touch-friendly control panel that fills the desktop work area (leaving the taskbar),
and runs the server in the background.

## User Flow

1. Launch TeslaPC (desktop shortcut / scheduled task). The control panel opens full-screen
   (work area), acting as the laptop's "home screen".
2. The **Dashboard** tab shows live status and large controls; the **Log** tab shows the running
   output. The taskbar stays available to open/switch other apps (which the Tesla can then use).

## Technical Flow

- **`Program.Main`** (`[STAThread]`, `WinExe`): loads `.env`, then `Application.Run(new MainForm(args))`.
- **`MainForm`** (`MainForm.cs`):
  - Borderless (`FormBorderStyle.None`), `Bounds = Screen.PrimaryScreen.WorkingArea`, dark theme.
  - **Dashboard tab**: status rows (Server, HTTPS mode, Hotspot, Connected clients), the Tesla
    connect URL, and large buttons — **Toggle Hotspot**, **Restart** (server), **Minimize**, **Quit**.
  - **Log tab**: a read-only `RichTextBox`. `Console.Out` is redirected to it via `ControlWriter`,
    which also tees to the original stream (so a redirected log file still gets output). The
    `RichTextBox` is only updated while the Log tab is **visible**. While the tab is hidden, new
    lines accumulate in an in-memory `StringBuilder` buffer (cap 120 000 chars; trimmed to
    90 000 when the cap is exceeded). When the Log tab is opened, `ShowLogBuffer` rebuilds the
    `RichTextBox` from the buffer in one pass, then `AppendToBox` resumes live updates for as
    long as the tab stays open.
  - A 2 s `Timer` refreshes status from the service.
  - **Keeps the display awake** via `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED |
    ES_DISPLAY_REQUIRED)` — required so DXGI screen capture doesn't stall when the monitor would
    otherwise sleep (the desktop drops to a basic 800×600 surface and stops delivering frames).
  - The service is started on `Shown` (on a background thread, so the long startup — hotspot/netsh/
    ACME — never blocks the UI) and stopped on `FormClosing`.
- **`TeslaPcService`** (`TeslaPcService.cs`): owns the lifecycle extracted from the old `Main` —
  screen + audio capture, Tesla bypass (+ hotspot enable), firewall/HTTPS/ACME bootstrap, the
  unified web server, and the hotspot-watchdog + ACME-renewal loops (a `CancellationTokenSource`
  replaces the old console wait). Exposes status (`IsRunning`, `EnableHttps`, `HttpsHost`,
  `HotspotOn`, `ClientCount`, `PrimaryUrl`, …) and `ToggleHotspot()`.

## Key Classes

| Class | File | Responsibility |
|-------|------|----------------|
| `Program` | `Program.cs` | WinExe entry; loads `.env`; runs `MainForm` |
| `MainForm` | `MainForm.cs` | Touch control panel (Dashboard + Log), status, keep-awake |
| `TeslaPcService` | `TeslaPcService.cs` | Start/stop the server stack + background loops; status |

## Notes

- Because the panel fills the desktop, the Tesla's stream shows it as the backdrop; open apps layer
  on top (use the taskbar). Minimize the panel to reveal the normal desktop.
- The display must be on for screen capture; the keep-awake call prevents monitor sleep while
  running (also set `powercfg monitor-timeout-ac 0` for belt-and-suspenders).

## Database Schema / SQL Artifacts

None.
