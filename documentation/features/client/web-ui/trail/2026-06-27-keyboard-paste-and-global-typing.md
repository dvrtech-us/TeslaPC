# Keyboard: Paste Forwarding and Page-Wide Physical Typing

- Date: 2026-06-27
- Feature: web-ui (client) / input-control
- Related code: `TeslaPCInterface/index.html`

## Context

Two gaps existed in keyboard input:

1. **Paste was silently dropped.** The `#fakeKeyboard` `input` event listener only acted on
   `inputType === 'insertText'`. Paste events arrive with `inputType === 'insertFromPaste'`
   and were ignored, so Ctrl+V or long-press Paste on touch devices sent nothing to the host.

2. **Capture required focusing the box.** All keyboard input depended on `#fakeKeyboard` having
   focus. On a desktop browser a physical keyboard user had to click the box first; typing anywhere
   else on the page sent nothing to the host. This made the app awkward for non-touch use.

## Decisions

### Three unified input paths

All paths converge on `sendKey(key, code)` (sends `{Type:'key', Key, KeyCode}` over `/ws/input`)
and a new helper `sendText(text)`.

**Path 1 — On-screen box (`#fakeKeyboard`):** The existing `input` listener was extended to handle
both `insertText` and `insertFromPaste` input types, using `event.data` for both. The box remains
visible so touch devices can tap it to summon the on-screen keyboard.

**Path 2 — Page-wide physical typing (`document` `keydown`):** A listener on `document` forwards
keystrokes without requiring the box to be focused.

- Returns early when `isEditable(event.target)` is true (`INPUT`, `TEXTAREA`, or `contentEditable`
  set) — this prevents double-send when a real editable element is active.
- Returns early when `ctrlKey`, `metaKey`, or `altKey` is held — browser shortcuts (including the
  Ctrl+V paste that will fire the paste listener below) are not stolen.
- Returns early for keys in the `modifierKeys` array (standalone modifier keys) — modifier-only
  keystrokes produce no character and are not forwarded.
- Single-character keys (`event.key.length === 1`) are forwarded via `sendKey(event.key)`.
- Named and function keys (matched against the `namedKeys` list or `/^F\d{1,2}$/`) are forwarded
  via `sendKey(event.key, event.code)`.
- Both paths call `preventDefault()`.

The `namedKeys` and `modifierKeys` arrays are shared between the page-level listener and the box's
own `keydown` listener.

**Path 3 — Paste anywhere (`document` `paste`):** A listener reads
`event.clipboardData.getData('text')` and forwards the full text via `sendText`. It calls
`preventDefault()`, which serves two purposes: (a) prevents the clipboard content from being
inserted into `#fakeKeyboard` if it happens to be focused, and (b) suppresses the `input` event
that the box would otherwise fire — avoiding a duplicate send. The box is cleared after paste if it
was focused.

### `sendText` character mapping

`sendText(text)` walks the string and sends one character (or named key) per iteration:

- `\r\n` (CRLF) is consumed as a single unit and mapped to `Enter`.
- Bare `\r` or `\n` each map to `Enter`.
- `\t` maps to `Tab`.
- All other characters are sent as their literal value via `sendKey(char)`.

This mapping means pasted multi-line text produces proper line breaks on the host.

### Backend unchanged

`handleKey` in `WebServer.cs` is unchanged. It still ignores standalone modifiers, maps named keys
to SendKeys tokens, and types single characters with escaping. The change is entirely client-side.

## Alternatives Considered

- **Capture paste inside `#fakeKeyboard` only (extend the `input` listener)** — rejected; this
  fixes the paste gap but not the focus-required gap. The page-wide paste listener is needed anyway
  for the no-focus case, so the box listener becomes redundant for paste.
- **`document` `keypress` instead of `keydown`** — `keypress` is deprecated and does not fire for
  non-printable keys (arrows, function keys, etc.); `keydown` is the correct modern choice.
- **`input` event on `document`** — does not bubble from `<input>` elements in all browsers
  consistently; `keydown` + `paste` is the reliable combination.

## Consequences

- Positive: paste works from any context (Ctrl+V on desktop, long-press on touch, or paste into the
  box directly); physical typing works without focusing the box.
- Positive: browser shortcuts and real editable fields are unaffected — the editable-target check
  and modifier-held guard prevent interference.
- Positive: pasted multi-line text (e.g. from clipboard) produces correct Enter keystrokes on the
  host via the `sendText` mapping.
- Note: the backend is unchanged; all new logic is client-side in `index.html`.
