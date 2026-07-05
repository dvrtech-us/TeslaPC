using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
namespace Streaming;

/// <summary>
/// WebSocket display transport for MJPEG or H264 frames with an 8-byte host PTS prefix (format v2).
/// </summary>
internal sealed class DisplayWebSocket
{
    private const int PtsPrefixBytes = 8;

    private readonly StreamTiming _timing;
    private readonly ConcurrentDictionary<string, DisplayWsClient> _clients = new();
    private volatile bool _needH264;
    private H264FfmpegEncoder? _h264Encoder;
    private int _h264EncoderWidth;
    private int _h264EncoderHeight;

    public DisplayWebSocket(StreamTiming timing) => _timing = timing;

    public int ClientCount => _clients.Count;

    public bool NeedH264 => _needH264;

    public async Task HandleClientAsync(HttpListenerContext context, int outWidth, int outHeight, int fps, Action? onClientConnected = null)
    {
        WebSocket socket;
        var clientId = Guid.NewGuid().ToString();

        try
        {
            var wsContext = await context.AcceptWebSocketAsync(null);
            socket = wsContext.WebSocket;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Display WebSocket accept failed: {ex.Message}");
            return;
        }

        string renderer = (GetQueryParam(context.Request.Url?.Query, "renderer") ?? AppSettings.DisplayRenderer).Trim().ToLowerInvariant();
        if (renderer != "h264")
            renderer = "mjpeg";

        if (renderer == "h264" && !new H264FfmpegEncoder().IsAvailable)
        {
            renderer = "mjpeg";
            Console.WriteLine("[Display] H264 requested but ffmpeg not found — falling back to MJPEG.");
        }

        var client = new DisplayWsClient
        {
            Id = clientId,
            Socket = socket,
            Renderer = renderer,
            OutWidth = outWidth,
            OutHeight = outHeight,
        };

        try
        {
            var formatJson = JsonSerializer.Serialize(new
            {
                type = "format",
                formatVersion = 2,
                renderer,
                width = outWidth,
                height = outHeight,
                fps,
                streamEpochUs = _timing.StreamEpochUs,
                hostClockHz = _timing.HostClockHz,
            });
            var formatBytes = Encoding.UTF8.GetBytes(formatJson);
            await socket.SendAsync(formatBytes, WebSocketMessageType.Text, true, CancellationToken.None);

            _clients.TryAdd(clientId, client);
            if (renderer == "h264")
            {
                _needH264 = true;
                EnsureH264Encoder(outWidth, outHeight, fps);
            }

            onClientConnected?.Invoke();
            Console.WriteLine($"Display client connected: {clientId} ({renderer} {outWidth}x{outHeight})");

            var recv = new byte[128];
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(recv, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Display client error ({clientId}): {ex.Message}");
        }
        finally
        {
            _clients.TryRemove(clientId, out _);
            RecalculateH264Need();
            Console.WriteLine($"Display client disconnected: {clientId}");
            if (socket.State != WebSocketState.Closed && socket.State != WebSocketState.Aborted)
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
                catch { /* ignore */ }
            }
            socket.Dispose();
        }
    }

    public void BroadcastFrame(long hostPtsUs, byte[] jpeg, byte[]? h264)
    {
        if (_clients.IsEmpty)
            return;

        foreach (var client in _clients.Values)
        {
            byte[]? payload = client.Renderer == "h264" ? h264 : jpeg;
            if (payload == null || payload.Length == 0)
                continue;

            if (client.Socket.State != WebSocketState.Open)
                continue;

            var packet = new byte[PtsPrefixBytes + payload.Length];
            BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(0, PtsPrefixBytes), hostPtsUs);
            Buffer.BlockCopy(payload, 0, packet, PtsPrefixBytes, payload.Length);

            _ = SendAsync(client.Socket, packet);
        }
    }

    public byte[]? EncodeH264(byte[] bgr24, int width, int height, int fps)
    {
        if (!_needH264 || bgr24.Length == 0)
            return null;

        EnsureH264Encoder(width, height, fps);
        return _h264Encoder?.EncodeFrame(bgr24);
    }

    public void Shutdown()
    {
        _clients.Clear();
        _needH264 = false;
        _h264Encoder?.Dispose();
        _h264Encoder = null;
    }

    private void RecalculateH264Need()
    {
        _needH264 = false;
        foreach (var c in _clients.Values)
        {
            if (c.Renderer == "h264")
            {
                _needH264 = true;
                return;
            }
        }

        _h264Encoder?.Dispose();
        _h264Encoder = null;
    }

    private void EnsureH264Encoder(int width, int height, int fps)
    {
        if (_h264Encoder != null && _h264EncoderWidth == width && _h264EncoderHeight == height)
            return;

        _h264Encoder?.Dispose();
        _h264Encoder = new H264FfmpegEncoder();
        if (!_h264Encoder.IsAvailable)
        {
            _h264Encoder.Dispose();
            _h264Encoder = null;
            _needH264 = false;
            return;
        }

        _h264Encoder.Start(width, height, fps);
        _h264EncoderWidth = width;
        _h264EncoderHeight = height;
    }

    private static string? GetQueryParam(string? query, string key)
    {
        if (string.IsNullOrEmpty(query))
            return null;

        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Equals(key, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(kv[1]);
        }
        return null;
    }

    private static async Task SendAsync(WebSocket socket, byte[] packet)
    {
        try
        {
            await socket.SendAsync(packet, WebSocketMessageType.Binary, true, CancellationToken.None);
        }
        catch
        {
            // Client cleanup happens on receive loop exit.
        }
    }

    private sealed class DisplayWsClient
    {
        public required string Id { get; init; }
        public required WebSocket Socket { get; init; }
        public required string Renderer { get; init; }
        public int OutWidth { get; init; }
        public int OutHeight { get; init; }
    }
}