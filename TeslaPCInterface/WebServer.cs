using Streaming;
using System.Net.WebSockets;
using System.Net;
using System.Text.Json;
using System.Text;
using System;

public class WebServer
{
    private readonly HttpListener _Listener = new HttpListener();
    private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

    //serve the html file


    public async Task StartWebServerAsync(int port, int sslPort)
    {

        _Listener.Prefixes.Add("http://*:" + port + "/");
        _Listener.Prefixes.Add("https://*:" + sslPort + "/");
        Console.WriteLine("Listening for WebSocket connections on: ");
        foreach (var prefix in _Listener.Prefixes)
        {
            Console.WriteLine("\t" + prefix);
        }

        _Listener.Start();

        while (!_cancellationTokenSource.IsCancellationRequested)
        {
            try
            {


                var context = await _Listener.GetContextAsync();
                _ = Task.Run(async () =>
                {
                    if (context.Request.IsWebSocketRequest)
                    {
                        AcceptWebSocketAsync(context);
                    }
                    else
                    {

                        HandleHttpAsync(context);
                    }
                });
            }
            catch (HttpListenerException e)
            {
                Console.WriteLine("StartWebServer Async While: " + e.Message);
            }
        }

        //restart the server
        try
        {
            _Listener.Stop();
            _Listener.Close();
        }
        catch { }

        _ = StartWebServerAsync(port, sslPort);
    }

    private void HandleHttpAsync(HttpListenerContext context)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;
        string PathToHtml = "";
        string rootPath = "";
        var runningInDebugMode = false;

        string requestPath = request.Url.LocalPath.TrimStart('/');

        if (requestPath == "stream.jpg")
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
            rootPath = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            //get the location of bin in the path
            rootPath = rootPath.Substring(0, rootPath.LastIndexOf("bin"));
            Console.WriteLine("Path to html: " + rootPath);
        }
        //if not running in debug mode, serve the html file from the directory where the executable is located
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
                var inputData = JsonSerializer.Deserialize<InputData>(message);
                Console.WriteLine($"Mouse position: {inputData.X}, {inputData.Y}");
                //move the mouse
                inputData = inputData.GetAdjusted();
                Console.WriteLine($"Adjusted Mouse position: {inputData.X}, {inputData.Y}");

                Win32.SetCursorPos(inputData.X, inputData.Y);
                if (inputData.Type == "down")
                {
                    Win32.mouse_event(Win32.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
                }

                if (inputData.Type == "up")
                {
                    Win32.mouse_event(Win32.MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
                }
                if (inputData.Type == "key")
                {

                    /*{"Type":"key","Key":"Shift","Code":"ShiftLeft"}
index.html:136 Reopening socket
index.html:137 {"Type":"key","Key":"A","Code":"KeyA"}*/

                    //use sendkeys to send the key
                    //handle the shift key
                    if (inputData.key == "Shift")
                    {
                        if (inputData.keyCode == "ShiftLeft")
                        {
                            SendKeys.SendWait("+");
                        }
                        if (inputData.keyCode == "ShiftRight")
                        {
                            SendKeys.SendWait("+");
                        }
                    }
                    elseif(inputData.key == "Control")
                    {
                        if (inputData.keyCode == "ControlLeft")
                        {
                            SendKeys.SendWait("^");
                        }
                        if (inputData.keyCode == "ControlRight")
                        {
                            SendKeys.SendWait("^");
                        }
                    }
                        else
                    {

                        //handle escaping the keys that are not supported by sendkeys
                        if (inputData.key == "Escape")
                        {
                            SendKeys.SendKeys("{ESC}");
                        }

                        elseif(inputData.key == "Enter")
                        {
                            SendKeys.SendKeys("{ENTER}");
                        }
                        elseif(inputData.key == "Backspace")
                        {
                            SendKeys.SendKeys("{BACKSPACE}");
                        }
                        elseif(inputData.key == "Tab")
                        {
                            SendKeys.SendKeys("{TAB}");
                        }
                        elseif(inputData.key == "CapsLock")
                        {
                            SendKeys.SendKeys("{CAPSLOCK}");
                        }
                        elseif(inputData.key == "Space")
                        {
                            SendKeys.SendKeys(" ");
                        }else
                        {
                            SendKeys.SendKeys(inputData.key);
                        }





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