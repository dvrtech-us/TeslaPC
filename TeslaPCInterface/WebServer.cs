using Streaming;
using AudioStreamingServer;
using Media;
using System.Net.WebSockets;
using System.Net;
using System.Text.Json;
using System.Text;
using System;
using System.Web;

public class WebServer
{
    private readonly HttpListener _Listener = new HttpListener();
    private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

    //serve the html file

    private readonly ImageStreamingServer _imageStreamer;
    private readonly AudioCapture _audioCapture;
    private readonly MediaStreamer _media;
    private readonly MediaLibrary _library;

    public WebServer(ImageStreamingServer imageStreamer, AudioCapture audioCapture, MediaStreamer media, MediaLibrary library)
    {
        _imageStreamer = imageStreamer;
        _audioCapture = audioCapture;
        _media = media;
        _library = library;
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
        Log.Debug($"[HTTP] {context.Request.HttpMethod} {path}");
        await HandleRequest(context);
    }

    /// <summary>
    /// Routes all incoming requests by path:
    ///   /stream       Ã¢â€ â€™ MJPEG video stream
    ///   /ws/audio     Ã¢â€ â€™ Audio WebSocket
    ///   /ws/* or WS   Ã¢â€ â€™ Input WebSocket (mouse/keyboard)
    ///   everything else Ã¢â€ â€™ static file serving
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
            else if (path.StartsWith("/ws/display", StringComparison.OrdinalIgnoreCase))
            {
                await _imageStreamer.HandleDisplayWebSocketAsync(context);
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
        else if (path.StartsWith("/media/", StringComparison.OrdinalIgnoreCase))
        {
            HandleMedia(context, path);
        }
        else if (path.Equals("/config", StringComparison.OrdinalIgnoreCase))
        {
            HandleConfig(context);
        }
        else if (path.StartsWith("/display/", StringComparison.OrdinalIgnoreCase))
        {
            HandleDisplay(context, path);
        }
        else if (path.Equals("/version", StringComparison.OrdinalIgnoreCase))
        {
            WriteJson(context.Response, JsonSerializer.Serialize(new { version = AppSettings.Version }));
        }
        else
        {
            HandleHttpAsync(context);
        }
    }

