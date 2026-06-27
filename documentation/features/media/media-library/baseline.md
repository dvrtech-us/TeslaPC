# Media Library — Behavior Baseline

Known-good invariants. Update only when intended behavior changes.

## Invariants

- The SQLite database is located at `%ProgramData%\TeslaPC\teslapc.db`. The directory and
  table are created on construction if they do not exist.
- The schema is a single table `playback (path TEXT PRIMARY KEY COLLATE NOCASE, position REAL,
  duration REAL, watched INTEGER, play_count INTEGER, updated_at TEXT)`.
- `path` is compared case-insensitively (`COLLATE NOCASE`). All keys stored by `GetAll` are
  lower-cased.
- `WatchedFraction` is `0.95`. A file is watched when `position / duration ≥ 0.95`.
- **The `watched` flag is sticky.** `SaveProgress` raises it when the fraction is reached via
  `MAX(watched, <computed>)` and never clears it. Only explicit re-creation of the row would
  reset it, which no current code path does.
- `IncrementPlayCount` is called on every `Play`, regardless of resume or watch state.
- **Resume rule:** a file is resumed from the saved position only when all four conditions
  hold: `Watched == false`, `Duration > 0`, `Position > 5` s, and
  `Position / Duration < WatchedFraction`. If any condition fails, playback starts at 0.
- The `PersistTick` timer fires every **5 seconds** while the player is active. Position and
  duration are snapshotted under the playback lock; `SaveProgress` is called outside the lock.
- `Pause`, `Seek`, and `Stop` each call `SaveProgress` with the current or target position.
- Natural end-of-file calls `MarkWatched(path, duration)`, setting `position = duration` and
  `watched = 1`.
- All `MediaLibrary` methods are wrapped in `try`/`catch`. Exceptions are logged and swallowed;
  callers receive `null` (on read) or no return value (on write) without crashing.
- `MediaLibrary` is constructed by `TeslaPcService` and injected into both `MediaStreamer`
  and `WebServer`. A single instance is shared.
- `GetAll` returns a `Dictionary<string,Progress>` keyed by lower-cased path. It is called
  once per `/list.html` render to decorate all files in a single query.

## Failure Behavior

- SQLite write failure (disk full, locked, etc.) → exception is caught, logged, and swallowed.
  The player continues; the next `PersistTick` will retry.
- SQLite read failure → `Get` returns `null`; `GetAll` returns an empty dictionary. The
  file browser renders without badges; playback starts at 0 without resume.
- Database file missing at startup → created automatically by the constructor.
- `%ProgramData%\TeslaPC\` directory missing → created automatically by the constructor.

## Access Control

`MediaLibrary` is a server-side component with no HTTP endpoint of its own. It is populated
and queried exclusively by `MediaStreamer` and `WebServer` (file browser). The database file
is at `%ProgramData%\TeslaPC\teslapc.db` and is not served over HTTP.
