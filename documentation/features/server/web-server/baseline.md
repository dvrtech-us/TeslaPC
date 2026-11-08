# Web Server — Behavior Baseline

## Flow invariants

- One process serves HTTP `8080` and, unless `--localhost`, HTTPS `8443` with rustls.
- `--localhost` binds `127.0.0.1:8080` only and skips firewall, ACME, HTTPS, and the Tesla bypass.
- `GET /version` and `GET /config` `version` are the crate version from `CARGO_PKG_VERSION`.
- WebSocket classification is `classify_socket`: `/ws/audio` is audio; every other path is input.
- `teslapc.png` is served from `include_bytes!` with content type `image/png`. The body starts with the PNG signature.
- No route requires authentication.

## Defaults

- Ports `8080` and `8443` in `serve.rs`.
- `--no-tesla-bypass` skips only the bypass.

## Failure

- Bind failure prints the OS error and exits.
- Privilege failures for firewall, hotspot, input, and certificates are logged. The HTTP listener still runs.
