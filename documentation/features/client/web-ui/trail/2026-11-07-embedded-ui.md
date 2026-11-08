# UI bytes embedded in the Rust binary

- Date: 2026-11-07
- Feature: web-ui
- Related code: `teslapc/src/assets.rs`, `teslapc/assets/`

## Context

The C# server read files with `File.ReadAllText`, which corrupted PNG bytes.

## Decisions

Assets are `include_bytes!`. PNG and JPEG are returned as those bytes. HTML is the only kind rewritten for host and port.

## Alternatives Considered

- Read assets from disk at runtime: rejected for the shipped UI. The bytes are in the binary.

## Consequences

- Positive: `teslapc.png` survives a binary-safe GET.
- Negative: changing a page requires a rebuild.
