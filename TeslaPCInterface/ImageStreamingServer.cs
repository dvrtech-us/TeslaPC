
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
        /// Gets or sets the source of images that will be streamed to the
        /// any connected client.
        /// </summary>
        public IEnumerable<Image> ImagesSource { get; set; }

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
            ThreadPool.QueueUserWorkItem(_ => writeJPEG(ctx));
        }

        /// <summary>
        /// Writes the images to the client as MJPEG.
        /// </summary>
        /// <param name="ctx"></param>
        private static ImageCodecInfo GetJpegCodec()
        {
            foreach (var codec in ImageCodecInfo.GetImageEncoders())
            {
                if (codec.MimeType == "image/jpeg")
                    return codec;
            }
            throw new InvalidOperationException("JPEG codec not found");
        }

        private void writeJPEG(HttpListenerContext ctx)
        {

            try
            {

                // Writes the response header to the client.
                MjpegWriter wr = new(ctx, "--boundary");
                wr.WriteHeader();
                SetProcessDpiAwareness( ProcessDPIAwareness.ProcessPerMonitorDPIAware);

                Size screenSize = new(System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width, System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height);

                // Cap output resolution to the configured maximum
                int outWidth = Math.Min(screenSize.Width, _maxWidth);
                int outHeight = Math.Min(screenSize.Height, _maxHeight);
                bool needsResize = outWidth != screenSize.Width || outHeight != screenSize.Height;

                using Bitmap srcImage = new(screenSize.Width, screenSize.Height);
                using Graphics srcGraphics = Graphics.FromImage(srcImage);

                // Only allocate a scaled bitmap if we actually need to resize
                Bitmap? scaledImage = needsResize ? new Bitmap(outWidth, outHeight) : null;
                Graphics? scaledGraphics = needsResize ? Graphics.FromImage(scaledImage!) : null;

                // Set up JPEG encoder once outside the loop
                var jpegCodec = GetJpegCodec();
                using EncoderParameters encoderParameters = new(1);
                encoderParameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 60L);

                var ms = new MemoryStream();
                int lastStart = Environment.TickCount;
                while (true)
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

                    wr.Write(ms);
                    if(_cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        break;
                    }
                    //wait for the next frame
                    int elapsed = Environment.TickCount - lastStart;
                    lastStart = Environment.TickCount;
                    if (elapsed < Interval)
                    Thread.Sleep(Interval - elapsed );

                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
            }
            finally
            {
                ctx.Response.OutputStream.Close();
            }
        }


        public void Stop()
        {


            try
            {
                _cancellationTokenSource.Cancel();
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
                    // Dispose managed resources
                    _cancellationTokenSource.Dispose();

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
