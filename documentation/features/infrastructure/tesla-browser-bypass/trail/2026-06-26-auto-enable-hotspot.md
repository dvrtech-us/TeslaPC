# Auto-enable Mobile Hotspot at startup

- Date: 2026-06-26
- Feature: tesla-browser-bypass
- Related code: `TeslaPCInterface/HotspotManager.cs`, `TeslaPCInterface/Program.cs`

## Context

The bypass requires the Windows Mobile Hotspot adapter to exist (to attach `100.64.0.1`). If the
hotspot was off, `Setup()` found no adapter and the bypass silently skipped — the operator had to
remember to enable the hotspot manually first.

## Decisions

- Added `HotspotManager.EnsureHotspotOn()`: uses the WinRT `NetworkOperatorTetheringManager`
  (`StartTetheringAsync` + poll `TetheringOperationalState`) via a PowerShell child process — same
  shell-out style as the SSL/netsh code, so no target-framework change. Requires an active internet
  connection profile to share and elevation (the app runs elevated).
- `Program.cs` calls it before `bypass.Setup()` (when the bypass is enabled), then waits ~2 s for
  the virtual adapter to come up. Failure is non-fatal: it logs and the bypass simply skips, as before.

## Alternatives Considered

- **Bump TFM to `net10.0-windows10.0.x` and call WinRT natively** — cleaner/type-safe, but a larger
  build change; deferred in favor of the consistent PowerShell shell-out.

## Consequences

- Positive: launching TeslaPC brings the hotspot up automatically; the Tesla can connect without a
  manual step.
- Note: Windows still **auto-disables** the hotspot when no device connects for a few minutes. A
  periodic re-enable watchdog (or disabling the idle-off setting) would keep it always-on — possible
  future work; not done here.
