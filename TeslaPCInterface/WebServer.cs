using Streaming;
using AudioStreamingServer;
using System.Net.WebSockets;
using System.Net;
using System.Text.Json;
using System.Text;
using System;

public class WebServer
{
    private readonly HttpListener _Listener = new HttpListener();
    private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

    private readonly ImageStreamingServer _imageStreamer;
    private readonly AudioCapture _audioCapture;

    public WebServer(ImageStreamingServer imageStreamer, AudioCapture audioCapture)
    {
        _imageStreamer = imageStreamer;
        _audioCapture = audioCapture;
    }

    public async Task StartWebServerAsync(
        int port,
        int sslPort,
        bool localhostOnly = false,
        bool enableHttps = true,
        TaskCompletionSource<bool>? started = null)
    {
        if (localhostOnly)
        {
            // Use strong wildcard; http.sys host-specific 127.0.0.1 registrations
            // can reject requests before they reach GetContext (503, arrived=0).
            _Listener.Prefixes.Add($"http://+:{port}/");
        }
        else
        {
            // Strong wildcard (+) matches the http.sys URL ACL reservations added by
            // SslCertificateBootstrap (http://+:port/, https://+:sslPort/). A weak
            // wildcard (*) registers a different URL group than the reservation, so
            // http.sys rejects every request with 503 before it reaches GetContext.
            _Listener.Prefixes.Add($"http://+:{port}/");
            if (enableHttps)
                _Listener.Prefixes.Add($"https://+:{sslPort}/");
        }

        Console.WriteLine("Unified server listening on: ");
        foreach (var prefix in _Listener.Prefixes)
        {
            Console.WriteLine("\t" + prefix);
        }

        try
        {
            _Listener.Start();
        }
        catch (HttpListenerException ex)
        {
            Console.WriteLine($"Failed to start HTTP listener (exit {ex.ErrorCode}): {ex.Message}");
            if (!localhostOnly)
            {
                Console.WriteLine("Binding to all interfaces requires administrator privileges or a URL ACL reservation.");
                Console.WriteLine("Run as administrator, or reserve the URL with:");
                Console.WriteLine($"  netsh http add urlacl url=http://+:{port}/ user=Everyone");
                Console.WriteLine($"  netsh http add urlacl url=https://+:{sslPort}/ user=Everyone");
                Console.WriteLine("For local testing only, pass --localhost.");
            }
            started?.TrySetException(ex);
            throw;
        }

        // Dedicated accept thread. http.sys returns 503 until GetContext() is actively
        // dequeuing; a delayed thread-pool start leaves the port registered but dead.
        var acceptThread = new Thread(AcceptLoop)
        {
            IsBackground = true,
            Name = "HttpAccept"
        };
        acceptThread.Start();
        started?.TrySetResult(true);

        // Keep serverTask alive until shutdown cancels the accept loop.
        await Task.Delay(Timeout.Infinite, _cancellationTokenSource.Token);
    }

    private void AcceptLoop()
    {
        Console.WriteLine("[HTTP] Accept loop started");
        while (!_cancellationTokenSource.IsCancellationRequested && _Listener.IsListening)
        {
            try
            {
                var context = _Listener.GetContext();
                ThreadPool.QueueUserWorkItem(_ => ProcessRequest(context));
            }
            catch (HttpListenerException) when (_cancellationTokenSource.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (_cancellationTokenSource.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HTTP] Accept error: {ex.Message}");
                Thread.Sleep(50);
            }
        }

        if (!_cancellationTokenSource.IsCancellationRequested)
            Console.WriteLine("[HTTP] Accept loop stopped unexpectedly; new requests will return 503 until restart.");
    }

