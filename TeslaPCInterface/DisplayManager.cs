using System.Runtime.InteropServices;

/// <summary>
/// Reads and changes the primary monitor's display mode (resolution) via the Win32
/// EnumDisplaySettings / ChangeDisplaySettings APIs. Used by the web "Fit my screen" button to set
/// the desktop to the supported resolution whose aspect ratio best matches the browser's viewport.
/// </summary>
public static class DisplayManager
{
    public readonly record struct Mode(int Width, int Height);

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int CDS_UPDATEREGISTRY = 0x00000001;
    private const int DISP_CHANGE_SUCCESSFUL = 0;
    private const int DM_BITSPERPEL = 0x00040000;
    private const int DM_PELSWIDTH = 0x00080000;
    private const int DM_PELSHEIGHT = 0x00100000;

    /// <summary>The current primary-display resolution.</summary>
    public static Mode Current()
    {
        var dm = NewDevMode();
        if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm))
            return new Mode(dm.dmPelsWidth, dm.dmPelsHeight);
        return new Mode(0, 0);
    }

    /// <summary>All distinct 32-bpp resolutions the primary display supports, largest first.</summary>
    public static List<Mode> Modes()
    {
        var set = new HashSet<(int, int)>();
        var list = new List<Mode>();
        var dm = NewDevMode();
        for (int i = 0; EnumDisplaySettings(null, i, ref dm); i++)
        {
            if (dm.dmBitsPerPel >= 32 && set.Add((dm.dmPelsWidth, dm.dmPelsHeight)))
                list.Add(new Mode(dm.dmPelsWidth, dm.dmPelsHeight));
            dm = NewDevMode();
        }
        list.Sort((a, b) => (b.Width * b.Height).CompareTo(a.Width * a.Height));
        return list;
    }

    /// <summary>
    /// Picks the supported resolution whose aspect ratio is closest to <paramref name="targetAspect"/>
    /// (width/height), preferring larger area, restricted to heights in [480, maxHeight]. Null if none.
    /// </summary>
    public static Mode? BestForAspect(double targetAspect, int maxHeight)
    {
        if (targetAspect <= 0) return null;
        Mode? best = null;
        double bestErr = double.MaxValue;
        foreach (var m in Modes())
        {
            if (m.Height < 480 || m.Height > maxHeight) continue;
            double err = Math.Abs((double)m.Width / m.Height - targetAspect);
            // Prefer the closest aspect; on a near-tie (within 1%), prefer the larger area.
            if (err < bestErr - 0.01 ||
                (Math.Abs(err - bestErr) <= 0.01 && best.HasValue && m.Width * m.Height > best.Value.Width * best.Value.Height))
            {
                best = m;
                bestErr = Math.Min(bestErr, err);
            }
        }
        return best;
    }

    /// <summary>Applies a supported resolution to the primary display. Returns true on success.</summary>
    public static bool TrySet(int width, int height)
    {
        // Find the matching enumerated mode (keeps the driver's preferred refresh/bit depth).
        var dm = NewDevMode();
        DEVMODE? match = null;
        for (int i = 0; EnumDisplaySettings(null, i, ref dm); i++)
        {
            if (dm.dmPelsWidth == width && dm.dmPelsHeight == height && dm.dmBitsPerPel >= 32)
            {
                if (match == null || dm.dmDisplayFrequency > match.Value.dmDisplayFrequency)
                    match = dm;
            }
            dm = NewDevMode();
        }

        DEVMODE target;
        if (match != null)
        {
            target = match.Value;
        }
        else
        {
            // Fall back to the current mode with the resolution fields overridden.
            target = NewDevMode();
            if (!EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref target))
                return false;
            target.dmPelsWidth = width;
            target.dmPelsHeight = height;
        }
        target.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL;

        int result = ChangeDisplaySettings(ref target, CDS_UPDATEREGISTRY);
        if (result != DISP_CHANGE_SUCCESSFUL)
            Console.WriteLine($"[Display] ChangeDisplaySettings to {width}x{height} returned {result}.");
        return result == DISP_CHANGE_SUCCESSFUL;
    }

    private static DEVMODE NewDevMode()
    {
        var dm = new DEVMODE { dmDeviceName = new string('\0', 32), dmFormName = new string('\0', 32) };
        dm.dmSize = (short)Marshal.SizeOf<DEVMODE>();
        return dm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern int ChangeDisplaySettings(ref DEVMODE lpDevMode, int dwFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }
}
