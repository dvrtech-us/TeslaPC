using NAudio.Wave;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;

public class AudioCapture
{
    private WaveInEvent? waveSource = null;
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    /// <summary>
    /// Starts capturing the audio from the default audio input device.
    /// </summary>
    private void StartCapturing()
    {
        waveSource = new WaveInEvent
        {
            WaveFormat = new WaveFormat(44100, 2) // 44.1kHz mono PCM
        };

        waveSource.DataAvailable += waveSource_DataAvailable;
        waveSource.RecordingStopped += waveSource_RecordingStopped;


        waveSource.StartRecording();
    }
    /// <summary>
    /// A queue that holds the audio data that will be sent to the client.
    /// </summary>
    private readonly ConcurrentQueue<byte[]> _audioDataQueue = new();
    /// <summary>
    /// This event is called when the audio data is available.
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
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

    /// <summary>
    /// This event is called when the recording is stopped.
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
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
    public async Task Start(int port, int sslPort)
    {
        HttpListener listener = new HttpListener();
        listener.Prefixes.Add($"http://*:{port}/");
        listener.Prefixes.Add($"https://*:{sslPort}/");

        listener.Start();

        Console.WriteLine($"Audio Server started on: ");
        foreach (var prefix in listener.Prefixes)
        {
            Console.WriteLine("\t" + prefix);
        }
        //start capturing the audio
        StartCapturing();

        //start the server
        while (!_cancellationTokenSource.IsCancellationRequested)
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
                            // Rent an array from the pool
                            byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(buffer.Length);

                            try
                            {
                                // Copy your data into the rented array
                                buffer.AsSpan().CopyTo(rentedBuffer);

                                // Use the rented array
                                await webSocket.SendAsync(new ArraySegment<byte>(rentedBuffer, 0, buffer.Length), WebSocketMessageType.Binary, true, CancellationToken.None);
                            }
                            finally
                            {
                                // Return the array to the pool
                                ArrayPool<byte>.Shared.Return(rentedBuffer);
                            }
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

}