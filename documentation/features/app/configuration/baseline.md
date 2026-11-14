# Configuration — Behavior Baseline

- `GET /config` contains `httpsHost`, `acmeEmail`, `videoRoot`, `logLevel`, `streamHeight`, `inputMode`, `version`, `cfTokenSet` and does not contain the Cloudflare token.
- A blank `cfToken` keeps the stored line.
- Unknown env lines and comments are preserved.
- `restartNeeded` is true only when host, token, or email was written.
- Settings Save never sends `streamHeight`, sends only changed fields, and is disabled until `GET /config` has loaded.
- Stream height outside `240`–`2160` is not saved.
- Input mode is stored only for `touch` or `mouse`.
- Default video root is `C:\video\` on Windows when the key is unset.
- Default log level is `info`. Default input mode is `touch`. Default stream height is `1080`.
