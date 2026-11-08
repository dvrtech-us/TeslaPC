# Rust server replaces HttpListener

- Date: 2026-11-07
- Feature: web-server
- Related code: `teslapc/src/serve.rs`, `teslapc/src/routes.rs`

## Context

The C# `HttpListener` process was the product. The rewrite is one Rust binary.

## Decisions

Axum serves HTTP. HTTPS is a separate rustls accept loop on `8443`. `--localhost` binds `127.0.0.1` and does not open `8443`. Static files, including `teslapc.png`, are compiled into the binary and PNG is not decoded as text.

## Alternatives Considered

- Keep http.sys URL ACLs: rejected. TLS is in-process.
- Keep the WinForms host: rejected. Logs go to stdout.

## Consequences

- Positive: one binary, Windows and Linux, no URL ACL.
- Negative: an existing http.sys certificate binding is not reused.
