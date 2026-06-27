# Convert console app to a WinForms control panel

- Date: 2026-06-27
- Feature: app/control-panel
- Related code: `Program.cs`, `MainForm.cs`, `TeslaPcService.cs`, `TeslaPCInterface.csproj`

## Context

The app ran as a console app and printed status/URLs to the console. The user asked for a proper
windowed app with large touch-friendly controls that "replaces the desktop" while keeping the taskbar.

## Decisions

- `OutputType` → **`WinExe`**; `Program.Main` becomes a `[STAThread]` WinForms entry that loads
  `.env` and runs `MainForm`.
- **`MainForm`**: borderless, sized to the work area (keeps taskbar), dark touch UI with a
  **Dashboard** tab (status + large Toggle-Hotspot/Restart/Minimize/Quit buttons + connect URL) and
  a **Log** tab (console teed into a `RichTextBox`).
- Extracted the entire server lifecycle from `Main` into **`TeslaPcService`** (start/stop + status +
  background loops via a `CancellationTokenSource`), so the UI can host and control it, and the
  server logic stays unchanged.
- Service start runs on a background thread (startup does blocking netsh/PowerShell/ACME work).
- Added `SetThreadExecutionState` keep-awake after hitting display-sleep → DXGI capture stalling
  (desktop fell back to 800×600 and stopped delivering frames).

## Verification note

Deployed and confirmed running on the laptop (process up, HTTP 200, startup banner shows bypass +
trusted HTTPS). A screen-capture screenshot of the rendered panel could not be grabbed remotely
because the laptop monitor had slept and injected input over SSH lands in session 0, not the
console session — the keep-awake change prevents that going forward.

## Consequences

- Positive: a real touch control panel; status at a glance; log without a console window.
- Note: since the panel fills the desktop, the Tesla streams it as the backdrop; minimize or open
  apps over it. Screen capture still requires the display to be on (now enforced by keep-awake).
