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


namespace PrimaryProcess
{
    class Program
    {
        static async Task Main(string[] args)
        {



            _ = startServers();
            Console.WriteLine("WebSocket server started. Press any key to stop.");
            Console.ReadKey();



            await webServer.StopAsync();
        }

        static async Task startServers()
        {

            Size size = new(System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width, System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height);



            //set resolution to the smaller of size or 1280x720
            var resolution = new Size(Math.Min(size.Width, 1280), Math.Min(size.Height, 720));

            var webServer = new WebServer();

            var audioCapture = new AudioCapture();
            var imageServer = new ImageStreamingServer(resolution.Width, resolution.Height, 30);
            while (true)
            {
                _ = imageServer.Start(8081, 8444);
                _ = webServer.StartWebServerAsync(8080, 8443);
                _ = audioCapture.Start(8082, 8445);

                // Wait for all tasks to complete
                Task.WaitAll(imageServer.Task, webServer.Task, audioCapture.Task);

                // If any of the tasks is completed, the loop will restart them
            }
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