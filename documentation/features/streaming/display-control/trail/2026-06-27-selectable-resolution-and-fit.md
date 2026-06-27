# Add display-resolution control and Fit-screen button

- Date: 2026-06-27
- Feature: streaming/display-control
- Related code: `TeslaPCInterface/DisplayManager.cs` (new), `TeslaPCInterface/WebServer.cs`,
  `TeslaPCInterface/ImageStreamingServer.cs`, `TeslaPCInterface/index.html`

## Context

The Tesla in-car browser renders the stream inside a fixed-size viewport. If the host PC's
desktop resolution does not match that viewport's aspect ratio, the MJPEG stream is letterboxed
or pillarboxed in the browser, wasting bandwidth and reducing apparent sharpness. There was no
way to adjust the desktop resolution from the browser without alt-tabbing on the host.

Additionally, after adding the configurable stream-height cap, a Client at 1080p cap on a
2K desktop would still receive a downscaled stream — the capture was doing unnecessary work and
the browser was upscaling back. Matching the desktop to the stream height produces a pixel-
perfect capture path.

Two goals drove this feature:

1. **Fit screen**: one click to align the desktop resolution to the stream viewport aspect and
   height, eliminating scale mismatches.
2. **Exact set**: advanced Clients can set an arbitrary supported resolution from the web UI.

## Decisions

### `DisplayManager` — minimal Win32 wrapper

A new static class `DisplayManager` was added rather than shelling out to `DisplaySwitch.exe`
or a PowerShell script. In-process Win32 P/Invoke gives synchronous control and a structured
return value. The class is limited to what is needed: enumerate modes, read current mode, set
a mode. No UI, no WinForms dependency.

`ChangeDisplaySettings` is called with `CDS_UPDATEREGISTRY` so the chosen resolution persists
across reboots and login sessions, consistent with how a user would change the resolution
manually in Display Settings. Transient-only (`CDS_TEST` / `0`) was considered but rejected
because the Tesla browser session typically outlasts a display-sleep cycle on a connected
monitor.

### `BestForAspect` — aspect-ratio matching rather than nearest-resolution

The "Fit screen" use case sends the browser viewport dimensions, not a resolution. Matching by
nearest total pixels would prefer the largest mode, ignoring aspect. Matching by aspect ratio
finds the mode that minimises letterboxing, which is the actual goal. The height constraint
`[480, maxH]` prevents accidentally switching to a very large mode (4K on a laptop is often
unusable for the host) and anchors the selection to the stream-quality cap.

On near-ties (two modes whose aspect ratios are equally close), larger area is preferred — a
1920×1080 mode is chosen over 1600×900 when both fit the aspect equally well.

### Three `/display/*` routes

`/display/info` was added as a read-only diagnostic endpoint so the web UI can show supported
modes without making a change. `/display/match` is the "Fit screen" path (aspect-ratio
matching). `/display/set` is the explicit path for future UI controls (e.g. a dropdown of all
supported modes). All three return the same JSON schema so the client can always display the
current state.

### `RestartCapture()` always called

`RestartCapture()` is called unconditionally after every set/match attempt, even if `TrySet`
returns `false`. This is because:
- A failed `TrySet` may still have caused a partial mode switch (some drivers change the mode
  before returning an error code).
- Leaving a stale DXGI session running against an inconsistent desktop is worse than an
  unnecessary session restart (which costs at most one frame of blackness).

### No UI for `/display/set` in this iteration

The **Fit screen** button maps to `/display/match`. A full mode-picker dropdown (using the
`modes` array from `/display/info`) was deferred; the `/display/set` endpoint exists and is
tested but has no UI element yet. It can be called from developer tools or a future settings
addition.

### Headless caveat documented

The headless limitation (no active display → 800×600 fallback, DXGI captures nothing) was
already present for screen capture. It is explicitly re-stated in the display-control docs
because resolution control is particularly unintuitive in the headless case: `TrySet` may
return `true` (the call succeeded) but the effective resolution becomes 800×600, not the
requested value.

## Consequences

- **Positive:** one-click aspect-ratio alignment eliminates letterboxing and the unnecessary
  downscale/upscale cycle in the capture pipeline.
- **Positive:** `RestartCapture()` prevents DXGI staging-texture crashes after a mode switch.
- **Positive:** the three-route design cleanly separates read (info), smart-set (match), and
  explicit-set (set) concerns; future UI additions only need to call the right endpoint.
- **Note:** `CDS_UPDATEREGISTRY` makes the resolution change persistent. If a Client changes
  the resolution and closes the browser without reverting, the host desktop remains at the
  new resolution. This is intentional for the Tesla use case (the desired resolution is
  a long-term preference) but may surprise Clients who expected a session-scoped change.
- **Note:** the headless / asleep-display limitation makes the feature effectively unusable
  without a physical monitor or HDMI dummy plug. This is documented in the feature page and
  the baseline.
