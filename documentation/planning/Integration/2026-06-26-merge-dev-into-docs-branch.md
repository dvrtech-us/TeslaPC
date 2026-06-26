# Integrating the docs/fixes branch with Dev

- Date: 2026-06-26
- Branch: `claude/add-claude-documentation-0kMNo`
- Base of divergence: `a6288b0` ("clean up")

## Context

The branch was cut from `main` (stale, at `a6288b0`) rather than `Dev` (the active branch,
28 commits ahead with keyboard support, VLC playback, and a file browser). The branch added
the documentation system, the unified-server architecture, DXGI capture, SSL/firewall
bootstrap, the Tesla bypass, and three validated fixes. Merging into `Dev` produced conflicts
in four files that both sides had edited: `WebServer.cs`, `Program.cs`, `index.html`,
`TeslaPCInterface.csproj`.

## Decisions

- **Strategy:** merge `origin/Dev` into the branch, resolve once, retarget the PR to `Dev`.
- **Architecture:** keep the **unified single-server** design (this branch) over Dev's older
  multi-port `startServers()` approach. Dev's separate `ImageStreamingServer.Start(8081)` /
  `AudioCapture.Start(8082)` wiring was dropped because those methods don't exist in the
  unified servers (which Dev never modified, so our versions won the merge).
- **`WebServer.cs`:** took Dev's additions (file browser, VLC `play.html`/`list.html`
  handling, `handleKey` keyboard replay) and kept our 503 strong-wildcard fix and routing.
  `StopAsync` kept non-async (its body returns `Task.CompletedTask`).
- **`Program.cs`:** kept our unified `Main` (SSL/firewall/bypass/DXGI startup) and Dev's
  shared DTOs (`InputData`, `KeyData`, `Win32`) that the merged `WebServer.cs` depends on.
- **`index.html`:** kept our tested unified-origin client and added a `keydown` sender so
  Dev's `handleKey` works end-to-end (sends `{Type:'keydown',Key,KeyCode}` over `/ws/input`).
- **Target framework:** both `net6.0` (ours) and Dev's `net7.0` are end-of-life. Bumped to
  **`net10.0-windows`** (current LTS). Verified the merged solution builds (0 errors).

## Consequences

- Positive: Dev's features (keyboard, file browser/VLC) and our infrastructure (unified server,
  DXGI, SSL/firewall bootstrap, Tesla bypass, docs, three fixes) coexist on one branch that
  builds on a supported framework.
- Follow-up: the laptop (had only the .NET 6 SDK) needs the .NET 10 SDK to build/run; install
  performed as part of this integration.
