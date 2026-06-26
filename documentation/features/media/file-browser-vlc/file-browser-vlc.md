# File Browser & VLC Playback

A server-side file browser that lists video files on the host and launches a chosen file in
**VLC** (full-screen) on the host machine. Reached from the main UI via the **Files (VLC)**
button. Came from the `Dev` line of development; integrated during the Dev merge.

## User Flow

1. From the main page, click **Files (VLC)** → navigates to `/list.html`.
2. `/list.html` shows the files and folders under `C:\video\` (or a `?path=` folder). Folders
   are links that drill in; an **Up** link goes to the parent; **Back to Screen** returns to `/`.
3. Clicking a file opens `/play.html?FILENAME=<path>`, which **launches VLC full-screen on the
   host** playing that file (any previously running VLC is killed first).

## Technical Flow

All handling is in `WebServer.HandleHttpAsync` (static file serving + HTML templating) plus
helpers in `WebServer.cs`.

### `/list.html` (file browser)

- Query: `?path=<folder>` (defaults to `C:\video\` when absent).
- `returnAllFilesAsHtmlLinks(path)` builds the markup:
  - each file → `<a href="/play.html?FILENAME=<fullpath>">`
  - each subfolder → `<a href="/list.html?path=<folder>">`
  - an **Up** link to the parent (unless already at `C:\video\`), else a **Back to Screen** link.
- The result is substituted into the `{{GUTS}}` placeholder in `list.html`.

### `/play.html` (VLC launcher)

- Query: `?FILENAME=<fullpath>`.
- If absent → page shows "No video file specified".
- Otherwise: `Process.Start("taskkill", "/F /IM vlc.exe")` to stop any running VLC, a 2 s wait,
  then `Process.Start("C:\\Program Files\\VideoLAN\\VLC\\vlc.exe", "-vvv \"<FILENAME>\" --fullscreen")`.
- The `{{VIDEO}}` placeholder is replaced with a status string.

### HTML templating (`handleHTMLReplacements`)

Applied to all `text/html` responses: replaces `//LOCALHOST` with the request host, and when the
request port is `8443` swaps `:8080→:8443`, `:8081→:8444`, `:8082→:8445`, `ws://→wss://`,
`http://→https://`.

## Key Classes / Methods

| Member | File | Responsibility |
|--------|------|----------------|
| `WebServer.HandleHttpAsync` | `TeslaPCInterface/WebServer.cs` | Serves `list.html`/`play.html`, applies templating, launches VLC |
| `WebServer.returnAllFilesAsHtmlLinks` | `TeslaPCInterface/WebServer.cs` | Builds file/folder link markup for the browser |
| `WebServer.handleHTMLReplacements` | `TeslaPCInterface/WebServer.cs` | Host/port/protocol substitution for HTML responses |
| `list.html` | `TeslaPCInterface/list.html` | File-browser page template (`{{GUTS}}`) |
| `play.html` | `TeslaPCInterface/play.html` | Playback status page template (`{{VIDEO}}`) |
| `style.css` | `TeslaPCInterface/style.css` | Shared styles for these pages |

## Routes and Access Control

| Route | Protocol | Auth | Handler |
|-------|----------|------|---------|
| `/list.html` | HTTP/HTTPS | none | `HandleHttpAsync` (templated) |
| `/play.html` | HTTP/HTTPS | none | `HandleHttpAsync` (launches VLC) |

No authentication. **Anyone who can reach the server can browse the host's `C:\video` tree (or
any `?path=` folder) and start VLC on the host** — a notable capability to keep in mind for
network exposure.

## Constants / Environment

| Item | Value |
|------|-------|
| Default browse root | `C:\video\` |
| VLC executable path | `C:\Program Files\VideoLAN\VLC\vlc.exe` |
| VLC launch args | `-vvv "<file>" --fullscreen` |
| Pre-launch delay | 2000 ms (after killing existing VLC) |

## Prerequisites

- **VLC must be installed** at the path above (e.g. `winget install VideoLAN.VLC`).
- A **browse folder** must exist; `C:\video\` by default, or pass `?path=`.
- If VLC is missing or the folder doesn't exist, the request fails (HTTP 500) because
  `Process.Start`/`Directory.GetFiles` throw.

## Database Schema

None.

## SQL Artifacts

None.
