# File Browser & VLC Playback — Behavior Baseline

Known-good invariants. Update only when intended behavior changes.

## Invariants

- `/list.html` lists the contents of `?path=` or, when absent, **`C:\video\`**.
- Files link to `/play.html?FILENAME=<fullpath>`; subfolders link to `/list.html?path=<folder>`.
- At the browse root (`C:\video\`) a **Back to Screen** link is shown; otherwise an **Up** link
  to the parent folder.
- `/play.html` **kills any running VLC** (`taskkill /F /IM vlc.exe`), waits 2 s, then launches
  `vlc.exe -vvv "<file>" --fullscreen` on the host. With no `FILENAME` it shows "No video file
  specified" and does not launch VLC.
- VLC runs **on the host**, full-screen — it is not streamed back through the browser.
- `text/html` responses pass through `handleHTMLReplacements` (host substitution; on port 8443,
  port/protocol upgrades).

## Access Control

- No authentication. The browser can traverse the host filesystem via `?path=` and start VLC.

## Failure Behavior

- VLC not installed at `C:\Program Files\VideoLAN\VLC\vlc.exe` → `Process.Start` throws → HTTP 500.
- Browse folder missing → `Directory.GetFiles` throws → HTTP 500.

## Prerequisites (environment, not code)

- VLC installed at the standard path.
- `C:\video\` (or the chosen `?path=`) exists.
