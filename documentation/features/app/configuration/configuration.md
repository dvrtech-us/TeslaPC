# Configuration / Settings

Lets users read and write the four core TeslaPC environment variables without hand-editing
`.env`. Settings are persisted to `%ProgramData%\TeslaPC\.env` and immediately mirrored into
the live process environment by `AppSettings.Save`. Two independent surfaces are provided: a
**Config tab** in the WinForms control panel and a **Settings page** (`/config.html`) in the
web UI.

## User Flow

### WinForms Config tab (`MainForm`)

1. Open the control panel. The third tab (after **Dashboard** and **Log**) is **Config**.
2. The tab shows four fields:
   - **Public URL (hostname)** — `TESLAPC_HTTPS_HOST`
   - **Cloudflare API token** — `TESLAPC_CF_TOKEN` (masked with `UseSystemPasswordChar`)
   - **Let's Encrypt email** — `TESLAPC_ACME_EMAIL`
   - **Video folder** — `TESLAPC_VIDEO_ROOT` with a **Browse…** button that opens a
     `FolderBrowserDialog`.
3. Click **Save settings**:
   - Calls `AppSettings.Save` with a dictionary of the changed values. The hostname and email
     are always included; the video folder and the Cloudflare token are included **only when
     non-blank** — so leaving the token box empty keeps the existing token (the omitted key is
     never written).
   - Applies the new video folder live via `_service.SetVideoRoot(path)`.
   - Clears the token input box.
   - Displays a status message: settings saved; host/cert changes require the Dashboard
     **Restart** button (no full process restart needed).

### Web Settings page (`/config.html`)

1. From the main screen control bar or the file-browser top bar, click **Settings** →
   navigates to `/config.html`.
2. On load, the page issues `GET /config` and pre-fills the form:
   - Hostname, ACME email, and video folder are shown as text.
   - The Cloudflare token field is a password input with placeholder
     `leave blank to keep current`; its value is **never pre-filled** (the server never
     returns the token).
3. Edit any field and click **Save**. The page posts `application/x-www-form-urlencoded` to
   `POST /config` and shows one of:
   - **Saved** — video folder change took effect immediately.
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
| `DefaultVideoRoot` | `C:\video\` |
| `EnvFilePath` | `%ProgramData%\TeslaPC\.env` (expanded at runtime) |

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
{ "httpsHost": "my.example.com", "acmeEmail": "admin@example.com", "videoRoot": "C:\\video\\", "cfTokenSet": true }
```

`cfTokenSet` is `true` when `TESLAPC_CF_TOKEN` is set to a non-empty value. **The token
value is never included in the response.**

#### `POST /config`

Accepts `application/x-www-form-urlencoded` with fields: `httpsHost`, `acmeEmail`,
`videoRoot`, and optionally `cfToken`.

1. Builds a dictionary, adding `httpsHost`/`acmeEmail` when present and `videoRoot`/`cfToken`
   only when non-blank (so a blank token is omitted and the stored one is preserved).
2. Calls `AppSettings.Save`.
3. If `videoRoot` was included, sets `_media.Root` to apply the new folder live.
4. Sets `restartNeeded = true` if `httpsHost`, `cfToken`, or `acmeEmail` were saved.
5. Returns JSON:

```json
{ "saved": true, "restartNeeded": false }
```

### Video folder — live application (no restart)

`MediaStreamer.Root` is the single source of truth for the video folder. `TeslaPcService`
sets `_media.Root = AppSettings.VideoRoot` at startup and exposes:

- `VideoRoot` property — returns `_media.Root`.
- `SetVideoRoot(string path)` — sets `_media.Root` immediately.

`WebServer.returnAllFilesAsHtmlLinks` (the `/list.html` file browser) reads `_media.Root`
instead of a hardcoded path, so the new folder takes effect for the next browser request
with no restart.

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
| `AppSettings` | `TeslaPCInterface/AppSettings.cs` | Constants, `Get`, `VideoRoot`, `Save` |
| `AppSettings.Save` | `TeslaPCInterface/AppSettings.cs` | Rewrite `%ProgramData%\TeslaPC\.env`; mirror values into live env |
| `AppSettings.VideoRoot` | `TeslaPCInterface/AppSettings.cs` | Read video root from env or fall back to `C:\video\` |
| `Program.LoadDotEnv` | `TeslaPCInterface/Program.cs` | Load app-dir `.env` then `%ProgramData%` `.env` at startup |
| `WebServer.HandleConfig` | `TeslaPCInterface/WebServer.cs` | `GET /config` (safe read) and `POST /config` (write + apply) |
| `TeslaPcService.VideoRoot` | `TeslaPCInterface/TeslaPcService.cs` | Exposes `_media.Root` |
| `TeslaPcService.SetVideoRoot` | `TeslaPCInterface/TeslaPcService.cs` | Sets `_media.Root` live |
| `MediaStreamer.Root` | `TeslaPCInterface/MediaStreamer.cs` | Single source of truth for video browse root |
| `MainForm` (Config tab) | `TeslaPCInterface/MainForm.cs` | WinForms Config tab (four fields + Browse… + Save settings) |
| `config.html` | `TeslaPCInterface/config.html` | Web settings page (form, `GET /config` pre-fill, `POST /config` submit) |
| `style.css` | `TeslaPCInterface/style.css` | `.settings`, `.field`, `.finput`, `.fhint`, `.msg` classes for the settings form |

## Routes

| Route | Method | Protocol | Auth | Handler |
|-------|--------|----------|------|---------|
| `/config` | GET | HTTP/HTTPS | none | `WebServer.HandleConfig` — returns `{ httpsHost, acmeEmail, videoRoot, cfTokenSet }` JSON |
| `/config` | POST | HTTP/HTTPS | none | `WebServer.HandleConfig` — saves settings, applies video folder live, returns `{ saved, restartNeeded }` JSON |
| `/config.html` | GET | HTTP/HTTPS | none | Static page served from `TeslaPCInterface/config.html` |

## Settings / Environment Keys

| Key | `AppSettings` constant | Default | Apply timing |
|-----|----------------------|---------|--------------|
| `TESLAPC_HTTPS_HOST` | `HttpsHostKey` | _(none)_ | Next server restart |
| `TESLAPC_CF_TOKEN` | `CloudflareTokenKey` | _(none)_ | Next server restart |
| `TESLAPC_ACME_EMAIL` | `AcmeEmailKey` | _(none)_ | Next server restart |
| `TESLAPC_VIDEO_ROOT` | `VideoRootKey` | `C:\video\` | **Live** (immediate) |

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
