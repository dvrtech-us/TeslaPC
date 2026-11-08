# Configuration

Settings live in `.env`. Windows path: `%ProgramData%\TeslaPC\.env`. Linux path: `$XDG_CONFIG_HOME/teslapc/.env` (default `~/.config/teslapc/.env`). An `.env` next to the executable is loaded first. The config-dir file is loaded second and overrides it. Variables already set in the process environment are left alone.

`GET /config` returns `httpsHost`, `acmeEmail`, `videoRoot`, `logLevel`, `streamHeight`, `inputMode`, `version`, and `cfTokenSet`. It never returns the token. `public_config_json` has no token field.

`POST /config` is `application/x-www-form-urlencoded`. `apply_config_body` updates named keys and leaves every other line, including comments, in place. A blank `cfToken` does not replace the stored token. The response is `{ "saved": true, "restartNeeded": true }` when host, token, or email was written.

Live keys, applied without a restart: `TESLAPC_VIDEO_ROOT`, `TESLAPC_LOG_LEVEL`, `TESLAPC_STREAM_HEIGHT`, `TESLAPC_INPUT_MODE`. A valid stream height also bumps the capture epoch. The height must be `240`–`2160`. Log level accepts `error`, `warn`/`warning`, `info`, `debug`/`verbose`, or `0`–`3`.

The Settings page is `teslapc/assets/config.html`. The 480/720/1080 buttons set `streamHeight` only. Hotspot buttons `POST /hotspot` with `on=1` or `on=0`.
