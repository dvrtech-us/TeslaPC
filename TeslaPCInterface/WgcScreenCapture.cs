using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace Streaming;

/// <summary>
/// Captures the primary monitor using Windows.Graphics.Capture (WGC). Unlike DXGI Desktop
/// Duplication, WGC captures the fully composited DWM output, so hardware-overlay (MPO) video
/// that shows as a black rectangle under duplication renders correctly. It also includes the
/// cursor by default. DRM-protected content still renders black — that exclusion applies to
/// every capture API by design.
///
/// Falls back to unavailable state if initialization fails (pre-1903 Windows, no monitor,
/// or TESLAPC_NO_WGC=1); caller should use DXGI, then GDI.
/// </summary>
internal sealed class WgcScreenCapture : IDisposable
{
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _stagingTexture;
    private int _textureWidth;
    private int _textureHeight;
    private byte[] _rowCopyBuffer = Array.Empty<byte>();
    private bool _disposed;

    public Size CaptureSize { get; private set; }
    public bool IsAvailable { get; private set; }

    public WgcScreenCapture()
    {
        if (Environment.GetEnvironmentVariable("TESLAPC_NO_WGC") == "1")
        {
            Console.WriteLine("[Capture] WGC disabled via TESLAPC_NO_WGC=1.");
            return;
        }

        try
        {
            Initialize();
            IsAvailable = true;
            Console.WriteLine($"[Capture] Windows Graphics Capture ready ({CaptureSize.Width}x{CaptureSize.Height}).");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Capture] WGC unavailable, will use DXGI/GDI fallback: {ex.Message}");
            IsAvailable = false;
        }
    }

    private void Initialize()
    {
        if (!GraphicsCaptureSession.IsSupported())
            throw new InvalidOperationException("GraphicsCaptureSession is not supported on this OS.");

        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            null,
            out _device,
            out _context).CheckError();

        _winrtDevice = CreateWinRtDevice(_device!);
        _item = CreateItemForPrimaryMonitor();
        CaptureSize = new Size(_item.Size.Width, _item.Size.Height);
        _item.Closed += (_, _) => IsAvailable = false;

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winrtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            _item.Size);

        _session = _framePool.CreateCaptureSession(_item);
        TryDisableCaptureBorder(_session);
        _session.StartCapture();
    }

    /// <summary>Wraps the D3D11 device as a WinRT IDirect3DDevice for the frame pool.</summary>
    private static IDirect3DDevice CreateWinRtDevice(ID3D11Device device)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr abi);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(abi);
        }
        finally
        {
            Marshal.Release(abi);
        }
    }

    private static GraphicsCaptureItem CreateItemForPrimaryMonitor()
    {
        IntPtr hmon = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
        if (hmon == IntPtr.Zero)
            throw new InvalidOperationException("No primary monitor found.");

        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        Guid itemGuid = GraphicsCaptureItemGuid;
        IntPtr abi = interop.CreateForMonitor(hmon, ref itemGuid);
        try
        {
            return GraphicsCaptureItem.FromAbi(abi);
        }
        finally
        {
            Marshal.Release(abi);
        }
    }

    /// <summary>
    /// Hides the OS "capture in progress" border where the API and access rules allow it
    /// (Win11 / Win10 21H2+; unpackaged apps may be denied — that only leaves the border visible).
    /// </summary>
    private static void TryDisableCaptureBorder(GraphicsCaptureSession session)
    {
        try
        {
            if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
                session.IsBorderRequired = false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Capture] Could not hide the WGC capture border: {ex.Message}");
        }
    }

    /// <summary>
    /// Copies the latest composited frame into <paramref name="target"/>.
    /// Returns false when no new frame has arrived since the last call.
    /// </summary>
    public bool TryCapture(Bitmap target)
    {
        if (!IsAvailable || _framePool == null || _context == null)
            return false;

        Direct3D11CaptureFrame? frame = null;
        try
        {
            // Drain the pool so a slow consumer always gets the newest frame, not a stale one.
            while (true)
            {
                var next = _framePool.TryGetNextFrame();
                if (next == null) break;
                frame?.Dispose();
                frame = next;
            }
            if (frame == null)
                return false;

            // A mode/DPI switch resizes the content; the caller restarts the session on
            // resolution changes, so just skip mismatched frames here.
            if (frame.ContentSize.Width != CaptureSize.Width || frame.ContentSize.Height != CaptureSize.Height)
                return false;

            var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
            Guid textureGuid = typeof(ID3D11Texture2D).GUID;
            IntPtr texturePtr = access.GetInterface(ref textureGuid);
            using var frameTexture = new ID3D11Texture2D(texturePtr);

            EnsureStagingTexture(frameTexture);

            _context.CopyResource(_stagingTexture!, frameTexture);

            var mapped = _context.Map(_stagingTexture!, 0, MapMode.Read, MapFlags.None);
            try
            {
                CopyMappedFrame(target, mapped, _textureWidth, _textureHeight);
            }
            finally
            {
                _context.Unmap(_stagingTexture!, 0);
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Capture] WGC frame error: {ex.Message}");
            return false;
        }
        finally
        {
            frame?.Dispose();
        }
    }

    private void EnsureStagingTexture(ID3D11Texture2D frameTexture)
    {
        var desc = frameTexture.Description;
        if (_stagingTexture != null && _textureWidth == desc.Width && _textureHeight == desc.Height)
            return;

        _stagingTexture?.Dispose();
        _textureWidth = desc.Width;
        _textureHeight = desc.Height;

        desc.BindFlags = BindFlags.None;
        desc.CPUAccessFlags = CpuAccessFlags.Read;
        desc.Usage = ResourceUsage.Staging;
        desc.MiscFlags = 0;

        _stagingTexture = _device!.CreateTexture2D(desc);
    }

    private void CopyMappedFrame(Bitmap target, MappedSubresource mapped, int width, int height)
    {
        if (target.Width != width || target.Height != height)
            throw new InvalidOperationException("WGC capture bitmap size mismatch.");

        var bitmapData = target.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            int srcStride = mapped.RowPitch;
            int dstStride = bitmapData.Stride;
            int rowBytes = width * 4;
            IntPtr src = mapped.DataPointer;
            IntPtr dst = bitmapData.Scan0;

            if (_rowCopyBuffer.Length < rowBytes)
                _rowCopyBuffer = new byte[rowBytes];

            for (int row = 0; row < height; row++)
            {
                Marshal.Copy(src + row * srcStride, _rowCopyBuffer, 0, rowBytes);
                Marshal.Copy(_rowCopyBuffer, 0, dst + row * dstStride, rowBytes);
            }
        }
        finally
        {
            target.UnlockBits(bitmapData);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        try { _session?.Dispose(); } catch { }
        try { _framePool?.Dispose(); } catch { }
        _item = null;
        try { _winrtDevice?.Dispose(); } catch { }
        _stagingTexture?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _disposed = true;
    }

    // ---- Interop ----

    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [ComImport]
    [System.Runtime.InteropServices.Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [ComImport]
    [System.Runtime.InteropServices.Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private const uint MONITOR_DEFAULTTOPRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
}
