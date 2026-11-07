using System.Runtime.InteropServices;

/// <summary>
/// Replays one finger as a Windows touch contact (<c>InjectTouchInput</c>).
/// A contact that stays down is a press-and-hold, which Windows turns into a right-click
/// when that gesture is enabled (the default). Hover is never injected.
/// While the contact is down, an update frame is repeated so the gesture is not cancelled.
/// </summary>
static class TouchInput
{
    const uint TouchFeedbackDefault = 1;
    const uint PtTouch = 2;
    const uint FlagInRange = 0x00000002;
    const uint FlagInContact = 0x00000004;
    const uint FlagDown = 0x00010000;
    const uint FlagUpdate = 0x00020000;
    const uint FlagUp = 0x00040000;
    const uint TouchMaskContactArea = 0x0001;
    const uint TouchMaskPressure = 0x0004;
    const int PointerId = 1;

    static readonly object Gate = new();
    static System.Threading.Timer? _hold;
    static bool _ready;
    static bool _initFailed;
    static bool _down;
    static int _x, _y;
    static uint _frame;

    public static void Down(int x, int y) => Send(x, y, contactDown: true);
    public static void Move(int x, int y)
    {
        lock (Gate)
        {
            if (!_down) return;
        }
        Send(x, y, contactDown: false);
    }

    public static void Up(int x, int y)
    {
        lock (Gate)
        {
            if (!_down) return;
            _x = x;
            _y = y;
            if (!Inject(FlagInRange | FlagUp))
                return;
            _down = false;
            StopHold();
        }
    }

    /// <summary>Lifts a stuck contact when the input socket closes.</summary>
    public static void EndContact()
    {
        lock (Gate)
        {
            if (!_down) return;
            Inject(FlagInRange | FlagUp);
            _down = false;
            StopHold();
        }
    }

    static void Send(int x, int y, bool contactDown)
    {
        lock (Gate)
        {
            _x = x;
            _y = y;
            if (!_down)
            {
                if (!contactDown) return;
                if (!Inject(FlagInRange | FlagInContact | FlagDown))
                    return;
                _down = true;
                _hold ??= new System.Threading.Timer(_ => HoldTick(), null, 100, 100);
            }
            else
            {
                Inject(FlagInRange | FlagInContact | FlagUpdate);
            }
        }
    }

    static void HoldTick()
    {
        lock (Gate)
        {
            if (_down)
                Inject(FlagInRange | FlagInContact | FlagUpdate);
        }
    }

    static void StopHold()
    {
        _hold?.Dispose();
        _hold = null;
    }

    static bool Inject(uint flags)
    {
        if (!EnsureInit()) return false;
        var contact = new POINTER_TOUCH_INFO
        {
            pointerInfo = new POINTER_INFO
            {
                pointerType = PtTouch,
                pointerId = PointerId,
                frameId = ++_frame,
                pointerFlags = flags,
                ptPixelLocation = new Win32.POINT(_x, _y),
                ptPixelLocationRaw = new Win32.POINT(_x, _y)
            },
            touchMask = TouchMaskContactArea | TouchMaskPressure,
            rcContact = new RECT { left = _x - 2, top = _y - 2, right = _x + 2, bottom = _y + 2 },
            pressure = 512
        };
        if (InjectTouchInput(1, new[] { contact }))
            return true;
        Log.Warn($"Touch inject failed ({flags:X}) at {_x},{_y}: {Marshal.GetLastWin32Error()}");
        return false;
    }

    static bool EnsureInit()
    {
        if (_ready) return true;
        if (_initFailed) return false;
        if (InitializeTouchInjection(1, TouchFeedbackDefault))
        {
            _ready = true;
            Log.Info("Touch input injection ready.");
            return true;
        }
        _initFailed = true;
        Log.Warn($"InitializeTouchInjection failed: {Marshal.GetLastWin32Error()}");
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool InitializeTouchInjection(uint maxCount, uint dwMode);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool InjectTouchInput(uint count, POINTER_TOUCH_INFO[] contacts);

    [StructLayout(LayoutKind.Sequential)]
    struct POINTER_INFO
    {
        public uint pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public Win32.POINT ptPixelLocation;
        public Win32.POINT ptHimetricLocation;
        public Win32.POINT ptPixelLocationRaw;
        public Win32.POINT ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int InputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint touchFlags;
        public uint touchMask;
        public RECT rcContact;
        public RECT rcContactRaw;
        public uint orientation;
        public uint pressure;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int left, top, right, bottom;
    }
}
