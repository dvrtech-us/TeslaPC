# SQLite Playback Progress Database (initial)

- Date: 2026-06-27
- Feature: media/media-library
- Related code: `TeslaPCInterface/MediaLibrary.cs`, `TeslaPCInterface/MediaStreamer.cs`,
  `TeslaPCInterface/TeslaPcService.cs`, `TeslaPCInterface/WebServer.cs`

## Context

Before this change, every video started from the beginning on every play. There was no
persistent record of which files had been watched or how far a Client had progressed. On a
slow connection or with a long movie, returning to a file after a Tesla session ended meant
scrubbing manually with the seek bar. The file browser also showed all files identically —
there was no visual indication of partially-watched or completed content.

The goals were:

1. **Auto-resume** interrupted playback from where the Client left off.
2. **Permanent watched flag** so completed files are visually distinguished.
3. **Play count** for lightweight usage tracking.
4. **Single-query file-browser decoration** — badge every file in `/list.html` without an
   N+1 query pattern.

## Decisions

### SQLite via `Microsoft.Data.Sqlite`

A relational store was chosen over a flat JSON/CSV file because:

- Parameterized upserts are concise and safe (no manual escaping).
- `COLLATE NOCASE` on the primary key handles case-insensitive Windows paths naturally.
- The schema can grow (additional columns, future tables) without a file-format migration.

`Microsoft.Data.Sqlite` was chosen as the official .NET SQLite binding (maintained by
Microsoft, aligns with the project's C# / .NET 10 stack). `SQLitePCLRaw.bundle_e_sqlite3`
2.1.11 is pinned as the native backing library. An advisory (GHSA-2m69-gcr7-jv3q) exists on
`SQLitePCLRaw.lib.e_sqlite3` with no patched release; risk is nil for this local,
parameterized, single-user DB. The advisory is recorded in the feature doc and in
`AGENTS.md` Current Known Gaps.

### Single connection held open for the process lifetime

Opening and closing a connection per operation would add latency with no benefit (the DB is
a local file with no concurrent users). The connection is opened in the constructor and held
until the process exits. All methods use the shared connection.

### `WatchedFraction = 0.95` — why not 1.0?

End-credits, post-credit scenes, and some containers report a duration slightly longer than
the actual video content, so `position / duration` rarely reaches exactly `1.0` on a natural
play-through. `0.95` is the same threshold used by common media-center applications (e.g.
Plex, Kodi). The threshold is a named constant to make future adjustment straightforward.

### Sticky watched flag (never cleared by `SaveProgress`)

A Client who finishes a file should not have it reset to "partially watched" if they scrub
back or if the final few seconds are replayed. The `MAX(watched, <computed>)` SQL expression
ensures that once the flag is set it stays set regardless of the position written by
subsequent `SaveProgress` calls.

### Resume minimum position of 5 seconds

Positions very close to the start (≤ 5 s) are treated as "not started" rather than being
resumed. This avoids an awkward 2-second jump forward when a Client opened a file briefly
and then closed it almost immediately.

### `PersistTick` — snapshot inside lock, write outside

Holding a database lock for the duration of a SQLite write while the ffmpeg read loops also
need the playback lock would risk priority inversion. The pattern — snapshot the values
inside the lock, release the lock, then call `SaveProgress` — keeps the lock narrow and
limits SQLite write latency to background timing, not playback latency.

### `GetAll` for the file browser

Calling `Get(path)` once per file in `/list.html` would scale linearly with folder size.
`GetAll` loads the entire `playback` table into a dictionary in one query; `WebServer`
performs a dictionary lookup per file when building the HTML. For the typical video library
size (hundreds to low thousands of files) this is faster and simpler than lazy per-file
queries.

### `TeslaPcService` as the construction site

`MediaLibrary` is constructed in `TeslaPcService` (alongside `MediaStreamer`) so it shares
the same lifetime as the server. Both `MediaStreamer` and `WebServer` receive the same
instance, ensuring a single SQLite connection and a single in-process state.

## Consequences

- **Positive:** Auto-resume works transparently — no UI required; the browser opens the
  player at the saved position.
- **Positive:** Watched files are flagged immediately on EOF; the file browser reflects the
  state on the next `/list.html` load.
- **Positive:** `GetAll` keeps the file-browser render at O(1) DB queries per page load.
- **New dependency:** `Microsoft.Data.Sqlite` + `SQLitePCLRaw.bundle_e_sqlite3` are added to
  the `.csproj`. The bundle ships a native `e_sqlite3.dll`; total added binary size is
  approximately 1.5 MB.
- **Data location:** `%ProgramData%\TeslaPC\teslapc.db` is new. Uninstall must remove it
  manually (no installer exists yet).
- **Advisory GHSA-2m69-gcr7-jv3q** is present with no fix available; risk accepted and
  documented.
