# Configuration / Settings — Behavior Baseline

Known-good invariants. Update only when intended behavior changes.

## Invariants

- The six settings (`TESLAPC_HTTPS_HOST`, `TESLAPC_CF_TOKEN`, `TESLAPC_ACME_EMAIL`,
  `TESLAPC_VIDEO_ROOT`, `TESLAPC_LOG_LEVEL`, `TESLAPC_STREAM_HEIGHT`) are persisted to
  `%ProgramData%\TeslaPC\.env` by `AppSettings.Save`.
- `Program.LoadDotEnv` loads the app-directory `.env` first, then `%ProgramData%\TeslaPC\.env`.
  **Existing environment variables win** — neither file overwrites a key already set in the
  process environment.
- `GET /config` **never returns the Cloudflare token value**. It returns only `cfTokenSet:
  bool` (true when the env var is non-empty). The token is treated as write-only from the
  network's perspective.
- A blank `cfToken` leaves the existing token unchanged. This is enforced by the **callers**
  (the WinForms Save handler and `WebServer.HandleConfig`), which add `TESLAPC_CF_TOKEN` to the
  save dictionary only when the submitted value is non-blank. `AppSettings.Save` itself has no
  token special-case — it writes exactly the keys it is given, so an empty token must never be
  passed to it.
- `AppSettings.Save` preserves all unrelated lines and comments in `%ProgramData%\TeslaPC\.env`.
  It replaces existing `KEY=value` lines in-place and appends new keys; it does not truncate
  or reformat the file.
- The video folder (`TESLAPC_VIDEO_ROOT`) applies **immediately** after save without a server
  restart. `TeslaPcService.SetVideoRoot` sets `MediaStreamer.Root` live; subsequent requests
  to `/list.html` and `/play.html` use the new path.
- `MediaStreamer.Root` is the **single source of truth** for the video browse root. Neither
  `WebServer` nor any other component holds its own copy of the path.
- Host, Cloudflare token, and ACME email changes are **mirrored into the live process
  environment** by `AppSettings.Save` but are not re-read by `TeslaPcService` until it is
  re-constructed. The Dashboard **Restart** button triggers that reconstruction; a full process
  exit is not required.
- Both the WinForms Config tab and the web `/config.html` page edit the same
  `%ProgramData%\TeslaPC\.env` file via the same `AppSettings.Save` code path.
- `AppSettings.VideoRoot` returns `Environment.GetEnvironmentVariable("TESLAPC_VIDEO_ROOT")`
  when non-empty; otherwise `C:\video\` (`AppSettings.DefaultVideoRoot`).
- `TESLAPC_LOG_LEVEL` is read by `Log.SetLevel(AppSettings.Get(AppSettings.LogLevelKey))` at
  startup (after `Program.LoadDotEnv`). Default threshold when the key is absent or
  unrecognised is `Info`. Accepted parse values: `error`/`0`, `warn`/`warning`/`1`,
  `info`/`2`, `debug`/`verbose`/`3` (case-insensitive).
- `Log.SetLevel` applies the new threshold **immediately** — no restart required. It is called
  by both `POST /config` (web) and the WinForms Config-tab Save handler.
- `GET /config` returns `logLevel` (the current `Log.LevelName` lowercase string) alongside
  the other settings. `logLevel` is **not** a secret and is always included in the response.
- `logLevel` is **not** part of the `restartNeeded` flag returned by `POST /config`; it takes
  effect live.
- `GET /config` includes `streamHeight` (integer — current `_imageStreamer.MaxHeight`; default
  `1080`). `POST /config` accepts `streamHeight` (240–2160); calls
  `_imageStreamer.SetMaxResolution(h*4, h)` immediately and persists via `AppSettings.Save`.
  `streamHeight` is **not** part of `restartNeeded` — it takes effect live.
- `AppSettings.StreamHeight` clamps the env-var value to `[240, 2160]`; values outside the
  range or absent/unparseable fall back to `AppSettings.DefaultStreamHeight` (1080).
- The stream-resolution dropdown (480p / 720p / 1080p) appears in both `index.html` and
  `config.html` and posts to `POST /config` as `streamHeight`.
