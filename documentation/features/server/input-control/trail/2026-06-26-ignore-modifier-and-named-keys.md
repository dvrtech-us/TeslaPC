# Stop Typing Modifier/Named Key Names (Shift, arrows, etc.)

- Date: 2026-06-26
- Feature: input-control
- Related code: `TeslaPCInterface/WebServer.cs` (`handleKey`)

## Context

After keyboard support arrived via the Dev merge, pressing a non-character key typed its name
as literal text. `handleKey` only mapped `Backspace`/`Enter`/`Tab` and some punctuation; every
other key fell through to `SendKeys.SendWait(key)`. So pressing **Shift** sent `SendKeys.SendWait("Shift")`,
typing the word "Shift" on the host; the same happened for `Control`, `Alt`, `ArrowLeft`,
`F5`, etc. (User report: "when I hold shift it sends the letters SHIFT to the screen.")

## Decisions

Rewrote `handleKey`'s key handling:

- **Ignore** standalone modifier/lock/non-text keys (`Shift`, `Control`, `Alt`, `Meta`, `OS`,
  `AltGraph`, `CapsLock`, `NumLock`, `ScrollLock`, `ContextMenu`, `Fn`, `Dead`, `Unidentified`,
  `Process`, …) — return without typing. Shifted characters already arrive composed (`A`, `!`).
- **Map named keys** (length > 1) to `SendKeys` tokens: arrows → `{LEFT}`/`{RIGHT}`/`{UP}`/`{DOWN}`,
  `Delete`/`Insert`/`Home`/`End`/`PageUp`/`PageDown`, `Escape`→`{ESC}`, `F1`–`F12`, plus the
  existing `Backspace`/`Enter`/`Tab`.
- **Drop unknown named keys** (length > 1, unmapped) instead of typing their literal name.
- **Single characters**: send as-is, wrapping only the SendKeys metacharacters `+ ^ % ~ ( ) { } [ ]`.

## Consequences

- Positive: modifiers no longer leak as text; arrows/F-keys/navigation work; no key ever types
  its own name.
- Note: the previous code wrapped many non-special chars (`: " ' < > , . ? / \ | = - _ * &`) in
  `{}`, which was unnecessary — those are normal in SendKeys and are now sent as-is.
