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
        private Task? _broadcastTask;
        private Task? _keepaliveTask;

        // WASAPI loopback often stops firing during short show-silence gaps. Inject zero PCM
        // frames so clients keep decoding until real audio returns or the idle cap is hit.
        private const int SilenceKeepaliveMaxSeconds = 30;
        /// <summary>Loopback poll interval; must be well below <see cref="SilenceKeepaliveStartMs"/>.</summary>
        private const int KeepaliveIntervalMs = 20;
        /// <summary>
        /// WASAPI still delivers buffers with short gaps during normal playback; only inject silence
        /// after this much continuous idle time (show-silence gaps, not inter-buffer timing).
        /// </summary>
        private const int SilenceKeepaliveStartMs = 250;

        private DateTime _lastRealAudioUtc = DateTime.UtcNow;
        private int _typicalBufferBytes;

        private readonly ConcurrentQueue<byte[]> _audioDataQueue = new();
        private readonly SemaphoreSlim _audioDataSignal = new(0);
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
            _audioDataQueue.Enqueue(pcm);
            _audioDataSignal.Release();
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

                _lastRealAudioUtc = DateTime.UtcNow;
                _typicalBufferBytes = e.ByteCount;

                // In media mode the file's audio (from MediaStreamer) owns the queue; suppress
                // loopback so the two sources don't interleave.
                if (!_clients.IsEmpty && !_mediaMode)
                {
                    // Copy the raw PCM data (no WaveWriter/WAV header)
                    byte[] buffer = new byte[e.ByteCount];
                    Array.Copy(e.Data, e.Offset, buffer, 0, e.ByteCount);
                    _audioDataQueue.Enqueue(buffer);
                    _audioDataSignal.Release();
                }
            };

            capture.Start();
            _capturing = true;
            _lastRealAudioUtc = DateTime.UtcNow;

            // Start a background task that broadcasts audio to all connected clients
            _broadcastTask = Task.Run(() => BroadcastAudioAsync(_cancellationTokenSource.Token));
            _keepaliveTask = Task.Run(() => SilenceKeepaliveAsync(_cancellationTokenSource.Token));
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
        /// Injects zero-filled PCM chunks while WASAPI is idle so browsers keep receiving
        /// frames during short show-silence gaps. Stops after <see cref="SilenceKeepaliveMaxSeconds"/>
        /// of continuous idle time.
        /// </summary>
        private async Task SilenceKeepaliveAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(KeepaliveIntervalMs, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!_capturing || _mediaMode || _clients.IsEmpty || _typicalBufferBytes <= 0)
                    continue;

                var idle = DateTime.UtcNow - _lastRealAudioUtc;
                if (idle.TotalSeconds >= SilenceKeepaliveMaxSeconds)
                    continue;

                if (idle.TotalMilliseconds < SilenceKeepaliveStartMs)
                    continue;

                // Don't run ahead of the broadcaster — avoids a backlog of silence frames that
                // delays real audio when the show resumes.
                if (!_audioDataQueue.IsEmpty)
                    continue;

                var silence = new byte[_typicalBufferBytes];
                _audioDataQueue.Enqueue(silence);
                _audioDataSignal.Release();
            }
        }

        /// <summary>
        /// Broadcasts queued audio data to all connected WebSocket clients.
        /// Uses a semaphore to avoid CPU spinning when the queue is empty.
        /// </summary>
        private async Task BroadcastAudioAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _audioDataSignal.WaitAsync(cancellationToken);

                if (_audioDataQueue.TryDequeue(out var buffer))
                {
                    var sendTasks = new List<Task>(_clients.Count);
                    foreach (var ws in _clients.Values)
                    {
                        if (ws.State == WebSocketState.Open)
                            sendTasks.Add(SendAudioAsync(ws, buffer));
                    }

                    if (sendTasks.Count > 0)
                        await Task.WhenAll(sendTasks);
                }
            }
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

                    foreach (var task in new[] { _broadcastTask, _keepaliveTask })
                    {
                        if (task == null) continue;
                        try
                        {
                            task.Wait(TimeSpan.FromSeconds(2));
                        }
                        catch (AggregateException)
                        {
                            // Task was cancelled
                        }
                    }

                    _cancellationTokenSource.Dispose();
                    _audioDataSignal.Dispose();
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
