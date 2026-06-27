using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace PrimaryProcess;

/// <summary>
/// Touch-friendly control panel for TeslaPC. Borderless, fills the desktop work area (leaves the
/// taskbar), with a Dashboard tab (status + large controls) and a Log tab (console output).
/// </summary>
internal sealed class MainForm : Form
{
    private static readonly Color Bg = Color.FromArgb(11, 13, 17);
    private static readonly Color Surface = Color.FromArgb(22, 26, 33);
    private static readonly Color Surface2 = Color.FromArgb(31, 37, 48);
    private static readonly Color Accent = Color.FromArgb(59, 130, 246);
    private static readonly Color TextC = Color.FromArgb(243, 246, 250);
    private static readonly Color Muted = Color.FromArgb(152, 164, 179);
    private static readonly Color Good = Color.FromArgb(55, 200, 113);
    private static readonly Color Warn = Color.FromArgb(245, 185, 69);
    private static readonly Color Bad = Color.FromArgb(226, 35, 26);

    private readonly string[] _args;
    private TeslaPcService _service = new();
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 2000 };

    private Button _tabDash = null!, _tabLog = null!;
    private Panel _dashPanel = null!, _logPanel = null!;
    private RichTextBox _log = null!;
    private Label _serverVal = null!, _httpsVal = null!, _hotspotVal = null!, _clientsVal = null!, _urlVal = null!;
    private Button _btnHotspot = null!, _btnRestart = null!, _btnMin = null!, _btnQuit = null!;

    public MainForm(string[] args)
    {
        _args = args;
        Text = "TeslaPC";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = Screen.PrimaryScreen!.WorkingArea;  // fill desktop, keep the taskbar
        BackColor = Bg;
        ForeColor = TextC;
        Font = new Font("Segoe UI", 12F);
        ShowInTaskbar = true;

        BuildUi();

        Console.SetOut(new ControlWriter(AppendLog, Console.Out));
        Shown += OnShownAsync;
        FormClosing += OnClosing;
        _statusTimer.Tick += (_, _) => UpdateStatus();
    }

    private void BuildUi()
    {
        var content = new Panel { Dock = DockStyle.Fill, BackColor = Bg };

        _dashPanel = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Visible = true };
        _logPanel = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Visible = false };
        BuildDashboard();
        BuildLog();
        content.Controls.Add(_dashPanel);
        content.Controls.Add(_logPanel);

        var tabBar = new Panel { Dock = DockStyle.Top, Height = 76, BackColor = Surface };
        _tabDash = MakeTab("Dashboard", 0, true);
        _tabLog = MakeTab("Log", 220, false);
        tabBar.Controls.Add(_tabLog);
        tabBar.Controls.Add(_tabDash);

        Controls.Add(content);
        Controls.Add(tabBar);
        ShowTab(true);
    }

    private Button MakeTab(string text, int x, bool _)
    {
        var b = new Button
        {
            Text = text,
            Left = x,
            Top = 0,
            Width = 220,
            Height = 76,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 15F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter
        };
        b.FlatAppearance.BorderSize = 0;
        b.Click += (s, _) => ShowTab(s == _tabDash);
        return b;
    }

    private void ShowTab(bool dash)
    {
        _dashPanel.Visible = dash;
        _logPanel.Visible = !dash;
        _tabDash.BackColor = dash ? Bg : Surface;
        _tabDash.ForeColor = dash ? Accent : Muted;
        _tabLog.BackColor = dash ? Surface : Bg;
        _tabLog.ForeColor = dash ? Muted : Accent;
    }

    private void BuildDashboard()
    {
        var root = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(48, 36, 48, 36)
        };

        root.Controls.Add(new Label
        {
            Text = "TeslaPC",
            AutoSize = true,
            Font = new Font("Segoe UI", 34F, FontStyle.Bold),
            ForeColor = TextC,
            Margin = new Padding(0, 0, 0, 24)
        });

        _serverVal = AddStatusRow(root, "Server");
        _httpsVal = AddStatusRow(root, "HTTPS");
        _hotspotVal = AddStatusRow(root, "Hotspot");
        _clientsVal = AddStatusRow(root, "Connected");

        var urlCaption = new Label { Text = "Open on the Tesla", AutoSize = true, ForeColor = Muted, Font = new Font("Segoe UI", 12F), Margin = new Padding(0, 28, 0, 4) };
        root.Controls.Add(urlCaption);
        _urlVal = new Label { Text = "—", AutoSize = true, ForeColor = Accent, Font = new Font("Segoe UI", 20F, FontStyle.Bold), Margin = new Padding(0, 0, 0, 28) };
        root.Controls.Add(_urlVal);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoSize = true, Margin = new Padding(0) };
        _btnHotspot = MakeButton("Toggle Hotspot", Surface2, OnToggleHotspot);
        _btnRestart = MakeButton("Restart", Surface2, OnRestart);
        _btnMin = MakeButton("Minimize", Surface2, (_, _) => WindowState = FormWindowState.Minimized);
        _btnQuit = MakeButton("Quit", Bad, (_, _) => Close());
        buttons.Controls.Add(_btnHotspot);
        buttons.Controls.Add(_btnRestart);
        buttons.Controls.Add(_btnMin);
        buttons.Controls.Add(_btnQuit);
        root.Controls.Add(buttons);

        _dashPanel.Controls.Add(root);
    }

    private Label AddStatusRow(FlowLayoutPanel parent, string name)
    {
        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 6) };
        row.Controls.Add(new Label { Text = name, AutoSize = true, ForeColor = Muted, Font = new Font("Segoe UI", 16F), Width = 220, Margin = new Padding(0, 6, 24, 0), MinimumSize = new Size(220, 0) });
        var val = new Label { Text = "…", AutoSize = true, ForeColor = TextC, Font = new Font("Segoe UI", 16F, FontStyle.Bold), Margin = new Padding(0, 6, 0, 0) };
        row.Controls.Add(val);
        parent.Controls.Add(row);
        return val;
    }

    private Button MakeButton(string text, Color back, EventHandler onClick)
    {
        var b = new Button
        {
            Text = text,
            Width = 280,
            Height = 84,
            FlatStyle = FlatStyle.Flat,
            BackColor = back,
            ForeColor = TextC,
            Font = new Font("Segoe UI", 16F, FontStyle.Bold),
            Margin = new Padding(0, 0, 16, 16)
        };
        b.FlatAppearance.BorderSize = 0;
        b.Click += onClick;
        return b;
    }

    private void BuildLog()
    {
        _log = new RichTextBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(8, 10, 13),
            ForeColor = Color.FromArgb(200, 210, 220),
            BorderStyle = BorderStyle.None,
            ReadOnly = true,
            Font = new Font("Consolas", 11F),
            WordWrap = false,
            DetectUrls = false
        };
        _logPanel.Controls.Add(_log);
        _logPanel.Padding = new Padding(12);
    }

    // Keep the system and display awake while running — an always-on streaming appliance must not
    // let the monitor sleep, or DXGI screen capture stalls (the desktop drops to a basic 800x600
    // surface and stops delivering frames).
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);
    private const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001, ES_DISPLAY_REQUIRED = 0x00000002;

    private async void OnShownAsync(object? sender, EventArgs e)
    {
        SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
        _statusTimer.Start();
        await Task.Run(async () =>
        {
            try { await _service.StartAsync(_args); }
            catch (Exception ex) { Console.WriteLine("[Startup] " + ex.Message); }
        });
        UpdateStatus();
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        _statusTimer.Stop();
        try { _service.StopAsync().Wait(5000); } catch { }
    }

    private async void OnToggleHotspot(object? sender, EventArgs e)
    {
        _btnHotspot.Enabled = false;
        await Task.Run(() => { try { _service.ToggleHotspot(); } catch (Exception ex) { Console.WriteLine(ex.Message); } });
        _btnHotspot.Enabled = true;
        UpdateStatus();
    }

    private async void OnRestart(object? sender, EventArgs e)
    {
        _btnRestart.Enabled = false;
        Console.WriteLine("[UI] Restarting server...");
        var old = _service;
        await Task.Run(async () =>
        {
            try { await old.StopAsync(); } catch { }
            var fresh = new TeslaPcService();
            try { await fresh.StartAsync(_args); } catch (Exception ex) { Console.WriteLine("[Restart] " + ex.Message); }
            _service = fresh;
        });
        _btnRestart.Enabled = true;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var s = _service;
        SetStatus(_serverVal, s.IsRunning ? "Running" : "Starting…", s.IsRunning ? Good : Warn);
        if (s.LocalhostOnly) SetStatus(_httpsVal, "localhost only", Muted);
        else if (!s.EnableHttps) SetStatus(_httpsVal, "HTTP only", Warn);
        else if (s.HttpsHost != null) SetStatus(_httpsVal, "Trusted (Let's Encrypt)", Good);
        else SetStatus(_httpsVal, "Self-signed", Warn);
        SetStatus(_hotspotVal, s.HotspotOn ? "On" : "Off", s.HotspotOn ? Good : Bad);
        SetStatus(_clientsVal, s.ClientCount.ToString(), s.ClientCount > 0 ? Good : Muted);
        _urlVal.Text = string.IsNullOrEmpty(s.PrimaryUrl) ? "—" : s.PrimaryUrl;
        _btnHotspot.Visible = s.BypassEnabled;
    }

    private static void SetStatus(Label l, string text, Color color) { l.Text = text; l.ForeColor = color; }

    private void AppendLog(string text)
    {
        if (_log.IsDisposed) return;
        if (_log.InvokeRequired) { try { _log.BeginInvoke(new Action(() => AppendLog(text))); } catch { } return; }
        if (_log.TextLength > 120000) _log.Text = _log.Text.Substring(_log.TextLength - 90000);
        _log.AppendText(text);
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    /// <summary>Tees console output to the Log tab (and the original stream, e.g. the redirected log file).</summary>
    private sealed class ControlWriter : TextWriter
    {
        private readonly Action<string> _append;
        private readonly TextWriter? _tee;
        public ControlWriter(Action<string> append, TextWriter? tee) { _append = append; _tee = tee; }
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(string? value) { if (!string.IsNullOrEmpty(value)) { _append(value); _tee?.Write(value); } }
        public override void WriteLine(string? value) { _append((value ?? "") + Environment.NewLine); _tee?.WriteLine(value); }
        public override void WriteLine() { _append(Environment.NewLine); _tee?.WriteLine(); }
    }
}
