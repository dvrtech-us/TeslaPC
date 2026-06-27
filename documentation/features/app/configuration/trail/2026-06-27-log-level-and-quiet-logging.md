# Add leveled logging and expose log level in config UIs

- Date: 2026-06-27
- Feature: app/configuration
- Related code: `TeslaPCInterface/Log.cs` (new), `TeslaPCInterface/WebServer.cs`,
  `TeslaPCInterface/MainForm.cs`, `TeslaPCInterface/AppSettings.cs`, `TeslaPCInterface/config.html`

## Context

The application previously wrote every log line unconditionally via `Console.WriteLine`. High-
frequency code paths — every HTTP request and every mouse-move input message — each produced
multiple lines of output. With a redirected log file (common when running as a scheduled task),
this created very large files quickly and made it hard to see meaningful events. There was also
no way to enable more verbose output for debugging without changing source code.

Two goals drove this change:

1. **Reduce noise by default.** Mouse-move and per-request lines should be silent in normal
   operation, visible only when actively debugging.
2. **Make the level controllable at runtime.** The level should be readable/writable via the same
   config surfaces already used for the other settings — no file editing, no restart.

## Decisions

### New `Log` static class (`TeslaPCInterface/Log.cs`, global namespace)

A minimal leveled logger was added rather than pulling in a third-party logging library. The
app's logging needs are simple (no structured logging, no sinks, no categories), and adding a
dependency would complicate the single-binary deployment model. The class wraps
`Console.WriteLine` so the existing `ControlWriter` tee (WinForms Log tab + redirected file)
continues to work without any change.

**Level enum:** `Error = 0`, `Warn = 1`, `Info = 2`, `Debug = 3`. Messages at a level
numerically greater than the current threshold are dropped before reaching `Console.WriteLine`.

**Output format:** `[ERROR]`, `[WARN]`, `[DEBUG]` prefixes for non-Info levels; Info messages
are untagged (they represent the normal operational stream and do not need a visual marker).

**`Parse(string?)`:** accepts `error`/`0`, `warn`/`warning`/`1`, `info`/`2`,
`debug`/`verbose`/`3`, case-insensitive, null-safe. Unknown values fall back to `Info`. The
`verbose` alias is accepted so the env var is interchangeable with common conventions.

**`SetLevel(string?)`:** thin wrapper around `Parse` that writes to the static field.
Intentionally not thread-locked — the worst case is a race on startup or during a config save,
which produces at most one misclassified log line. Not worth the overhead for a single writer.

**`LevelName`:** returns the lowercase string name of the current level, used to round-trip the
value through `GET /config` and the WinForms ComboBox pre-fill.

### Default threshold: `Info`

`Info` was chosen as the default to preserve the existing visible output (startup banners, cert
binding results, hotspot events, client connect/disconnect) while silencing the high-frequency
paths. `Debug` would produce the pre-change volume; `Warn` would hide too much for normal use.

### High-frequency lines moved to `Log.Debug`

The per-HTTP-request line (`[HTTP] {method} {path}`) and the per-input-message lines (received
input, mouse move coordinates) in `WebServer.cs` were moved to `Log.Debug`. At 30 fps the mouse
lines alone produced ~3 lines per frame. These are valuable for protocol-level debugging but are
noise in normal operation. Moving them to Debug (silent by default) achieves the noise-reduction
goal without losing the information.

### Exposed in both config UIs, applied live

`TESLAPC_LOG_LEVEL` was added to `AppSettings` as `LogLevelKey` and included in both config
surfaces:

- **WinForms Config tab**: a `ComboBox` (`_cfgLog`) with four options; pre-filled from
  `Log.LevelName` on tab load; `Log.SetLevel` is called immediately on Save (before
  `AppSettings.Save`).
- **Web `/config.html`**: a `<select name="logLevel">` with four options; `GET /config` returns
  `logLevel` so the form can pre-select the current value; `POST /config` calls
  `Log.SetLevel(logLevel)` if the field is present.

The level is **not** added to `restartNeeded`. It changes a single static field and takes effect
on the next log call — a restart would be wasteful and confusing.

`TESLAPC_LOG_LEVEL` is also persisted to `%ProgramData%\TeslaPC\.env` by `AppSettings.Save` so
the chosen level survives a restart.

## Consequences

- **Positive:** Log tab and redirected log files are quiet by default. Mouse-move spam is gone.
  The level can be raised to `debug` live from either UI without restarting the server.
- **Positive:** No third-party dependency added; the `ControlWriter` tee continues to work
  unchanged.
- **Note:** `Log.SetLevel` is not thread-safe. The risk (one misclassified line on a concurrent
  level change) is accepted as negligible.
- **Note:** The `[INFO]` tag was intentionally omitted for Info-level messages. If a tagged
  format is needed for log parsing later, it can be added to `Log.Info` without breaking the
  level mechanic.
