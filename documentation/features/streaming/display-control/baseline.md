# Display Control — Behavior Baseline

Known-good invariants for display resolution control. Update only when intended behavior changes.

## Enumeration Invariants

- `DisplayManager.Modes()` returns only 32-bpp (`dmBitsPerPel == 32`) modes.
- Modes are deduplicated by `(Width, Height)` and returned sorted by area descending (largest first).
- `DisplayManager.Current()` reflects the live primary-display resolution at call time.

## `BestForAspect` Selection Rules

- Only modes with `Height` in `[480, maxHeight]` are considered (hard lower bound `480`).
- The selected mode minimises `|mode.Width / (double)mode.Height - targetAspect|`.
- On near-ties, the larger-area mode is preferred.
- If no mode satisfies the height constraint, `BestForAspect` returns `null` and no change is
  made (the response returns `changed: false`).

## Resolution Change Invariants

- `TrySet` calls `ChangeDisplaySettings` with `CDS_UPDATEREGISTRY` — the change persists across reboots.
- `TrySet` returns `true` only on `DISP_CHANGE_SUCCESSFUL`; any other return code yields `false`.
- `TrySet` always selects the enumerated mode whose `(Width, Height)` matches the requested
  dimensions, preserving the mode's native refresh rate and bit depth.

## Capture-Restart Invariants

- `_imageStreamer.RestartCapture()` is called **only when the resolution actually changed**
  (i.e. `TrySet` returned `true`); if nothing changed it is not called.
- `RestartCapture()` increments `_restartEpoch` via `Interlocked.Increment`, which causes the
  running capture session to exit and restart at the next frame boundary.
- A new `DxgiScreenCapture` is constructed for each restarted session; DXGI Desktop Duplication
  is never reused across a display-mode switch.

## Route Invariants

- All three `/display/*` routes return `application/json` with schema
  `{ changed: bool, width: int, height: int, modes: [{w,h}] }`.
- `/display/info` never modifies the display or the capture state.
- The handler is method-agnostic: it reads `w`/`h` from the query string, so `/display/match`
  and `/display/set` work over GET (the "Fit screen" button uses a GET `fetch`).

## Access Control

- All `/display/*` routes require no authentication, consistent with all other TeslaPC routes.

## Failure Behavior

- `BestForAspect` returns `null` → no display change; `changed: false` in response; `RestartCapture()` is **not** called.
- `TrySet` returns `false` → no display change; `changed: false` in response; `RestartCapture()` is **not** called.
- Headless / asleep display → `ChangeDisplaySettings` falls back to 800×600; DXGI captures nothing; the response may show `changed: true` but the effective resolution is 800×600.
