// See https://aka.ms/new-console-template for more information
using System.Net.WebSockets;
using System.Net;
using System.Text;
using System.Text.Json;

using System.Runtime.InteropServices;
using Streaming;
using System.Reflection.Metadata;
using System.Security.Policy;

namespace PrimaryProcess
{
    class Program
    {
        static async Task Main(string[] args)
        {
            _ = StartMJPEGStream();
            var webServer = new WebServer();

            var audioCapture = new AudioCapture();

            _ = webServer.StartWebServerAsync();
            _ = audioCapture.Start(8082);





            Console.WriteLine("WebSocket server started. Press any key to stop.");
            Console.ReadKey();



            await webServer.StopAsync();
        }

        public static async Task StartMJPEGStream()
        {
            int PortNumber = 8081;
            ImageStreamingServer server = new ImageStreamingServer(1280, 720);
            server.Start(PortNumber);
    
           
        }

    }




  


}
class MousePosition
{
    //{"Type":"click","X":820,"Y":45,"DisplaySize":{"width":1280,"height":720}}
    public string Type { get; set; }
    public int X { get; set; }
    public int Y { get; set; }

    public DisplaySize DisplaySize { get; set; }

    public MousePosition GetAdjusted()
    {
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