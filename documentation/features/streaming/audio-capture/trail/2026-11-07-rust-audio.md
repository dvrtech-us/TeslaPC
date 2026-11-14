# Audio format and keepalive decisions

- Date: 2026-11-07
- Feature: audio-capture
- Related code: `teslapc/src/decisions/audio.rs`, `teslapc/src/routes.rs`

## Context

The C# loopback announced a JSON format and injected silence during show-silence gaps.

## Decisions

`format_message` and `should_inject_silence` are the tested functions. The socket handler calls them. File playback uses the same PCM queue.

## Alternatives Considered

- A second audio socket for files: rejected. One queue keeps the client format stable.

## Consequences

- Positive: keepalive thresholds are fixed in tests.
- Negative: a machine without a loopback device gets the format frame and no PCM until media playback.
