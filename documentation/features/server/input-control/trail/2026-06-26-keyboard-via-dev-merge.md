# Keyboard Input Arrives (Dev merge) + Client Input Box Restored

- Date: 2026-06-26
- Feature: input-control (and client web-ui)
- Related code: `TeslaPCInterface/WebServer.cs` (`handleKey`), `TeslaPCInterface/Program.cs` (`KeyData`), `TeslaPCInterface/index.html` (`#fakeKeyboard`)

## Context

The initial input-control docs described a mouse-only implementation (keyboard "not
implemented"). Merging `origin/Dev` brought real keyboard support on the server:
`WebServer.handleKey` replays keys with `SendKeys.SendWait`, debounces same-key repeats within
100 ms, and maps special keys to `SendKeys` tokens. Routing: any `/ws/input` message whose JSON
contains `"key"` is parsed as `KeyData { Type, Key, KeyCode }` and sent to `handleKey`.

During the merge the client `index.html` was reset to our (mouse-only) version, which
**dropped Dev's visible `#fakeKeyboard` input box**. A document-level `keydown` listener was
added as a stopgap, but that only works for a physical keyboard — the Tesla browser is
touch-only and needs a focusable input to summon the on-screen keyboard.

## Decisions

- Restored the visible `#fakeKeyboard` text input next to the playback button.
- Replaced the document-level `keydown` stopgap with `keyup` + `keypress` handlers on that
  input (matching Dev): `keyup` covers all keys including Backspace/Enter/Tab; `keypress`
  covers character keys. Each sends `{Type, Key, KeyCode}` over the existing `/ws/input`
  socket; the box is cleared after each `keyup` so text doesn't accumulate.
- Documented that `keybd_event` (declared in `Win32`) is still unused — keyboard goes through
  `SendKeys`, not `keybd_event`.

## Consequences

- Positive: keyboard works end-to-end again, including the on-screen keyboard on the Tesla.
- Note: `handleKey`'s 100 ms same-key debounce means sending both `keyup` and `keypress` for one
  character results in a single replayed keystroke; special keys (no `keypress`) come through
  `keyup`.
