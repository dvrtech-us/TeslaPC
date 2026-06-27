# Initial Documentation of Screen Capture

- Date: 2026-06-26
- Feature: screen-capture
- Related code: `TeslaPCInterface/ImageStreamingServer.cs`, `TeslaPCInterface/DxgiScreenCapture.cs`, `TeslaPCInterface/MjpegWriter.cs`

## Context

Documents the as-built capture/stream pipeline at the time the documentation system was
introduced. Recent commit history shows two relevant changes already in place: a single
shared capture loop across clients (replacing per-client capture) and the addition of DXGI
Desktop Duplication with a GDI fallback.

## Decisions

- Recorded the single-shared-capture-loop design and its lifecycle tied to `_clientCount`.
- Documented the stale-frame-drop behavior that keeps slow clients on the latest frame.
- Captured the DXGI-preferred / GDI-fallback selection and the DXGI recovery path (`RecreateDuplication` on access loss).
- Recorded the synchronous `WriteHeader` on the request thread as a deliberate http.sys 503 workaround.

## Alternatives Considered

- **Per-client capture loops** — superseded by the shared loop for efficiency; documented as historical, not current.
- **Documenting GDI as primary** — incorrect for current code; DXGI is primary with GDI as fallback.

## Consequences

- Positive: the concurrency model (one writer, many readers, monotonic frame counter) is now an explicit regression reference.
- Negative: the fixed JPEG quality (60) and best-effort pacing are documented as constants, which future tuning work must update here.