    /// <summary>
    /// Configuration endpoint backing the web Settings page.
    ///   GET  /config  -> current values as JSON. The Cloudflare token is **never** returned (only a
    ///                    `cfTokenSet` flag), so the secret can't be read back over the network.
    ///   POST /config  -> save (application/x-www-form-urlencoded): httpsHost, acmeEmail, videoRoot,
    ///                    and optionally cfToken (blank = keep existing). The video folder applies
    ///                    immediately; host/token/email changes take effect on the next server restart.
    /// </summary>
    private void HandleConfig(HttpListenerContext context)
    {
        if (context.Request.HttpMethod == "POST")
        {
            string previousDisplayRenderer = AppSettings.DisplayRenderer;
            string previousDisplayTransport = AppSettings.DisplayTransport;
            string body;
            using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                body = reader.ReadToEnd();
            var form = HttpUtility.ParseQueryString(body);

            var toSave = new Dictionary<string, string>();
            string? host = form.Get("httpsHost");
            string? email = form.Get("acmeEmail");
            string? videoRoot = form.Get("videoRoot");
            string? cfToken = form.Get("cfToken");
            string? logLevel = form.Get("logLevel");
            string? streamHeight = form.Get("streamHeight");
            string? displayRenderer = form.Get("displayRenderer");
            string? displayTransport = form.Get("displayTransport");
            string? audioBoost = form.Get("audioBoost");
            string? h264BitrateMbps = form.Get("h264BitrateMbps");

            if (host != null) toSave[AppSettings.HttpsHostKey] = host.Trim();
            if (email != null) toSave[AppSettings.AcmeEmailKey] = email.Trim();
            if (videoRoot != null && !string.IsNullOrWhiteSpace(videoRoot)) toSave[AppSettings.VideoRootKey] = videoRoot.Trim();
            if (Log.Parse(logLevel) is { } _) toSave[AppSettings.LogLevelKey] = logLevel!.Trim().ToLowerInvariant();
            if (int.TryParse(streamHeight, out var sh) && sh >= 240 && sh <= 2160) toSave[AppSettings.StreamHeightKey] = sh.ToString();
            if (displayRenderer != null)
            {
                var r = displayRenderer.Trim().ToLowerInvariant();
                if (r == "h264" || r == "mjpeg") toSave[AppSettings.DisplayRendererKey] = r;
            }
            if (displayTransport != null)
            {
                var t = displayTransport.Trim().ToLowerInvariant();
                if (t == "http" || t == "websocket") toSave[AppSettings.DisplayTransportKey] = t;
            }
            if (audioBoost != null
                && double.TryParse(audioBoost.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var gain))
            {
                gain = Math.Clamp(gain, AppSettings.MinAudioBoost, AppSettings.MaxAudioBoost);
                toSave[AppSettings.AudioBoostKey] = gain.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (h264BitrateMbps != null
                && double.TryParse(h264BitrateMbps.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var mbps))
            {
                if (mbps <= 0)
                    toSave[AppSettings.H264BitrateKey] = "";
                else
                {
                    mbps = Math.Clamp(mbps, AppSettings.MinH264BitrateMbps, AppSettings.MaxH264BitrateMbps);
                    int bps = (int)Math.Round(mbps * 1_000_000);
                    bps = Math.Clamp(bps, AppSettings.MinH264Bitrate, AppSettings.MaxH264Bitrate);
                    toSave[AppSettings.H264BitrateKey] = bps.ToString();
                }
            }
            // Only overwrite the token when a non-blank value is supplied (the form leaves it blank to keep).
            if (!string.IsNullOrWhiteSpace(cfToken)) toSave[AppSettings.CloudflareTokenKey] = cfToken.Trim();

            try
            {
                AppSettings.Save(toSave);
                // The video folder and log level apply live; host/cert settings need a restart.
                if (toSave.ContainsKey(AppSettings.VideoRootKey))
                    _media.Root = toSave[AppSettings.VideoRootKey];
                if (toSave.ContainsKey(AppSettings.LogLevelKey))
                    Log.SetLevel(toSave[AppSettings.LogLevelKey]);
                if (toSave.ContainsKey(AppSettings.StreamHeightKey))
                {
                    int h = int.Parse(toSave[AppSettings.StreamHeightKey]);
                    _imageStreamer.SetMaxResolution(h * 4, h);
                }
                bool displayModeChanged =
                    (toSave.ContainsKey(AppSettings.DisplayRendererKey) && AppSettings.DisplayRenderer != previousDisplayRenderer) ||
                    (toSave.ContainsKey(AppSettings.DisplayTransportKey) && AppSettings.DisplayTransport != previousDisplayTransport);
                if (displayModeChanged)
                    _imageStreamer.RestartDisplayClients("display config changed");
                if (toSave.ContainsKey(AppSettings.H264BitrateKey))
                    _imageStreamer.RestartH264Encoder();
                bool restartNeeded = toSave.ContainsKey(AppSettings.HttpsHostKey)
                    || toSave.ContainsKey(AppSettings.CloudflareTokenKey)
                    || toSave.ContainsKey(AppSettings.AcmeEmailKey);
                Console.WriteLine("[Config] Saved settings via web UI.");
                WriteJson(context.Response, JsonSerializer.Serialize(new { saved = true, restartNeeded }));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Config] Save failed: {ex.Message}");
                WriteJson(context.Response, JsonSerializer.Serialize(new { saved = false, error = ex.Message }));
            }
            return;
        }

        // GET: report current values, but never the token itself.
        var (outW, outH) = _imageStreamer.StreamOutputSize;
        int autoBitrateBps = H264MediaFoundationEncoder.EstimateDefaultBitrateBps(
            outW, outH, _imageStreamer.StreamFps);
        var payload = new
        {
            httpsHost = AppSettings.Get(AppSettings.HttpsHostKey) ?? "",
            acmeEmail = AppSettings.Get(AppSettings.AcmeEmailKey) ?? "",
            videoRoot = _media.Root,
            logLevel = Log.LevelName,
            streamHeight = _imageStreamer.MaxHeight,
            displayRenderer = AppSettings.DisplayRenderer,
            displayTransport = AppSettings.DisplayTransport,
            h264Available = H264MediaFoundationEncoder.IsAvailable,
            h264BitrateMbps = AppSettings.H264BitrateMbps ?? 0,
            h264BitrateAutoMbps = autoBitrateBps / 1_000_000.0,
            audioBoost = AppSettings.AudioBoost,
            version = AppSettings.Version,
            cfTokenSet = !string.IsNullOrWhiteSpace(AppSettings.Get(AppSettings.CloudflareTokenKey))
        };
        WriteJson(context.Response, JsonSerializer.Serialize(payload));
    }

