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
    private readonly object _h264Lock = new();
    private volatile bool _needH264;
    private H264MediaFoundationEncoder? _h264Encoder;
    private int _h264EncoderWidth;
    private int _h264EncoderHeight;
    private int _h264EncoderFps;

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

        if (renderer == "h264" && !H264MediaFoundationEncoder.IsAvailable)
        {
            renderer = "mjpeg";
            Console.WriteLine("[Display] H264 requested but native Media Foundation encoder is unavailable — falling back to MJPEG.");
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

    public void BroadcastFrame(long hostPtsUs, byte[] jpeg, IReadOnlyList<byte[]>? h264Frames)
    {
        if (_clients.IsEmpty)
            return;

        foreach (var client in _clients.Values)
        {
            if (client.Socket.State != WebSocketState.Open)
                continue;

            if (client.Renderer == "h264")
            {
                if (h264Frames == null || h264Frames.Count == 0)
                    continue;

                foreach (var h264 in h264Frames)
                    SendFrame(client.Socket, hostPtsUs, h264);
            }
            else
            {
                SendFrame(client.Socket, hostPtsUs, jpeg);
            }
        }
    }

    public List<byte[]> EncodeH264(byte[] bgr24, int width, int height, int fps)
    {
        if (!_needH264 || bgr24.Length == 0)
            return [];

        lock (_h264Lock)
        {
            EnsureH264Encoder(width, height, fps);
            return _h264Encoder?.EncodeFrame(bgr24) ?? [];
        }
    }

    public void StopH264Encoder()
    {
        lock (_h264Lock)
        {
            _h264Encoder?.Dispose();
            _h264Encoder = null;
            _h264EncoderWidth = 0;
            _h264EncoderHeight = 0;
            _h264EncoderFps = 0;
        }
    }

    public void Shutdown()
    {
        _clients.Clear();
        _needH264 = false;
        StopH264Encoder();
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

        // The capture loop owns encoder creation/use. It observes NeedH264 and disposes the
        // encoder from that same thread on the next tick.
    }

    private void EnsureH264Encoder(int width, int height, int fps)
    {
        if (_h264Encoder != null && _h264EncoderWidth == width && _h264EncoderHeight == height && _h264EncoderFps == fps)
            return;

        _h264Encoder?.Dispose();
        _h264Encoder = null;
        if (!H264MediaFoundationEncoder.IsAvailable)
        {
            _needH264 = false;
            return;
        }

        _h264Encoder = new H264MediaFoundationEncoder();
        _h264Encoder.Start(width, height, fps);
        _h264EncoderWidth = width;
        _h264EncoderHeight = height;
        _h264EncoderFps = fps;
    }

    private static void SendFrame(WebSocket socket, long hostPtsUs, byte[] payload)
    {
        if (payload.Length == 0)
            return;

        var packet = new byte[PtsPrefixBytes + payload.Length];
        BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(0, PtsPrefixBytes), hostPtsUs);
        Buffer.BlockCopy(payload, 0, packet, PtsPrefixBytes, payload.Length);

        _ = SendAsync(socket, packet);
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
