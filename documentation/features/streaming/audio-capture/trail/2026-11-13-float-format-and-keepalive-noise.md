# Fix float format detection and replace zero keepalive with noise

- Date: 2026-11-13
- Feature: audio-capture
- Related code: `teslapc/src/decisions/audio.rs`, `teslapc/src/state.rs`, `teslapc/src/routes.rs`, `teslapc/src/win_capture.rs`, `teslapc/src/linux.rs`

## Problem

- `describe_wave` tested `sub.to_u128() & 0xffff == 3` on the `WAVE_FORMAT_EXTENSIBLE` SubFormat GUID. That reads the tail of the GUID, so it was always false. A 32-bit float mix format was announced on `/ws/audio` as `pcm32` and the browser decoded float bits as integers, which produced loud noise.
- WASAPI packets flagged `AUDCLNT_BUFFERFLAGS_SILENT` were pushed with the undefined buffer content.
- Keepalive buffers were sized from `typical_bytes` (10 ms), one per poll. The `20` ms sleep really woke about every `30` ms on the lab laptop, so noise covered only about `0.31` s of every second.
- Zero-PCM keepalive let the Tesla browser switch audio source during short silences. The project owner asked for low-level noise instead, limited to short silences.

## Decision

- `describe_wave` tests `sub.data1 == 3`.
- `run_loopback` pushes `0x2`-flagged packets as zeros of the packet length.
- Keepalive emits white noise (`keepalive_noise`) in the capture sample format, and only while silence has lasted under `SILENCE_MAX_MS`.
- `KeepaliveInput` gains `silent_ms` (time since the last packet that held sound) next to `idle_ms` (time since any packet).
- `PcmHub::push` replaces an all-zero captured packet with noise while `cover_silence(silent_ms())` and forwards it unchanged after that. Packets with sound are never modified. ffmpeg playback goes through the same `push`, so it gets the same replacement.
- `silence_keepalive` measures the time since the previous tick. New pure function `keepalive_bytes(elapsed_ms, sample_rate, bits_per_sample, channels)` returns whole frames for that time, capped at `KEEPALIVE_MAX_CHUNK_MS`. `typical_buffer_bytes` stays only as a gate in `should_inject_silence`.
- The noise format comes from `PcmHub::set_sample_kind`, called by `run_loopback` and Linux `start_audio` (`32`, float).

## Constants

- `SILENCE_MAX_MS`: `30000` to `10000`.
- `KEEPALIVE_NOISE_AMPLITUDE`: `1/4096` (about -72 dBFS), new.
- `KEEPALIVE_MAX_CHUNK_MS`: `100`, new.
- `SILENCE_START_MS` `250` and `KEEPALIVE_POLL_MS` `20` are unchanged.

## Files

- `teslapc/src/decisions/audio.rs` (`is_silent`, `cover_silence`, `keepalive_noise`, `keepalive_bytes`, constants, `KeepaliveInput`)
- `teslapc/src/state.rs` (`PcmHub`: `last_sound`, `sample_kind`, `noise_seed`, `set_sample_kind`, `noise`, `silent_ms`, `push`)
- `teslapc/src/routes.rs` (`silence_keepalive`)
- `teslapc/src/win_capture.rs` (`run_loopback`, `describe_wave`)
- `teslapc/src/linux.rs` (`start_audio`)