    /// <summary>
    /// Display-resolution endpoints (change the actual Windows desktop resolution):
    ///   /display/info          -> current resolution + supported modes (JSON)
    ///   /display/match?w=&h=   -> set the desktop to the supported mode best matching the given
    ///                             viewport aspect ratio (capped at the current stream height)
    ///   /display/set?w=&h=     -> set a specific supported resolution
    /// After a successful change the capture session is restarted (DXGI can't survive a mode switch).
    /// </summary>
    private void HandleDisplay(HttpListenerContext context, string path)
    {
        var query = HttpUtility.ParseQueryString(context.Request.Url?.Query ?? "");
        string action = path.Substring("/display/".Length).TrimEnd('/').ToLowerInvariant();
        bool changed = false;

        if (action == "match")
        {
            if (int.TryParse(query.Get("w"), out var vw) && int.TryParse(query.Get("h"), out var vh) && vw > 0 && vh > 0)
            {
                // Target a desktop resolution near the stream height so what we capture is ~1:1 with
                // what we stream (crisp, no extra down/upscaling) rather than an oversized mode.
                int maxH = Math.Max(480, _imageStreamer.MaxHeight);
                var best = DisplayManager.BestForAspect((double)vw / vh, maxH);
                if (best is { } m && (DisplayManager.Current() is var cur && (cur.Width != m.Width || cur.Height != m.Height)))
                {
                    changed = DisplayManager.TrySet(m.Width, m.Height);
                    if (changed) { _imageStreamer.RestartCapture(); Console.WriteLine($"[Display] Matched viewport {vw}x{vh} -> {m.Width}x{m.Height}"); }
                }
            }
        }
        else if (action == "set")
        {
            if (int.TryParse(query.Get("w"), out var w) && int.TryParse(query.Get("h"), out var h) && w > 0 && h > 0)
            {
                changed = DisplayManager.TrySet(w, h);
                if (changed) _imageStreamer.RestartCapture();
            }
        }

        var cur2 = DisplayManager.Current();
        WriteJson(context.Response, JsonSerializer.Serialize(new
        {
            changed,
            width = cur2.Width,
            height = cur2.Height,
            modes = DisplayManager.Modes().Select(m => new { w = m.Width, h = m.Height })
        }));
    }

    /// <summary>
    /// Media player control endpoints. All return the current player state as JSON so the player
    /// page can keep its controls in sync:
    ///   /media/play?path=...   start playing a file (must be under the browse root)
    ///   /media/pause           pause            /media/resume   resume
    ///   /media/seek?t=SECONDS  seek             /media/stop     stop, restore live screen
    ///   /media/status          current state
    /// </summary>
    private void HandleMedia(HttpListenerContext context, string path)
    {
        var query = HttpUtility.ParseQueryString(context.Request.Url?.Query ?? "");
        string action = path.Substring("/media/".Length).TrimEnd('/').ToLowerInvariant();

        switch (action)
        {
            case "play":
                string? file = query.Get("path");
                if (!string.IsNullOrEmpty(file))
                    _media.Play(file);
                break;
            case "pause":
                _media.Pause();
                break;
            case "resume":
                _media.Resume();
                break;
            case "stop":
                _media.Stop();
                break;
            case "seek":
                if (double.TryParse(query.Get("t"), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var t))
                    _media.Seek(t);
                break;
            case "status":
                break;
            default:
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
        }

        WriteJson(context.Response, JsonSerializer.Serialize(_media.Status()));
    }

