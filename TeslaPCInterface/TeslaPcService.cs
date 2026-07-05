using System.Net.NetworkInformation;
using AudioStreamingServer;
using Media;
using Streaming;

namespace PrimaryProcess;

/// <summary>
/// Owns the TeslaPC server lifecycle (screen + audio capture, unified web server, Tesla bypass,
/// firewall/HTTPS/ACME bootstrap, and the hotspot + renewal background loops). Extracted from the
/// old console Main so the WinForms UI can start/stop it and read its status.
/// </summary>
internal sealed class TeslaPcService
{
    public const int HttpPort = 8080;
    public const int HttpsPort = 8443;

    private readonly CancellationTokenSource _cts = new();
    private ImageStreamingServer? _imageServer;
    private AudioCapture? _audioCapture;
    private MediaLibrary? _library;
    private MediaStreamer? _media;
    private WebServer? _webServer;
    private TeslaBrowserBypass.TeslaBrowserBypass? _bypass;

    private string? _httpsHost, _cfToken, _acmeEmail;

    // Status (read by the UI).
    public bool IsRunning { get; private set; }
    public bool EnableHttps { get; private set; }
    public bool LocalhostOnly { get; private set; }
    public bool BypassEnabled { get; private set; }
    public bool BypassActive => _bypass != null;
    public string? HttpsHost => _httpsHost;
    public string? TrustedUrl { get; private set; }
    public string PrimaryUrl { get; private set; } = "";
    public List<string> ConnectUrls { get; } = new();
    public bool HotspotOn => HotspotManager.LastKnownOn;
    public int ClientCount => _imageServer?.ClientCount ?? 0;
    /// <summary>The folder the file browser/media player is rooted at.</summary>
    public string VideoRoot => _media?.Root ?? AppSettings.VideoRoot;

    /// <summary>Applies a new video folder immediately (no restart needed) to the running media player.</summary>
    public void SetVideoRoot(string path)
    {
        if (_media != null && !string.IsNullOrWhiteSpace(path))
            _media.Root = path.Trim();
    }

