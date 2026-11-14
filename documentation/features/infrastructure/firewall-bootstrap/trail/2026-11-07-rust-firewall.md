# Firewall rule from the Rust process

- Date: 2026-11-07
- Feature: firewall-bootstrap
- Related code: `teslapc/src/win.rs`

## Context

`FirewallBootstrap.cs` added the same netsh rule.

## Decisions

The Rust server shells to `netsh` with the same rule name and ports. `--localhost` skips it.

## Alternatives Considered

- A Windows Firewall COM API: rejected. `netsh` matches the previous rule.

## Consequences

- Positive: the rule name stays `TeslaPC Server`.
- Negative: adding the rule still needs an elevated process.
