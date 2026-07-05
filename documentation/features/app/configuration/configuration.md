# Configuration / Settings

Lets users read and write the core TeslaPC environment variables without hand-editing
`.env`. Settings are persisted to `%ProgramData%\TeslaPC\.env` and immediately mirrored into
the live process environment by `AppSettings.Save`. Two independent surfaces are provided: a
**Config tab** in the WinForms control panel and a **Settings page** (`/config.html`) in the
web UI.

## User Flow

### WinForms Config tab (`MainForm`)

1. Open the control panel. The third tab (after **Dashboard** and **Log**) is **Config**.
2. The tab shows five fields:
   - **Public URL (hostname)** — `TESLAPC_HTTPS_HOST`
   - **Cloudflare API token** — `TESLAPC_CF_TOKEN` (masked with `UseSystemPasswordChar`)
   - **Let's Encrypt email** — `TESLAPC_ACME_EMAIL`
   - **Video folder** — `TESLAPC_VIDEO_ROOT` with a **Browse…** button that opens a
     `FolderBrowserDialog`.
   - **Log level** (`_cfgLog` `ComboBox`) — four options: `error`, `warn`, `info`, `debug`.
     Pre-filled from `Log.LevelName` when the tab loads.
3. Click **Save settings**:
   - Calls `AppSettings.Save` with a dictionary of the changed values. The hostname and email
     are always included; the video folder and the Cloudflare token are included **only when
     non-blank** — so leaving the token box empty keeps the existing token (the omitted key is
     never written).
   - Applies the new video folder live via `_service.SetVideoRoot(path)`.
   - Calls `Log.SetLevel(selectedLevel)` to apply the new log level **immediately** (no restart).
   - Clears the token input box.
   - Displays a status message: settings saved; host/cert changes require the Dashboard
     **Restart** button (no full process restart needed).

### Web Settings page (`/config.html`)

1. From the main screen control bar or the file-browser top bar, click **Settings** →
   navigates to `/config.html`.
2. On load, the page issues `GET /config` and pre-fills the form:
   - Hostname, ACME email, video folder, and log level are shown.
   - The Cloudflare token field is a password input with placeholder
     `leave blank to keep current`; its value is **never pre-filled** (the server never
     returns the token).
   - The log-level field is a `<select name="logLevel">` with options `error`, `warn`,
     `info`, `debug`; pre-selected from the `logLevel` value returned by `GET /config`.
   - The display-codec field is a two-button choice group (`#codecChoices`, values `mjpeg` /
     `h264`) plus hidden `displayRenderer` and `displayTransport` inputs. H264 uses WebCodecs
     in the browser and the native Windows Media Foundation H264 encoder on the PC. If
     `GET /config` returns `h264Available:false`, the page changes the hint to explain that
     the native encoder was not found.
   - The stream-resolution field is a three-button choice group (`#resChoices`, values `480` /
     `720` / `1080`) plus a hidden `streamHeight` input; pre-selected from `GET /config`
     (native `<select>` popups are unreliable on the Tesla in-car browser).
   - The **Audio boost** slider (`0.5`–`6.0`, default `1.0`) pre-fills from `audioBoost` on
     `GET /config`. Applies on the next tap-to-connect (or page refresh).
3. Edit any field and click **Save**. The page posts `application/x-www-form-urlencoded` to
   `POST /config` and shows one of:
   - **Saved** — video folder, log-level, or stream-resolution change took effect immediately.
   - **Saved — restart required** — host, token, or email changed; use the Dashboard
     **Restart** button to apply cert/ACME changes (no full process restart needed).

## Technical Flow

### AppSettings (`TeslaPCInterface/AppSettings.cs`, global namespace)

`AppSettings` is a static class with no instance; all members are `const`, `static`, or
`static` computed properties.

**Constants (environment-variable keys):**

