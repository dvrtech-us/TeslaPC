# Initial Documentation of the Web UI

- Date: 2026-06-26
- Feature: web-ui
- Related code: `TeslaPCInterface/index.html`, `TeslaPCInterface/PCMPlayerProcessor.js`

## Context

Documents the as-built single-page client when the documentation system was introduced.

## Decisions

- Recorded the `<img src="/stream">` MJPEG approach (no canvas) and the on-load input socket.
- Documented the audio start path: format negotiation → AudioWorklet, with the `AudioBufferSourceNode` fallback for browsers without worklet support.
- Captured the same-origin `getWsUrl` scheme selection and the user-gesture requirement for audio.

## Alternatives Considered

- **Canvas-based rendering of decoded JPEG frames** — not used; native `<img>` MJPEG rendering is simpler and is the implemented choice.
- **Auto-reconnect for all sockets** — not implemented; documented as a known gap rather than invented behavior.

## Consequences

- Positive: a clear reference for the minimal, dependency-free client.
- Negative: the absence of reconnect logic and keyboard input is now explicitly documented as a gap.
