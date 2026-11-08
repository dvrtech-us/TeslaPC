# Media Library

`Library::open` in `teslapc/src/store.rs` creates `playback`:

| Column | Type |
|--------|------|
| `path` | TEXT PRIMARY KEY COLLATE NOCASE |
| `position` | REAL NOT NULL DEFAULT 0 |
| `duration` | REAL NOT NULL DEFAULT 0 |
| `watched` | INTEGER NOT NULL DEFAULT 0 |
| `play_count` | INTEGER NOT NULL DEFAULT 0 |
| `updated_at` | TEXT |

Windows file: `%ProgramData%\TeslaPC\teslapc.db`. Linux file: `$XDG_DATA_HOME/teslapc/teslapc.db`, default `~/.local/share/teslapc/teslapc.db`.

`watched_now` is sticky. It becomes true when `duration > 0` and `position / duration >= 0.95` (`WATCHED_FRACTION`). `save_progress` also stores `MAX(watched, new)` so a later shorter position cannot clear it. End of file calls `mark_watched`.

There is no `sql/` folder. The `CREATE TABLE` runs in `Library::open`.
