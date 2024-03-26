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
        }

    
        public void Write(MemoryStream imageStream)
        {
            // Write boundary
            byte[] boundaryBytes = Encoding.ASCII.GetBytes(
            "\r\n--" + _boundary
            + "\r\nContent-Type: image/jpeg"
            + "\r\nContent-Length: " + imageStream.Length + "\r\n"
            + "\r\n");
            _context.Response.OutputStream.Write(boundaryBytes, 0, boundaryBytes.Length);



            imageStream.WriteTo(_context.Response.OutputStream);
          
            _context.Response.OutputStream.Write(_endOfImageBytes, 0, _endOfImageBytes.Length);

            _context.Response.OutputStream.FlushAsync();
        }
      
        private static byte[] _endOfImageBytes = Encoding.ASCII.GetBytes("\r\n");
      

       



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
