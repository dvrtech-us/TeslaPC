using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace Streaming
{

    /// <summary>
    /// Provides a stream writer that can be used to write images as MJPEG
    /// or (Motion JPEG) to any stream.
    /// </summary>
    public class MjpegWriter : IDisposable
    {
        private readonly HttpListenerContext _context;
        private readonly string _boundary;
        private bool _disposed = false;

        public MjpegWriter(HttpListenerContext context, string boundary)
        {
            _context = context;
            _boundary = boundary;
        }

        public void WriteHeader()
        {
            _context.Response.ContentType = "multipart/x-mixed-replace; boundary=" + _boundary;
            _context.Response.StatusCode = 200;
            _context.Response.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
            _context.Response.Headers.Add("Pragma", "no-cache");
            // Long-lived multipart body: must stream without a fixed Content-Length.
            _context.Response.SendChunked = true;
            _context.Response.KeepAlive = true;
        }


        public void Write(byte[] jpegData)
        {
            // Write boundary
            byte[] boundaryBytes = Encoding.ASCII.GetBytes(
            "\r\n--" + _boundary
            + "\r\nContent-Type: image/jpeg"
            + "\r\nContent-Length: " + jpegData.Length + "\r\n"
            + "\r\n");
            _context.Response.OutputStream.Write(boundaryBytes, 0, boundaryBytes.Length);

            _context.Response.OutputStream.Write(jpegData, 0, jpegData.Length);

            _context.Response.OutputStream.Write(_endOfImageBytes, 0, _endOfImageBytes.Length);

            _context.Response.OutputStream.Flush();
        }

        private static readonly byte[] _endOfImageBytes = Encoding.ASCII.GetBytes("\r\n");





        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Dispose managed resources
                    _context?.Response.OutputStream?.Dispose();
                }

                // Dispose unmanaged resources

                _disposed = true;
            }
        }
    }
}
