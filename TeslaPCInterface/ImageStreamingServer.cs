
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net;
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
        /// <summary>Number of clients currently receiving the MJPEG stream.</summary>
        public int ClientCount => Volatile.Read(ref _clientCount);
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

            // Send MJPEG headers on the request thread. If we defer this to the
            // thread pool, http.sys can return 503 Service Unavailable.
            var writer = new MjpegWriter(ctx, "boundary");
            writer.WriteHeader();
            ThreadPool.QueueUserWorkItem(_ => StreamToClient(ctx, writer));
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

                while (!_cancellationTokenSource.Token.IsCancellationRequested
                       && Volatile.Read(ref _clientCount) > 0)
                {
                    if (!RunCaptureSession())
                        break;
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

        private bool RunCaptureSession()
        {
            using var dxgiCapture = new DxgiScreenCapture();
            bool useDxgi = dxgiCapture.IsAvailable;
            if (!useDxgi)
            {
                Console.WriteLine("[Capture] Using GDI CopyFromScreen fallback.");
            }

            Size screenSize = useDxgi
                ? dxgiCapture.CaptureSize
                : new Size(
                    System.Windows.Forms.Screen.PrimaryScreen!.Bounds.Width,
                    System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height);

            // Scale uniformly to fit within the cap box (preserves aspect ratio; never upscales),
            // so the stream always matches the screen's shape regardless of its aspect ratio.
            double scale = Math.Min(1.0, Math.Min((double)_maxWidth / screenSize.Width, (double)_maxHeight / screenSize.Height));
            int outWidth = Math.Max(1, (int)Math.Round(screenSize.Width * scale));
            int outHeight = Math.Max(1, (int)Math.Round(screenSize.Height * scale));
            bool needsResize = outWidth != screenSize.Width || outHeight != screenSize.Height;

            using Bitmap srcImage = new(screenSize.Width, screenSize.Height, PixelFormat.Format32bppArgb);
            using Graphics? srcGraphics = useDxgi ? null : Graphics.FromImage(srcImage);

            using Bitmap? scaledImage = needsResize ? new Bitmap(outWidth, outHeight, PixelFormat.Format24bppRgb) : null;
            using Graphics? scaledGraphics = needsResize ? Graphics.FromImage(scaledImage!) : null;
            if (scaledGraphics != null)
            {
                scaledGraphics.CompositingQuality = CompositingQuality.HighSpeed;
                scaledGraphics.InterpolationMode = InterpolationMode.Bilinear;
                scaledGraphics.SmoothingMode = SmoothingMode.None;
            }

            var jpegCodec = GetJpegCodec();
            using EncoderParameters encoderParameters = new(1);
            encoderParameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 60L);

            using var ms = new MemoryStream();
            int lastStart = Environment.TickCount;
            while (!_cancellationTokenSource.Token.IsCancellationRequested
                   && Volatile.Read(ref _clientCount) > 0)
            {
                bool captured = false;
                if (useDxgi)
                {
                    captured = dxgiCapture.TryCapture(srcImage);
                }
                else
                {
                    srcGraphics!.CopyFromScreen(0, 0, 0, 0, screenSize);
                    captured = true;
                }

                if (captured)
                {
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

                    PublishFrame(ms);
                }

                int elapsed = Environment.TickCount - lastStart;
                lastStart = Environment.TickCount;
                if (elapsed < Interval)
                    Thread.Sleep(Interval - elapsed);
            }

            return false;
        }

        private void PublishFrame(MemoryStream jpegStream)
        {
            byte[] frameBytes = jpegStream.ToArray();
            lock (_frameLock)
            {
                _currentFrame = frameBytes;
                _frameNumber++;
                Monitor.PulseAll(_frameLock);
            }
        }

        /// <summary>
        /// Per-client thread that reads shared frames and writes MJPEG to the response.
        /// Drops stale frames so slow clients always receive the latest image.
        /// </summary>
        private void StreamToClient(HttpListenerContext ctx, MjpegWriter wr)
        {
            try
            {
                int lastFrame = 0;
                while (!_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    byte[] frame;
                    int frameNumber;
                    if (!TryWaitForLatestFrame(ref lastFrame, out frame, out frameNumber))
                        return;

                    // If a newer frame arrived while we were waiting, skip this one.
                    lock (_frameLock)
                    {
                        if (_frameNumber > frameNumber)
                            continue;
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

        private bool TryWaitForLatestFrame(ref int lastFrame, out byte[] frame, out int frameNumber)
        {
            frame = Array.Empty<byte>();
            frameNumber = 0;

            lock (_frameLock)
            {
                while (_frameNumber == lastFrame)
                {
                    if (_captureThread == null || !_captureThread.IsAlive)
                        return false;

                    if (!Monitor.Wait(_frameLock, 1000))
                    {
                        if (_captureThread == null || !_captureThread.IsAlive)
                            return false;
                        continue;
                    }

                    if (_cancellationTokenSource.Token.IsCancellationRequested)
                        return false;
                }

                frameNumber = _frameNumber;
                frame = _currentFrame;
                lastFrame = frameNumber;
            }

            return true;
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