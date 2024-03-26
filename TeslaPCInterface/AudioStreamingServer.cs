using CSCore;
using CSCore.CoreAudioAPI;
using CSCore.SoundIn;
using CSCore.Codecs.WAV;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
namespace AudioStreamingServer
{
    public class AudioCapture : IDisposable
    {
        private WasapiLoopbackCapture? capture = null;
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private bool _disposed = false;
        /// <summary>
        /// Starts capturing the audio from the default audio input device.
        /// </summary>
        public void StartCapture()
        {
            capture = new WasapiLoopbackCapture();
            // initialize the soundIn instance
            capture.Initialize();

            // choose the correct format 
            var format = new WaveFormat(48000, 16, 2); // 48kHz, 16bit, stereo

            // create a wavewriter to write the data to

            MemoryStream memoryStream = new MemoryStream();
            WaveWriter writer = new WaveWriter(memoryStream, format);
            // setup an eventhandler to receive the recorded data
            capture.DataAvailable += async (s, e) =>
  {
      // Write the recorded audio to the MemoryStream
      writer.Write(e.Data, e.Offset, e.ByteCount);

      // Convert the MemoryStream's data to an ArraySegment<byte>
      var buffer = new ArraySegment<byte>(memoryStream.ToArray());

      // Send the data over the WebSocket
      _audioDataQueue.Enqueue(buffer.Array);

      // Clear the MemoryStream
      memoryStream.SetLength(0);
  };

            // start capturing
            capture.Start();
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
            if (capture != null)
            {
                capture.Dispose();
                capture = null;
            }

        }


        /// <summary>
        /// Starts the server to accepts any new connections on the specified port.
        /// </summary>
        /// <param name="port"></param>
        /// <param name="sslPort"></param>  
        /// <returns></returns>
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
                                    var bu = new ArraySegment<byte>(buffer, 0, buffer.Length);
                                    // Use the rented array
                                    await webSocket.SendAsync(bu, WebSocketMessageType.Binary, true, CancellationToken.None);
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
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Dispose managed resources
                    capture?.Dispose();
                    _cancellationTokenSource?.Dispose();
                }

                // Dispose unmanaged resources

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}