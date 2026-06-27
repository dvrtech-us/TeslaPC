# Hotspot Watchdog — keep it always-on

- Date: 2026-06-26
- Feature: tesla-browser-bypass
- Related code: `TeslaPCInterface/Program.cs` (watchdog loop), `TeslaPCInterface/HotspotManager.cs`

## Context

`HotspotManager` turns the hotspot on at startup, but Windows **auto-disables Mobile Hotspot**
after a few minutes with no connected device. So if the Tesla wasn't connected shortly after
launch, the hotspot would be off when the user later tried to connect.

## Decisions

- `HotspotManager.EnsureHotspotOn()` now returns `HotspotResult` (`AlreadyOn` / `TurnedOn` / `Failed`)
  so callers can tell when it actually re-enabled the radio.
- Added a background **watchdog** in `Program.cs` (runs only when the bypass is active): every 60 s
  it calls `EnsureHotspotOn()`, and if the hotspot was just turned on **or** the bypass IP
  (`100.64.0.1`) is no longer present, it waits ~2 s and re-runs `bypass.Setup()` to re-attach the
  CGNAT IP + portproxy (the virtual adapter is recreated when the hotspot cycles). Stops on shutdown.

## Consequences

- Positive: the hotspot + bypass self-heal, so the Tesla can connect at any time without manual
  steps. `Setup()` is idempotent, so re-attaching is safe.
- Negative: when idle, the hotspot will cycle off (Windows) and back on (watchdog) roughly every
  few minutes, briefly toggling the Wi-Fi radio; the cost of always-available. A `powershell` probe
  runs each 60 s. (Disabling Windows' idle-off setting instead would avoid the cycling but isn't
  exposed by a stable API.)
