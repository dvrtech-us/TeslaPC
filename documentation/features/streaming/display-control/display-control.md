# Display Control

Allows the browser Client to query and change the Windows primary-display resolution from the
web interface. Changing the resolution to match the stream aspect ratio and height eliminates
the scale step in the capture pipeline (pixel-perfect capture), and aligns the desktop
workspace with the viewport seen in the browser.

## User Flow

1. On the main screen, click the **Fit screen** button in the control bar.
2. The browser measures the `.screen` element's `clientWidth` and `clientHeight` and requests
   `/display/match?w=&h=` (a GET `fetch`).
3. The server selects the desktop resolution whose aspect ratio is closest to the stream
   viewport, restricted to heights within `[480, max(480, _imageStreamer.MaxHeight)]`, and
   applies it via Win32 `ChangeDisplaySettings`.
4. The screen capture session is restarted so DXGI picks up the new desktop dimensions.
5. The server responds with `{ changed, width, height, modes }`.

Advanced Clients can also set an exact resolution by requesting `/display/set?w=&h=` directly.

## Technical Flow

### `DisplayManager` (`TeslaPCInterface/DisplayManager.cs`)

`DisplayManager` is a global static class. It has no instance; all members are static. It
calls Win32 `EnumDisplaySettings` and `ChangeDisplaySettings` via P/Invoke.

**`Mode` record** — `(int Width, int Height)`. Represents a supported display resolution.

**`Current()`** — calls `EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS)` and returns a
`Mode` for the current primary resolution.

**`Modes()`** — iterates `EnumDisplaySettings(null, 0..N)` until the call returns false;
filters to 32 bpp (`dmBitsPerPel == 32`); deduplicates by `(Width, Height)`; returns the list
sorted largest area first.

**`BestForAspect(double targetAspect, int maxHeight)`** — selects the supported `Mode` that:
- has `Height` in `[480, maxHeight]`, and
- minimises `|mode.Width / (double)mode.Height - targetAspect|`.
- On near-ties (aspect delta within a small epsilon), prefers larger area.
Returns `null` if no mode satisfies the height constraint.

**`TrySet(int w, int h)`** — finds the first `EnumDisplaySettings` mode matching `(w, h)` with
32 bpp, populates a `DEVMODE`, and calls `ChangeDisplaySettings` with `CDS_UPDATEREGISTRY`.
Returns `true` on `DISP_CHANGE_SUCCESSFUL`, `false` otherwise. Using the enumerated mode
preserves the display's native refresh rate and bit depth.

### `WebServer.HandleDisplay` — route dispatcher

`HandleDisplay` is called by the main request handler for any path starting with `/display/`.
It dispatches to one of three sub-handlers based on the sub-path:

#### `GET /display/info`

Returns the current resolution and the full list of supported modes (read-only, no change):

```json
{ "changed": false, "width": 1920, "height": 1080, "modes": [{"w": 1920, "h": 1080}, {"w": 1600, "h": 900}, ...] }
```

#### `/display/match?w=&h=` (used via GET)

1. Parse `w` and `h` from the query string (the viewport dimensions from the browser).
2. Compute `targetAspect = w / (double)h`.
3. Compute `maxH = max(480, _imageStreamer.MaxHeight)`.
4. Call `DisplayManager.BestForAspect(targetAspect, maxH)` to find the best-match mode.
5. If a mode is found and differs from the current resolution, call `DisplayManager.TrySet(mode.Width, mode.Height)`.
6. If `TrySet` succeeded, call `_imageStreamer.RestartCapture()` (bumps `_restartEpoch`; the
   running DXGI session sees the epoch change and re-initialises with the new dimensions).
7. Return `{ changed, width, height, modes }` where `changed` reflects whether `TrySet` was
   called and succeeded.

#### `/display/set?w=&h=` (used via GET)

1. Parse `w` and `h` from the query string.
2. Call `DisplayManager.TrySet(w, h)` directly.
3. If `TrySet` succeeded, call `_imageStreamer.RestartCapture()`.
4. Return `{ changed, width, height, modes }`.

### Capture-session restart after resolution change

`RestartCapture()` on `ImageStreamingServer` increments `_restartEpoch` via `Interlocked`.
The running capture session snapshots this epoch at its start; when it sees a changed value it
returns `true`, causing `CaptureLoop` to construct a fresh `DxgiScreenCapture` with the new
desktop dimensions. No clients are dropped during the restart — the old session continues
publishing frames until the new one takes over.

