
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
        private bool _disposed = false;


        /// <summary>
        /// constructor that takes in the size of the screen
        /// </summary>
        /// <param name="width"></param>
        /// <param name="height"></param>

        public ImageStreamingServer(int width, int height, int fps)
        {


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
        private void writeJPEG(HttpListenerContext ctx)
        {

            try
            {

                // Writes the response header to the client.
                MjpegWriter wr = new(ctx, "--boundary");
                wr.WriteHeader();
                SetProcessDpiAwareness( ProcessDPIAwareness.ProcessPerMonitorDPIAware);

                Size size = new(System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width, System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height);


                Bitmap srcImage = new(size.Width, size.Height);
                Graphics srcGraphics = Graphics.FromImage(srcImage);





                var ms = new MemoryStream();
                int lastStart = Environment.TickCount;
                while (true)
                {
                    srcGraphics.CopyFromScreen(0, 0, 0, 0, size);


                    ms.SetLength(0);
                    //set jpeg quality
                    EncoderParameters encoderParameters = new(1);
                    encoderParameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 60L);
                    srcImage.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);

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
