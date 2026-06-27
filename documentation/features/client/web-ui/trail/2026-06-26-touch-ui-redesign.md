# Touch-First In-Car UI Redesign

- Date: 2026-06-26
- Feature: web-ui (and media/file-browser-vlc)
- Related code: `TeslaPCInterface/style.css`, `index.html`, `list.html`, `play.html`, `WebServer.cs` (`returnAllFilesAsHtmlLinks`)

## Context

After the Dev merge, the file browser had no real UI (raw `<h1>` headings and stacked buttons),
and the main page used ad-hoc inline styles. The app is operated from the **Tesla in-car touch
screen**, which needs large tap targets and a glare-friendly dark theme.

## Decisions

- Introduced a shared **`style.css`** design system (no external resources — CSP/offline safe):
  near-black ground (`#0b0d11`), layered surfaces, one calm blue accent (`#3b82f6`), amber for
  folders (`#f5b945`); large controls (56–96 px) with active-press feedback; CSS variables.
- **Main page (`index.html`):** the stream fills a `.screen` area (`object-fit: contain`) with a
  fixed bottom `.controlbar` (Start playback / Keyboard / Files). Inline styles removed in favor
  of `/style.css`; added a no-zoom `viewport` meta. All streaming/audio/input JS and element IDs
  left unchanged.
- **File browser:** `returnAllFilesAsHtmlLinks` now emits a sticky nav (Screen / Up / path) and a
  responsive `.grid` of `.tile` cards (folders first with `📁`, then videos with `🎬` + extension
  badge). Names HTML-encoded, query paths URL-encoded (fixes spaces in filenames).
- **`play.html`:** a centered "Now Playing" card with a Back-to-Screen button.

## Alternatives Considered

- **Apply Bridgehead corporate branding** — rejected; this is a personal in-car project, so a
  dashboard-appropriate dark theme fits better than a corporate identity. (Easily revisited if a
  branded look is wanted.)

## Consequences

- Positive: consistent, thumb-friendly UI across all pages; the file browser is usable in a car.
- Note: icons are Unicode emoji (no icon font) to stay self-contained under the Tesla browser's CSP.
