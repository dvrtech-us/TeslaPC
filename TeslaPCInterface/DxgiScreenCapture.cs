using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using MapFlags = Vortice.Direct3D11.MapFlags;
using DxgiResult = Vortice.DXGI.ResultCode;

namespace Streaming;

/// <summary>
/// Captures the primary monitor using DXGI Desktop Duplication (D3D11).
/// Falls back to unavailable state if initialization fails; caller should use GDI.
/// </summary>
internal sealed class DxgiScreenCapture : IDisposable
{
    private IDXGIFactory1? _factory;
    private IDXGIAdapter1? _adapter;
    private IDXGIOutput1? _output;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _stagingTexture;
    private int _textureWidth;
    private int _textureHeight;
    private byte[] _rowCopyBuffer = Array.Empty<byte>();
    private bool _disposed;

    public Size CaptureSize { get; private set; }
    public bool IsAvailable { get; private set; }

    public DxgiScreenCapture()
    {
        try
        {
            Initialize();
            IsAvailable = true;
            Console.WriteLine($"[Capture] DXGI desktop duplication ready ({CaptureSize.Width}x{CaptureSize.Height}).");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Capture] DXGI unavailable, will use GDI fallback: {ex.Message}");
            IsAvailable = false;
        }
    }

    private void Initialize()
    {
        _factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>()
            ?? throw new InvalidOperationException("Failed to create DXGI factory.");

        var primaryBounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds
            ?? throw new InvalidOperationException("No primary screen found.");

        if (!TryFindPrimaryOutput(primaryBounds, out _adapter, out _output))
            throw new InvalidOperationException("Could not find DXGI output for the primary monitor.");

        // DuplicateOutput requires a D3D11 device created on the same adapter as the output.
        D3D11.D3D11CreateDevice(
            _adapter,
            DriverType.Unknown,
            DeviceCreationFlags.BgraSupport,
            null,
            out _device,
            out _context).CheckError();

        _duplication = _output.DuplicateOutput(_device)
            ?? throw new InvalidOperationException("DuplicateOutput returned null.");

        _textureWidth = CaptureSize.Width;
        _textureHeight = CaptureSize.Height;
    }

    private bool TryFindPrimaryOutput(Rectangle primaryBounds, out IDXGIAdapter1 adapter, out IDXGIOutput1 output)
    {
        adapter = null!;
        output = null!;

        for (int adapterIndex = 0; _factory!.EnumAdapters1(adapterIndex, out var candidateAdapter).Success; adapterIndex++)
        {
            for (int outputIndex = 0; candidateAdapter.EnumOutputs(outputIndex, out var candidateOutput).Success; outputIndex++)
            {
                using (candidateOutput)
                {
                    var candidateOutput1 = candidateOutput.QueryInterface<IDXGIOutput1>();
                    if (candidateOutput1 == null)
                        continue;

                    var desc = candidateOutput1.Description;
                    var desktop = desc.DesktopCoordinates;

                    bool containsOrigin = primaryBounds.Left >= desktop.Left
                        && primaryBounds.Left < desktop.Right
                        && primaryBounds.Top >= desktop.Top
                        && primaryBounds.Top < desktop.Bottom;

                    if (!containsOrigin)
                    {
                        candidateOutput1.Dispose();
                        continue;
                    }

                    adapter = candidateAdapter;
                    output = candidateOutput1;
                    CaptureSize = new Size(desktop.Right - desktop.Left, desktop.Bottom - desktop.Top);
                    return true;
                }
            }

            candidateAdapter.Dispose();
        }

        return false;
    }

    /// <summary>
    /// Captures the latest desktop frame into <paramref name="target"/>.
    /// Returns false when no new frame is available (timeout) or after recoverable errors.
    /// </summary>
    public bool TryCapture(Bitmap target)
    {
        if (!IsAvailable || _duplication == null || _device == null || _context == null)
            return false;

        IDXGIResource? desktopResource = null;
        bool acquired = false;
        try
        {
            var result = _duplication.AcquireNextFrame(16, out _, out desktopResource);
            if (result == DxgiResult.WaitTimeout)
                return false;

            if (result == DxgiResult.AccessLost || result == DxgiResult.AccessDenied)
            {
                Console.WriteLine("[Capture] DXGI access lost; recreating duplication...");
                RecreateDuplication();
                return false;
            }

            result.CheckError();
            acquired = true;

            using var desktopTexture = desktopResource.QueryInterface<ID3D11Texture2D>();
            if (desktopTexture == null)
                return false;

            EnsureStagingTexture(desktopTexture);

            _context.CopyResource(_stagingTexture!, desktopTexture);

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
            Console.WriteLine($"[Capture] DXGI frame error: {ex.Message}");
            return false;
        }
        finally
        {
            desktopResource?.Dispose();
            if (acquired)
            {
                try
                {
                    _duplication?.ReleaseFrame();
                }
                catch
                {
                    // Best effort.
                }
            }
        }
    }

    private void EnsureStagingTexture(ID3D11Texture2D desktopTexture)
    {
        var desc = desktopTexture.Description;
        if (_stagingTexture != null && _textureWidth == desc.Width && _textureHeight == desc.Height)
            return;

        _stagingTexture?.Dispose();
        _textureWidth = desc.Width;
        _textureHeight = desc.Height;

        if (CaptureSize.Width != _textureWidth || CaptureSize.Height != _textureHeight)
        {
            throw new InvalidOperationException(
                $"DXGI texture size {_textureWidth}x{_textureHeight} does not match output size {CaptureSize.Width}x{CaptureSize.Height}.");
        }

        desc.BindFlags = BindFlags.None;
        desc.CPUAccessFlags = CpuAccessFlags.Read;
        desc.Usage = ResourceUsage.Staging;
        desc.MiscFlags = 0;

        _stagingTexture = _device!.CreateTexture2D(desc);
    }

    private void CopyMappedFrame(Bitmap target, MappedSubresource mapped, int width, int height)
    {
        if (target.Width != width || target.Height != height)
            throw new InvalidOperationException("DXGI capture bitmap size mismatch.");

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

    private void RecreateDuplication()
    {
        _duplication?.Dispose();
        _duplication = null;
        _stagingTexture?.Dispose();
        _stagingTexture = null;

        if (_output == null || _device == null)
        {
            IsAvailable = false;
            return;
        }

        try
        {
            _duplication = _output.DuplicateOutput(_device);
            if (_duplication == null)
                throw new InvalidOperationException("DuplicateOutput returned null.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Capture] DXGI recreation failed: {ex.Message}");
            IsAvailable = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _stagingTexture?.Dispose();
        _duplication?.Dispose();
        _output?.Dispose();
        _adapter?.Dispose();
        _factory?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _disposed = true;
    }
}