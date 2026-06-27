using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Windows.Forms;

namespace PrimaryProcess;

/// <summary>
/// Touch-friendly control panel for TeslaPC. Borderless, fills the desktop work area (leaves the
/// taskbar). A compact top bar carries the logo + Dashboard/Log tabs; the Dashboard shows a single
/// status row (dots/checks) plus the connect URL and large controls; the Log tab shows console output.
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
    private Label _serverDot = null!, _httpsDot = null!, _hotspotDot = null!, _clientsDot = null!, _clientsTxt = null!, _urlVal = null!;
    private Button _btnHotspot = null!;

    public MainForm(string[] args)
    {
        _args = args;
        Text = "TeslaPC";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = Screen.PrimaryScreen!.WorkingArea;  // fill desktop, keep the taskbar
        BackColor = Bg;
        ForeColor = TextC;
        Font = new Font("Segoe UI", 11F);
        ShowInTaskbar = true;
        try { Icon = Icon.FromHandle(((Bitmap)MakeLogo(32)).GetHicon()); } catch { }

        BuildUi();

        Console.SetOut(new ControlWriter(AppendLog, Console.Out));
        Shown += OnShownAsync;
        FormClosing += OnClosing;
        _statusTimer.Tick += (_, _) => UpdateStatus();
    }

    // ---- Logo: an accent rounded square with a white play triangle (streaming) ----
    private static Image MakeLogo(int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        float r = size * 0.22f;
        using (var path = RoundRect(new RectangleF(0, 0, size - 1, size - 1), r))
        using (var b = new SolidBrush(Accent))
            g.FillPath(b, path);
        var tri = new PointF[]
        {
            new(size * 0.40f, size * 0.30f),
            new(size * 0.40f, size * 0.70f),
            new(size * 0.72f, size * 0.50f)
        };
        using (var w = new SolidBrush(Color.White))
            g.FillPolygon(w, tri);
        return bmp;
    }

    private static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private void BuildUi()
    {
        var content = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
        _dashPanel = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Visible = true };
        _logPanel = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Visible = false, Padding = new Padding(10) };
        BuildDashboard();
        BuildLog();
        content.Controls.Add(_dashPanel);
        content.Controls.Add(_logPanel);

        var tabBar = new Panel { Dock = DockStyle.Top, Height = 60, BackColor = Surface };
        var logo = new PictureBox { Image = MakeLogo(36), SizeMode = PictureBoxSizeMode.AutoSize, Left = 14, Top = 12 };
        var wordmark = new Label { Text = "TeslaPC", AutoSize = true, ForeColor = TextC, Font = new Font("Segoe UI", 14F, FontStyle.Bold), Left = 58, Top = 16 };
        _tabDash = MakeTab("Dashboard", 200);
        _tabLog = MakeTab("Log", 360);
        tabBar.Controls.Add(logo);
        tabBar.Controls.Add(wordmark);
        tabBar.Controls.Add(_tabLog);
        tabBar.Controls.Add(_tabDash);

        Controls.Add(content);
        Controls.Add(tabBar);
        ShowTab(true);
    }

    private Button MakeTab(string text, int x)
    {
        var b = new Button
        {
            Text = text,
            Left = x,
            Top = 0,
            Width = 160,
            Height = 60,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 13F, FontStyle.Bold),
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
            Padding = new Padding(28, 22, 28, 22)
        };

        // Single compact status row.
        var status = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 18) };
        _serverDot = AddIndicator(status, "Server", out _);
        _httpsDot = AddIndicator(status, "HTTPS", out _);
        _hotspotDot = AddIndicator(status, "Hotspot", out _);
        _clientsDot = AddIndicator(status, "", out _clientsTxt);
        root.Controls.Add(status);

        root.Controls.Add(new Label { Text = "Open on the Tesla", AutoSize = true, ForeColor = Muted, Font = new Font("Segoe UI", 11F), Margin = new Padding(0, 0, 0, 2) });
        _urlVal = new Label { Text = "—", AutoSize = true, ForeColor = Accent, Font = new Font("Segoe UI", 18F, FontStyle.Bold), Margin = new Padding(0, 0, 0, 22) };
        root.Controls.Add(_urlVal);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoSize = true, Margin = new Padding(0) };
        _btnHotspot = MakeButton("Toggle Hotspot", Surface2, OnToggleHotspot);
        buttons.Controls.Add(_btnHotspot);
        buttons.Controls.Add(MakeButton("Restart", Surface2, OnRestart));
        buttons.Controls.Add(MakeButton("Minimize", Surface2, (_, _) => WindowState = FormWindowState.Minimized));
        buttons.Controls.Add(MakeButton("Quit", Bad, (_, _) => Close()));
        root.Controls.Add(buttons);

        _dashPanel.Controls.Add(root);
    }

    // One status item: a colored symbol label (dot/check) + a name label, grouped horizontally.
    private Label AddIndicator(FlowLayoutPanel parent, string name, out Label nameLabel)
    {
        var group = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 28, 0) };
        var dot = new Label { Text = "●", AutoSize = true, ForeColor = Muted, Font = new Font("Segoe UI", 17F), Margin = new Padding(0, 2, 6, 0) };
        nameLabel = new Label { Text = name, AutoSize = true, ForeColor = TextC, Font = new Font("Segoe UI", 15F), Margin = new Padding(0, 6, 0, 0) };
        group.Controls.Add(dot);
        group.Controls.Add(nameLabel);
        parent.Controls.Add(group);
        return dot;
    }

    private Button MakeButton(string text, Color back, EventHandler onClick)
    {
        var b = new Button
        {
            Text = text,
            Width = 178,
            Height = 66,
            FlatStyle = FlatStyle.Flat,
            BackColor = back,
            ForeColor = TextC,
            Font = new Font("Segoe UI", 13F, FontStyle.Bold),
            Margin = new Padding(0, 0, 12, 12)
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
            Font = new Font("Consolas", 10F),
            WordWrap = false,
            DetectUrls = false
        };
        _logPanel.Controls.Add(_log);
    }

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
        Console.WriteLine("[UI] Restarting server...");
        var old = _service;
        await Task.Run(async () =>
        {
            try { await old.StopAsync(); } catch { }
            var fresh = new TeslaPcService();
            try { await fresh.StartAsync(_args); } catch (Exception ex) { Console.WriteLine("[Restart] " + ex.Message); }
            _service = fresh;
        });
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var s = _service;
        SetDot(_serverDot, null, s.IsRunning ? Good : Warn);
        // HTTPS: green check when trusted (Let's Encrypt), amber dot for self-signed, red for off.
        if (s.LocalhostOnly || !s.EnableHttps) SetDot(_httpsDot, "●", Bad);
        else if (s.HttpsHost != null) SetDot(_httpsDot, "✔", Good);
        else SetDot(_httpsDot, "●", Warn);
        SetDot(_hotspotDot, null, s.HotspotOn ? Good : Bad);
        SetDot(_clientsDot, null, s.ClientCount > 0 ? Good : Muted);
        _clientsTxt.Text = s.ClientCount == 1 ? "1 viewer" : $"{s.ClientCount} viewers";
        _urlVal.Text = string.IsNullOrEmpty(s.PrimaryUrl) ? "—" : s.PrimaryUrl;
        _btnHotspot.Visible = s.BypassEnabled;
    }

    private static void SetDot(Label dot, string? symbol, Color color)
    {
        if (symbol != null) dot.Text = symbol;
        dot.ForeColor = color;
    }

    private void AppendLog(string text)
    {
        if (_log.IsDisposed) return;
        if (_log.InvokeRequired) { try { _log.BeginInvoke(new Action(() => AppendLog(text))); } catch { } return; }
        if (_log.TextLength > 120000) _log.Text = _log.Text.Substring(_log.TextLength - 90000);
        _log.AppendText(text);
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    /// <summary>Tees console output to the Log tab (and the original stream, e.g. a redirected log file).</summary>
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
