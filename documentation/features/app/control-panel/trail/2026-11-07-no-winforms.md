# WinForms panel removed

- Date: 2026-11-07
- Feature: control-panel
- Related code: `teslapc/src/serve.rs`, `teslapc/src/win.rs`, `teslapc/src/linux.rs`

## Context

The C# app hosted the server inside a WinForms control panel.

## Decisions

The Rust binary is the server. Logs go to stdout. Hotspot on/off is on the Settings page. Service install is `teslapc service install`.

## Alternatives Considered

- Port the WinForms window: rejected.

## Consequences

- Positive: the process can run as a service.
- Negative: there is no desktop log tab.