    private static void WriteJson(HttpListenerResponse response, string json)
    {
        byte[] buffer = Encoding.UTF8.GetBytes(json);
        response.StatusCode = 200;
        response.ContentType = "application/json";
        response.ContentLength64 = buffer.Length;
        response.OutputStream.Write(buffer, 0, buffer.Length);
        response.OutputStream.Close();
        response.Close();
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
        if (response.ContentType == "text/html")
        {
            var QueryParameters = HttpUtility.ParseQueryString(request.Url.Query);
            if (request.Url.LocalPath == "/list.html")
            {
                string guts = "";
                //get path from the query string
      
                string? path = QueryParameters.Get("path");
                guts = returnAllFilesAsHtmlLinks(string.IsNullOrEmpty(path) ? _media.Root : path);

                responseString = responseString.Replace("{{GUTS}}", guts);

            }

            if (request.Url.LocalPath == "/play.html")
            {
                string? requestFilePath = QueryParameters.Get("FILENAME");
                if (string.IsNullOrEmpty(requestFilePath))
                {
                    responseString = responseString
                        .Replace("{{TITLE}}", "No video file specified")
                        .Replace("{{FILEPATH}}", "");
                }
                else if (_media.IsFfmpegAvailable)
                {
                    // In-app playback: decode the file with ffmpeg into the MJPEG + audio streams,
                    // then serve the player page (play.html). The Tesla never decodes "video", so it
                    // keeps playing while the car is in motion.
                    _media.Play(requestFilePath);
                    responseString = responseString
                        .Replace("{{TITLE}}", HttpUtility.HtmlEncode(Path.GetFileNameWithoutExtension(requestFilePath)))
                        .Replace("{{FILEPATH}}", HttpUtility.JavaScriptStringEncode(requestFilePath));
                }
                else
                {
                    // Fallback when ffmpeg isn't installed: launch VLC full-screen on the host and let
                    // the live screen-capture stream carry it (the original behavior).
                    responseString = LaunchVlcFallbackHtml(requestFilePath);
                }
            }

            //replace all instances of the string "localhost:8081" with the actual IP address of the server

            responseString = handleHTMLReplacements(responseString, request);


        }








        byte[] buffer = Encoding.UTF8.GetBytes(responseString);
        response.ContentLength64 = buffer.Length;
        System.IO.Stream output = response.OutputStream;
        output.Write(buffer, 0, buffer.Length);
        output.Close();
        response.Close();
        return;
    }

    /// <summary>
    /// Fallback used only when ffmpeg is not installed: kill any running VLC, launch the file
    /// full-screen on the host, and return a small status card that bounces back to the screen view.
    /// </summary>
    private string LaunchVlcFallbackHtml(string filePath)
    {
        string status;
        try
        {
            System.Diagnostics.Process.Start("taskkill", "/F /IM vlc.exe");
            System.Threading.Thread.Sleep(2000);
            System.Diagnostics.Process.Start("C:\\Program Files\\VideoLAN\\VLC\\vlc.exe", $" -vvv \"{filePath}\" --fullscreen");
            status = "Playing on PC (ffmpeg not installed)";
            Console.WriteLine($"[Media] VLC fallback launched for {filePath}");
        }
        catch (Exception ex)
        {
            status = "Could not start VLC: " + ex.Message;
            Console.WriteLine($"[Media] VLC fallback failed: {ex.Message}");
        }

        return "<html><head><title>Now Playing</title>" +
               "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no\">" +
               "<link rel=\"stylesheet\" type=\"text/css\" href=\"/style.css\" />" +
               "<meta http-equiv=\"refresh\" content=\"3; url=http://LOCALHOST:8080/\"></head>" +
               "<body><div class=\"center\"><div class=\"card\">" +
               "<div class=\"big-ic\">&#127916;</div><h1>" + HttpUtility.HtmlEncode(status) + "</h1>" +
               "<a class=\"btn primary\" href=\"http://LOCALHOST:8080/\">&#9664; Back to Screen</a>" +
               "</div></div></body></html>";
    }

    private string handleHTMLReplacements(string html, HttpListenerRequest request)
    {
        html = html.Replace("//LOCALHOST", "//" + getRequestHost(request));
        //check if the request is on port 8443

        if (request.Url.Port == 8443)
        {
            html = html.Replace(":8080", ":8443");
            html = html.Replace(":8081", ":8444");
            html = html.Replace(":8082", ":8445");
            html = html.Replace("ws://", "wss://");
            html = html.Replace("http://", "https://");
        }
        return html;
    }

