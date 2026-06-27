# Desktop Control Panel — Behavior Baseline

Known-good invariants. Update only when intended behavior changes.

## Invariants

- The app is **`WinExe`** (windowed). `Program.Main` is `[STAThread]`, loads `.env`, and runs `MainForm`.
- `MainForm` is borderless and sized to `Screen.PrimaryScreen.WorkingArea` (fills the desktop, the
  taskbar stays visible). It is not topmost (other windows layer above; the panel is the backdrop).
- The server stack is started on the form's `Shown` event on a **background thread**, so the long
  startup never freezes the UI; it is stopped on `FormClosing` (waits up to 5 s).
- `Console.Out` is redirected to the Log tab and **teed** to the original stream.
- The app calls `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED)`
  on start to keep the system and display awake (screen capture stalls if the monitor sleeps).
- Dashboard status is refreshed every 2 s from `TeslaPcService` (server, HTTPS mode, hotspot,
  client count, primary URL). The Toggle-Hotspot button is hidden when the bypass is disabled.
- All server behavior (capture, web server, bypass, bootstrap, watchdog, renewal) is unchanged —
  it was moved verbatim from `Program.Main` into `TeslaPcService`.

## Controls

- **Toggle Hotspot** — turns the hotspot off (and sets `ManuallyOff` so the watchdog leaves it) or
  back on (and re-attaches the bypass).
- **Restart** — stops and re-creates the `TeslaPcService` in-process.
- **Minimize** / **Quit** — minimize to taskbar / close the app.
