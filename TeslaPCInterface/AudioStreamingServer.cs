using CSCore;
using CSCore.SoundIn;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AudioStreamingServer
{
    public class AudioCapture : IDisposable
    {
        private readonly StreamTiming _timing;
        private WasapiLoopbackCapture? capture = null;
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private bool _disposed = false;
        private bool _capturing = false;
        private Task? _streamPumpTask;

        // WASAPI loopback stops firing during show-silence gaps. The stream pump sends PCM on a
        // fixed clock: real loopback buffers when available, synthetic silence otherwise.
        private const int StreamPumpIntervalMs = 10;
        /// <summary>Sub-audible float sample on synthetic silence (~-100 dBFS) for strict clients.</summary>
        private const float SilenceMarkerLevel = 1e-5f;
        private const int MaxQueuedChunks = 8;

        private int _typicalBufferBytes;

        private readonly ConcurrentQueue<byte[]> _audioDataQueue = new();
        private readonly ConcurrentDictionary<string, WebSocket> _clients = new();

        private int _sampleRate;
        private int _bitsPerSample;
        private int _channels;
        private string _sampleFormat = "float";

        // Media mode: while MediaStreamer plays a file it pushes the file's decoded PCM into the same
        // queue via EnqueueMediaAudio, and loopback enqueuing is suppressed so the two don't mix.
        private volatile bool _mediaMode;

        public AudioCapture(StreamTiming timing) => _timing = timing;

        /// <summary>Device sample rate (Hz) announced to audio clients.</summary>
        public int SampleRate => _sampleRate;
        /// <summary>Device channel count announced to audio clients.</summary>
        public int Channels => _channels;
        /// <summary>Device sample format ("float" | "pcm16" | "pcm24" | "pcm32") announced to clients.</summary>
        public string SampleFormat => _sampleFormat;

        /// <summary>
        /// When true, system-loopback capture stops feeding the broadcast queue; MediaStreamer
        /// supplies audio via <see cref="EnqueueMediaAudio"/> instead.
        /// </summary>
        public bool MediaMode
        {
            get => _mediaMode;
            set => _mediaMode = value;
        }

        /// <summary>
        /// Pushes a frame-aligned PCM buffer (in the announced device format) into the broadcast
        /// queue. Used by MediaStreamer to stream a decoded video file's audio over /ws/audio.
        /// Buffers are dropped when no client is connected so the queue can't grow unbounded.
        /// </summary>
        public void EnqueueMediaAudio(byte[] pcm)
        {
            if (pcm.Length == 0 || _clients.IsEmpty)
                return;
            EnqueuePcm(pcm);
        }

        /// <summary>
        /// Starts capturing audio from the default loopback device (system audio).
        /// Reads the actual device format so the client knows how to decode.
        /// </summary>
        public void StartCapturing()
        {
            if (_capturing)
                return;

            capture = new WasapiLoopbackCapture();
            capture.Initialize();

            // Read the actual capture format from the device
            var waveFormat = capture.WaveFormat;
            _sampleRate = waveFormat.SampleRate;
            _bitsPerSample = waveFormat.BitsPerSample;
            _channels = waveFormat.Channels;
            _sampleFormat = ResolveSampleFormat(waveFormat);

            Console.WriteLine($"Audio capture format: {_sampleRate}Hz, {_sampleFormat}, {_channels}ch");

            capture.DataAvailable += (s, e) =>
            {
                if (e.ByteCount <= 0)
                    return;

                _typicalBufferBytes = e.ByteCount;

                // In media mode the file's audio (from MediaStreamer) owns the queue; suppress
                // loopback so the two sources don't interleave.
                if (!_clients.IsEmpty && !_mediaMode)
                {
                    byte[] buffer = new byte[e.ByteCount];
                    Array.Copy(e.Data, e.Offset, buffer, 0, e.ByteCount);
                    EnqueuePcm(buffer);
                }
            };

            capture.Start();
            _capturing = true;

            _streamPumpTask = Task.Run(() => ContinuousStreamPumpAsync(_cancellationTokenSource.Token));
        }

        private void EnqueuePcm(byte[] buffer)
        {
            _audioDataQueue.Enqueue(buffer);
            while (_audioDataQueue.Count > MaxQueuedChunks && _audioDataQueue.TryDequeue(out _))
            {
                // Drop oldest buffers so a burst of loopback events cannot build unbounded latency.
            }
        }

        /// <summary>
        /// Returns a JSON string with the audio format metadata for the client.
        /// </summary>
        private string GetFormatMetadata()
        {
            return JsonSerializer.Serialize(new
            {
                type = "format",
                formatVersion = 2,
                sampleRate = _sampleRate,
                bitsPerSample = _bitsPerSample,
                channels = _channels,
                sampleFormat = _sampleFormat,
                streamEpochUs = _timing.StreamEpochUs,
                hostClockHz = _timing.HostClockHz,
            });
        }

        private static string ResolveSampleFormat(WaveFormat waveFormat)
        {
            if (waveFormat.WaveFormatTag == AudioEncoding.IeeeFloat)
                return "float";

            if (waveFormat.WaveFormatTag == AudioEncoding.Pcm)
                return PcmFormatName(waveFormat.BitsPerSample);

            if (waveFormat.WaveFormatTag == AudioEncoding.Extensible && waveFormat is WaveFormatExtensible extensible)
            {
                if (extensible.SubFormat == AudioSubTypes.IeeeFloat)
                    return "float";
                if (extensible.SubFormat == AudioSubTypes.Pcm)
                    return PcmFormatName(waveFormat.BitsPerSample);
            }

            Console.WriteLine($"Unknown audio encoding tag {waveFormat.WaveFormatTag}, inferring from bit depth");
            return waveFormat.BitsPerSample == 32 ? "float" : PcmFormatName(waveFormat.BitsPerSample);
        }

        private static string PcmFormatName(int bitsPerSample)
        {
            return bitsPerSample switch
            {
                16 => "pcm16",
                24 => "pcm24",
                32 => "pcm32",
                _ => $"pcm{bitsPerSample}"
            };
        }

        /// <summary>
        /// Handles a single WebSocket client from the unified server.
        /// Sends format metadata, then keeps alive until disconnect.
        /// </summary>
        public async Task HandleClientAsync(HttpListenerContext listenerContext)
        {
            WebSocket webSocket;
            string clientId = Guid.NewGuid().ToString();

            try
            {
                var webSocketContext = await listenerContext.AcceptWebSocketAsync(null);
                webSocket = webSocketContext.WebSocket;
            }
            catch (Exception e)
            {
                Console.WriteLine($"WebSocket accept failed: {e.Message}");
                return;
            }

            try
            {
                // Send format metadata as the first text message
                var formatJson = GetFormatMetadata();
                var formatBytes = Encoding.UTF8.GetBytes(formatJson);
                await webSocket.SendAsync(
                    new ArraySegment<byte>(formatBytes),
                    WebSocketMessageType.Text,
                    true,
                    _cancellationTokenSource.Token);

                // Register this client for broadcasting
                _clients.TryAdd(clientId, webSocket);
                Console.WriteLine($"Audio client connected: {clientId}");

                // Keep the connection alive by reading (handles close frames)
                var recvBuffer = new byte[256];
                while (webSocket.State == WebSocketState.Open)
                {
                    var result = await webSocket.ReceiveAsync(
                        new ArraySegment<byte>(recvBuffer), _cancellationTokenSource.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Audio client error ({clientId}): {e.Message}");
            }
            finally
            {
                _clients.TryRemove(clientId, out _);
                Console.WriteLine($"Audio client disconnected: {clientId}");
                if (webSocket.State != WebSocketState.Closed && webSocket.State != WebSocketState.Aborted)
                {
                    try
                    {
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                    }
                    catch { }
                }
                webSocket.Dispose();
            }
        }

        /// <summary>
        /// Sends PCM on a fixed clock while clients are connected. Live loopback mode always
        /// emits a chunk (real audio or synthetic silence). Media mode only sends queued file PCM.
        /// </summary>
        private async Task ContinuousStreamPumpAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(StreamPumpIntervalMs, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!_capturing || _clients.IsEmpty)
                    continue;

                if (_mediaMode)
                {
                    if (_audioDataQueue.TryDequeue(out var mediaChunk))
                        await BroadcastToClientsAsync(mediaChunk);
                    continue;
                }

                byte[] chunk = _audioDataQueue.TryDequeue(out var liveChunk)
                    ? liveChunk
                    : CreateSilenceChunk(StreamChunkBytes());
                await BroadcastToClientsAsync(chunk);
            }
        }

        private int BytesPerSecond =>
            _sampleRate * _channels * Math.Max(1, _bitsPerSample / 8);

        private int StreamChunkBytes()
        {
            if (_typicalBufferBytes > 0)
                return _typicalBufferBytes;

            int bytesPerFrame = _channels * Math.Max(1, _bitsPerSample / 8);
            return Math.Max(bytesPerFrame, BytesPerSecond * StreamPumpIntervalMs / 1000);
        }

        /// <summary>
        /// Synthetic silence with a sub-audible marker on the first float sample so strict
        /// clients keep the audio session active during show-silence gaps.
        /// </summary>
        private byte[] CreateSilenceChunk(int length)
        {
            var chunk = new byte[length];
            if (_sampleFormat == "float" && length >= 4)
                BitConverter.TryWriteBytes(chunk.AsSpan(0, 4), BitConverter.SingleToInt32Bits(SilenceMarkerLevel));
            return chunk;
        }

        private async Task BroadcastToClientsAsync(byte[] buffer)
        {
            if (buffer.Length == 0 || _clients.IsEmpty)
                return;

            var sendTasks = new List<Task>(_clients.Count);
            foreach (var ws in _clients.Values)
            {
                if (ws.State == WebSocketState.Open)
                    sendTasks.Add(SendAudioAsync(ws, buffer));
            }

            if (sendTasks.Count > 0)
                await Task.WhenAll(sendTasks);
        }

        private async Task SendAudioAsync(WebSocket ws, byte[] buffer)
        {
            try
            {
                var packet = new byte[8 + buffer.Length];
                BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(0, 8), _timing.HostPtsUs);
                Buffer.BlockCopy(buffer, 0, packet, 8, buffer.Length);
                await ws.SendAsync(
                    new ArraySegment<byte>(packet),
                    WebSocketMessageType.Binary,
                    true,
                    CancellationToken.None);
            }
            catch
            {
                // Client will be cleaned up in HandleClientAsync
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _cancellationTokenSource.Cancel();

                    foreach (var ws in _clients.Values)
                    {
                        if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                        {
                            try
                            {
                                ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None)
                                    .GetAwaiter().GetResult();
                            }
                            catch { }
                        }
                        ws.Dispose();
                    }
                    _clients.Clear();

                    capture?.Stop();
                    capture?.Dispose();
                    capture = null;
                    _capturing = false;

                    if (_streamPumpTask != null)
                    {
                        try
                        {
                            _streamPumpTask.Wait(TimeSpan.FromSeconds(2));
                        }
                        catch (AggregateException)
                        {
                            // Task was cancelled
                        }
                    }

                    _cancellationTokenSource.Dispose();
                }
                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}