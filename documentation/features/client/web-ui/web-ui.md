# Web UI

The browser UI is the files in `teslapc/assets/`, compiled into the binary with `include_bytes!`. `assets::asset` returns the bytes and a content type. `teslapc.png` is `image/png` and is not passed through a string decoder.

`index.html` opens `/stream`, `/ws/audio`, and `/ws/input` on the same origin. The main-page control bar has Keyboard, Files, Settings, Fit screen (`#fitBtn`, `fitScreen(this)`), and a read-only indicator (`#streamInfo`). There is no resolution control on the main page; it lives on Settings.

Fit screen calls `GET /display/match?w=&h=` with the `.screen` size, one request at a time. The button is disabled until the response or a 5 s timeout. Its label is replaced for 4 s by `PC set to <w>x<h>` (`changed: true`), `Unchanged <w>x<h>` (`changed: false`, which also covers a refused change), or `Change failed` (network error, non-2xx, non-JSON, or timeout). A newer message cancels the older timer; the label is then restored. `#fitBtn` has a fixed `190px` width so the bar does not jump; the messages are short so they fit it. The Fit screen icon is inline SVG (the Tesla browser has no glyph for U+26F6). The keyboard field keeps at least `150px`.

`#streamInfo` shows `<naturalWidth>×<naturalHeight> · <format>` of `#streamImg`, polled every 1 s, for example `1280×720 · MJPEG`. The format comes from the `VIDEO_STREAM` constant (`{ path: '/stream', format: 'MJPEG' }`), which `startVideoStream` also uses. Before a size exists, or while the connection-lost panel shows, it shows `—`. It has `pointer-events: none`, is not focusable, and does not shrink; it does not appear on `play.html`. Pointer events include `DisplaySize`. Paste is expanded in the page into per-character key messages; the server mapping is `expand_paste` / `map_key`.

`list.html` is a template. The server replaces `{{GUTS}}`. `play.html` replaces `{{TITLE}}` and `{{FILEPATH}}`. `config.html` is the Settings page, including hotspot on/off.

HTML responses still rewrite `//LOCALHOST` to the request host and, on port `8443`, rewrite `ws://`/`http://` and the `8080` port.
