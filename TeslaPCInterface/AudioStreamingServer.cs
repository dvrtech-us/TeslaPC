using NAudio.Wave;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;

public class AudioCapture
{
    private WaveInEvent waveSource = null;

    private void StartCapturing()
    {
        waveSource = new WaveInEvent();
        waveSource.WaveFormat = new WaveFormat(44100, 2); // 44.1kHz mono PCM

        waveSource.DataAvailable += waveSource_DataAvailable;
        waveSource.RecordingStopped += waveSource_RecordingStopped;


        waveSource.StartRecording();
    }
    private ConcurrentQueue<byte[]> _audioDataQueue = new ConcurrentQueue<byte[]>();
    private void waveSource_DataAvailable(object sender, WaveInEventArgs e)
    {
        try
        {
            //put the data in a queue
            _audioDataQueue.Enqueue(e.Buffer);

        }
        catch (Exception ex)
        {
            Console.WriteLine("data available " + ex.Message);
            // Handle the case where the client disconnects or an error occurs.

        }
    }

    private void waveSource_RecordingStopped(object sender, StoppedEventArgs e)
    {
        if (waveSource != null)
        {
            waveSource.Dispose();
            waveSource = null;
        }

    }


    /// <summary>
    /// Starts the server to accepts any new connections on the specified port.
    /// </summary>
    /// <param name="port"></param>
    public async Task Start(int port)
    {
        HttpListener listener = new HttpListener();
        listener.Prefixes.Add($"http://*:{port}/");
        listener.Start();

        Console.WriteLine($"Server started on {listener.Prefixes.First()}");
        StartCapturing();
        while (true)
        {
            HttpListenerContext listenerContext = await listener.GetContextAsync();

            if (listenerContext.Request.IsWebSocketRequest)
            {
                HttpListenerWebSocketContext webSocketContext = await listenerContext.AcceptWebSocketAsync(null);
                WebSocket webSocket = webSocketContext.WebSocket;

                try
                {

                    while (webSocket.State == WebSocketState.Open)
                    {
                        if (_audioDataQueue.TryDequeue(out var buffer))
                        {
                            await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Binary, true, CancellationToken.None);
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.Message);
                }
                finally
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                }
            }
            else
            {
                listenerContext.Response.StatusCode = 400;
                listenerContext.Response.Close();
            }
        }
    }
    public byte[] GenerateHeader(int sampleRate, int bitsPerSample, int channels, int samples)
    {
        int dataSize = 10240000; // Some very big number here
        List<byte> header = new List<byte>();

        header.AddRange(Encoding.ASCII.GetBytes("RIFF"));
        header.AddRange(BitConverter.GetBytes(dataSize + 36));
        header.AddRange(Encoding.ASCII.GetBytes("WAVE"));
        header.AddRange(Encoding.ASCII.GetBytes("fmt "));
        header.AddRange(BitConverter.GetBytes(16));
        header.AddRange(BitConverter.GetBytes((short)1));
        header.AddRange(BitConverter.GetBytes((short)channels));
        header.AddRange(BitConverter.GetBytes(sampleRate));
        header.AddRange(BitConverter.GetBytes(sampleRate * channels * bitsPerSample / 8));
        header.AddRange(BitConverter.GetBytes((short)(channels * bitsPerSample / 8)));
        header.AddRange(BitConverter.GetBytes((short)bitsPerSample));
        header.AddRange(Encoding.ASCII.GetBytes("data"));
        header.AddRange(BitConverter.GetBytes(dataSize));

        return header.ToArray();
    }
}