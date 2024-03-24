// See https://aka.ms/new-console-template for more information
using System.Net.WebSockets;
using System.Net;
using System.Text;
using System.Text.Json;

using System.Runtime.InteropServices;
using Streaming;
using System.Reflection.Metadata;
using System.Security.Policy;

namespace WebSocketServer
{
    class Program
    {
        static async Task Main(string[] args)
        {
            CaptureScreenToMJPEGStream();
            var webServer = new WebServer();

            var audioCapture = new AudioCapture();
       
            webServer.StartWebServerAsync();
            audioCapture.Start(8082);





            Console.WriteLine("WebSocket server started. Press any key to stop.");
            Console.ReadKey();



            await webServer.StopAsync();
        }

        public static async Task CaptureScreenToMJPEGStream()
        {
            int PortNumber = 8081;
            ImageStreamingServer server = new ImageStreamingServer(1280, 720);
            server.Start(PortNumber);
    
           
        }

        public static async Task WaitForInput()
        {

        }
    }




    public class WebServer
    {
        private readonly HttpListener _Listener = new HttpListener();
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        //serve the html file


        public async Task StartWebServerAsync()
        {

            _Listener.Prefixes.Add("http://*:8080/");
            Console.WriteLine("Listening for WebSocket connections on: ");
            foreach (var prefix in _Listener.Prefixes)
            {
                Console.WriteLine("\t" + prefix);
            }

            _Listener.Start();

            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                var context = await _Listener.GetContextAsync();
                _ = Task.Run(async () =>
                   {
                       if (context.Request.IsWebSocketRequest)
                       {
                           await AcceptWebSocketAsync(context);
                       }
                       else
                       {

                           await HandleHttpAsync(context);
                       }
                   });
            }
        }

        private async Task HandleHttpAsync(HttpListenerContext context)
        {
            HttpListenerRequest request = context.Request;
            HttpListenerResponse response = context.Response;
            string PathToHtml = "";
            string rootPath = "";
            var runningInDebugMode = false;

            string requestPath =  request.Url.LocalPath.TrimStart('/');

            if(requestPath == "stream.jpg")
            {
                // Writes the response header to the client.
                MjpegWriter wr = new MjpegWriter(context, "--boundary");
                wr.WriteHeader();

              
            }


            //detect if visual studio is running in debug mode
            if (System.Diagnostics.Debugger.IsAttached)
            {
                runningInDebugMode = true;
            }
            //if running in debug mode, serve the html file from the project directory
            if (!runningInDebugMode)
            {
                rootPath = "";
            }
            else
            {
                rootPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "");
                //get the location of bin in the path
                rootPath = PathToHtml.Substring(0, rootPath.LastIndexOf("bin"));
                Console.WriteLine("Path to html: " + rootPath);
            }
            //if not running in debug mode, serve the html file from the directory where the executable is located
            //see if the request is for the html file
            if (request.Url.LocalPath == "/")
            {
                PathToHtml = System.IO.Path.Combine(rootPath, "index.html");
            }
            else
            {
                PathToHtml = System.IO.Path.Combine(rootPath, request.Url.LocalPath.TrimStart('/'));
            }
            //if the file does not exist, return a 404 error
            if (!System.IO.File.Exists(PathToHtml))
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

            string responseString = System.IO.File.ReadAllText(PathToHtml);
            response.StatusCode = 200;
            //serve the right content type
            if (PathToHtml.EndsWith(".html"))
            {
                //get actual server ip request was made to
                string serverIp = request.LocalEndPoint.Address.ToString();

                response.ContentType = "text/html";


                //replace all instances of the string "localhost:8081" with the actual IP address of the server

                responseString = responseString.Replace("//LOCALHOST", "//" + serverIp);
                responseString = responseString.Replace("127.0.0.1", "172.16.1.29");

            }
            else if (PathToHtml.EndsWith(".js"))
            {
                response.ContentType = "application/javascript";
            }
            else if (PathToHtml.EndsWith(".css"))
            {
                response.ContentType = "text/css";
            }
            else if (PathToHtml.EndsWith(".png"))
            {
                response.ContentType = "image/png";
            }
            else if (PathToHtml.EndsWith(".jpg"))
            {
                response.ContentType = "image/jpeg";
            }

     

     

         
         
            byte[] buffer = System.Text.Encoding.UTF8.GetBytes(responseString);
            response.ContentLength64 = buffer.Length;
            System.IO.Stream output = response.OutputStream;
            output.Write(buffer, 0, buffer.Length);
            output.Close();
            response.Close();
            return;
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
                    if (mousePosition.Type == "click")
                    {
                        //click the mouse
                        //simulate a mouse click
                        Win32.mouse_event(Win32.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
                        Win32.mouse_event(Win32.MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
                    }

                }
                catch (WebSocketException e)
                {
                    Console.WriteLine($"WebSocket error: {e.Message}");
                }
            }
        }





        public async Task StopAsync()
        {
            _cancellationTokenSource.Cancel();
            _Listener.Stop();

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
        return new MousePosition { X = x, Y = y };
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