
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Streaming
{

    /// <summary>
    /// Provides a streaming server that can be used to stream any images source
    /// to any client.
    /// </summary>
    public class ImageStreamingServer : IDisposable
    {
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly int _maxWidth;
        private readonly int _maxHeight;
        private bool _disposed = false;

        // Shared capture state
        private readonly object _frameLock = new();
        private byte[] _currentFrame = Array.Empty<byte>();
        private int _frameNumber;
        private int _clientCount;
        private Thread? _captureThread;


        /// <summary>
        /// constructor that takes in the size of the screen
        /// </summary>
        /// <param name="width"></param>
        /// <param name="height"></param>

        public ImageStreamingServer(int width, int height, int fps)
        {
            _maxWidth = width;
            _maxHeight = height;

            this.Interval = 1000 / fps;

        }

        /// <summary>
        /// Gets or sets the interval in milliseconds (or the delay time) between
        /// the each image and the other of the stream (the default is 500 milliseconds).
        /// </summary>
        public int Interval { get; set; }

        /// <summary>
        /// Handles an MJPEG stream request from the unified server.
        /// Writes MJPEG frames to the response until the client disconnects.
        /// </summary>
        public void HandleStreamRequest(HttpListenerContext ctx)
        {
            Interlocked.Increment(ref _clientCount);
            EnsureCaptureRunning();
            ThreadPool.QueueUserWorkItem(_ => StreamToClient(ctx));
        }

        /// <summary>
        /// Starts the shared capture thread if it isn't already running.
        /// </summary>
        private void EnsureCaptureRunning()
        {
            lock (_frameLock)
            {
                if (_captureThread == null || !_captureThread.IsAlive)
                {
                    _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "ScreenCapture" };
                    _captureThread.Start();
                }
            }
        }

        private static ImageCodecInfo GetJpegCodec()
        {
            foreach (var codec in ImageCodecInfo.GetImageEncoders())
            {
                if (codec.MimeType == "image/jpeg")
                    return codec;
            }
            throw new InvalidOperationException("JPEG codec not found");
        }

        /// <summary>
        /// Single capture loop shared by all clients. Captures the screen,
        /// encodes to JPEG, and notifies waiting client threads.
        /// </summary>
        private void CaptureLoop()
        {
            try
            {
                SetProcessDpiAwareness(ProcessDPIAwareness.ProcessPerMonitorDPIAware);

                Size screenSize = new(System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width, System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height);

                // Cap output resolution to the configured maximum
                int outWidth = Math.Min(screenSize.Width, _maxWidth);
                int outHeight = Math.Min(screenSize.Height, _maxHeight);
                bool needsResize = outWidth != screenSize.Width || outHeight != screenSize.Height;

                using Bitmap srcImage = new(screenSize.Width, screenSize.Height);
                using Graphics srcGraphics = Graphics.FromImage(srcImage);

                using Bitmap? scaledImage = needsResize ? new Bitmap(outWidth, outHeight) : null;
                using Graphics? scaledGraphics = needsResize ? Graphics.FromImage(scaledImage!) : null;

                var jpegCodec = GetJpegCodec();
                using EncoderParameters encoderParameters = new(1);
                encoderParameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 60L);

                using var ms = new MemoryStream();
                int lastStart = Environment.TickCount;

                while (!_cancellationTokenSource.Token.IsCancellationRequested
                       && Volatile.Read(ref _clientCount) > 0)
                {
                    srcGraphics.CopyFromScreen(0, 0, 0, 0, screenSize);

                    ms.SetLength(0);

                    if (needsResize)
                    {
                        scaledGraphics!.DrawImage(srcImage, 0, 0, outWidth, outHeight);
                        scaledImage!.Save(ms, jpegCodec, encoderParameters);
                    }
                    else
                    {
                        srcImage.Save(ms, jpegCodec, encoderParameters);
                    }

                    // Publish frame to all waiting clients
                    lock (_frameLock)
                    {
                        _currentFrame = ms.ToArray();
                        _frameNumber++;
                        Monitor.PulseAll(_frameLock);
                    }

                    // Wait for the next frame
                    int elapsed = Environment.TickCount - lastStart;
                    lastStart = Environment.TickCount;
                    if (elapsed < Interval)
                        Thread.Sleep(Interval - elapsed);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Capture loop error: {e.Message}");
            }
            finally
            {
                lock (_frameLock)
                {
                    _captureThread = null;
                    Monitor.PulseAll(_frameLock);
                }
            }
        }

        /// <summary>
        /// Per-client thread that reads shared frames and writes MJPEG to the response.
        /// </summary>
        private void StreamToClient(HttpListenerContext ctx)
        {
            try
            {
                MjpegWriter wr = new(ctx, "boundary");
                wr.WriteHeader();

                int lastFrame = 0;
                while (!_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    byte[] frame;
                    lock (_frameLock)
                    {
                        while (_frameNumber == lastFrame)
                        {
                            if (_captureThread == null || !_captureThread.IsAlive)
                                return;

                            if (!Monitor.Wait(_frameLock, 1000))
                            {
                                if (_captureThread == null || !_captureThread.IsAlive)
                                    return;
                                continue;
                            }

                            if (_cancellationTokenSource.Token.IsCancellationRequested)
                                return;
                        }
                        frame = _currentFrame;
                        lastFrame = _frameNumber;
                    }

                    wr.Write(frame);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Stream client error: {e.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref _clientCount);
                ctx.Response.OutputStream.Close();
            }
        }


        public void Stop()
        {
            try
            {
                _cancellationTokenSource.Cancel();
                // Wake any waiting client threads so they can exit
                lock (_frameLock)
                {
                    Monitor.PulseAll(_frameLock);
                }
            }
            finally
            {
            }
        }

        private enum ProcessDPIAwareness
        {
            ProcessDPIUnaware = 0,
            ProcessSystemDPIAware = 1,
            ProcessPerMonitorDPIAware = 2
        }

        [DllImport("shcore.dll")]
        private static extern int SetProcessDpiAwareness(ProcessDPIAwareness value);



        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    Stop();
                    _cancellationTokenSource.Dispose();
                }

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