    public async Task StartAsync(string[] args)
    {
        // Cap box: the capture loop scales the live screen into it preserving aspect ratio (and never
        // upscales). Configurable via TESLAPC_STREAM_HEIGHT (default 1080); the width cap is generous
        // (4x height) so height is the binding dimension for normal and ultrawide displays.
        int h = AppSettings.StreamHeight;
        var streamTiming = new StreamTiming();
        _imageServer = new ImageStreamingServer(h * 4, h, 30, streamTiming);
        _audioCapture = new AudioCapture(streamTiming);
        _audioCapture.StartCapturing();
        _library = new MediaLibrary();
        _media = new MediaStreamer(_imageServer, _audioCapture, _library) { Root = AppSettings.VideoRoot };

        BypassEnabled = !args.Contains("--no-tesla-bypass");
        if (BypassEnabled)
        {
            if (HotspotManager.EnsureHotspotOn() == HotspotResult.TurnedOn)
                await Task.Delay(2000);
            _bypass = new TeslaBrowserBypass.TeslaBrowserBypass();
            if (!_bypass.Setup(HttpPort, HttpsPort)) { _bypass.Dispose(); _bypass = null; }
        }

        LocalhostOnly = args.Contains("--localhost");

        int hostIdx = Array.IndexOf(args, "--https-host");
        if (hostIdx >= 0 && hostIdx + 1 < args.Length) _httpsHost = args[hostIdx + 1];
        if (string.IsNullOrWhiteSpace(_httpsHost)) _httpsHost = Environment.GetEnvironmentVariable("TESLAPC_HTTPS_HOST");
        if (string.IsNullOrWhiteSpace(_httpsHost)) _httpsHost = null;

        _cfToken = Environment.GetEnvironmentVariable("TESLAPC_CF_TOKEN");
        _acmeEmail = Environment.GetEnvironmentVariable("TESLAPC_ACME_EMAIL");
        bool acmeEnabled = _httpsHost != null && !string.IsNullOrWhiteSpace(_cfToken);

        if (!LocalhostOnly)
        {
            FirewallBootstrap.TryEnsureFirewallOpen(HttpPort, HttpsPort);
            if (acmeEnabled)
                await AcmeCertificateManager.TryEnsureCertificateAsync(_httpsHost!, _acmeEmail, _cfToken);
            EnableHttps = SslCertificateBootstrap.TryEnsureHttpsReady(HttpsPort, HttpPort, _httpsHost);
        }

        _webServer = new WebServer(_imageServer, _audioCapture, _media, _library);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = _webServer.StartWebServerAsync(HttpPort, HttpsPort, LocalhostOnly, EnableHttps, started);
        try { await started.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception ex) { Console.WriteLine($"Server failed to start: {ex.GetBaseException().Message}"); }

        _ = serverTask.ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception != null)
                Console.WriteLine($"Server error: {t.Exception.GetBaseException().Message}");
        }, TaskScheduler.Default);

        BuildConnectUrls();
        IsRunning = true;

        StartHotspotWatchdog();
        StartRenewalLoop(acmeEnabled);
    }

    private void BuildConnectUrls()
    {
        string Urls(string host) => EnableHttps
            ? $"http://{host}:{HttpPort}/   https://{host}:{HttpsPort}/"
            : $"http://{host}:{HttpPort}/";

        ConnectUrls.Clear();
        Console.WriteLine();
        Console.WriteLine("==================== TeslaPC ====================");
        if (LocalhostOnly)
        {
            PrimaryUrl = $"http://localhost:{HttpPort}/";
            ConnectUrls.Add($"On this PC:   {PrimaryUrl}");
            Console.WriteLine("  Running (localhost only)");
        }
        else
        {
            Console.WriteLine($"  Running on HTTP {HttpPort}" + (EnableHttps ? $" / HTTPS {HttpsPort}" : " (HTTP only)"));
            ConnectUrls.Add($"On this PC:   {Urls("localhost")}");
            foreach (var ip in GetLocalIPv4Addresses())
                ConnectUrls.Add($"Network:      {Urls(ip)}");
            if (_bypass != null)
                ConnectUrls.Add($"Tesla (IP):   {Urls(TeslaBrowserBypass.TeslaBrowserBypass.BypassIP)}");
            if (EnableHttps && _httpsHost != null)
            {
                TrustedUrl = $"https://{_httpsHost}:{HttpsPort}/";
                ConnectUrls.Add($"Trusted:      {TrustedUrl}");
            }
        }
        PrimaryUrl = TrustedUrl
            ?? (_bypass != null ? $"http://{TeslaBrowserBypass.TeslaBrowserBypass.BypassIP}:{HttpPort}/" : $"http://localhost:{HttpPort}/");
        foreach (var u in ConnectUrls) Console.WriteLine("    " + u);
        Console.WriteLine("================================================");
        Console.WriteLine();
    }

    private void StartHotspotWatchdog()
    {
        if (!BypassEnabled || _bypass == null) return;
        var bp = _bypass;
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(60), _cts.Token);
                    if (HotspotManager.ManuallyOff) continue;   // user turned it off from the UI
                    var r = HotspotManager.EnsureHotspotOn();
                    if (r == HotspotResult.TurnedOn || !HasBypassIp())
                    {
                        await Task.Delay(2000, _cts.Token);
                        bp.Setup(HttpPort, HttpsPort);
                        Console.WriteLine("[Hotspot] Watchdog re-attached the Tesla bypass.");
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Console.WriteLine($"[Hotspot] Watchdog error: {ex.Message}"); }
            }
        });
    }

    private void StartRenewalLoop(bool acmeEnabled)
    {
        if (LocalhostOnly || !acmeEnabled) return;
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromHours(12), _cts.Token);
                    if (await AcmeCertificateManager.TryEnsureCertificateAsync(_httpsHost!, _acmeEmail, _cfToken))
                        SslCertificateBootstrap.TryEnsureHttpsReady(HttpsPort, HttpPort, _httpsHost);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Console.WriteLine($"[ACME] Renewal check error: {ex.Message}"); }
            }
        });
    }

    /// <summary>Toggle the hotspot from the UI. When turned off, the watchdog leaves it off until on again.</summary>
    public void ToggleHotspot()
    {
        if (HotspotManager.LastKnownOn)
        {
            HotspotManager.ManuallyOff = true;
            HotspotManager.TurnOff();
        }
        else
        {
            HotspotManager.ManuallyOff = false;
            if (HotspotManager.EnsureHotspotOn() != HotspotResult.Failed && _bypass != null)
            {
                System.Threading.Thread.Sleep(1500);
                _bypass.Setup(HttpPort, HttpsPort);
            }
        }
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        try { _bypass?.Dispose(); } catch { }
        if (_webServer != null) { try { await _webServer.StopAsync(); } catch { } }
        try { _media?.Stop(); } catch { }
        try { _imageServer?.Stop(); _imageServer?.Dispose(); } catch { }
        try { _audioCapture?.Dispose(); } catch { }
        IsRunning = false;
    }

    private static bool HasBypassIp()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                    if (ua.Address.ToString() == TeslaBrowserBypass.TeslaBrowserBypass.BypassIP)
                        return true;
        }
        catch { }
        return false;
    }

    private static List<string> GetLocalIPv4Addresses()
    {
        var result = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    string ip = addr.Address.ToString();
                    if (ip.StartsWith("127.") || ip.StartsWith("169.254.")
                        || ip == TeslaBrowserBypass.TeslaBrowserBypass.BypassIP) continue;
                    if (!result.Contains(ip)) result.Add(ip);
                }
            }
        }
        catch { }
        return result;
    }
}