    private string getClientIp(HttpListenerRequest request)
    {
        string ip = request.Headers["X-Forwarded-For"];
        if (string.IsNullOrEmpty(ip))
        {
            ip = request.RemoteEndPoint.Address.ToString();
        }
        return ip;
    }

    private string getRequestHost(HttpListenerRequest request)
    {
        string host = request.Headers["Host"];
        if (string.IsNullOrEmpty(host))
        {
            host = request.RemoteEndPoint.Address.ToString();
        }
        //remove the port number
        host = host.Split(':')[0];

        return host;
    }

    private string returnAllFilesAsHtmlLinks(string path)
    {
        var sb = new StringBuilder();
        string root = _media.Root;
        bool atRoot = string.Equals(path.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        // Sticky navigation bar: Screen, Up (when not at root), and the current path.
        sb.Append("<div class=\"topbar\">");
        sb.Append("<a class=\"navbtn\" href=\"/\"><svg viewBox=\"0 0 24 24\" class=\"btn-ic\" aria-hidden=\"true\" focusable=\"false\"><rect x=\"3\" y=\"4\" width=\"18\" height=\"12\" rx=\"2\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\"/><line x1=\"12\" y1=\"16\" x2=\"12\" y2=\"19\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\"/><line x1=\"8\" y1=\"19\" x2=\"16\" y2=\"19\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\"/></svg>Screen</a>");
        sb.Append("<a class=\"navbtn\" href=\"/config.html\">&#9881; Settings</a>");
        if (!atRoot)
        {
            string parentPath = Path.GetDirectoryName(path.TrimEnd('\\')) ?? root;
            sb.Append("<a class=\"navbtn\" href=\"/list.html?path=" + HttpUtility.UrlEncode(parentPath) + "\">&#8593; Up</a>");
        }
        sb.Append("<span class=\"path\">" + HttpUtility.HtmlEncode(path) + "</span>");
        sb.Append("</div>");

        // A missing or unreadable folder must not 500 the request — show a friendly message instead.
        if (!Directory.Exists(path))
        {
            sb.Append("<div class=\"empty\">This folder doesn't exist:<br>"
                + HttpUtility.HtmlEncode(path)
                + "<br><br>Pick a valid <b>Video folder</b> in <a class=\"navbtn\" href=\"/config.html\">&#9881; Settings</a>.</div>");
            return sb.ToString();
        }

        string[] directories;
        string[] files;
        try
        {
            directories = Directory.GetDirectories(path);
            var videoExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".mp4", ".mkv", ".avi", ".mov", ".m4v", ".wmv", ".flv", ".webm", ".mpg", ".mpeg", ".ts", ".m2ts" };
            files = Directory.GetFiles(path)
                .Where(f => videoExts.Contains(Path.GetExtension(f)))
                .ToArray();
        }
        catch (Exception ex)
        {
            Log.Warn($"[Files] Could not read '{path}': {ex.Message}");
            sb.Append("<div class=\"empty\">Couldn't read this folder:<br>"
                + HttpUtility.HtmlEncode(ex.Message) + "</div>");
            return sb.ToString();
        }

        // Look up watched/resume state for every file in one DB query.
        var progressByPath = _library.GetAll();

        sb.Append("<div class=\"list\">");

        // Folders first.
        foreach (string directory in directories)
        {
            string name = Path.GetFileName(directory.TrimEnd('\\'));
            sb.Append("<a class=\"row folder\" href=\"/list.html?path=" + HttpUtility.UrlEncode(directory) + "\">");
            sb.Append("<span class=\"ic\">&#128193;</span>");
            sb.Append("<span class=\"nm\">" + HttpUtility.HtmlEncode(name) + "</span>");
            sb.Append("<span class=\"chev\">&#8250;</span></a>");
        }

        // Then files — full filename, with a Watched check (≥95% played) or resume percentage.
        foreach (string file in files)
        {
            string name = Path.GetFileName(file);   // full name including extension
            progressByPath.TryGetValue(file.ToLowerInvariant(), out var prog);
            bool watched = prog?.Watched ?? false;
            int pct = prog?.Percent ?? 0;

            sb.Append("<a class=\"row file" + (watched ? " watched" : "") + "\" href=\"/play.html?FILENAME="
                + HttpUtility.UrlEncode(file) + "\">");
            sb.Append("<span class=\"ic\">" + (watched ? "&#10003;" : "&#127916;") + "</span>");
            sb.Append("<span class=\"nm\">" + HttpUtility.HtmlEncode(name) + "</span>");
            if (watched)
                sb.Append("<span class=\"badge done\">Watched</span>");
            else if (pct > 0)
                sb.Append("<span class=\"badge\">" + pct + "%</span>");
            sb.Append("</a>");
        }

        sb.Append("</div>");

        if (directories.Length == 0 && files.Length == 0)
            sb.Append("<div class=\"empty\">This folder is empty.</div>");

        return sb.ToString();
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

                //decode the message
                var message = Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);
                Log.Debug($"Received input: {message}");

