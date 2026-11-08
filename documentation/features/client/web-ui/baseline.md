# Web UI — Behavior Baseline

- `/` returns the index document.
- `/teslapc.png` bytes start with `89 50 4E 47` and the content type is `image/png`.
- The resolution control posts only `streamHeight`.
- Pages are served from the binary, not from the C# project directory.
