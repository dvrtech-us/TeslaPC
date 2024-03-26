
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

        public ImageStreamingServer(int width, int height)
        {

            this.ImagesSource = Screen.Snapshots(width, height, true);
            this.Interval = 25;

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
        /// The listener that listens for any new connections.
        /// </summary>
        private HttpListener? _listener;


        /// <summary>
        /// Starts the server to accepts any new connections on the specified port.
        /// </summary>
        /// <param name="port"></param>
        public async Task Start(int port, int sslPort)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://*:{port}/");

            _listener.Prefixes.Add($"https://*:{sslPort}/");

            Console.WriteLine($"Image Server started on: ");
            foreach (var prefix in _listener.Prefixes)
            {
                Console.WriteLine("\t" + prefix);
            }


            _listener.Start();

            _ = ThreadPool.QueueUserWorkItem(o =>
            {
                try
                {
                    while (_listener.IsListening && !_cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        _ = ThreadPool.QueueUserWorkItem(c =>
                        {
                            var ctx = c as HttpListenerContext;
                            writeJPEG(ctx);

                        }, _listener.GetContext());
                    }
                }
                catch { } // suppress any exceptions
            });
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

                // Streams the images from the source to the client.
                foreach (var imgStream in Screen.Streams(this.ImagesSource))
                {
                    if (this.Interval > 0)
                        Thread.Sleep(this.Interval);

                    wr.Write(imgStream);
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





        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Dispose managed resources
                    _cancellationTokenSource.Dispose();
                    _listener?.Stop();
                    _listener?.Close();

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


    /// <summary>
    /// Provides a way to capture the screen and stream it to the client.
    /// </summary>

    static class Screen
    {


        public static IEnumerable<Image> Snapshots()
        {
            return Screen.Snapshots(System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width, System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height, true);
        }

        /// <summary>
        /// Returns a 
        /// </summary>
        /// <param name="delayTime"></param>
        /// <returns></returns>
        public static IEnumerable<Image> Snapshots(int width, int height, bool showCursor)
        {
            SetDpiAwareness();
            Size size = new(System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width, System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height);


            Bitmap srcImage = new(size.Width, size.Height);
            Graphics srcGraphics = Graphics.FromImage(srcImage);

            bool scaled = (width != size.Width || height != size.Height);

            var dstImage = srcImage;
            var dstGraphics = srcGraphics;

            if (scaled)
            {
                Console.WriteLine("Scaling images to " + width + "x" + height);
                //resize the image to the specified width and height
                dstImage = new Bitmap(width, height);
                dstGraphics = Graphics.FromImage(dstImage);
            }

            Rectangle src = new(0, 0, size.Width, size.Height);
            Rectangle dst = new(0, 0, width, height);
            Size curSize = new(32, 32);

            while (true)
            {
                srcGraphics.CopyFromScreen(0, 0, 0, 0, size);

                //if (showCursor)
                //  Cursors.Default.Draw(srcGraphics, new Rectangle(Cursor.Position, curSize));

                if (scaled)
                    dstGraphics.DrawImage(srcImage, dst, src, GraphicsUnit.Pixel);

                yield return dstImage;

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

        private static void SetDpiAwareness()
        {
            try
            {
                if (Environment.OSVersion.Version.Major >= 6)
                {
                    SetProcessDpiAwareness(ProcessDPIAwareness.ProcessPerMonitorDPIAware);
                }
            }
            catch (EntryPointNotFoundException)//this exception occures if OS does not implement this API, just ignore it.
            {
            }
        }

        internal static IEnumerable<MemoryStream> Streams(this IEnumerable<Image> source)
        {
            var ms = new MemoryStream();

            foreach (var img in source)
            {
                ms.SetLength(0);
                img.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                yield return ms;
            }

            ms.Close();


        }


    }

}
