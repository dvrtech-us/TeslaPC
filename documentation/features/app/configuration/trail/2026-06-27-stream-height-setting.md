# Add TESLAPC_STREAM_HEIGHT to configuration surfaces

- Date: 2026-06-27
- Feature: app/configuration
- Related code: `TeslaPCInterface/AppSettings.cs`, `TeslaPCInterface/WebServer.cs`,
  `TeslaPCInterface/index.html`, `TeslaPCInterface/config.html`

## Context

The MJPEG stream resolution cap was previously hardcoded to 1280×720 and was not adjustable
at runtime. Clients on fast connections wanted 1080p sharpness; Clients on slower connections
wanted a way to drop to 480p without restarting the server. Adding `TESLAPC_STREAM_HEIGHT` to
the configuration system was the minimal change to make the cap tunable from the same surfaces
already used for other settings.

## Decisions

### `AppSettings.StreamHeightKey` / `AppSettings.StreamHeight`

`TESLAPC_STREAM_HEIGHT` is handled exactly like the other tunable keys: a string constant
(`StreamHeightKey`), a typed computed property (`StreamHeight`) that parses and clamps the
value, and a `DefaultStreamHeight` constant (`1080`). The clamp range `[240, 2160]` covers
240p through 4K; values outside the range fall back to the default rather than crashing.

The default was set to `1080` (raised from the previous hardcoded `720`) because 1080p is the
native resolution of the most common laptop displays — streaming at native avoids the quality
loss of a lossy downscale.

### `GET /config` — `streamHeight` field

`streamHeight` is added to the JSON response as the current `_imageStreamer.MaxHeight`. This
is the live value (already changed by a previous `POST /config`) rather than the env-var value,
so the UI accurately reflects the in-flight state.

### `POST /config` — `streamHeight` field, live application

`streamHeight` is processed alongside `logLevel` as a live-apply field (not in `restartNeeded`).
When present and in range, `_imageStreamer.SetMaxResolution(h*4, h)` is called immediately;
`AppSettings.Save` persists it so the value survives a restart.

Width is derived as `4 × height` (not separately controllable) because this covers any aspect
ratio up to 4:1 — the height cap alone determines quality for any display geometry, and a
separate width control would add complexity without benefit for the target use case (primary
monitor streaming).

### Dropdowns in both UIs

A three-option dropdown (480p / 720p / 1080p) was added to `index.html` (main screen control
bar) and `config.html` (Settings page). Three fixed choices rather than a free-entry numeric
field keeps the UI simple and avoids invalid values reaching the server. The selected option
maps to the height value sent as `streamHeight` in the `POST /config` body.

## Consequences

- **Positive:** resolution is adjustable live from any connected browser; no server restart
  required.
- **Positive:** the setting persists across restarts via `%ProgramData%\TeslaPC\.env`.
- **Note:** the env-var value (`TESLAPC_STREAM_HEIGHT`) is only read at startup (when
  `TeslaPcService` constructs `ImageStreamingServer`). A live `POST /config` changes the
  in-memory cap directly; if the process restarts the persisted env-var value is used, so they
  remain consistent as long as `AppSettings.Save` is called alongside `SetMaxResolution`.