| Constant | Value |
|----------|-------|
| `HttpsHostKey` | `TESLAPC_HTTPS_HOST` |
| `CloudflareTokenKey` | `TESLAPC_CF_TOKEN` |
| `AcmeEmailKey` | `TESLAPC_ACME_EMAIL` |
| `VideoRootKey` | `TESLAPC_VIDEO_ROOT` |
| `LogLevelKey` | `TESLAPC_LOG_LEVEL` |
| `StreamHeightKey` | `TESLAPC_STREAM_HEIGHT` |
| `DisplayRendererKey` | `TESLAPC_DISPLAY_RENDERER` |
| `DisplayTransportKey` | `TESLAPC_DISPLAY_TRANSPORT` |
| `AudioBoostKey` | `TESLAPC_AUDIO_BOOST` |
| `DefaultVideoRoot` | `C:\video\` |
| `DefaultStreamHeight` | `1080` |
| `DefaultDisplayRenderer` | `mjpeg` |
| `DefaultDisplayTransport` | `websocket` |
| `DefaultAudioBoost` | `1.0` |
| `MinAudioBoost` / `MaxAudioBoost` | `0.25` / `6.0` |
| `EnvFilePath` | `%ProgramData%\TeslaPC\.env` (expanded at runtime) |

**`AudioBoost` property** — reads `Get(AudioBoostKey)`, parses as invariant-culture `double`,
clamps to `[0.25, 6.0]`. Returns `DefaultAudioBoost` (1.0) when absent or unparseable. Applied
client-side in `PCMPlayerProcessor.js` and the `index.html` / `audio-client.js` fallbacks.

**`StreamHeight` property** — reads `Get(StreamHeightKey)`, parses as an integer, clamps to
`[240, 2160]`. Returns `DefaultStreamHeight` (1080) when the key is absent, unparseable, or
out of range.

**`DisplayRenderer` property** — reads `Get(DisplayRendererKey)`, lowercases it, and returns
`"h264"` only for an exact `h264` value. All other values fall back to `"mjpeg"`.

**`DisplayTransport` property** — reads `Get(DisplayTransportKey)`, lowercases it, and returns
`"http"` only for an exact `http` value. All other values fall back to `"websocket"`.

**`Get(string key)`** — reads from `Environment.GetEnvironmentVariable(key)` (the live
process environment that `Program.LoadDotEnv` has already populated).

**`VideoRoot` property** — returns `Get(VideoRootKey)` if non-null/non-empty; otherwise
`DefaultVideoRoot`.

**`Save(IReadOnlyDictionary<string,string> values)`:**

1. Ensures the `%ProgramData%\TeslaPC\` directory exists (`Directory.CreateDirectory`) and reads
   the existing file (empty list if absent).
2. For each key/value pair in `values`:
   - Replaces any existing `KEY=…` line, or appends `KEY=value` at the end.
   - Comments and unrelated lines are preserved verbatim.
3. Writes the updated content back to `%ProgramData%\TeslaPC\.env`.
4. Mirrors each changed value into the live process via
   `Environment.SetEnvironmentVariable(key, value)`.

`Save` writes exactly the keys it is given — it has no per-key special-casing. The **write-only
token** behavior (blank = keep current) is enforced by the *callers*: both the WinForms Save
handler and `WebServer.HandleConfig` add `TESLAPC_CF_TOKEN` to the dictionary only when the
submitted value is non-blank, so an empty token field never reaches `Save` and the stored token
is left intact.

### Startup loading (`Program.LoadDotEnv`)

`Program.LoadDotEnv` (unchanged) is called at startup before `MainForm` is constructed:

1. Reads the app-directory `.env` (alongside the executable).
2. Reads `%ProgramData%\TeslaPC\.env`.
3. **Existing environment variables win** — a key already set (e.g. by a parent process or
   the Windows environment) is not overwritten by either `.env` file.

### `/config` HTTP endpoint (`WebServer.HandleConfig`)

#### `GET /config`

Returns a JSON object with the current non-secret settings:

```json
{ "httpsHost": "my.example.com", "acmeEmail": "admin@example.com", "videoRoot": "C:\\video\\", "cfTokenSet": true, "logLevel": "info", "streamHeight": 1080, "displayRenderer": "mjpeg", "displayTransport": "websocket", "h264Available": true, "audioBoost": 1.0, "version": "1.0.0" }
```

`cfTokenSet` is `true` when `TESLAPC_CF_TOKEN` is set to a non-empty value. **The token
value is never included in the response.** `logLevel` is the current level as a lowercase
string (`error`, `warn`, `info`, or `debug`) returned by `Log.LevelName`. `streamHeight` is
the current `_imageStreamer.MaxHeight` integer (default `1080`). `version` is the application
version string from `AppSettings.Version` (e.g. `"1.0.0"`); surfaced here so the web Settings
page can display it in the footer. `displayRenderer` and `displayTransport` are the current
display settings from `AppSettings`. `h264Available` is true when
`H264MediaFoundationEncoder.IsAvailable` can instantiate the native Windows H264 encoder MFT.

#### `POST /config`

Accepts `application/x-www-form-urlencoded` with fields: `httpsHost`, `acmeEmail`,
`videoRoot`, `logLevel`, `streamHeight`, `displayRenderer`, `displayTransport`, `audioBoost`,
and optionally `cfToken`.

1. Builds a dictionary, adding `httpsHost`/`acmeEmail` when present and `videoRoot`/`cfToken`
   only when non-blank (so a blank token is omitted and the stored one is preserved).
2. Calls `AppSettings.Save`.
3. If `videoRoot` was included, sets `_media.Root` to apply the new folder live.
4. If `logLevel` was included, calls `Log.SetLevel(logLevel)` to apply the new threshold
   **immediately** (no restart needed).
5. If `streamHeight` was included and parses to a value in `[240, 2160]`, calls
   `_imageStreamer.SetMaxResolution(h*4, h)` **immediately** (no restart needed). The new cap
   takes effect on the next capture-session start (triggered automatically by the epoch bump).
6. If `displayRenderer` is present, saves only `h264` or `mjpeg`; if `displayTransport` is
   present, saves only `http` or `websocket`. When either value changes, `WebServer.HandleConfig`
   calls `_imageStreamer.RestartDisplayClients("display config changed")` so connected display
   WebSocket clients reconnect and renegotiate the global display mode.
7. Sets `restartNeeded = true` if `httpsHost`, `cfToken`, or `acmeEmail` were saved.
   `logLevel` and `streamHeight` are **not** in `restartNeeded` — both take effect live.
8. Returns JSON:

```json
{ "saved": true, "restartNeeded": false }
```

### Log level — live application (no restart)

`Log.SetLevel(string?)` (in `TeslaPCInterface/Log.cs`) parses the string (accepted values:
`error`/`0`, `warn`/`warning`/`1`, `info`/`2`, `debug`/`verbose`/`3`, case-insensitive)
and updates the global `Log.Level` threshold immediately. Messages logged at a level above
the threshold are discarded before any `Console.WriteLine` call, so the change takes effect
for all subsequent log output without a restart. The level is also persisted to
`%ProgramData%\TeslaPC\.env` via `AppSettings.Save(LogLevelKey, …)` so it survives restarts.

`Log.LevelName` returns the current threshold as lowercase: `error`, `warn`, `info`, or
`debug`.

At startup, `Program.LoadDotEnv` runs and `Log.SetLevel(AppSettings.Get(AppSettings.LogLevelKey))`
is called before the server starts, so the env-file value is honoured from the first log line.

### Video folder — live application (no restart)

`MediaStreamer.Root` is the single source of truth for the video folder. `TeslaPcService`
sets `_media.Root = AppSettings.VideoRoot` at startup and exposes:

- `VideoRoot` property — returns `_media.Root`.
- `SetVideoRoot(string path)` — sets `_media.Root` immediately.

`WebServer.returnAllFilesAsHtmlLinks` (the `/list.html` file browser) reads `_media.Root`
instead of a hardcoded path, so the new folder takes effect for the next browser request
with no restart.

### Display renderer / transport — live global application

`displayRenderer` and `displayTransport` are process-wide settings. `POST /config` snapshots
the previous values before `AppSettings.Save`, compares them after save, and calls
`ImageStreamingServer.RestartDisplayClients("display config changed")` when either value changed.
`DisplayWebSocket.RestartClients` closes each connected display WebSocket; browser clients then
re-read `/config` and reconnect display in the selected mode. This avoids mixed MJPEG/H264
WebSocket clients after a codec switch. Existing legacy HTTP `/stream` clients cannot be
server-pushed and continue until their browser reloads or reconnects.

### Host / Cloudflare token / ACME email — apply on next server restart

`AppSettings.Save` mirrors the new values into the live process environment immediately, but
`TeslaPcService` re-reads them only when it is (re-)constructed. The WinForms Dashboard
**Restart** button destroys and re-creates `TeslaPcService`, which triggers:

- `AcmeCertificateManager` re-reads `TESLAPC_HTTPS_HOST` and `TESLAPC_CF_TOKEN`.
- `SslCertificateBootstrap` re-reads `TESLAPC_HTTPS_HOST`.

A full process restart is not required; the in-app Restart is sufficient.

## Key Classes / Methods

| Member | File | Responsibility |
|--------|------|----------------|
| `AppSettings` | `TeslaPCInterface/AppSettings.cs` | Constants, `Get`, `VideoRoot`, `Save`, `Version` |
| `AppSettings.Save` | `TeslaPCInterface/AppSettings.cs` | Rewrite `%ProgramData%\TeslaPC\.env`; mirror values into live env |
| `AppSettings.VideoRoot` | `TeslaPCInterface/AppSettings.cs` | Read video root from env or fall back to `C:\video\` |
| `AppSettings.StreamHeight` | `TeslaPCInterface/AppSettings.cs` | Read and clamp `TESLAPC_STREAM_HEIGHT`; default `1080` |
| `AppSettings.DisplayRenderer` | `TeslaPCInterface/AppSettings.cs` | Read `TESLAPC_DISPLAY_RENDERER`; allows only `mjpeg` or `h264` |
| `AppSettings.DisplayTransport` | `TeslaPCInterface/AppSettings.cs` | Read `TESLAPC_DISPLAY_TRANSPORT`; allows only `websocket` or `http` |
| `AppSettings.Version` | `TeslaPCInterface/AppSettings.cs` | Reads `AssemblyInformationalVersionAttribute`; strips `+git` suffix; falls back to assembly version |
| `Program.LoadDotEnv` | `TeslaPCInterface/Program.cs` | Load app-dir `.env` then `%ProgramData%` `.env` at startup |
| `WebServer.HandleConfig` | `TeslaPCInterface/WebServer.cs` | `GET /config` (safe read) and `POST /config` (write + apply) |
| `TeslaPcService.VideoRoot` | `TeslaPCInterface/TeslaPcService.cs` | Exposes `_media.Root` |
| `TeslaPcService.SetVideoRoot` | `TeslaPCInterface/TeslaPcService.cs` | Sets `_media.Root` live |
| `MediaStreamer.Root` | `TeslaPCInterface/MediaStreamer.cs` | Single source of truth for video browse root |
| `Log` | `TeslaPCInterface/Log.cs` | Global leveled logger; `SetLevel`, `LevelName`, `Error`/`Warn`/`Info`/`Debug` methods |
| `Log.SetLevel` | `TeslaPCInterface/Log.cs` | Parse and apply a new log-level threshold immediately |
| `Log.LevelName` | `TeslaPCInterface/Log.cs` | Returns current threshold as lowercase string |
| `MainForm` (Config tab) | `TeslaPCInterface/MainForm.cs` | WinForms Config tab (five fields incl. log level + Browse… + Save settings) |
| `config.html` | `TeslaPCInterface/config.html` | Web settings page (form, `GET /config` pre-fill, `POST /config` submit) |
| `style.css` | `TeslaPCInterface/style.css` | `.settings`, `.field`, `.finput`, `.fhint`, `.msg` classes for the settings form |

## Routes

| Route | Method | Protocol | Auth | Handler |
|-------|--------|----------|------|---------|
| `/config` | GET | HTTP/HTTPS | none | `WebServer.HandleConfig` — returns `{ httpsHost, acmeEmail, videoRoot, cfTokenSet, logLevel, streamHeight, displayRenderer, displayTransport, h264Available, version }` JSON |
| `/config` | POST | HTTP/HTTPS | none | `WebServer.HandleConfig` — saves settings, applies video folder / log level / stream height live, saves display renderer/transport for next display connection, returns `{ saved, restartNeeded }` JSON |
| `/config.html` | GET | HTTP/HTTPS | none | Static page served from `TeslaPCInterface/config.html` |
| `/version` | GET | HTTP/HTTPS | none | `WebServer.HandleRequest` — returns `{ "version": "<version>" }` JSON (e.g. `{ "version": "1.0.0" }`); lightweight endpoint for health checks or external tooling |

## Versioning

`AppSettings.Version` returns the application version at runtime:

1. Reads `AssemblyInformationalVersionAttribute` from the executing assembly.
2. Strips any `+git` suffix (e.g. `1.0.0+abc1234` → `1.0.0`).
3. Falls back to the assembly version if the informational attribute is absent.

The version string is set in `TeslaPCInterface.csproj` via `<Version>1.0.0</Version>`. It is
surfaced in:

| Surface | Detail |
|---------|--------|
| WinForms control panel | Muted `v1.0.0` label under the "TeslaPC" wordmark in the tab bar (`MainForm`) |
| Web Settings page (`config.html`) footer | Pre-filled by the `version` field from `GET /config` |
| `GET /config` response | `version` field in the JSON object |
| `GET /version` endpoint | `{ "version": "1.0.0" }` — lightweight standalone endpoint |

**Release convention:** tag git at the released commit as `v<Version>` (e.g. `v1.0.0`).

## Settings / Environment Keys

| Key | `AppSettings` constant | Default | Apply timing |
|-----|----------------------|---------|--------------|
| `TESLAPC_HTTPS_HOST` | `HttpsHostKey` | _(none)_ | Next server restart |
| `TESLAPC_CF_TOKEN` | `CloudflareTokenKey` | _(none)_ | Next server restart |
| `TESLAPC_ACME_EMAIL` | `AcmeEmailKey` | _(none)_ | Next server restart |
| `TESLAPC_VIDEO_ROOT` | `VideoRootKey` | `C:\video\` | **Live** (immediate) |
| `TESLAPC_LOG_LEVEL` | `LogLevelKey` | `info` | **Live** (immediate) |
| `TESLAPC_STREAM_HEIGHT` | `StreamHeightKey` | `1080` | **Live** (immediate via `POST /config`; startup value used at next session restart) |
| `TESLAPC_DISPLAY_RENDERER` | `DisplayRendererKey` | `mjpeg` | Next display connection |
| `TESLAPC_DISPLAY_TRANSPORT` | `DisplayTransportKey` | `websocket` | Next display connection |
| `TESLAPC_AUDIO_BOOST` | `AudioBoostKey` | `1.0` | Next audio connect (`index.html` tap or `play.html` start) |

## Access Control

The `/config` endpoint is **unauthenticated**, consistent with all other TeslaPC routes.
Access is controlled at the network layer (the device is reachable only via the local
hotspot in normal operation).

**Security note:** anyone who can reach the server can:

- Read the current hostname, ACME email, and video folder via `GET /config`.
- Overwrite all four settings via `POST /config`, including replacing the Cloudflare token
  and public URL.

The Cloudflare token is **never returned** by `GET /config` (only `cfTokenSet: bool`), but
it can be overwritten by a `POST`. This is acceptable for the trusted-hotspot use case;
review if network exposure widens.

## Prerequisites / Notes

- `%ProgramData%\TeslaPC\` must be writable by the process. `AppSettings.Save` creates the
  directory if needed (`Directory.CreateDirectory`) and writes the `.env` file there.
- The app-directory `.env` (legacy) is still loaded at startup but is not written by
  `AppSettings.Save`. Migrate secrets to `%ProgramData%\TeslaPC\.env` to take advantage of
  the in-app editor.
- Host / cert / email changes take effect on the next **Dashboard → Restart**; a full process
  exit and re-launch is not required.

## Database Schema

None.

## SQL Artifacts

None.
