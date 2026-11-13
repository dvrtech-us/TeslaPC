# WASAPI loopback retries with backoff

- Date: 2026-11-13
- Feature: audio-capture
- Related code: `teslapc/src/win.rs` (`start_audio`), `teslapc/src/win_capture.rs` (`run_loopback`), `teslapc/src/decisions/audio.rs`

## Problem

`run_loopback` ran once. Any error ended the `WasapiLoopback` thread: no default render endpoint at boot, `AUDCLNT_E_DEVICE_INVALIDATED` when the default device changes, or any later failure. Audio stayed gone until the process restarted.

## Decision

- `start_audio` loops: run, set `pcm.capturing` false, log, wait, run again. Each run starts from `GetDefaultAudioEndpoint`, so a changed default device is used.
- The error is logged only when its text differs from the last logged text, so a missing device does not print every attempt. A run longer than `LOOPBACK_HEALTHY_RUN_MS` clears that memory.
- The wait is the pure function `decisions::audio::loopback_backoff_ms(previous_ms, ran_ms)`.
- `run_loopback` pairs each successful `CoInitializeEx` with `CoUninitialize` through the `ComScope` drop guard, declared before the COM objects, so the failed attempt's `IMMDeviceEnumerator`, `IMMDevice`, `IAudioClient`, and `IAudioCaptureClient` are released first and repeated attempts do not leak COM references. `RPC_E_CHANGED_MODE` is tolerated and not balanced.
- The keepalive-noise logic is unchanged.

## Constants

- `LOOPBACK_BACKOFF_START_MS` = `1000`
- `LOOPBACK_BACKOFF_MAX_MS` = `10000` (schedule 1, 2, 4, 8, 10, 10 ... seconds)
- `LOOPBACK_HEALTHY_RUN_MS` = `30000`

## Files

- `teslapc/src/win.rs`: retry loop in `start_audio`.
- `teslapc/src/win_capture.rs`: `ComScope`.
- `teslapc/src/decisions/audio.rs`, `teslapc/tests/protocol.rs`: backoff function and test.
