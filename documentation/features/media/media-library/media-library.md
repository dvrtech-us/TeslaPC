# Media Library (SQLite Playback Database)

Persists per-file playback progress, watch status, and play count to a local SQLite database
so that the in-app ffmpeg player can **resume** interrupted videos and mark them as watched.
`MediaLibrary` is the only database component in TeslaPC.

## User-Visible Behavior

- **Resume on open.** When a Client opens a file that was previously partially played (not
  yet watched, saved position > 5 s, and < 95 % through), playback begins from the saved
  position rather than from the start.
- **Watched state.** Once a file reaches 95 % of its duration the watched flag is set
  permanently. Watched files always start from the beginning on subsequent plays (the watch
  state is sticky — it cannot be un-set by `SaveProgress`).
- **Continuous auto-save.** While a file is playing, progress is written to the database
  every 5 seconds so a sudden close loses at most ~5 s of position data.
- **Play count.** Each call to `Play` increments the play count for the file regardless of
  watch state or resume behavior.

## Technical Flow

### `MediaLibrary` class (`TeslaPCInterface/MediaLibrary.cs`)

`MediaLibrary` is a non-static class (no global state). `TeslaPcService` constructs one
instance at startup and passes it to both `MediaStreamer` and `WebServer`.

**Database location:** `%ProgramData%\TeslaPC\teslapc.db` (the same directory used by
`AppSettings.Save` for the `.env` file). The directory is created if absent; the database
file and schema table are created on construction.

**Constructor:** opens (or creates) the SQLite connection via `Microsoft.Data.Sqlite`,
creates the `playback` table if it does not exist, and holds the connection open for the
lifetime of the process. All operations are wrapped in `try`/`catch` that logs the exception
via the project logger and degrades gracefully (callers see null / default values on read
failures; writes fail silently).

### Database Schema

**Table:** `playback`

| Column | Type | Constraints | Description |
|--------|------|-------------|-------------|
| `path` | `TEXT` | `PRIMARY KEY COLLATE NOCASE` | Absolute file path; case-insensitive key |
| `position` | `REAL` | — | Playback position in seconds |
| `duration` | `REAL` | — | Total duration in seconds (from ffprobe) |
| `watched` | `INTEGER` | — | `1` when the file has been fully watched; `0` otherwise |
| `play_count` | `INTEGER` | — | Total number of times `Play` has been called for this file |
| `updated_at` | `TEXT` | — | ISO-8601 timestamp of the last write |

All SQL uses parameterized queries. The schema is created in code (no `.sql` files).

**`WatchedFraction` constant:** `0.95`. A file is considered watched when
`position / duration ≥ 0.95`.

### `Progress` record

Returned by `Get` and the values computed from `GetAll`. Fields:

| Member | Type | Description |
|--------|------|-------------|
| `Position` | `double` | Saved position in seconds |
| `Duration` | `double` | Total duration in seconds |
| `Watched` | `bool` | Whether the file has been fully watched |
| `PlayCount` | `int` | Number of times played |
| `Percent` | `int` | `round(min(1, Position / Duration) * 100)` |

### Public Methods

| Method | Signature | Behavior |
|--------|-----------|---------|
| `Get` | `Progress? Get(string path)` | Returns the saved `Progress` for the given path, or `null` if no record exists. |
| `GetAll` | `Dictionary<string,Progress> GetAll()` | Returns all records keyed by lower-cased path; used by the file browser to decorate every file in one query. |
| `SaveProgress` | `void SaveProgress(string path, double position, double duration)` | Upsert: sets `position`, `duration`, and `updated_at`. The `watched` flag is set to `MAX(watched, 1)` when `position / duration ≥ WatchedFraction` and is **never cleared** — once watched, always watched. |
| `MarkWatched` | `void MarkWatched(string path, double duration)` | Sets `position = duration` and `watched = 1`; called on natural end-of-file. |
| `IncrementPlayCount` | `void IncrementPlayCount(string path)` | Upserts the row and increments `play_count` by 1. |

