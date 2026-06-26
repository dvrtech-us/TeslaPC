# Fix Keyboard Double-Typing (one event per keystroke)

- Date: 2026-06-26
- Feature: input-control (client web-ui + server handleKey)
- Related code: `TeslaPCInterface/index.html`, `TeslaPCInterface/WebServer.cs` (`handleKey`)

## Context

Typing in the on-screen keyboard box produced each character **twice**. The client sent two
messages per keystroke — `keypress` *and* `keyup`, both carrying the same `Key` — and the server
suppressed the duplicate only if the two arrived within a **100 ms** debounce window. Holding a
key slightly longer than 100 ms let the `keyup` escape the window, so the character was typed
twice. The debounce was a fragile band-aid over a double-send.

## Decisions

- **Send exactly one message per keystroke** from the client:
  - characters via the input box's `input` event (`inputType === 'insertText'`, iterate
    `event.data`) — fires once per character and works with on-screen/touch keyboards;
  - non-character keys (`Backspace`, `Enter`, `Tab`, `Escape`, `Delete`, `Insert`, `Home`,
    `End`, `PageUp`, `PageDown`, arrows, `F1`–`F12`) via `keydown` with `preventDefault()`.
  - `Type` is now always `"key"`.
- **Removed the 100 ms server-side debounce** (and the `lastInputData`/`lastInputTime` fields) in
  `handleKey`. With one message per keystroke it is unnecessary and would otherwise drop
  legitimate fast repeats (e.g. the double letters in "ll").

## Consequences

- Positive: no more double characters; fast repeats and held-key OS repeat both work; the input
  box approach keeps on-screen-keyboard support for the Tesla.
- Note: the modifier-ignore and named-key→`SendKeys` token mapping from the earlier fix are
  unchanged; only the event sourcing and debounce changed.
