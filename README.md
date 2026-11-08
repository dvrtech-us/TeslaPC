# TeslaPC

Rust remote-desktop server. One binary serves the screen, system audio, and input to a browser,
including the Tesla in-car browser.

```powershell
.\build.ps1
.\teslapc\target\release\teslapc.exe
```

`--localhost` serves HTTP on `127.0.0.1:8080` only. See [AGENTS.md](AGENTS.md) for flags,
paths, and the feature docs.
