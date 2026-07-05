using System.Runtime.CompilerServices;

namespace Streaming;

/// <summary>
/// BT.601 BGR/BGRA to NV12 conversion for the native H264 encoder input path.
/// </summary>
internal static class Bgr24ToNv12Converter
{
    private const int YR = 66;
    private const int YG = 129;
    private const int YB = 25;
    private const int UR = -38;
    private const int UG = -74;
    private const int UB = 112;
    private const int VR = 112;
    private const int VG = -94;
    private const int VB = -18;

    public static int GetNv12Length(int width, int height) => width * height * 3 / 2;

    public static void ConvertBgr24(ReadOnlySpan<byte> bgr24, int width, int height, Span<byte> nv12)
    {
        if (bgr24.Length < width * height * 3)
            throw new ArgumentException("BGR24 buffer is too small.", nameof(bgr24));
        if (nv12.Length < GetNv12Length(width, height))
            throw new ArgumentException("NV12 buffer is too small.", nameof(nv12));

        int yPlaneSize = width * height;
        int sourceStride = width * 3;
        WriteYPlaneBgr24(bgr24, width, height, sourceStride, nv12);
        WriteUvPlaneBgr24(bgr24, width, height, sourceStride, nv12, yPlaneSize);
    }

    public static void ConvertBgra32(IntPtr scan0, int width, int height, int stride, Span<byte> nv12)
    {
        if (nv12.Length < GetNv12Length(width, height))
            throw new ArgumentException("NV12 buffer is too small.", nameof(nv12));

        unsafe
        {
            var bgra = new ReadOnlySpan<byte>((void*)scan0, stride * height);
            int yPlaneSize = width * height;
            WriteYPlaneBgra32(bgra, width, height, stride, nv12);
            WriteUvPlaneBgra32(bgra, width, height, stride, nv12, yPlaneSize);
        }
    }

    private static void WriteYPlaneBgr24(ReadOnlySpan<byte> bgr24, int width, int height, int sourceStride, Span<byte> nv12)
    {
        for (int y = 0; y < height; y++)
        {
            int sourceRow = y * sourceStride;
            int yRow = y * width;
            int x = 0;
            for (; x <= width - 4; x += 4)
            {
                int source = sourceRow + x * 3;
                nv12[yRow + x] = YFromBgr(bgr24[source], bgr24[source + 1], bgr24[source + 2]);
                source += 3;
                nv12[yRow + x + 1] = YFromBgr(bgr24[source], bgr24[source + 1], bgr24[source + 2]);
                source += 3;
                nv12[yRow + x + 2] = YFromBgr(bgr24[source], bgr24[source + 1], bgr24[source + 2]);
                source += 3;
                nv12[yRow + x + 3] = YFromBgr(bgr24[source], bgr24[source + 1], bgr24[source + 2]);
            }
            for (; x < width; x++)
            {
                int source = sourceRow + x * 3;
                nv12[yRow + x] = YFromBgr(bgr24[source], bgr24[source + 1], bgr24[source + 2]);
            }
        }
    }

    private static void WriteYPlaneBgra32(ReadOnlySpan<byte> bgra, int width, int height, int stride, Span<byte> nv12)
    {
        for (int y = 0; y < height; y++)
        {
            int sourceRow = y * stride;
            int yRow = y * width;
            int x = 0;
            for (; x <= width - 4; x += 4)
            {
                int source = sourceRow + x * 4;
                nv12[yRow + x] = YFromBgr(bgra[source], bgra[source + 1], bgra[source + 2]);
                source += 4;
                nv12[yRow + x + 1] = YFromBgr(bgra[source], bgra[source + 1], bgra[source + 2]);
                source += 4;
                nv12[yRow + x + 2] = YFromBgr(bgra[source], bgra[source + 1], bgra[source + 2]);
                source += 4;
                nv12[yRow + x + 3] = YFromBgr(bgra[source], bgra[source + 1], bgra[source + 2]);
            }
            for (; x < width; x++)
            {
                int source = sourceRow + x * 4;
                nv12[yRow + x] = YFromBgr(bgra[source], bgra[source + 1], bgra[source + 2]);
            }
        }
    }

    private static void WriteUvPlaneBgr24(ReadOnlySpan<byte> bgr24, int width, int height, int sourceStride, Span<byte> nv12, int yPlaneSize)
    {
        for (int y = 0; y < height; y += 2)
        {
            int row0 = y * sourceStride;
            int row1 = (y + 1) * sourceStride;
            int uvRow = yPlaneSize + (y / 2) * width;
            for (int x = 0; x < width; x += 2)
            {
                int s00 = row0 + x * 3;
                int s01 = s00 + 3;
                int s10 = row1 + x * 3;
                int s11 = s10 + 3;

                UvFromBgr(bgr24[s00], bgr24[s00 + 1], bgr24[s00 + 2], out int u00, out int v00);
                UvFromBgr(bgr24[s01], bgr24[s01 + 1], bgr24[s01 + 2], out int u01, out int v01);
                UvFromBgr(bgr24[s10], bgr24[s10 + 1], bgr24[s10 + 2], out int u10, out int v10);
                UvFromBgr(bgr24[s11], bgr24[s11 + 1], bgr24[s11 + 2], out int u11, out int v11);

                nv12[uvRow + x] = ClampToByte((u00 + u01 + u10 + u11 + 2) / 4);
                nv12[uvRow + x + 1] = ClampToByte((v00 + v01 + v10 + v11 + 2) / 4);
            }
        }
    }

    private static void WriteUvPlaneBgra32(ReadOnlySpan<byte> bgra, int width, int height, int stride, Span<byte> nv12, int yPlaneSize)
    {
        for (int y = 0; y < height; y += 2)
        {
            int row0 = y * stride;
            int row1 = (y + 1) * stride;
            int uvRow = yPlaneSize + (y / 2) * width;
            for (int x = 0; x < width; x += 2)
            {
                int s00 = row0 + x * 4;
                int s01 = s00 + 4;
                int s10 = row1 + x * 4;
                int s11 = s10 + 4;

                UvFromBgr(bgra[s00], bgra[s00 + 1], bgra[s00 + 2], out int u00, out int v00);
                UvFromBgr(bgra[s01], bgra[s01 + 1], bgra[s01 + 2], out int u01, out int v01);
                UvFromBgr(bgra[s10], bgra[s10 + 1], bgra[s10 + 2], out int u10, out int v10);
                UvFromBgr(bgra[s11], bgra[s11 + 1], bgra[s11 + 2], out int u11, out int v11);

                nv12[uvRow + x] = ClampToByte((u00 + u01 + u10 + u11 + 2) / 4);
                nv12[uvRow + x + 1] = ClampToByte((v00 + v01 + v10 + v11 + 2) / 4);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte YFromBgr(int b, int g, int r) =>
        ClampToByte(((YR * r + YG * g + YB * b + 128) >> 8) + 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UvFromBgr(int b, int g, int r, out int u, out int v)
    {
        u = ((UR * r + UG * g + UB * b + 128) >> 8) + 128;
        v = ((VR * r + VG * g + VB * b + 128) >> 8) + 128;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ClampToByte(int value) => value < 0 ? (byte)0 : value > 255 ? (byte)255 : (byte)value;
}