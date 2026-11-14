# Initial Documentation of the Web Server

- Date: 2026-06-26
- Feature: web-server
- Related code: `TeslaPCInterface/Program.cs`, `TeslaPCInterface/WebServer.cs`

## Context

The documentation system was introduced after the server had already been built and
iterated on (unified HTTP/HTTPS routing, shared capture loop, HTTPS bootstrap). This entry
captures the as-built behavior of the server and startup orchestration as the baseline.

## Decisions

- Documented the single-`HttpListener` design and the strong-wildcard prefix choice (`+`/`*`) that works around http.sys returning 503 on host-specific registrations.
- Recorded the dedicated `"HttpAccept"` background thread + thread-pool dispatch model and the reason it exists (http.sys 503s until `GetContext()` actively dequeues).
- Documented the fixed routing order and the fact that `/ws/input` is matched by exclusion rather than an explicit path check.
- Recorded the 10-second startup timeout and the `--localhost` / `--no-tesla-bypass` flag semantics.

## Alternatives Considered

- **Separate listeners per protocol/port** — rejected as already-superseded; the unified listener with path routing is the implemented design.
- **Documenting desired future behavior (e.g. auth)** — explicitly out of scope; docs describe actual behavior, and no auth exists today.

## Consequences

- Positive: a clear regression reference for routing and startup ordering.
- Negative: the "no authentication" reality is now documented prominently, which should prompt a future security decision before any non-LAN exposure.
