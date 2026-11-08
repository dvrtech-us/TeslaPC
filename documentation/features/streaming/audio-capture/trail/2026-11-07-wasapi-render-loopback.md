# WASAPI render loopback pushes PCM

- Date: 2026-11-07
- Feature: audio-capture
- Related code: `teslapc/src/win_capture.rs`

## Context

The loopback thread returned before calling WASAPI, so `capturing` stayed false and silence keepalive never saw a buffer size.

## Decisions

- `run_loopback` activates the default render endpoint and initializes a shared loopback client.
- On `Start`, `capturing` is set and `typical_bytes` is taken from the mix format.
- Each packet is pushed with `real=true` unless media playback is active.
- The log line on failure is the OS error from the call that failed.

## Alternatives Considered

- Injecting silence with a hard-coded buffer size and no device: rejected. The size comes from `GetMixFormat`.

## Consequences

- Positive: a localhost launch logged `WASAPI loopback started`.
- Negative: a machine with no render endpoint still has no PCM, and HTTP stays up.