    private void ProcessRequest(object? state)
    {
        var context = (HttpListenerContext)state!;
        try
        {
            ProcessRequestAsync(context).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HTTP] Request error ({context.Request.HttpMethod} {context.Request.Url?.LocalPath}): {ex.Message}");
            try
            {
                if (context.Response.OutputStream.CanWrite)
                {
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                }
            }
            catch { }
        }
    }

    private async Task ProcessRequestAsync(HttpListenerContext context)
    {
        string path = context.Request.Url?.LocalPath ?? "/";
        Console.WriteLine($"[HTTP] {context.Request.HttpMethod} {path}");
        await HandleRequest(context);
    }

    /// <summary>
    /// Routes all incoming requests by path:
    ///   /stream       → MJPEG video stream
    ///   /ws/audio     → Audio WebSocket
    ///   /ws/* or WS   → Input WebSocket (mouse/keyboard)
    ///   everything else → static file serving
    /// </summary>
    private async Task HandleRequest(HttpListenerContext context)
    {
        string path = context.Request.Url?.LocalPath ?? "/";

        if (context.Request.IsWebSocketRequest)
        {
            if (path.StartsWith("/ws/audio", StringComparison.OrdinalIgnoreCase))
            {
                await _audioCapture.HandleClientAsync(context);
            }
            else
            {
                await AcceptWebSocketAsync(context);
            }
        }
        else if (path.Equals("/stream", StringComparison.OrdinalIgnoreCase))
        {
            _imageStreamer.HandleStreamRequest(context);
        }
        else
        {
            HandleHttpAsync(context);
        }
    }

    private void HandleHttpAsync(HttpListenerContext context)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;
        string PathToHtml = "";
        string rootPath = "";
        var runningInDebugMode = false;

        string requestPath = request.Url.LocalPath.TrimStart('/');

        //detect if visual studio is running in debug mode
        if (System.Diagnostics.Debugger.IsAttached)
        {
            runningInDebugMode = true;
        }
        if (runningInDebugMode)
        {
            var location = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (!string.IsNullOrEmpty(location) && location.Contains("bin"))
            {
                rootPath = location.Substring(0, location.LastIndexOf("bin"));
            }
            else
            {
                rootPath = location ?? AppContext.BaseDirectory;
            }
            Console.WriteLine("Path to html: " + rootPath);
        }
        else
        {
            rootPath = AppContext.BaseDirectory;
        }
        //see if the request is for the html file
        if (request.Url.LocalPath == "/")
        {
            PathToHtml = Path.Combine(rootPath, "index.html");
        }
        else
        {
            PathToHtml = Path.Combine(rootPath, request.Url.LocalPath.TrimStart('/'));
        }
        //if the file does not exist, return a 404 error
        if (!File.Exists(PathToHtml))
        {
            response.StatusCode = 404;
            response.Close();
            return;
        }
        //only allow html, js , css and image files to be served
        if (!PathToHtml.EndsWith(".html") && !PathToHtml.EndsWith(".js") && !PathToHtml.EndsWith(".css") && !PathToHtml.EndsWith(".png") && !PathToHtml.EndsWith(".jpg"))
        {
            response.StatusCode = 403;
            response.Close();
            return;
        }

        string responseString = File.ReadAllText(PathToHtml);
        response.StatusCode = 200;
        //serve the right content type
        response.ContentType = getContentType(PathToHtml);


        byte[] buffer = Encoding.UTF8.GetBytes(responseString);
        response.ContentLength64 = buffer.Length;
        System.IO.Stream output = response.OutputStream;
        output.Write(buffer, 0, buffer.Length);
        output.Close();
        response.Close();
        return;
    }

    private string getContentType(string path)
    {
        if (path.EndsWith(".html"))
        {
            return "text/html";
        }
        else if (path.EndsWith(".js"))
        {
            return "application/javascript";
        }
        else if (path.EndsWith(".css"))
        {
            return "text/css";
        }
        else if (path.EndsWith(".png"))
        {
            return "image/png";
        }
        else if (path.EndsWith(".jpg"))
        {
            return "image/jpeg";
        }
        else
        {
            return "text/plain";
        }
    }

    private async Task AcceptWebSocketAsync(HttpListenerContext context)
    {
        HttpListenerWebSocketContext webSocketContext = await context.AcceptWebSocketAsync(null);
        WebSocket webSocket = webSocketContext.WebSocket;

        while (webSocket.State == WebSocketState.Open)
        {
            try
            {
                var buffer = new byte[1024 * 4];
                var receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancellationTokenSource.Token);
                if (receiveResult.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                    break;
                }

                Console.WriteLine($"Received message: {Encoding.UTF8.GetString(buffer, 0, receiveResult.Count)}");
                //Received message: {"x":513,"y":369}
                //decode the message
                var message = Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);
                var mousePosition = JsonSerializer.Deserialize<MousePosition>(message);
                Console.WriteLine($"Mouse position: {mousePosition.X}, {mousePosition.Y}");
                //move the mouse
                mousePosition = mousePosition.GetAdjusted();
                Console.WriteLine($"Adjusted Mouse position: {mousePosition.X}, {mousePosition.Y}");

                Win32.SetCursorPos(mousePosition.X, mousePosition.Y);
                if (mousePosition.Type == "down")
                {
                    Win32.mouse_event(Win32.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
                }

                if (mousePosition.Type == "up")
                {
                    Win32.mouse_event(Win32.MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
                }



            }
            catch (WebSocketException e)
            {
                Console.WriteLine($"WebSocket error: {e.Message}");
            }
        }
    }

    public Task StopAsync()
    {
        _cancellationTokenSource.Cancel();
        try
        {
            if (_Listener.IsListening)
                _Listener.Stop();
            _Listener.Close();
        }
        catch (HttpListenerException)
        {
            // Listener may already be stopped
        }
        return Task.CompletedTask;
    }
}
