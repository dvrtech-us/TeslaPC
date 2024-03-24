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
        private HttpListenerContext _context;
        private string _boundary;

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

        public void Write(Image image)
        {
            MemoryStream ms = BytesOf(image);
            this.Write(ms);
        }

        public void Write(MemoryStream imageStream)
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine();
            sb.AppendLine(_boundary);
            sb.AppendLine("Content-Type: image/jpeg");
            sb.AppendLine("Content-Length: " + imageStream.Length.ToString());
            sb.AppendLine();

            Write(sb.ToString());
            imageStream.WriteTo(_context.Response.OutputStream);
            Write("\r\n");

            _context.Response.OutputStream.Flush();
        }

        private void Write(string text)
        {
            byte[] data = BytesOf(text);
            _context.Response.OutputStream.Write(data, 0, data.Length);
        }

        private static byte[] BytesOf(string text)
        {
            return Encoding.ASCII.GetBytes(text);
        }

        private static MemoryStream BytesOf(Image image)
        {
            MemoryStream ms = new MemoryStream();
            image.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
            return ms;
        }

        public void Dispose()
        {
            throw new NotImplementedException();
        }
    }
}
