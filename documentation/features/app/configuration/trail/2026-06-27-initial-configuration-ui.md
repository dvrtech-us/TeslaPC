# Add in-app configuration UI (Config tab + web Settings page)

- Date: 2026-06-27
- Feature: app/configuration
- Related code: `TeslaPCInterface/AppSettings.cs` (new), `TeslaPCInterface/WebServer.cs`
  (`HandleConfig`), `TeslaPCInterface/MainForm.cs` (Config tab), `TeslaPCInterface/config.html`
  (new), `TeslaPCInterface/style.css` (`.settings`/`.field`/`.finput`/`.fhint`/`.msg`),
  `TeslaPCInterface/TeslaPcService.cs` (`VideoRoot`, `SetVideoRoot`),
  `TeslaPCInterface/MediaStreamer.cs` (`Root`)

## Context

The four core settings (`TESLAPC_HTTPS_HOST`, `TESLAPC_CF_TOKEN`, `TESLAPC_ACME_EMAIL`,
`TESLAPC_VIDEO_ROOT`) previously required hand-editing `.env` files and restarting the process.
This was error-prone for the `CF_TOKEN` secret and inconvenient when changing the video folder
between sessions. The goal was to expose these settings in the existing control surfaces —
the WinForms panel and the web UI — without requiring a full process restart for common
changes.

## Decisions

### Persist to `%ProgramData%\TeslaPC\.env` (not the app-directory `.env`)

`Program.LoadDotEnv` already reads both files (app-dir first, then `%ProgramData%`). Writing
to `%ProgramData%\TeslaPC\.env` keeps the app-directory file as a static template/default and
avoids overwriting a file that may sit in a read-only install location. This also means the
settings written by the UI are readable by future process restarts without any other change to
the startup sequence.

### `AppSettings.Save` rewrites the file in-place (preserve comments / unrelated lines)

A naive rewrite that only stored the four touched keys would destroy any other entries or
comments already in the file. The in-place approach — replace matching `KEY=value` lines,
append new ones — is less brittle for users who have added extra keys manually or who have
comments in the file for self-documentation.

### Write-only Cloudflare token on the web endpoint

Returning the token in `GET /config` would expose a secret over an unauthenticated HTTP/HTTPS
endpoint. The decision was to return only `cfTokenSet: bool`, indicating whether a token is
configured, and never its value. On `POST /config`, a blank `cfToken` field is interpreted as
"leave unchanged" so Clients do not have to re-enter the token every time they save other
settings. The WinForms token field uses `UseSystemPasswordChar` for the same reason (masks on
screen; cleared after save).

### Video folder applies live; host / cert / email apply on next service restart

The video root (`_media.Root`) is a mutable property on `MediaStreamer` — changing it
immediately affects which folder the file browser serves. No component caches it elsewhere,
so the change is instant and safe under concurrent requests.

Hostname, Cloudflare token, and ACME email are read by `TeslaPcService` only during
construction (they are passed into `AcmeCertificateManager` and `SslCertificateBootstrap`).
Re-reading them mid-run would require re-running the ACME/cert-binding flow, which is
heavyweight and may fail. The existing Dashboard **Restart** button already destroys and
re-creates `TeslaPcService`, which achieves exactly the right effect — the saved env values
are live in the process environment (mirrored by `AppSettings.Save`), and the new service
instance picks them up on construction. A full process exit is not needed.

### Unauthenticated `/config` endpoint

The app has no authentication layer on any route; access is gated at the network level (the
hotspot). Adding authentication to `/config` alone would be inconsistent and would not
materially improve security for the threat model (anyone on the hotspot is already trusted).
The risk (token overwrite by an attacker on the same hotspot) is accepted and documented.

## Consequences

- Positive: settings are editable without touching files or restarting the process; video
  folder changes take effect immediately; the Cloudflare token can be set from the control
  panel without exposing it to the network.
- Positive: `%ProgramData%\TeslaPC\.env` becomes the canonical location for runtime
  configuration; the app-directory `.env` remains useful as a baseline/template.
- Note: `%ProgramData%\TeslaPC\` must exist before `AppSettings.Save` is first called. The
  directory is not created automatically.
- Note: the web `/config` endpoint is unauthenticated; anyone reachable on the network can
  overwrite settings including the Cloudflare API token. This is documented as an accepted
  risk for the hotspot use case.
