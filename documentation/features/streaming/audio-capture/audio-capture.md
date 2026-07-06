# Audio Capture & Streaming

Captures system audio (WASAPI loopback) via CSCore and broadcasts raw PCM to browsers over
the `/ws/audio` WebSocket. The capture format is discovered from the device at runtime;
there is **no server-side resampling** — clients resample to their own audio context rate.

## User Flow

1. The user clicks **Start playback** in the web UI (browsers require a user gesture to start audio).
2. The client opens `/ws/audio`; the server replies first with a JSON format descriptor, then streams binary PCM.
3. The client decodes, resamples if needed, buffers, and plays the audio.

## Technical Flow

### Capture (`AudioCapture`, `AudioStreamingServer.cs`, namespace `AudioStreamingServer`)

- `StartCapturing()` (`:32`): creates `CSCore.SoundIn.WasapiLoopbackCapture`, calls `Initialize()`, and reads `capture.WaveFormat` to populate `_sampleRate`, `_bitsPerSample`, `_channels`, `_sampleFormat`. No format is hardcoded.
- `ResolveSampleFormat()` (`:83`) maps the device encoding to a string: `IeeeFloat → "float"`; PCM → `"pcm16"`/`"pcm24"`/`"pcm32"` by bit depth; `Extensible` resolved via `WaveFormatExtensible.SubFormat`; fallback `32-bit → "float"`, else `PcmFormatName`.
- The `DataAvailable` handler copies `e.Data[e.Offset .. +e.ByteCount]` into a `byte[]` and enqueues into `ConcurrentQueue<byte[]> _audioDataQueue` when `e.ByteCount > 0`, at least one client is connected, and not in media mode. Each event updates `_typicalBufferBytes`.

### Continuous stream pump (`ContinuousStreamPumpAsync`)

WASAPI loopback stops firing during show-silence gaps, so the server runs a fixed 10 ms PCM clock:

- Skips when not capturing or no clients are connected.
- **Live loopback:** each tick dequeues a loopback buffer when available, otherwise sends synthetic silence (`CreateSilenceChunk`). Float silence writes `1e-5f` on the first sample for strict clients (e.g. Tesla browser).
- **Media mode:** only dequeues file PCM from `EnqueueMediaAudio` (no synthetic silence).
- Loopback enqueue is capped at `MaxQueuedChunks` (8); oldest buffers drop on overflow.

### Broadcast (`BroadcastToClientsAsync`)

- Fans out one PCM chunk per pump tick to all open clients via `Task.WhenAll` of `SendAudioAsync`.
- Clients are tracked in `ConcurrentDictionary<string, WebSocket> _clients`, keyed by `Guid`.
- Per-client send exceptions are swallowed; the client is removed in the `HandleClientAsync` finally block.

### Client handshake (`HandleClientAsync`, `:118`)

1. Accept the WebSocket.
2. Send one **text** frame: the JSON format descriptor.
3. Stream subsequent **binary** frames = raw PCM exactly as captured (no WAV header, no framing, no length prefix).
4. A 256-byte receive buffer polls only for the close frame.

### Format descriptor (text frame)

```json
{ "type": "format", "sampleRate": 48000, "bitsPerSample": 32, "channels": 2, "sampleFormat": "float" }
```

(Values are whatever the device reports; the example is illustrative.)

### Client playback (`PCMPlayerProcessor.js`, AudioWorklet `'pcm-player-processor'`)

- `handleMessage` decodes a binary chunk to `Float32Array` (`decodeToFloat32`), resamples if `sourceSampleRate !== playbackSampleRate` (linear interpolation), and appends to an internal buffer.
- Buffer cap = `playbackSampleRate * channels * 2` samples (~2 seconds); oldest samples are discarded channel-aligned on overflow.
- `process()` deinterleaves the buffer into the output channels per 128-frame render quantum; on underrun fills output channels with `0` to keep the worklet graph alive.
- See [web-ui](../../client/web-ui/web-ui.md) for the non-worklet fallback (`playPcmChunkFallback`).

## Key Classes

| Class / function | File | Responsibility |
|------------------|------|----------------|
| `AudioCapture` | `TeslaPCInterface/AudioStreamingServer.cs` | WASAPI loopback capture, format discovery, client broadcast |
| `AudioCapture.HandleClientAsync` | `AudioStreamingServer.cs:118` | Accept WS, send format, manage client lifecycle |
| `AudioCapture.ContinuousStreamPumpAsync` | `AudioStreamingServer.cs` | Fixed-clock PCM output (loopback or synthetic silence) |
| `AudioCapture.ResolveSampleFormat` | `AudioStreamingServer.cs:83` | Map device `WaveFormat` to a format string |
| `PCMPlayerProcessor` | `TeslaPCInterface/PCMPlayerProcessor.js` | AudioWorklet: decode, resample, buffer, output |

## Constants

| Constant | Value | Location |
|----------|-------|----------|
| Server-side format | runtime from device (not hardcoded) | `AudioStreamingServer.cs:42` |
| Close-frame receive buffer | `256` bytes | `AudioStreamingServer.cs:150` |
| Stream pump interval | `10` ms (`StreamPumpIntervalMs`) | `AudioStreamingServer.cs` |
| PCM queue cap | `8` (`MaxQueuedChunks`) | `AudioStreamingServer.cs` |
| Synthetic silence marker | `1e-5f` on first float sample | `AudioStreamingServer.cs` |
| Client buffer cap | `playbackSampleRate * channels * 2` samples (~2 s) | `PCMPlayerProcessor.js:21` |
| pcm16 / pcm24 / pcm32 divisors | `32768.0` / `8388608.0` / `2147483648.0` | `PCMPlayerProcessor.js:49,62,71` |
| AudioWorklet module name | `'pcm-player-processor'` | `PCMPlayerProcessor.js:138` |

## Wire Protocol Summary

| Direction | Frame type | Content |
|-----------|-----------|---------|
| server → client (first) | Text | JSON format descriptor |
| server → client (subsequent) | Binary | raw PCM chunk (one per `DataAvailable` event) |
| client → server | — | none processed except the close frame |

## Routes and Access Control

| Route | Protocol | Auth | Handler |
|-------|----------|------|---------|
| `/ws/audio` | WebSocket | none | `AudioCapture.HandleClientAsync` |

## Integration Points

- Routed from [web-server](../../server/web-server/web-server.md) (`/ws/audio` prefix match, highest routing priority).
- Started in `Program.Main` (`audioCapture.StartCapturing()`); disposed on shutdown.

## Database Schema

None.

## SQL Artifacts

None.
