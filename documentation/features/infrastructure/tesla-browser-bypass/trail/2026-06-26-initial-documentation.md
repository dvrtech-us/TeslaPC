# Initial Documentation of the Tesla Browser Bypass

- Date: 2026-06-26
- Feature: tesla-browser-bypass
- Related code: `TeslaPCInterface/TeslaBrowserBypass.cs`, `TeslaPCInterface/Program.cs`

## Context

The Tesla in-car browser refuses RFC 1918 addresses, so a PC sharing its screen over Mobile
Hotspot (`192.168.137.x`) cannot be reached directly. This documents the implemented CGNAT
workaround as built.

## Decisions

- Recorded the choice of `100.64.0.1` (CGNAT, outside RFC 1918) as the secondary IP and the `SkipAsSource=True` flag.
- Documented adapter discovery (description match, then `192.168.137.*` fallback) and the idempotent IP-add.
- Captured the separation between the bypass firewall rule (`TeslaPC Bypass`, IP-scoped) and the general server rule (`TeslaPC Server`).
- Recorded guaranteed cleanup via `IDisposable` on shutdown and `Ctrl+C`.

## Alternatives Considered

- **Asking the user to change the hotspot subnet** — not feasible; Windows fixes the hotspot range at `192.168.137.x`.
- **A public relay / tunnel** — heavier and out of scope; the CGNAT secondary IP solves the in-car case locally.

## Consequences

- Positive: the Tesla browser connects with no third-party infrastructure.
- Negative: requires administrator and leaves network state that must be cleaned up — hence the strict `Dispose` contract documented in the baseline.
