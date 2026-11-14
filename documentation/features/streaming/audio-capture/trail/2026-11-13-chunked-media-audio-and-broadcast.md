# Chunked media audio and broadcast fan-out

## Problem

- `playback::audio_reader` pushed one sample frame (8 bytes for float stereo) per `PcmHub::push`, about `48000` pushes per second. The hub queue kept `32`, so most media audio was dropped.
- Each `/ws/audio` client had an unbounded `mpsc` channel. A slow client queued audio without limit and received it late. Senders were pushed into `PcmHub.clients` and never removed.
- Clients could send nothing the server read, so the browser's buffer state was invisible from the car.

## Decision

- `audio_reader` pushes frame-aligned 10 ms chunks (`decisions::audio::audio_chunk_bytes`) and flushes the whole-frame tail when ffmpeg ends.
- `PcmHub` owns one `tokio::sync::broadcast::Sender<Bytes>`. `audio_socket` subscribes after sending the format frame. `Lagged(n)` is logged and skipped; `Closed` ends the socket. Dropped receivers leave the channel automatically, so the `clients` Vec and its leak are gone.
- `PcmHub.queue`, `notify`, and the `pcm_dispatch` task were removed. `push` keeps its silent-to-noise replacement, `last_real` / `last_sound` / `typical_bytes` updates, and the early return when `client_count` is `0`, then calls `tx.send`. Broadcast send is non-blocking and bounded, so the dispatch hop added nothing.
- `silence_keepalive` used `queue_empty`. It now uses `no_packet_since_tick` (`PcmHub::take_idle_tick`: no real packet since the previous tick). Keepalive's own pushes (`real=false`) do not set it, so noise stays continuous. Keepalive noise behavior and timing are unchanged.
- `audio_socket` logs client stats frames through `decisions::audio::client_stats_line`.

## Constants

- `AUDIO_CHUNK_MS` = `10`
- `PCM_BROADCAST_CAPACITY` = `64` chunks (about `640` ms)
- `CLIENT_STATS_MAX_BYTES` = `512`

## Files

- `teslapc/src/playback.rs`: `audio_reader`.
- `teslapc/src/state.rs`: `PcmHub`, `PCM_BROADCAST_CAPACITY`.
- `teslapc/src/routes.rs`: `audio_socket`, `silence_keepalive`; `pcm_dispatch` removed.
- `teslapc/src/serve.rs`: dispatch task no longer spawned.
- `teslapc/src/decisions/audio.rs`, `teslapc/tests/protocol.rs`: chunk size, stats line, `KeepaliveInput.no_packet_since_tick`, tests.