All methods wrap their body in `try`/`catch`; any `SqliteException` or other exception is
logged and swallowed so the caller degrades gracefully.

### `MediaStreamer` Integration (`TeslaPCInterface/MediaStreamer.cs`)

`TeslaPcService` constructs `MediaLibrary` and passes it into `MediaStreamer` (and
`WebServer`) at startup.

#### On `Play(path)`

1. `IncrementPlayCount(path)` is called immediately.
2. **Resume logic:** if `Get(path)` returns a non-null `Progress` where `Watched == false`,
   `Duration > 0`, `Position > 5` seconds, and `Position / Duration < WatchedFraction`,
   both ffmpeg processes are started at that saved `Position` as the `-ss` offset. Otherwise
   playback starts from offset 0.

#### Periodic persist timer (`PersistTick`)

A `System.Threading.Timer` fires every **5 seconds** while the player is in any state.
When the timer fires:
1. Acquires the playback lock, snapshots the current position and duration, then releases
   the lock immediately.
2. Outside the lock, calls `SaveProgress(path, position, duration)` with the snapshot.
This pattern avoids holding the lock during a SQLite write.

#### On `Pause`, `Seek`, and `Stop`

Each control operation persists the current or target position via `SaveProgress` before (or
immediately after) killing the ffmpeg processes.

#### On natural end-of-file (`EndOfStream`)

When the video pipe closes with the generation still current, playing, and not paused,
`MarkWatched(path, duration)` is called. This sets `position = duration` and `watched = 1`
in the database, then the normal stop/`ExitMediaMode` path runs.

## Key Classes / Methods

| Member | File | Responsibility |
|--------|------|----------------|
| `MediaLibrary` | `TeslaPCInterface/MediaLibrary.cs` | SQLite connection, schema creation, all DB reads and writes |
| `MediaLibrary.Get` | `TeslaPCInterface/MediaLibrary.cs` | Read one record by path |
| `MediaLibrary.GetAll` | `TeslaPCInterface/MediaLibrary.cs` | Read all records (used by file browser) |
| `MediaLibrary.SaveProgress` | `TeslaPCInterface/MediaLibrary.cs` | Upsert position/duration; set watched flag when ≥ 95 % |
| `MediaLibrary.MarkWatched` | `TeslaPCInterface/MediaLibrary.cs` | Set position = duration and watched = 1 |
| `MediaLibrary.IncrementPlayCount` | `TeslaPCInterface/MediaLibrary.cs` | Upsert and increment play_count |
| `MediaStreamer` | `TeslaPCInterface/MediaStreamer.cs` | Calls `MediaLibrary` on Play, Pause, Seek, Stop, and EOF; runs `PersistTick` timer |
| `TeslaPcService` | `TeslaPCInterface/TeslaPcService.cs` | Constructs `MediaLibrary`; injects it into `MediaStreamer` and `WebServer` |

## Constants

| Name | Value | Description |
|------|-------|-------------|
| `WatchedFraction` | `0.95` | Fraction of duration at which a file is considered watched |
| Persist interval | 5 seconds | How often `PersistTick` writes the current position |
| Resume minimum position | 5 seconds | A saved position ≤ 5 s is treated as "from the start" |
| DB path | `%ProgramData%\TeslaPC\teslapc.db` | SQLite database file |

## Database Schema

Table `playback` — see [Database Schema](#database-schema) above.

No `.sql` migration files exist. The schema (`CREATE TABLE IF NOT EXISTS`) is executed in
`MediaLibrary`'s constructor via `Microsoft.Data.Sqlite`.

## SQL Artifacts

None. The schema is created in code.

## Known Gap: SQLite Advisory

`SQLitePCLRaw.lib.e_sqlite3` (a transitive dependency of `Microsoft.Data.Sqlite`) carries
advisory **GHSA-2m69-gcr7-jv3q** with no patched release available as of 2026-06-27.
Practical risk is nil for this use case: the database is a local, single-user store,
all queries use parameterized statements, and no untrusted SQL is executed. The package is
pinned at `SQLitePCLRaw.bundle_e_sqlite3` version **2.1.11** (the latest available).