DXGI Desktop Duplication cannot survive a display-mode switch: the GPU reallocates the desktop
texture at the new size, and the staging-texture size check in
`DxgiScreenCapture.EnsureStagingTexture` would throw on a mismatch. The `RestartCapture()`
call ensures the new session always starts with a correctly-sized staging texture.

### "Fit screen" button (`index.html`)

The button reads `document.querySelector('.screen').clientWidth` and `.clientHeight` and
requests `/display/match?w={w}&h={h}` (GET `fetch`). It does not require any user confirmation. The stream
automatically updates within one capture-session restart cycle (typically one or two frames of
black or no output while DXGI re-initialises).

## Key Classes

| Class | File | Responsibility |
|-------|------|----------------|
| `DisplayManager` | `TeslaPCInterface/DisplayManager.cs` | Win32 `EnumDisplaySettings` / `ChangeDisplaySettings` wrapper; `Modes()`, `Current()`, `BestForAspect()`, `TrySet()` |
| `WebServer` | `TeslaPCInterface/WebServer.cs` | `HandleDisplay` dispatcher; `/display/info`, `/display/match`, `/display/set` sub-handlers |
| `ImageStreamingServer` | `TeslaPCInterface/ImageStreamingServer.cs` | `RestartCapture()` — bumps `_restartEpoch` to force a new DXGI session |

## Routes

| Route | Method | Protocol | Auth | Handler |
|-------|--------|----------|------|---------|
| `/display/info` | GET | HTTP/HTTPS | none | `WebServer.HandleDisplay` — returns current resolution + supported modes |
| `/display/match` | GET | HTTP/HTTPS | none | `WebServer.HandleDisplay` — sets best-aspect-match resolution; restarts capture (method-agnostic; reads `w`/`h` from query) |
| `/display/set` | GET | HTTP/HTTPS | none | `WebServer.HandleDisplay` — sets exact resolution; restarts capture (method-agnostic; reads `w`/`h` from query) |

All routes return `application/json`:

```json
{ "changed": true, "width": 1920, "height": 1080, "modes": [{"w": 1920, "h": 1080}, ...] }
```

## Constants / Environment

| Item | Value | Notes |
|------|-------|-------|
| `BestForAspect` minimum height | `480` | Hard lower bound; prevents very small modes |
| `BestForAspect` maximum height | `max(480, _imageStreamer.MaxHeight)` | Targets a mode near the stream cap height |
| `ChangeDisplaySettings` flag | `CDS_UPDATEREGISTRY` | Persists the resolution across reboots |
| Required bpp | `32` | Only 32-bpp modes are enumerated or selected |
| Modes sort order | Largest area first | `DisplayManager.Modes()` returns largest first |

No additional environment variables are introduced by this feature. The effective maximum
height for `BestForAspect` is derived from `AppSettings.StreamHeight` (via
`_imageStreamer.MaxHeight`).

## Failure / Notes

- If `BestForAspect` finds no mode satisfying the height constraint, no change is made;
  the response returns `{ changed: false, width: current, height: current, modes: [...] }`.
- If `ChangeDisplaySettings` returns any code other than `DISP_CHANGE_SUCCESSFUL`, `TrySet`
  returns `false`; the response reflects `changed: false`.
- `RestartCapture()` is called only when the resolution actually changed (`TrySet` returned
  `true`); when nothing changes, the existing capture session keeps running untouched.
- **Headless / asleep display caveat:** `ChangeDisplaySettings` on a system with no active
  display (no monitor connected, lid closed without a dummy plug) falls back to 800×600 and
  DXGI captures nothing useful. An HDMI dummy plug or a physically attached monitor is required
  for resolution changes to have any effect. This is the same headless limitation that applies
  to screen capture generally.
- The `/display/match` and `/display/set` endpoints are unauthenticated, consistent with all
  other TeslaPC routes. Any Client who can reach the server can change the display resolution.

## Integration Points

- Consumes `ImageStreamingServer.RestartCapture()` — see
  [screen-capture](../screen-capture/screen-capture.md).
- `BestForAspect` upper-height bound is derived from `_imageStreamer.MaxHeight`, which is set
  by `AppSettings.StreamHeight` and configurable via the [configuration](../../app/configuration/configuration.md) feature.
- The **Fit screen** button is part of the [web-ui](../../client/web-ui/web-ui.md) control bar.

## Database Schema

None.

## SQL Artifacts

None.
