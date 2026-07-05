
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
        private readonly StreamTiming _timing;
        private readonly DisplayWebSocket _displayWebSocket;
        private readonly int _fps;
        private volatile int _maxWidth;
        private volatile int _maxHeight;
        private int _restartEpoch;   // bumped to force the capture session to restart (cap/desktop-res change)
        private bool _disposed = false;

        // Shared capture state
        private readonly object _frameLock = new();
        private byte[] _currentFrame = Array.Empty<byte>();
        private int _frameNumber;
        private long _framePtsUs;
        private int _httpClientCount;
        /// <summary>HTTP /stream plus WebSocket /ws/display clients.</summary>
        public int ClientCount => Volatile.Read(ref _httpClientCount) + _displayWebSocket.ClientCount;
        private Thread? _captureThread;
        private int _lastOutWidth = 1;
        private int _lastOutHeight = 1;

        // Media mode: while a video file is playing, MediaStreamer publishes decoded JPEG frames
        // into the shared buffer via PublishMediaFrame, and the screen-capture loop stands down so
        // the two sources never fight over _currentFrame. Client send threads are source-agnostic —
        // they keep streaming whatever the latest published frame is.
        private volatile bool _mediaMode;
        /// <summary>True while an external source (MediaStreamer) is driving the frame buffer.</summary>
        public bool MediaMode => _mediaMode;

        /// <summary>Current cap box the live screen is scaled into (preserving aspect, never upscaling).</summary>
        public int MaxWidth => _maxWidth;
        public int MaxHeight => _maxHeight;

        /// <summary>
        /// Changes the output cap box live. The running capture session restarts on its next tick and
        /// reallocates at the new size.
        /// </summary>
        public void SetMaxResolution(int width, int height)
        {
            _maxWidth = Math.Max(16, width);
            _maxHeight = Math.Max(16, height);
            RestartCapture();
        }

        /// <summary>
        /// Forces the capture session to restart (rebuilds the DXGI duplication and scaled buffers).
        /// Call after the Windows display resolution changes — DXGI can't continue across a mode switch.
        /// </summary>
        public void RestartCapture()
        {
            Interlocked.Increment(ref _restartEpoch);
            lock (_frameLock) Monitor.PulseAll(_frameLock);
        }

        public void RestartDisplayClients(string reason)
        {
            _displayWebSocket.RestartClients(reason);
        }


        /// <summary>
        /// constructor that takes in the size of the screen
        /// </summary>
        /// <param name="width"></param>
        /// <param name="height"></param>

        public ImageStreamingServer(int width, int height, int fps, StreamTiming timing)
        {
            _maxWidth = width;
            _maxHeight = height;
            _fps = fps;
            _timing = timing;
            _displayWebSocket = new DisplayWebSocket(timing);
            Interval = 1000 / fps;
        }

        /// <summary>Handles a display WebSocket client on <c>/ws/display</c>.</summary>
        public Task HandleDisplayWebSocketAsync(HttpListenerContext context)
        {
            var (w, h) = GetStreamOutputSize();
            return _displayWebSocket.HandleClientAsync(context, w, h, _fps, EnsureCaptureRunning);
        }

        /// <summary>Estimated capture output size (used before the first frame is published).</summary>
        private (int Width, int Height) GetStreamOutputSize()
        {
            if (_lastOutWidth > 1 && _lastOutHeight > 1)
                return (_lastOutWidth, _lastOutHeight);

            var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            double scale = Math.Min(1.0, Math.Min((double)_maxWidth / screen.Width, (double)_maxHeight / screen.Height));
            int w = MakeEven(Math.Max(1, (int)Math.Round(screen.Width * scale)));
            int h = MakeEven(Math.Max(1, (int)Math.Round(screen.Height * scale)));
            return (w, h);
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
            Interlocked.Increment(ref _httpClientCount);
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
                // In media mode the frame buffer is driven by MediaStreamer, not screen capture.
                if (_mediaMode)
                    return;
                if (_captureThread == null || !_captureThread.IsAlive)
                {
                    _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "ScreenCapture" };
                    _captureThread.Start();
                }
            }
        }

        /// <summary>
        /// Switches the frame buffer over to an external producer (MediaStreamer). The screen-capture
        /// loop exits on its next check; client send threads stay connected and start showing the
        /// frames published via <see cref="PublishMediaFrame"/>.
        /// </summary>
        public void EnterMediaMode()
        {
            lock (_frameLock)
            {
                _mediaMode = true;
                Monitor.PulseAll(_frameLock);
            }
        }

        /// <summary>
        /// Returns control of the frame buffer to screen capture. If clients are still connected the
        /// capture loop is restarted so the live screen resumes immediately.
        /// </summary>
        public void ExitMediaMode()
        {
            lock (_frameLock)
            {
                _mediaMode = false;
                Monitor.PulseAll(_frameLock);
            }
            if (ClientCount > 0)
                EnsureCaptureRunning();
        }

        /// <summary>
        /// Publishes a pre-encoded JPEG frame from an external source (MediaStreamer). Ignored unless
        /// media mode is active, so a late reader thread can't clobber the live screen after stop.
        /// </summary>
        public void PublishMediaFrame(byte[] jpeg)
        {
            if (!_mediaMode || jpeg.Length == 0)
                return;
            long pts = _timing.HostPtsUs;
            lock (_frameLock)
            {
                _currentFrame = jpeg;
                _framePtsUs = pts;
                _frameNumber++;
                Monitor.PulseAll(_frameLock);
            }

            _displayWebSocket.BroadcastFrame(pts, jpeg, null);
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
                       && ClientCount > 0
                       && !_mediaMode)
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
                _displayWebSocket.StopH264Encoder();
                lock (_frameLock)
                {
                    _captureThread = null;
                    Monitor.PulseAll(_frameLock);
                }
            }
        }

        private bool RunCaptureSession()
        {
            int epoch = Volatile.Read(ref _restartEpoch);
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
            int outWidth = MakeEven(Math.Max(1, (int)Math.Round(screenSize.Width * scale)));
            int outHeight = MakeEven(Math.Max(1, (int)Math.Round(screenSize.Height * scale)));
            _lastOutWidth = outWidth;
            _lastOutHeight = outHeight;
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
                   && ClientCount > 0
                   && !_mediaMode)
            {
                // Restart the session (reallocate at the new size) when the cap changes or the desktop
                // resolution changes — DXGI duplication can't survive a display-mode switch.
                if (Volatile.Read(ref _restartEpoch) != epoch)
                    return true;
                var nowBounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
                if (nowBounds.Width != screenSize.Width || nowBounds.Height != screenSize.Height)
                    return true;

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
                    bool needH264 = _displayWebSocket.NeedH264;
                    bool needJpeg = Volatile.Read(ref _httpClientCount) > 0 || _displayWebSocket.NeedMjpeg;

                    if (needsResize)
                    {
                        scaledGraphics!.DrawImage(srcImage, 0, 0, outWidth, outHeight);
                    }

                    byte[]? jpeg = null;
                    if (needJpeg)
                    {
                        ms.SetLength(0);
                        if (needsResize)
                            scaledImage!.Save(ms, jpegCodec, encoderParameters);
                        else
                            srcImage.Save(ms, jpegCodec, encoderParameters);
                        jpeg = ms.ToArray();
                    }

                    List<byte[]>? h264 = null;
                    if (needH264)
                    {
                        if (needsResize)
                        {
                            byte[] bgr = BitmapToBgr24(scaledImage!);
                            h264 = _displayWebSocket.EncodeH264(bgr, outWidth, outHeight, _fps);
                        }
                        else
                        {
                            h264 = EncodeH264FromBgra32(srcImage, outWidth, outHeight);
                        }
                    }
                    else
                    {
                        _displayWebSocket.StopH264Encoder();
                    }

                    PublishFrame(jpeg, h264);
                }

                int elapsed = Environment.TickCount - lastStart;
                lastStart = Environment.TickCount;
                if (elapsed < Interval)
                    Thread.Sleep(Interval - elapsed);
            }

            return false;
        }

        private void PublishFrame(byte[]? jpeg, IReadOnlyList<byte[]>? h264)
        {
            long pts = _timing.HostPtsUs;
            if (jpeg is { Length: > 0 })
            {
                lock (_frameLock)
                {
                    _currentFrame = jpeg;
                    _framePtsUs = pts;
                    _frameNumber++;
                    Monitor.PulseAll(_frameLock);
                }
            }

            _displayWebSocket.BroadcastFrame(pts, jpeg, h264);
        }

        private static int MakeEven(int value) => Math.Max(2, value & ~1);

        /// <summary>Tightly packed BGR24 (width * height * 3) for native H264/NV12 conversion.</summary>
        private static byte[] BitmapToBgr24(Bitmap bmp)
        {
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                int width = bmp.Width;
                int height = bmp.Height;
                int stride = Math.Abs(data.Stride);
                var packed = new byte[width * height * 3];
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(data.Scan0 + y * stride, packed, y * width * 3, width * 3);
                }
                return packed;
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        private List<byte[]> EncodeH264FromBgra32(Bitmap src, int width, int height)
        {
            var rect = new Rectangle(0, 0, width, height);
            var data = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                return _displayWebSocket.EncodeH264Bgra32(data.Scan0, width, height, data.Stride, _fps);
            }
            finally
            {
                src.UnlockBits(data);
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
                Interlocked.Decrement(ref _httpClientCount);
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
                    // Source is alive if either screen capture is running or media is driving frames.
                    if (!_mediaMode && (_captureThread == null || !_captureThread.IsAlive))
                        return false;

                    if (!Monitor.Wait(_frameLock, 1000))
                    {
                        if (!_mediaMode && (_captureThread == null || !_captureThread.IsAlive))
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
                    _displayWebSocket.Shutdown();
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
