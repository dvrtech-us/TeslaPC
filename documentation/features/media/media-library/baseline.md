# Media Library — Behavior Baseline

- Watched fraction is `0.95`.
- `watched_now(95, 100, false)` is true. `watched_now(94, 100, false)` is false. `watched_now(10, 100, true)` stays true.
- Columns are `path`, `position`, `duration`, `watched`, `play_count`, `updated_at`.
- `path` is `TEXT PRIMARY KEY COLLATE NOCASE`.
- The database path is `%ProgramData%\TeslaPC\teslapc.db` on Windows.
