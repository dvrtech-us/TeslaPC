# Listing and playback in the Rust server

- Date: 2026-11-07
- Feature: file-browser-vlc
- Related code: `teslapc/src/decisions/listing.rs`, `teslapc/src/playback.rs`, `teslapc/src/decisions/media.rs`

## Context

The C# server generated `/list.html` and decoded files with ffmpeg into the MJPEG and audio pipes.

## Decisions

The same routes remain. The list body is `render_guts`. VLC is Windows-only and only from `play.html`. Linux reports the missing-ffmpeg error.

## Alternatives Considered

- Launch VLC on Linux: rejected.

## Consequences

- Positive: a missing folder cannot 500 the browser.
- Negative: Linux playback requires ffmpeg.
