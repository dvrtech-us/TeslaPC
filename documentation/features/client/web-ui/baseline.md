# Web UI — Behavior Baseline

- `/` returns the index document.
- `/teslapc.png` bytes start with `89 50 4E 47` and the content type is `image/png`.
- The main page has no resolution control (`resBtn`, `resPicker`, `toggleResPicker`, `pickRes` are absent). It still loads `inputMode` from `GET /config`.
- Fit screen shows one of three messages in the button label for 4 s, sends one request at a time, and checks `r.ok` before parsing JSON.
- `#streamInfo` is read-only and non-interactive, shows `—` until the stream has a size, and never shows audio format.
- `play.html` control bar is unchanged.
- Pages are served from the binary, not from the C# project directory.
