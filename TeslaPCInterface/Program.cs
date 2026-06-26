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
                bypass = new TeslaBrowserBypass.TeslaBrowserBypass();
                if (!bypass.Setup(httpPort, httpsPort))
                {
                    bypass.Dispose();
                    bypass = null;
                }
            }

            // Single unified server handles all routes:
            //   /          → web UI (index.html)
            //   /stream    → MJPEG video stream
            //   /ws/input  → mouse/keyboard WebSocket
            //   /ws/audio  → audio WebSocket
            bool localhostOnly = args.Contains("--localhost");
            bool enableHttps = false;
            if (!localhostOnly)
            {
                FirewallBootstrap.TryEnsureFirewallOpen(httpPort, httpsPort);
                enableHttps = SslCertificateBootstrap.TryEnsureHttpsReady(httpsPort, httpPort);
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

            if (localhostOnly)
            {
                Console.WriteLine($"Unified server started on http://127.0.0.1:{httpPort} (localhost only).");
            }
            else if (enableHttps)
            {
                Console.WriteLine($"Unified server started on port {httpPort} (HTTP) / {httpsPort} (HTTPS).");
                Console.WriteLine($"Secure UI: https://localhost:{httpsPort}/");
            }
            else
            {
                Console.WriteLine($"Unified server started on port {httpPort} (HTTP only).");
            }

            _ = serverTask.ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                    Console.WriteLine($"Server error: {t.Exception.GetBaseException().Message}");
            }, TaskScheduler.Default);
            if (bypass != null)
            {
                Console.WriteLine($"Tesla browser access: http://{TeslaBrowserBypass.TeslaBrowserBypass.BypassIP}:{httpPort}");
            }
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

            shutdown.Wait();

            bypass?.Dispose();
            await webServer.StopAsync();
            imageServer.Stop();
            imageServer.Dispose();
            audioCapture.Dispose();
        }



    }




}
class MousePosition
{
    //{"Type":"click","X":820,"Y":45,"DisplaySize":{"width":1280,"height":720}}
    public string Type { get; set; }
    public int X { get; set; }
    public int Y { get; set; }

    public DisplaySize? DisplaySize { get; set; }

    public MousePosition GetAdjusted()
    {
        if(DisplaySize == null)
        {
            return this;
        }
        var x = (int)((double)X / DisplaySize.width * System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width);
        var y = (int)((double)Y / DisplaySize.height * System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height);
        return new MousePosition { X = x, Y = y, Type = Type };
    }
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
