// See https://aka.ms/new-console-template for more information
using System.Net.WebSockets;
using System.Net;
using System.Text;
using System.Text.Json;

using System.Runtime.InteropServices;
using Streaming;
using System.Reflection.Metadata;
using System.Security.Policy;
using AudioStreamingServer;
using TeslaBrowserBypass;


namespace PrimaryProcess
{
    class Program
    {
        static async Task Main(string[] args)
        {
            const int httpPort = 8080;
            const int httpsPort = 8443;

            // Load config/secrets from .env (app folder, then %ProgramData%\TeslaPC\.env which
            // wins and survives rebuilds). Lines are KEY=VALUE; existing real env vars take
            // precedence so a machine env var can still override the file.
            static void LoadDotEnv()
            {
                var paths = new[]
                {
                    Path.Combine(AppContext.BaseDirectory, ".env"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TeslaPC", ".env")
                };
                foreach (var path in paths)
                {
                    if (!File.Exists(path)) continue;
                    foreach (var raw in File.ReadAllLines(path))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        var key = line.Substring(0, eq).Trim();
                        var val = line.Substring(eq + 1).Trim().Trim('"');
                        if (key.Length > 0 && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                            Environment.SetEnvironmentVariable(key, val);
                    }
                }
            }
            LoadDotEnv();

            Size size = new(System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width, System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height);

            //set resolution to the smaller of size or 1280x720
            var resolution = new Size(Math.Min(size.Width, 1280), Math.Min(size.Height, 720));

            var imageServer = new ImageStreamingServer(resolution.Width, resolution.Height, 30);
            var audioCapture = new AudioCapture();
            audioCapture.StartCapturing();

            // Set up Tesla browser bypass (adds non-RFC1918 IP to hotspot adapter)
            TeslaBrowserBypass.TeslaBrowserBypass? bypass = null;
            bool bypassEnabled = !args.Contains("--no-tesla-bypass");
            if (bypassEnabled)
            {
                // Turn Mobile Hotspot on if it's off, so the bypass has an adapter to attach to,
                // then give the virtual adapter a moment to come up before configuring it.
                if (HotspotManager.EnsureHotspotOn() == HotspotResult.TurnedOn)
                    System.Threading.Thread.Sleep(2000);

                bypass = new TeslaBrowserBypass.TeslaBrowserBypass();
                if (!bypass.Setup(httpPort, httpsPort))
                {
                    bypass.Dispose();
                    bypass = null;
                }
            }

            // Single unified server handles all routes:
            //   /          â†’ web UI (index.html)
            //   /stream    â†’ MJPEG video stream
            //   /ws/input  â†’ mouse/keyboard WebSocket
            //   /ws/audio  â†’ audio WebSocket
            bool localhostOnly = args.Contains("--localhost");

            // Optional hostname for a publicly-trusted cert (win-acme / Let's Encrypt). When set
            // and a matching cert is installed in LocalMachine\My, HTTPS uses it instead of the
            // self-signed dev cert. Source: --https-host <host> or TESLAPC_HTTPS_HOST.
            string? httpsHost = null;
            int hostIdx = Array.IndexOf(args, "--https-host");
            if (hostIdx >= 0 && hostIdx + 1 < args.Length) httpsHost = args[hostIdx + 1];
            if (string.IsNullOrWhiteSpace(httpsHost)) httpsHost = Environment.GetEnvironmentVariable("TESLAPC_HTTPS_HOST");
            if (string.IsNullOrWhiteSpace(httpsHost)) httpsHost = null;

            // Cloudflare token / ACME contact for the in-app Let's Encrypt client (read from the
            // environment so the secret never lives on a command line or in the repo).
            string? cfToken = Environment.GetEnvironmentVariable("TESLAPC_CF_TOKEN");
            string? acmeEmail = Environment.GetEnvironmentVariable("TESLAPC_ACME_EMAIL");
            bool acmeEnabled = httpsHost != null && !string.IsNullOrWhiteSpace(cfToken);

            bool enableHttps = false;
            if (!localhostOnly)
            {
                FirewallBootstrap.TryEnsureFirewallOpen(httpPort, httpsPort);
                if (acmeEnabled)
                    await AcmeCertificateManager.TryEnsureCertificateAsync(httpsHost!, acmeEmail, cfToken);
                enableHttps = SslCertificateBootstrap.TryEnsureHttpsReady(httpsPort, httpPort, httpsHost);
            }

            var webServer = new WebServer(imageServer, audioCapture);
            var serverStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var serverTask = webServer.StartWebServerAsync(httpPort, httpsPort, localhostOnly, enableHttps, serverStarted);

            try
            {
                await serverStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Server failed to start: {ex.GetBaseException().Message}");
                bypass?.Dispose();
                imageServer.Dispose();
                audioCapture.Dispose();
                return;
            }

            // Collect LAN IPv4 addresses (skip loopback, link-local APIPA, and the CGNAT bypass IP).
            static List<string> GetLocalIPv4Addresses()
            {
                var result = new List<string>();
                try
                {
                    foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
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

            string Urls(string host) =>
                enableHttps ? $"http://{host}:{httpPort}/   https://{host}:{httpsPort}/"
                            : $"http://{host}:{httpPort}/";

            Console.WriteLine();
            Console.WriteLine("==================== TeslaPC ====================");
            if (localhostOnly)
            {
                Console.WriteLine("  Running (localhost only)");
                Console.WriteLine($"  On this PC:   http://localhost:{httpPort}/");
            }
            else
            {
                Console.WriteLine($"  Running on HTTP {httpPort}" + (enableHttps ? $" / HTTPS {httpsPort}" : " (HTTP only)"));
                Console.WriteLine("  Open a browser to any of these:");
                Console.WriteLine($"    On this PC:   {Urls("localhost")}");
                foreach (var ip in GetLocalIPv4Addresses())
                    Console.WriteLine($"    Network:      {Urls(ip)}");
                if (bypass != null)
                    Console.WriteLine($"    Tesla browser: {Urls(TeslaBrowserBypass.TeslaBrowserBypass.BypassIP)}");
                if (enableHttps && httpsHost != null)
                    Console.WriteLine($"    Trusted name:  https://{httpsHost}:{httpsPort}/   (use this on the Tesla for no warning)");
                if (enableHttps && httpsHost == null)
                    Console.WriteLine("  (HTTPS uses a self-signed cert; click through the browser warning.)");
                else if (enableHttps)
                    Console.WriteLine("  (HTTPS by IP uses the self-signed cert and warns; the trusted name above does not.)");
            }
            Console.WriteLine("================================================");
            Console.WriteLine();

            _ = serverTask.ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                    Console.WriteLine($"Server error: {t.Exception.GetBaseException().Message}");
            }, TaskScheduler.Default);
            Console.WriteLine("Press Ctrl+C to stop.");
            using var shutdown = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                shutdown.Set();
            };

            if (!Console.IsInputRedirected)
            {
                Console.WriteLine("Or press any key to stop.");
                _ = Task.Run(() =>
                {
                    Console.ReadKey(intercept: true);
                    shutdown.Set();
                });
            }

            // Hotspot watchdog: Windows auto-disables Mobile Hotspot after a few minutes with no
            // connected device. Re-enable it (and re-attach the bypass IP, since the virtual adapter
            // is recreated) so the Tesla can connect at any time.
            if (bypassEnabled && bypass != null)
            {
                var bp = bypass;
                static bool HasBypassIp()
                {
                    try
                    {
                        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                                if (ua.Address.ToString() == TeslaBrowserBypass.TeslaBrowserBypass.BypassIP)
                                    return true;
                    }
                    catch { }
                    return false;
                }
                _ = Task.Run(async () =>
                {
                    while (!shutdown.IsSet)
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(60));
                            if (shutdown.IsSet) break;
                            var r = HotspotManager.EnsureHotspotOn();
                            if (r == HotspotResult.TurnedOn || !HasBypassIp())
                            {
                                await Task.Delay(2000);
                                bp.Setup(httpPort, httpsPort);
                                Console.WriteLine("[Hotspot] Watchdog re-attached the Tesla bypass.");
                            }
                        }
                        catch (Exception ex) { Console.WriteLine($"[Hotspot] Watchdog error: {ex.Message}"); }
                    }
                });
            }

            // Auto-renew the Let's Encrypt certificate in the background. The manager no-ops while
            // the cert has > 30 days left; when it renews, re-bind the new cert to the HTTPS port.
            if (!localhostOnly && acmeEnabled)
            {
                _ = Task.Run(async () =>
                {
                    while (!shutdown.IsSet)
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromHours(12));
                            if (await AcmeCertificateManager.TryEnsureCertificateAsync(httpsHost!, acmeEmail, cfToken))
                                SslCertificateBootstrap.TryEnsureHttpsReady(httpsPort, httpPort, httpsHost);
                        }
                        catch (Exception ex) { Console.WriteLine($"[ACME] Renewal check error: {ex.Message}"); }
                    }
                });
            }

            shutdown.Wait();

            bypass?.Dispose();
            await webServer.StopAsync();
            imageServer.Stop();
            imageServer.Dispose();
            audioCapture.Dispose();
        }



    }




}
class InputData
{
    //{"Type":"click","X":820,"Y":45,"DisplaySize":{"width":1280,"height":720}}
    public string Type { get; set; }

    public int X { get; set; }
    public int Y { get; set; }

    public DisplaySize? DisplaySize { get; set; }

    public InputData GetAdjusted()
    {
        if (DisplaySize == null)
        {
            return this;
        }
        var x = (int)((double)X / DisplaySize.width * System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width);
        var y = (int)((double)Y / DisplaySize.height * System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height);
        return new InputData { X = x, Y = y, Type = Type };
    }
}

class KeyData
{
    //{"Type":"click","X":820,"Y":45,"DisplaySize":{"width":1280,"height":720}}
    public string Type { get; set; }

    public string Key { get; set; }

    public string KeyCode { get; set; }


}
class DisplaySize
{
    public int width { get; set; }
    public int height { get; set; }
}
public class Win32
{

    public const int MOUSEEVENTF_LEFTDOWN = 0x02;
    public const int MOUSEEVENTF_LEFTUP = 0x04;

    [DllImport("user32.dll")]
    public static extern void SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")]
    public static extern void ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("user32.dll")]
    public static extern void mouse_event(int dwFlags, int dx, int dy, int dwData, int dwExtraInfo);
    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x;
        public int y;

        public POINT(int X, int Y)
        {
            x = X;
            y = Y;
        }
    }
}
