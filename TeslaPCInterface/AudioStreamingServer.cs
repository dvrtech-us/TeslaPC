using CSCore;
using CSCore.SoundIn;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AudioStreamingServer
{
    public class AudioCapture : IDisposable
    {
        private WasapiLoopbackCapture? capture = null;
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private bool _disposed = false;

        private readonly ConcurrentQueue<byte[]> _audioDataQueue = new();
        private readonly SemaphoreSlim _audioDataSignal = new(0);
        private readonly ConcurrentDictionary<string, WebSocket> _clients = new();

        private int _sampleRate;
        private int _bitsPerSample;
        private int _channels;

        /// <summary>
        /// Starts capturing audio from the default loopback device (system audio).
        /// Reads the actual device format so the client knows how to decode.
        /// </summary>
        public void StartCapturing()
        {
            capture = new WasapiLoopbackCapture();
            capture.Initialize();

            // Read the actual capture format from the device
            var waveFormat = capture.WaveFormat;
            _sampleRate = waveFormat.SampleRate;
            _bitsPerSample = waveFormat.BitsPerSample;
            _channels = waveFormat.Channels;

            Console.WriteLine($"Audio capture format: {_sampleRate}Hz, {_bitsPerSample}-bit, {_channels}ch");

            capture.DataAvailable += (s, e) =>
            {
                if (e.ByteCount > 0)
                {
                    // Copy the raw PCM data (no WaveWriter/WAV header)
                    byte[] buffer = new byte[e.ByteCount];
                    Array.Copy(e.Data, e.Offset, buffer, 0, e.ByteCount);
                    _audioDataQueue.Enqueue(buffer);
                    _audioDataSignal.Release();
                }
            };

            capture.Start();

            // Start a background task that broadcasts audio to all connected clients
            _ = Task.Run(() => BroadcastAudioAsync(_cancellationTokenSource.Token));
        }

        /// <summary>
        /// Returns a JSON string with the audio format metadata for the client.
        /// </summary>
        private string GetFormatMetadata()
        {
            return JsonSerializer.Serialize(new
            {
                type = "format",
                sampleRate = _sampleRate,
                bitsPerSample = _bitsPerSample,
                channels = _channels
            });
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
                    CancellationToken.None);

                // Register this client for broadcasting
                _clients.TryAdd(clientId, webSocket);
                Console.WriteLine($"Audio client connected: {clientId}");

                // Keep the connection alive by reading (handles close frames)
                var recvBuffer = new byte[256];
                while (webSocket.State == WebSocketState.Open)
                {
                    var result = await webSocket.ReceiveAsync(
                        new ArraySegment<byte>(recvBuffer), CancellationToken.None);
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
                    var segment = new ArraySegment<byte>(buffer);

                    foreach (var kvp in _clients)
                    {
                        var ws = kvp.Value;
                        if (ws.State == WebSocketState.Open)
                        {
                            try
                            {
                                await ws.SendAsync(segment, WebSocketMessageType.Binary, true, CancellationToken.None);
                            }
                            catch
                            {
                                // Client will be cleaned up in HandleClientAsync
                            }
                        }
                    }
                }
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _cancellationTokenSource.Cancel();
                    capture?.Stop();
                    capture?.Dispose();
                    _cancellationTokenSource?.Dispose();
                    _audioDataSignal?.Dispose();
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
