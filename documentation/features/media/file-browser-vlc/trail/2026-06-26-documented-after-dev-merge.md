# Documenting File Browser & VLC Playback (after Dev merge) + restoring the entry link

- Date: 2026-06-26
- Feature: file-browser-vlc
- Related code: `TeslaPCInterface/WebServer.cs`, `TeslaPCInterface/list.html`, `TeslaPCInterface/play.html`, `TeslaPCInterface/index.html`

## Context

This feature originated on the `Dev` branch (file browser + host-side VLC launch) and arrived
via the Dev merge. The initial documentation set (written from the pre-merge unified-server
code) did not cover it, and during the merge `index.html` was reset to our version — which had
**no link to `/list.html`**, leaving the feature unreachable from the UI even though the backend
and pages were present.

## Decisions

- Restored a **Files (VLC)** button in `index.html` that navigates to `/list.html` (relative,
  same-origin, so it works over HTTP, HTTPS, and the Tesla CGNAT bypass).
- Added this feature's documentation (primary + baseline).
- Provisioned the runtime prerequisites on the test laptop: installed **VLC** and created
  **`C:\video\`** (the default browse root).

## Consequences

- Positive: the file browser / VLC playback is reachable and functional again.
- Security note (recorded in the baseline): the feature is unauthenticated and lets a client
  traverse the host filesystem via `?path=` and launch VLC on the host. Acceptable on a trusted
  LAN/hotspot; should be gated before any wider exposure.
