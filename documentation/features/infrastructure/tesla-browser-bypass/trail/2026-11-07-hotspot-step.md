# Hotspot wait and missing-address re-setup

- Date: 2026-11-07
- Feature: tesla-browser-bypass
- Related code: `teslapc/src/decisions/bypass.rs`, `teslapc/src/win.rs`

## Context

Settings always waited 1500 ms, including after `TURNEDON`. The watchdog ran setup only on `TURNEDON`, so an already-on hotspot missing `100.64.0.1` was left without the bypass.

## Decisions

- `hotspot_step` is the decision `win.rs` sleeps and sets up from.
- `TURNEDON` waits 2000 ms and sets up.
- `ALREADYON` without `100.64.0.1` sets up. Settings waits 1500 ms. The watchdog does not wait.
- The watchdog does nothing while the user turned the hotspot off.

## Alternatives Considered

- Always waiting 2000 ms: rejected. The 1500 ms delay is only the post-off Settings path.

## Consequences

- Positive: the four cases are asserted in `protocol.rs` against the shipped function.
- Negative: the PowerShell hotspot script still needs administrator rights to actually turn the radio on.
