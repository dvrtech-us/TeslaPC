# Web Server

One Rust process serves every TeslaPC route. `serve::run` in `teslapc/src/serve.rs` loads `.env`, binds the listeners, and stays up without a window. `routes::router` in `teslapc/src/routes.rs` dispatches paths.

## User flow

1. Start `teslapc.exe`. Stdout prints the listen address.
2. A browser opens `http://<host>:8080/` or `https://<host>:8443/`.
3. `--localhost` is HTTP only on `127.0.0.1:8080`.

## Technical flow

- HTTP port `8080`. HTTPS port `8443` (rustls), skipped by `--localhost`.
- Non-localhost HTTP binds `0.0.0.0:8080`. Localhost binds `127.0.0.1:8080`.
- `GET /version` returns `{ "version": "<Cargo package version>" }`. The same string is `version` on `GET /config`.
- Any WebSocket whose path does not start with `/ws/audio` (case-insensitive) is the input socket. `classify_socket` in `decisions/socket.rs` decides.
- Static files come from `assets::asset`. HTML is UTF-8. `teslapc.png` is the raw bytes with `Content-Type: image/png`.
- No route checks a credential.

## Routes

| Path | Method | Handler |
|------|--------|---------|
| `/`, `/index.html`, `/config.html`, `/style.css`, `*.js`, `/teslapc.png` | GET | `fallback` |
| `/stream` | GET | `stream` |
| `/ws/audio` | WebSocket | `audio_ws` |
| `/ws/input` and any other WebSocket | WebSocket | `input_ws` / `fallback` |
| `/config` | GET, POST | `config_get`, `config_post` |
| `/version` | GET | `version` |
| `/list.html` | GET | `list_page` |
| `/play.html` | GET | `play_page` |
| `/media/{action}` | GET | `media_action` |
| `/display/{action}` | GET | `display_action` |
| `/hotspot` | POST | `hotspot` |

## Failure

A bind error is printed and the process exits non-zero. A missing firewall, hotspot, or certificate privilege is logged and HTTP still starts.
