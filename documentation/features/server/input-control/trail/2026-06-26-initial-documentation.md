# Initial Documentation of Input Control

- Date: 2026-06-26
- Feature: input-control
- Related code: `TeslaPCInterface/WebServer.cs` (`AcceptWebSocketAsync`), `TeslaPCInterface/Program.cs` (`MousePosition`, `Win32`)

## Context

Captures the as-built remote input behavior at the time the documentation system was added.

## Decisions

- Documented the mouse-only reality: cursor moves on every message; left button down/up only on `down`/`up`.
- Explicitly recorded that keyboard support is declared (`keybd_event`) but **not** wired up, and that right-click/scroll/dedicated-click are absent — so future work has a clear starting baseline.
- Documented the coordinate-scaling math and the `DisplaySize`-null passthrough behavior.

## Alternatives Considered

- **Folding input into the web-server doc** — rejected; input has its own protocol, scaling rules, and a distinct future roadmap (keyboard), so it warrants its own feature boundary.

## Consequences

- Positive: the missing keyboard/scroll/right-click capabilities are now an explicit, documented gap rather than an implicit one.
- Negative: none.
