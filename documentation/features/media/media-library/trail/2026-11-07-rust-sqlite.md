# SQLite history in the Rust server

- Date: 2026-11-07
- Feature: media-library
- Related code: `teslapc/src/store.rs`, `teslapc/src/decisions/history.rs`

## Context

Playback resume and the watched badge came from `MediaLibrary.cs`.

## Decisions

The schema is unchanged. `watched_now` is the tested threshold. Linux stores the file under `$XDG_DATA_HOME`.

## Alternatives Considered

- A new table: rejected. The existing columns stay.

## Consequences

- Positive: the 95% rule is a unit test.
- Negative: an unwritable data directory disables history and is logged. Playback still starts.
