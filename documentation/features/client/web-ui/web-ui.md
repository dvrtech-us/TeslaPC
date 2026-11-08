# Web UI

The browser UI is the files in `teslapc/assets/`, compiled into the binary with `include_bytes!`. `assets::asset` returns the bytes and a content type. `teslapc.png` is `image/png` and is not passed through a string decoder.

`index.html` opens `/stream`, `/ws/audio`, and `/ws/input` on the same origin. The resolution buttons post `streamHeight` `480`, `720`, or `1080`. Pointer events include `DisplaySize`. Paste is expanded in the page into per-character key messages; the server mapping is `expand_paste` / `map_key`.

`list.html` is a template. The server replaces `{{GUTS}}`. `play.html` replaces `{{TITLE}}` and `{{FILEPATH}}`. `config.html` is the Settings page, including hotspot on/off.

HTML responses still rewrite `//LOCALHOST` to the request host and, on port `8443`, rewrite `ws://`/`http://` and the `8080` port.
