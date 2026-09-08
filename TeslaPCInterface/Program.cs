using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PrimaryProcess
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // Load config/secrets from .env (app folder, then %ProgramData%\TeslaPC\.env which wins
            // and survives rebuilds). Lines are KEY=VALUE; existing real env vars take precedence.
            LoadDotEnv();

            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(args));
        }

        static void LoadDotEnv()
        {
            var paths = new[]
            {
                Path.Combine(AppContext.BaseDirectory, ".env"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TeslaPC", ".env")
            };
            foreach (var path in paths)
            {
                if (!File.Exists(path)) continue;
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = line.Substring(0, eq).Trim();
                    var val = line.Substring(eq + 1).Trim().Trim('"');
                    if (key.Length > 0 && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                        Environment.SetEnvironmentVariable(key, val);
                }
            }
        }
    }
}

class InputData
{
    // Mouse: {"Type":"move|down|up|rightclick","X":820,"Y":45,"DisplaySize":{"width":1280,"height":720}}
    // Wheel:  {"Type":"wheel","Delta":-120,"X":820,"Y":45,"DisplaySize":{"width":1280,"height":720}}
    public string Type { get; set; }

    public int X { get; set; }
    public int Y { get; set; }

    // For wheel events (positive/negative; server may normalize for Windows convention)
    public int Delta { get; set; }

    public DisplaySize? DisplaySize { get; set; }

    public InputData GetAdjusted()
    {
        if (DisplaySize == null)
        {
            return this;
        }
        var x = (int)((double)X / DisplaySize.width * System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width);
        var y = (int)((double)Y / DisplaySize.height * System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height);
        return new InputData { X = x, Y = y, Type = Type, Delta = this.Delta };
    }
}

class KeyData
{
    //{"Type":"click","X":820,"Y":45,"DisplaySize":{"width":1280,"height":720}}
    public string Type { get; set; }

    public string Key { get; set; }

    public string KeyCode { get; set; }


}
class DisplaySize
{
    public int width { get; set; }
    public int height { get; set; }
}
public class Win32
{

    public const int MOUSEEVENTF_LEFTDOWN = 0x02;
    public const int MOUSEEVENTF_LEFTUP = 0x04;
    public const int MOUSEEVENTF_RIGHTDOWN = 0x08;
    public const int MOUSEEVENTF_RIGHTUP = 0x10;
    public const int MOUSEEVENTF_WHEEL = 0x0800;
    public const int MOUSEEVENTF_HWHEEL = 0x1000;

    [DllImport("user32.dll")]
    public static extern void SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")]
    public static extern void ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("user32.dll")]
    public static extern void mouse_event(int dwFlags, int dx, int dy, int dwData, int dwExtraInfo);
    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x;
        public int y;

        public POINT(int X, int Y)
        {
            x = X;
            y = Y;
        }
    }
}