                try
                {

                    if (message.Contains("key"))
                    {
                        var inputData = JsonSerializer.Deserialize<KeyData>(message);
                        handleKey(inputData);
                    }
                    else
                    {
                        var inputData = JsonSerializer.Deserialize<InputData>(message);
                        //move the mouse
                        inputData = inputData.GetAdjusted();
                        Log.Debug($"Mouse {inputData.Type} -> {inputData.X},{inputData.Y}");

                        Win32.SetCursorPos(inputData.X, inputData.Y);
                        if (inputData.Type == "down")
                        {
                            Win32.mouse_event(Win32.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
                        }

                        if (inputData.Type == "up")
                        {
                            Win32.mouse_event(Win32.MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
                        }

                        if (inputData.Type == "rightclick")
                        {
                            Win32.mouse_event(Win32.MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, 0);
                            Win32.mouse_event(Win32.MOUSEEVENTF_RIGHTUP, 0, 0, 0, 0);
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine("Error: " + e.Message);
                }
            }



            catch (WebSocketException e)
            {
                Console.WriteLine($"WebSocket error: {e.Message}");
            }
        }
    }

    private void handleKey(KeyData inputData)
    {
        var key = inputData.Key;
        if (string.IsNullOrEmpty(key))
            return;

        // Ignore standalone modifier / lock / non-text keys so we never type their
        // names (e.g. pressing Shift must not type "Shift"). Shifted characters
        // already arrive composed (e.g. "A", "!").
        switch (key)
        {
            case "Shift":
            case "Control":
            case "Alt":
            case "Meta":
            case "OS":
            case "AltGraph":
            case "CapsLock":
            case "NumLock":
            case "ScrollLock":
            case "ContextMenu":
            case "Fn":
            case "FnLock":
            case "Hyper":
            case "Super":
            case "Symbol":
            case "Dead":
            case "Process":
            case "Unidentified":
                return;
        }

        if (key.Length > 1)
        {
            // Named keys -> SendKeys tokens. Unknown named keys are ignored so we
            // never type a literal name like "ArrowLeft".
            string? token = key switch
            {
                "Backspace" => "{BACKSPACE}",
                "Enter" => "{ENTER}",
                "Tab" => "{TAB}",
                "Escape" => "{ESC}",
                "Delete" => "{DELETE}",
                "Insert" => "{INSERT}",
                "Home" => "{HOME}",
                "End" => "{END}",
                "PageUp" => "{PGUP}",
                "PageDown" => "{PGDN}",
                "ArrowLeft" => "{LEFT}",
                "ArrowRight" => "{RIGHT}",
                "ArrowUp" => "{UP}",
                "ArrowDown" => "{DOWN}",
                "F1" => "{F1}",
                "F2" => "{F2}",
                "F3" => "{F3}",
                "F4" => "{F4}",
                "F5" => "{F5}",
                "F6" => "{F6}",
                "F7" => "{F7}",
                "F8" => "{F8}",
                "F9" => "{F9}",
                "F10" => "{F10}",
                "F11" => "{F11}",
                "F12" => "{F12}",
                _ => null
            };

            if (token != null)
                SendKeys.SendWait(token);
            return;
        }

        // Single character: escape the characters SendKeys treats as special.
        if ("+^%~(){}[]".Contains(key))
            key = "{" + key + "}";

        SendKeys.SendWait(key);
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
