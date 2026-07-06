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
- The `DataAvailable` handler copies `e.Data[e.Offset .. +e.ByteCount]` into a `byte[]`, enqueues into `ConcurrentQueue<byte[]> _audioDataQueue`, and releases a `SemaphoreSlim` when `e.ByteCount > 0`, at least one client is connected, and not in media mode. Each event also updates `_lastRealAudioUtc` and `_typicalBufferBytes`.

### Silence keepalive (`SilenceKeepaliveAsync`)

WASAPI loopback often stops firing during short show-silence gaps. A background task polls every `KeepaliveIntervalMs` (10 ms):

- Skips when not capturing, in media mode, no clients, or the broadcast queue is non-empty.
- If loopback has been idle for ≥ `SilenceKeepaliveStartMs` (100 ms) but < `SilenceKeepaliveMaxSeconds` (30 s), enqueues a synthetic silence chunk (`CreateSilenceChunk`) sized to the last `DataAvailable` buffer or a 10 ms format-derived default, then signals the broadcaster.
- Float keepalive chunks write `1e-5f` on the first sample so strict clients (e.g. Tesla browser) keep the audio session active instead of handing off to vehicle radio during digital silence.
- After 30 s of continuous idle, keepalive stops until the next real `DataAvailable` event.

### Broadcast (`BroadcastAudioAsync`, `:185`)

- Waits on the semaphore, dequeues one buffer at a time, and fans out to all open clients via `Task.WhenAll` of `SendAudioAsync` (`:206`).
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
| `AudioCapture.BroadcastAudioAsync` | `AudioStreamingServer.cs:185` | Dequeue chunks, fan out to all clients |
| `AudioCapture.ResolveSampleFormat` | `AudioStreamingServer.cs:83` | Map device `WaveFormat` to a format string |
| `PCMPlayerProcessor` | `TeslaPCInterface/PCMPlayerProcessor.js` | AudioWorklet: decode, resample, buffer, output |

## Constants

| Constant | Value | Location |
|----------|-------|----------|
| Server-side format | runtime from device (not hardcoded) | `AudioStreamingServer.cs:42` |
| Close-frame receive buffer | `256` bytes | `AudioStreamingServer.cs:150` |
| Silence keepalive poll | `10` ms (`KeepaliveIntervalMs`) | `AudioStreamingServer.cs` |
| Silence keepalive start | `100` ms idle (`SilenceKeepaliveStartMs`) | `AudioStreamingServer.cs` |
| Silence keepalive marker | `1e-5f` on first float sample | `AudioStreamingServer.cs` |
| Silence keepalive idle cap | `30` s (`SilenceKeepaliveMaxSeconds`) | `AudioStreamingServer.cs` |
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
