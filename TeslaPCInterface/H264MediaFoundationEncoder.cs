using System.Runtime.InteropServices;

namespace Streaming;

/// <summary>
/// Low-latency BGR24 -> Annex-B H.264 encoder backed by the native Windows Media Foundation
/// H.264 encoder MFT (mfh264enc.dll).
/// </summary>
internal sealed class H264MediaFoundationEncoder : IDisposable
{
    private const int S_OK = 0;
    private const int S_FALSE = 1;
    private const int MF_VERSION = 0x00020070;
    private const int CLSCTX_INPROC_SERVER = 0x1;
    private const int COINIT_MULTITHREADED = 0x0;
    private const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
    private const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B0);
    private const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);

    private const int MFVideoInterlace_Progressive = 2;
    private const int eAVEncH264VProfile_Base = 66;

    private const int MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000;
    private const int MFT_MESSAGE_NOTIFY_END_STREAMING = 0x10000001;
    private const int MFT_MESSAGE_NOTIFY_END_OF_STREAM = 0x10000002;
    private const int MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;

    private const int MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x00000100;
    private const int MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES = 0x00000200;

    private static readonly Guid CLSID_CMSH264EncoderMFT = new("6CA50344-051A-4DED-9779-A43305165E35");
    private static readonly Guid IID_IMFTransform = new("BF94C121-5B05-4E6F-8000-BA598961414D");

    private static readonly Guid MF_MT_MAJOR_TYPE = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
    private static readonly Guid MF_MT_SUBTYPE = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
    private static readonly Guid MF_MT_AVG_BITRATE = new("20332624-FB0D-4D9E-BD0D-CBF6786C102E");
    private static readonly Guid MF_MT_FRAME_RATE = new("C459A2E8-3D2C-4E44-B132-FEE5156C7BB0");
    private static readonly Guid MF_MT_FRAME_SIZE = new("1652C33D-D6B2-4012-B834-72030849A37D");
    private static readonly Guid MF_MT_INTERLACE_MODE = new("E2724BB8-E676-4806-B4B2-A8D6EFB44CCD");
    private static readonly Guid MF_MT_MPEG2_PROFILE = new("AD76A80B-2D5C-4E0B-B375-64E520137036");
    private static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("C6376A1E-8D0A-4027-BE45-6D9A0AD39BB6");
    private static readonly Guid MF_MT_MAX_KEYFRAME_SPACING = new("94BDCCD8-C018-40C5-A05D-8EB30EEF56D5");

    private static readonly Guid IID_ICodecAPI = new("901DB4C7-31CE-41A2-85DC-8FA0BF41B6DA");
    private static readonly Guid CODECAPI_AVEncCommonLowLatency = new("672F4E66-1C9C-45C9-A1A1-EE9BB2474F50");
    private static readonly Guid CODECAPI_AVLowLatencyMode = new("9C27891A-ED7A-40E1-A10B-EBD8F9AA163C");
    private static readonly Guid CODECAPI_AVEncMPVDefaultBPictureCount = new("8D390AAC-D3DE-495C-9D25-752523C37517");
    private static readonly Guid CODECAPI_AVEncMPVGOPSize = new("A146E06A-C445-4844-8603-15E66C167189");
    private static readonly Guid CODECAPI_AVEncVideoForceKeyFrame = new("762AA82F-6E13-45D6-976A-3BB07DC7A1B0");

    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");

    private IMFTransform? _transform;
    private MFT_OUTPUT_STREAM_INFO _outputInfo;
    private bool _mediaFoundationStarted;
    private bool _comInitialized;
    private int _comThreadId;
    private int _width;
    private int _height;
    private int _fps;
    private int _inputBufferLength;
    private byte[] _nv12Scratch = Array.Empty<byte>();
    private long _sampleTimeHns;
    private long _sampleDurationHns;
    private bool _disposed;
    private int _framesFed;
    private int _framesOut;
    private int _nullOutputs;
    private long _lastStatsUtc = Environment.TickCount64;
    private volatile bool _forceNextKeyframe;

    public static bool IsAvailable => OperatingSystem.IsWindows() && CanCreateEncoder();

    public void Start(int width, int height, int fps)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Native H264 encoding requires Windows Media Foundation.");
        if ((width & 1) != 0 || (height & 1) != 0)
            throw new InvalidOperationException($"Native H264 encoding requires even dimensions, got {width}x{height}.");

        Stop();

        _width = width;
        _height = height;
        _fps = Math.Max(1, fps);
        _inputBufferLength = Bgr24ToNv12Converter.GetNv12Length(width, height);
        _nv12Scratch = new byte[_inputBufferLength];
        _sampleTimeHns = 0;
        _sampleDurationHns = 10_000_000L / _fps;
        _framesFed = 0;
        _framesOut = 0;
        _nullOutputs = 0;

        _comInitialized = InitializeComForCurrentThread();
        _comThreadId = Environment.CurrentManagedThreadId;
        Check(MFStartup(MF_VERSION, 0), "MFStartup");
        _mediaFoundationStarted = true;

        var clsid = CLSID_CMSH264EncoderMFT;
        var iid = IID_IMFTransform;
        Check(CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out _transform), "CoCreateInstance(CMSH264EncoderMFT)");
        if (_transform == null)
            throw new InvalidOperationException("Windows Media Foundation H264 encoder activation returned null.");

        int gopFrames = AppSettings.H264GopFrames(_fps);
        int bitrate = EstimateBitrate(width, height, _fps);
        ConfigureOutputType(_transform, width, height, _fps, gopFrames, bitrate);
        ConfigureInputType(_transform, width, height, _fps);
        ConfigureLowLatency(_transform, gopFrames);
        Check(_transform.GetOutputStreamInfo(0, out _outputInfo), "IMFTransform.GetOutputStreamInfo");

        // The synchronous inbox encoder starts producing low-latency output after these notifications.
        _transform.ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);
        _transform.ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero);

        Console.WriteLine($"[H264] Native Media Foundation encoder started {_width}x{_height}@{_fps} gop={gopFrames} bitrate={bitrate}");
    }

    /// <summary>Requests an IDR access unit on the next fed frame.</summary>
    public void RequestKeyframe() => _forceNextKeyframe = true;

    /// <summary>Feeds one tightly-packed BGR24 frame and returns complete Annex-B access units, if any.</summary>
    public List<byte[]> EncodeFrame(byte[] bgr24)
    {
        if (_transform == null || bgr24.Length < _width * _height * 3)
            return [];

        EnsureNv12Scratch();
        Bgr24ToNv12Converter.ConvertBgr24(bgr24, _width, _height, _nv12Scratch);
        return EncodePreparedNv12();
    }

    /// <summary>Feeds one locked 32bpp ARGB/BGRA bitmap row and returns complete Annex-B access units, if any.</summary>
    public List<byte[]> EncodeBgra32Frame(IntPtr scan0, int stride)
    {
        if (_transform == null || scan0 == IntPtr.Zero || stride < _width * 4)
            return [];

        EnsureNv12Scratch();
        Bgr24ToNv12Converter.ConvertBgra32(scan0, _width, _height, stride, _nv12Scratch);
        return EncodePreparedNv12();
    }

    private List<byte[]> EncodePreparedNv12()
    {
        var encoded = new List<byte[]>(capacity: 2);

        try
        {
            DrainAvailableOutput(encoded);

            if (_forceNextKeyframe)
            {
                _forceNextKeyframe = false;
                TryForceKeyframe(_transform);
            }

            using var sample = CreateInputSample(_nv12Scratch);
            int hr = _transform.ProcessInput(0, sample.Value, 0);
            if (hr == MF_E_NOTACCEPTING)
            {
                DrainAvailableOutput(encoded);
                hr = _transform.ProcessInput(0, sample.Value, 0);
            }
            Check(hr, "IMFTransform.ProcessInput");
            _framesFed++;
            _sampleTimeHns += _sampleDurationHns;

            DrainAvailableOutput(encoded);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[H264] native encode failed: {ex.Message}");
            return encoded;
        }

        if (encoded.Count == 0)
            _nullOutputs++;
        else
            _framesOut += encoded.Count;

        MaybeLogStats();
        return encoded;
    }

    private void EnsureNv12Scratch()
    {
        if (_nv12Scratch.Length != _inputBufferLength)
            _nv12Scratch = new byte[_inputBufferLength];
    }

    public void Stop()
    {
        if (_transform != null)
        {
            try { _transform.ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, IntPtr.Zero); } catch { }
            try { _transform.ProcessMessage(MFT_MESSAGE_NOTIFY_END_STREAMING, IntPtr.Zero); } catch { }
            ReleaseComObject(_transform);
            _transform = null;
        }

        if (_mediaFoundationStarted)
        {
            try { MFShutdown(); } catch { }
            _mediaFoundationStarted = false;
        }

        if (_comInitialized && Environment.CurrentManagedThreadId == _comThreadId)
        {
            try { CoUninitialize(); } catch { }
            _comInitialized = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }

    private static bool CanCreateEncoder()
    {
        IMFTransform? transform = null;
        bool mediaFoundationStarted = false;
        bool comInitialized = false;
        int comThreadId = Environment.CurrentManagedThreadId;

        try
        {
            comInitialized = InitializeComForCurrentThread();
            int hr = MFStartup(MF_VERSION, 0);
            if (hr < 0)
                return false;
            mediaFoundationStarted = true;

            var clsid = CLSID_CMSH264EncoderMFT;
            var iid = IID_IMFTransform;
            hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out transform);
            return hr >= 0 && transform != null;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (transform != null)
                ReleaseComObject(transform);
            if (mediaFoundationStarted)
            {
                try { MFShutdown(); } catch { }
            }
            if (comInitialized && Environment.CurrentManagedThreadId == comThreadId)
            {
                try { CoUninitialize(); } catch { }
            }
        }
    }

    private static void ConfigureOutputType(IMFTransform transform, int width, int height, int fps, int gopFrames, int bitrate)
    {
        Check(MFCreateMediaType(out var outputType), "MFCreateMediaType(output)");
        using var mediaType = new ComReleaser<IMFMediaType>(outputType);

        Check(SetGuid(outputType, MF_MT_MAJOR_TYPE, MFMediaType_Video), "output.SetGUID(MF_MT_MAJOR_TYPE)");
        Check(SetGuid(outputType, MF_MT_SUBTYPE, MFVideoFormat_H264), "output.SetGUID(MF_MT_SUBTYPE)");
        Check(SetUInt32(outputType, MF_MT_AVG_BITRATE, bitrate), "output.SetUINT32(MF_MT_AVG_BITRATE)");
        Check(SetUInt64(outputType, MF_MT_FRAME_RATE, PackRatio((uint)fps, 1)), "output.SetUINT64(MF_MT_FRAME_RATE)");
        Check(SetUInt64(outputType, MF_MT_FRAME_SIZE, PackRatio((uint)width, (uint)height)), "output.SetUINT64(MF_MT_FRAME_SIZE)");
        Check(SetUInt32(outputType, MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive), "output.SetUINT32(MF_MT_INTERLACE_MODE)");
        Check(SetUInt32(outputType, MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_Base), "output.SetUINT32(MF_MT_MPEG2_PROFILE)");
        Check(SetUInt64(outputType, MF_MT_PIXEL_ASPECT_RATIO, PackRatio(1, 1)), "output.SetUINT64(MF_MT_PIXEL_ASPECT_RATIO)");
        int keyframeSpacingHns = (int)Math.Min(int.MaxValue, (long)gopFrames * 10_000_000L / Math.Max(1, fps));
        Check(SetUInt32(outputType, MF_MT_MAX_KEYFRAME_SPACING, keyframeSpacingHns), "output.SetUINT32(MF_MT_MAX_KEYFRAME_SPACING)");

        Check(transform.SetOutputType(0, outputType, 0), "IMFTransform.SetOutputType");
    }

    private static void ConfigureLowLatency(IMFTransform transform, int gopFrames)
    {
        var codecApi = GetCodecApi(transform);
        if (codecApi == null)
        {
            Console.WriteLine("[H264] ICodecAPI unavailable — using media-type GOP/bitrate only");
            return;
        }

        TrySetCodecApiBool(codecApi, CODECAPI_AVEncCommonLowLatency, true);
        TrySetCodecApiBool(codecApi, CODECAPI_AVLowLatencyMode, true);
        TrySetCodecApiUInt(codecApi, CODECAPI_AVEncMPVDefaultBPictureCount, 0);
        TrySetCodecApiUInt(codecApi, CODECAPI_AVEncMPVGOPSize, (uint)gopFrames);
    }

    private static void TryForceKeyframe(IMFTransform transform)
    {
        var codecApi = GetCodecApi(transform);
        if (codecApi == null)
            return;

        TrySetCodecApiUInt(codecApi, CODECAPI_AVEncVideoForceKeyFrame, 1);
    }

    private static ICodecAPI? GetCodecApi(IMFTransform transform)
    {
        IntPtr unk = Marshal.GetIUnknownForObject(transform);
        try
        {
            var iid = IID_ICodecAPI;
            int hr = Marshal.QueryInterface(unk, in iid, out IntPtr codecPtr);
            if (hr < 0 || codecPtr == IntPtr.Zero)
                return null;

            try
            {
                return (ICodecAPI)Marshal.GetObjectForIUnknown(codecPtr);
            }
            finally
            {
                Marshal.Release(codecPtr);
            }
        }
        finally
        {
            Marshal.Release(unk);
        }
    }

    private static void TrySetCodecApiBool(ICodecAPI codecApi, Guid api, bool value)
    {
        object boxed = value;
        int hr = codecApi.SetValue(ref api, ref boxed);
        if (hr < 0)
            Console.WriteLine($"[H264] ICodecAPI.SetValue({api}) bool={value} failed: 0x{hr:X8}");
    }

    private static void TrySetCodecApiUInt(ICodecAPI codecApi, Guid api, uint value)
    {
        object boxed = value;
        int hr = codecApi.SetValue(ref api, ref boxed);
        if (hr < 0)
            Console.WriteLine($"[H264] ICodecAPI.SetValue({api}) uint={value} failed: 0x{hr:X8}");
    }

    private static void ConfigureInputType(IMFTransform transform, int width, int height, int fps)
    {
        Check(MFCreateMediaType(out var inputType), "MFCreateMediaType(input)");
        using var mediaType = new ComReleaser<IMFMediaType>(inputType);

        Check(SetGuid(inputType, MF_MT_MAJOR_TYPE, MFMediaType_Video), "input.SetGUID(MF_MT_MAJOR_TYPE)");
        Check(SetGuid(inputType, MF_MT_SUBTYPE, MFVideoFormat_NV12), "input.SetGUID(MF_MT_SUBTYPE)");
        Check(SetUInt64(inputType, MF_MT_FRAME_SIZE, PackRatio((uint)width, (uint)height)), "input.SetUINT64(MF_MT_FRAME_SIZE)");
        Check(SetUInt64(inputType, MF_MT_FRAME_RATE, PackRatio((uint)fps, 1)), "input.SetUINT64(MF_MT_FRAME_RATE)");
        Check(SetUInt32(inputType, MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive), "input.SetUINT32(MF_MT_INTERLACE_MODE)");
        Check(SetUInt64(inputType, MF_MT_PIXEL_ASPECT_RATIO, PackRatio(1, 1)), "input.SetUINT64(MF_MT_PIXEL_ASPECT_RATIO)");

        Check(transform.SetInputType(0, inputType, 0), "IMFTransform.SetInputType");
    }

    private ComReleaser<IMFSample> CreateInputSample(byte[] nv12)
    {
        Check(MFCreateSample(out var sample), "MFCreateSample(input)");
        Check(MFCreateMemoryBuffer(nv12.Length, out var buffer), "MFCreateMemoryBuffer(input)");

        using var bufferReleaser = new ComReleaser<IMFMediaBuffer>(buffer);
        Check(buffer.Lock(out var ptr, out _, out _), "IMFMediaBuffer.Lock(input)");
        try
        {
            Marshal.Copy(nv12, 0, ptr, nv12.Length);
        }
        finally
        {
            buffer.Unlock();
        }

        Check(buffer.SetCurrentLength(nv12.Length), "IMFMediaBuffer.SetCurrentLength(input)");
        Check(sample.AddBuffer(buffer), "IMFSample.AddBuffer(input)");
        Check(sample.SetSampleTime(_sampleTimeHns), "IMFSample.SetSampleTime");
        Check(sample.SetSampleDuration(_sampleDurationHns), "IMFSample.SetSampleDuration");

        return new ComReleaser<IMFSample>(sample);
    }

    private IMFSample? CreateOutputSample()
    {
        if ((_outputInfo.dwFlags & (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0)
            return null;

        int outputBufferSize = Math.Max(_outputInfo.cbSize, _inputBufferLength + 1024);
        Check(MFCreateSample(out var sample), "MFCreateSample(output)");
        Check(MFCreateMemoryBuffer(outputBufferSize, out var buffer), "MFCreateMemoryBuffer(output)");

        try
        {
            Check(sample.AddBuffer(buffer), "IMFSample.AddBuffer(output)");
        }
        finally
        {
            ReleaseComObject(buffer);
        }

        return sample;
    }

    private void DrainAvailableOutput(List<byte[]> encoded)
    {
        if (_transform == null)
            return;

        for (int i = 0; i < 8; i++)
        {
            IMFSample? outputSample = CreateOutputSample();
            IntPtr outputSamplePtr = outputSample is null ? IntPtr.Zero : Marshal.GetIUnknownForObject(outputSample);
            IMFSample? resolvedSample = null;
            bool releaseResolvedSample = false;
            IntPtr outputBuffer = IntPtr.Zero;
            var output = new MFT_OUTPUT_DATA_BUFFER
            {
                dwStreamID = 0,
                pSample = outputSamplePtr,
                dwStatus = 0,
                pEvents = IntPtr.Zero,
            };

            int hr = 0;
            try
            {
                outputBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<MFT_OUTPUT_DATA_BUFFER>());
                Marshal.StructureToPtr(output, outputBuffer, false);

                hr = _transform.ProcessOutput(0, 1, outputBuffer, out _);
                output = Marshal.PtrToStructure<MFT_OUTPUT_DATA_BUFFER>(outputBuffer);

                if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT)
                    return;
                Check(hr, "IMFTransform.ProcessOutput");

                var sample = outputSample;
                if (sample is null && output.pSample != IntPtr.Zero)
                {
                    resolvedSample = (IMFSample)Marshal.GetObjectForIUnknown(output.pSample);
                    releaseResolvedSample = true;
                    sample = resolvedSample;
                }

                if (sample is not null)
                {
                    byte[] bytes = CopySampleBytes(sample);
                    if (bytes.Length > 0)
                        encoded.Add(bytes);
                }
            }
            finally
            {
                if (output.pEvents != IntPtr.Zero)
                    Marshal.Release(output.pEvents);
                if (releaseResolvedSample && resolvedSample is not null)
                    ReleaseComObject(resolvedSample);
                if (output.pSample != IntPtr.Zero)
                    Marshal.Release(output.pSample);
                if (outputSamplePtr != IntPtr.Zero && output.pSample != outputSamplePtr)
                    Marshal.Release(outputSamplePtr);
                if (outputSample is not null)
                    ReleaseComObject(outputSample);
                if (outputBuffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(outputBuffer);
            }
        }
    }

    private static byte[] CopySampleBytes(IMFSample sample)
    {
        Check(sample.ConvertToContiguousBuffer(out var buffer), "IMFSample.ConvertToContiguousBuffer");
        using var bufferReleaser = new ComReleaser<IMFMediaBuffer>(buffer);

        Check(buffer.Lock(out var ptr, out _, out int currentLength), "IMFMediaBuffer.Lock(output)");
        try
        {
            if (currentLength <= 0)
                return [];

            var bytes = new byte[currentLength];
            Marshal.Copy(ptr, bytes, 0, currentLength);
            return bytes;
        }
        finally
        {
            buffer.Unlock();
        }
    }

    private void MaybeLogStats()
    {
        long now = Environment.TickCount64;
        if (now - _lastStatsUtc < 5000)
            return;
        _lastStatsUtc = now;
        Console.WriteLine($"[H264] native fed={_framesFed} out={_framesOut} null={_nullOutputs}");
    }

    public static int EstimateDefaultBitrateBps(int width, int height, int fps)
    {
        // ~0.10 bits per pixel per frame for desktop screen content.
        long bitsPerSecond = (long)(width * height * Math.Max(1, fps) * 0.10);
        return (int)Math.Clamp(bitsPerSecond, AppSettings.MinH264Bitrate, AppSettings.MaxH264Bitrate);
    }

    private static int EstimateBitrate(int width, int height, int fps)
    {
        if (AppSettings.H264BitrateOverride is int overrideBps)
            return overrideBps;

        return EstimateDefaultBitrateBps(width, height, fps);
    }

    private static ulong PackRatio(uint high, uint low) => ((ulong)high << 32) | low;

    private static int SetGuid(IMFAttributes attributes, Guid key, Guid value) => attributes.SetGUID(ref key, ref value);

    private static int SetUInt32(IMFAttributes attributes, Guid key, int value) => attributes.SetUINT32(ref key, value);

    private static int SetUInt64(IMFAttributes attributes, Guid key, ulong value) => attributes.SetUINT64(ref key, value);

    private static bool InitializeComForCurrentThread()
    {
        int hr = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
        if (hr == S_OK || hr == S_FALSE)
            return true;
        if (hr == RPC_E_CHANGED_MODE)
            return false;

        Check(hr, "CoInitializeEx");
        return false;
    }

    private static void Check(int hr, string operation)
    {
        if (hr >= 0)
            return;

        Console.WriteLine($"[H264] {operation} failed: 0x{hr:X8}");
        Marshal.ThrowExceptionForHR(hr);
    }

    private static void ReleaseComObject(object comObject)
    {
        try
        {
            Marshal.FinalReleaseComObject(comObject);
        }
        catch
        {
            // Best-effort cleanup during encoder shutdown.
        }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr pvReserved, int dwCoInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(
        ref Guid rclsid,
        IntPtr pUnkOuter,
        int dwClsContext,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IMFTransform? ppv);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(int version, int flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMediaType([MarshalAs(UnmanagedType.Interface)] out IMFMediaType ppMFType);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateSample([MarshalAs(UnmanagedType.Interface)] out IMFSample ppIMFSample);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMemoryBuffer(int cbMaxLength, [MarshalAs(UnmanagedType.Interface)] out IMFMediaBuffer ppBuffer);

    private readonly ref struct ComReleaser<T>(T value) where T : class
    {
        public T Value { get; } = value;

        public void Dispose() => ReleaseComObject(Value);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MFT_OUTPUT_STREAM_INFO
    {
        public int dwFlags;
        public int cbSize;
        public int cbAlignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MFT_INPUT_STREAM_INFO
    {
        public long hnsMaxLatency;
        public int dwFlags;
        public int cbSize;
        public int cbMaxLookahead;
        public int cbAlignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MFT_OUTPUT_DATA_BUFFER
    {
        public int dwStreamID;
        public IntPtr pSample;
        public int dwStatus;
        public IntPtr pEvents;
    }

    [ComImport]
    [Guid("BF94C121-5B05-4E6F-8000-BA598961414D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFTransform
    {
        [PreserveSig] int GetStreamLimits(out int pdwInputMinimum, out int pdwInputMaximum, out int pdwOutputMinimum, out int pdwOutputMaximum);
        [PreserveSig] int GetStreamCount(out int pcInputStreams, out int pcOutputStreams);
        [PreserveSig] int GetStreamIDs(int dwInputIDArraySize, [Out] int[]? pdwInputIDs, int dwOutputIDArraySize, [Out] int[]? pdwOutputIDs);
        [PreserveSig] int GetInputStreamInfo(int dwInputStreamID, out MFT_INPUT_STREAM_INFO pStreamInfo);
        [PreserveSig] int GetOutputStreamInfo(int dwOutputStreamID, out MFT_OUTPUT_STREAM_INFO pStreamInfo);
        [PreserveSig] int GetAttributes([MarshalAs(UnmanagedType.Interface)] out IMFAttributes pAttributes);
        [PreserveSig] int GetInputStreamAttributes(int dwInputStreamID, [MarshalAs(UnmanagedType.Interface)] out IMFAttributes pAttributes);
        [PreserveSig] int GetOutputStreamAttributes(int dwOutputStreamID, [MarshalAs(UnmanagedType.Interface)] out IMFAttributes pAttributes);
        [PreserveSig] int DeleteInputStream(int dwStreamID);
        [PreserveSig] int AddInputStreams(int cStreams, [In] int[] adwStreamIDs);
        [PreserveSig] int GetInputAvailableType(int dwInputStreamID, int dwTypeIndex, [MarshalAs(UnmanagedType.Interface)] out IMFMediaType ppType);
        [PreserveSig] int GetOutputAvailableType(int dwOutputStreamID, int dwTypeIndex, [MarshalAs(UnmanagedType.Interface)] out IMFMediaType ppType);
        [PreserveSig] int SetInputType(int dwInputStreamID, [MarshalAs(UnmanagedType.Interface)] IMFMediaType pType, int dwFlags);
        [PreserveSig] int SetOutputType(int dwOutputStreamID, [MarshalAs(UnmanagedType.Interface)] IMFMediaType pType, int dwFlags);
        [PreserveSig] int GetInputCurrentType(int dwInputStreamID, [MarshalAs(UnmanagedType.Interface)] out IMFMediaType ppType);
        [PreserveSig] int GetOutputCurrentType(int dwOutputStreamID, [MarshalAs(UnmanagedType.Interface)] out IMFMediaType ppType);
        [PreserveSig] int GetInputStatus(int dwInputStreamID, out int pdwFlags);
        [PreserveSig] int GetOutputStatus(out int pdwFlags);
        [PreserveSig] int SetOutputBounds(long hnsLowerBound, long hnsUpperBound);
        [PreserveSig] int ProcessEvent(int dwInputStreamID, IntPtr pEvent);
        [PreserveSig] int ProcessMessage(int eMessage, IntPtr ulParam);
        [PreserveSig] int ProcessInput(int dwInputStreamID, [MarshalAs(UnmanagedType.Interface)] IMFSample pSample, int dwFlags);
        [PreserveSig] int ProcessOutput(int dwFlags, int cOutputBufferCount, IntPtr pOutputSamples, out int pdwStatus);
    }

    [ComImport]
    [Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFAttributes
    {
        [PreserveSig] int GetItem(ref Guid guidKey, IntPtr pValue);
        [PreserveSig] int GetItemType(ref Guid guidKey, out int pType);
        [PreserveSig] int CompareItem(ref Guid guidKey, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
        [PreserveSig] int Compare([MarshalAs(UnmanagedType.Interface)] IMFAttributes pTheirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
        [PreserveSig] int GetUINT32(ref Guid guidKey, out int punValue);
        [PreserveSig] int GetUINT64(ref Guid guidKey, out ulong punValue);
        [PreserveSig] int GetDouble(ref Guid guidKey, out double pfValue);
        [PreserveSig] int GetGUID(ref Guid guidKey, out Guid pguidValue);
        [PreserveSig] int GetStringLength(ref Guid guidKey, out int pcchLength);
        [PreserveSig] int GetString(ref Guid guidKey, IntPtr pwszValue, int cchBufSize, out int pcchLength);
        [PreserveSig] int GetAllocatedString(ref Guid guidKey, out IntPtr ppwszValue, out int pcchLength);
        [PreserveSig] int GetBlobSize(ref Guid guidKey, out int pcbBlobSize);
        [PreserveSig] int GetBlob(ref Guid guidKey, IntPtr pBuf, int cbBufSize, out int pcbBlobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out int pcbSize);
        [PreserveSig] int GetUnknown(ref Guid guidKey, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        [PreserveSig] int SetItem(ref Guid guidKey, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid guidKey);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid guidKey, int unValue);
        [PreserveSig] int SetUINT64(ref Guid guidKey, ulong unValue);
        [PreserveSig] int SetDouble(ref Guid guidKey, double fValue);
        [PreserveSig] int SetGUID(ref Guid guidKey, ref Guid guidValue);
        [PreserveSig] int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        [PreserveSig] int SetBlob(ref Guid guidKey, IntPtr pBuf, int cbBufSize);
        [PreserveSig] int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out int pcItems);
        [PreserveSig] int GetItemByIndex(int unIndex, out Guid pguidKey, IntPtr pValue);
        [PreserveSig] int CopyAllItems([MarshalAs(UnmanagedType.Interface)] IMFAttributes pDest);
    }

    [ComImport]
    [Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaType : IMFAttributes
    {
        [PreserveSig] int GetMajorType(out Guid pguidMajorType);
        [PreserveSig] int IsCompressedFormat([MarshalAs(UnmanagedType.Bool)] out bool pfCompressed);
        [PreserveSig] int IsEqual([MarshalAs(UnmanagedType.Interface)] IMFMediaType pIMediaType, out int pdwFlags);
        [PreserveSig] int GetRepresentation(Guid guidRepresentation, out IntPtr ppvRepresentation);
        [PreserveSig] int FreeRepresentation(Guid guidRepresentation, IntPtr pvRepresentation);
    }

    [ComImport]
    [Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSample
    {
        // Flatten IMFAttributes into this declaration. COM interop inheritance does not
        // preserve the native vtable layout for IMFSample-specific methods.
        [PreserveSig] int GetItem(ref Guid guidKey, IntPtr pValue);
        [PreserveSig] int GetItemType(ref Guid guidKey, out int pType);
        [PreserveSig] int CompareItem(ref Guid guidKey, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
        [PreserveSig] int Compare([MarshalAs(UnmanagedType.Interface)] IMFAttributes pTheirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
        [PreserveSig] int GetUINT32(ref Guid guidKey, out int punValue);
        [PreserveSig] int GetUINT64(ref Guid guidKey, out ulong punValue);
        [PreserveSig] int GetDouble(ref Guid guidKey, out double pfValue);
        [PreserveSig] int GetGUID(ref Guid guidKey, out Guid pguidValue);
        [PreserveSig] int GetStringLength(ref Guid guidKey, out int pcchLength);
        [PreserveSig] int GetString(ref Guid guidKey, IntPtr pwszValue, int cchBufSize, out int pcchLength);
        [PreserveSig] int GetAllocatedString(ref Guid guidKey, out IntPtr ppwszValue, out int pcchLength);
        [PreserveSig] int GetBlobSize(ref Guid guidKey, out int pcbBlobSize);
        [PreserveSig] int GetBlob(ref Guid guidKey, IntPtr pBuf, int cbBufSize, out int pcbBlobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out int pcbSize);
        [PreserveSig] int GetUnknown(ref Guid guidKey, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        [PreserveSig] int SetItem(ref Guid guidKey, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid guidKey);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid guidKey, int unValue);
        [PreserveSig] int SetUINT64(ref Guid guidKey, ulong unValue);
        [PreserveSig] int SetDouble(ref Guid guidKey, double fValue);
        [PreserveSig] int SetGUID(ref Guid guidKey, ref Guid guidValue);
        [PreserveSig] int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        [PreserveSig] int SetBlob(ref Guid guidKey, IntPtr pBuf, int cbBufSize);
        [PreserveSig] int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out int pcItems);
        [PreserveSig] int GetItemByIndex(int unIndex, out Guid pguidKey, IntPtr pValue);
        [PreserveSig] int CopyAllItems([MarshalAs(UnmanagedType.Interface)] IMFAttributes pDest);

        [PreserveSig] int GetSampleFlags(out int pdwSampleFlags);
        [PreserveSig] int SetSampleFlags(int dwSampleFlags);
        [PreserveSig] int GetSampleTime(out long phnsSampleTime);
        [PreserveSig] int SetSampleTime(long hnsSampleTime);
        [PreserveSig] int GetSampleDuration(out long phnsSampleDuration);
        [PreserveSig] int SetSampleDuration(long hnsSampleDuration);
        [PreserveSig] int GetBufferCount(out int pdwBufferCount);
        [PreserveSig] int GetBufferByIndex(int dwIndex, [MarshalAs(UnmanagedType.Interface)] out IMFMediaBuffer ppBuffer);
        [PreserveSig] int ConvertToContiguousBuffer([MarshalAs(UnmanagedType.Interface)] out IMFMediaBuffer ppBuffer);
        [PreserveSig] int AddBuffer([MarshalAs(UnmanagedType.Interface)] IMFMediaBuffer pBuffer);
        [PreserveSig] int RemoveBufferByIndex(int dwIndex);
        [PreserveSig] int RemoveAllBuffers();
        [PreserveSig] int GetTotalLength(out int pcbTotalLength);
        [PreserveSig] int CopyToBuffer([MarshalAs(UnmanagedType.Interface)] IMFMediaBuffer pBuffer);
    }

    [ComImport]
    [Guid("901DB4C7-31CE-41A2-85DC-8FA0BF41B6DA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICodecAPI
    {
        [PreserveSig] int IsSupported(ref Guid Api);
        [PreserveSig] int IsModifiable(ref Guid Api);
        [PreserveSig] int GetParameterRange(ref Guid Api, [MarshalAs(UnmanagedType.Struct)] out object ValueMin, [MarshalAs(UnmanagedType.Struct)] out object ValueMax, [MarshalAs(UnmanagedType.Struct)] out object SteppingDelta);
        [PreserveSig] int GetParameterValues(ref Guid Api, out IntPtr ip, out int valuesCount);
        [PreserveSig] int GetDefaultValue(ref Guid Api, [MarshalAs(UnmanagedType.Struct)] out object Value);
        [PreserveSig] int GetValue(ref Guid Api, [MarshalAs(UnmanagedType.Struct)] out object Value);
        [PreserveSig] int SetValue(ref Guid Api, ref object Value);
    }

    [ComImport]
    [Guid("045FA593-8799-42B8-BC8D-8968C6453507")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr ppbBuffer, out int pcbMaxLength, out int pcbCurrentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out int pcbCurrentLength);
        [PreserveSig] int SetCurrentLength(int cbCurrentLength);
        [PreserveSig] int GetMaxLength(out int pcbMaxLength);
    }
}
