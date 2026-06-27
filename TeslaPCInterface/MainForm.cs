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

    private Button _tabDash = null!, _tabLog = null!, _tabConfig = null!;
    private Panel _dashPanel = null!, _logPanel = null!, _configPanel = null!;
    private RichTextBox _log = null!;
    private Label _serverDot = null!, _httpsDot = null!, _hotspotDot = null!, _clientsDot = null!, _clientsTxt = null!, _urlVal = null!;
    private Button _btnHotspot = null!;
    private TextBox _cfgHost = null!, _cfgToken = null!, _cfgEmail = null!, _cfgVideo = null!;
    private Label _cfgTokenState = null!, _cfgMsg = null!;

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
        _configPanel = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Visible = false, AutoScroll = true, Padding = new Padding(28, 22, 28, 22) };
        BuildDashboard();
        BuildLog();
        BuildConfig();
        content.Controls.Add(_dashPanel);
        content.Controls.Add(_logPanel);
        content.Controls.Add(_configPanel);

        var tabBar = new Panel { Dock = DockStyle.Top, Height = 60, BackColor = Surface };
        var logo = new PictureBox { Image = MakeLogo(36), SizeMode = PictureBoxSizeMode.AutoSize, Left = 14, Top = 12 };
        var wordmark = new Label { Text = "TeslaPC", AutoSize = true, ForeColor = TextC, Font = new Font("Segoe UI", 14F, FontStyle.Bold), Left = 58, Top = 16 };
        _tabDash = MakeTab("Dashboard", 200);
        _tabLog = MakeTab("Log", 360);
        _tabConfig = MakeTab("Config", 520);
        tabBar.Controls.Add(logo);
        tabBar.Controls.Add(wordmark);
        tabBar.Controls.Add(_tabConfig);
        tabBar.Controls.Add(_tabLog);
        tabBar.Controls.Add(_tabDash);

        Controls.Add(content);
        Controls.Add(tabBar);
        ShowTab(_tabDash);
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
        b.Click += (s, _) => ShowTab((Button)s!);
        return b;
    }

    private void ShowTab(Button active)
    {
        _dashPanel.Visible = active == _tabDash;
        _logPanel.Visible = active == _tabLog;
        _configPanel.Visible = active == _tabConfig;
        foreach (var tab in new[] { _tabDash, _tabLog, _tabConfig })
        {
            bool on = tab == active;
            tab.BackColor = on ? Bg : Surface;
            tab.ForeColor = on ? Accent : Muted;
        }
        if (active == _tabConfig) LoadConfigFields();
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

    private void BuildConfig()
    {
        var root = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true
        };

        root.Controls.Add(new Label { Text = "Settings", AutoSize = true, ForeColor = TextC, Font = new Font("Segoe UI", 18F, FontStyle.Bold), Margin = new Padding(0, 0, 0, 16) });

        _cfgHost = AddField(root, "Public URL (hostname)", "The hostname your trusted HTTPS certificate is issued for.", false);
        _cfgToken = AddField(root, "Cloudflare API token", "Used to issue the certificate via DNS. Stored on this PC; leave blank to keep the current one.", true);
        _cfgTokenState = new Label { Text = "", AutoSize = true, ForeColor = Muted, Font = new Font("Segoe UI", 9F), Margin = new Padding(2, 0, 0, 10) };
        root.Controls.Add(_cfgTokenState);
        _cfgEmail = AddField(root, "Let's Encrypt email (optional)", "", false);

        _cfgVideo = AddField(root, "Video folder", "Folder the Files browser and media player read from.", false);
        var browse = MakeButton("Browse…", Surface2, OnBrowseVideo);
        browse.Width = 140; browse.Height = 48; browse.Margin = new Padding(0, 0, 0, 16);
        root.Controls.Add(browse);

        var save = MakeButton("Save settings", Accent, OnSaveConfig);
        root.Controls.Add(save);

        _cfgMsg = new Label { Text = "", AutoSize = true, ForeColor = Muted, Font = new Font("Segoe UI", 11F), Margin = new Padding(0, 14, 0, 0), MaximumSize = new Size(620, 0) };
        root.Controls.Add(_cfgMsg);

        _configPanel.Controls.Add(root);
    }

    // A labeled text field (optionally masked) for the Config tab.
    private TextBox AddField(FlowLayoutPanel parent, string label, string hint, bool secret)
    {
        parent.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = TextC, Font = new Font("Segoe UI", 11F, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4) });
        var tb = new TextBox
        {
            Width = 560,
            Font = new Font("Segoe UI", 12F),
            BackColor = Surface,
            ForeColor = TextC,
            BorderStyle = BorderStyle.FixedSingle,
            UseSystemPasswordChar = secret,
            Margin = new Padding(0, 0, 0, hint.Length > 0 ? 2 : 14)
        };
        parent.Controls.Add(tb);
        if (hint.Length > 0)
            parent.Controls.Add(new Label { Text = hint, AutoSize = true, ForeColor = Muted, Font = new Font("Segoe UI", 9F), Margin = new Padding(2, 0, 0, 14), MaximumSize = new Size(620, 0) });
        return tb;
    }

    private void LoadConfigFields()
    {
        _cfgHost.Text = AppSettings.Get(AppSettings.HttpsHostKey) ?? "";
        _cfgEmail.Text = AppSettings.Get(AppSettings.AcmeEmailKey) ?? "";
        _cfgVideo.Text = _service.VideoRoot;
        _cfgToken.Text = "";
        bool tokenSet = !string.IsNullOrWhiteSpace(AppSettings.Get(AppSettings.CloudflareTokenKey));
        _cfgTokenState.Text = tokenSet
            ? "A token is saved. Leave blank to keep it, or type a new one to replace it."
            : "No token saved yet.";
        _cfgMsg.Text = "";
    }

    private void OnBrowseVideo(object? sender, EventArgs e)
    {
        using var dlg = new FolderBrowserDialog { Description = "Select the video folder", UseDescriptionForTitle = true };
        if (Directory.Exists(_cfgVideo.Text)) dlg.SelectedPath = _cfgVideo.Text;
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _cfgVideo.Text = dlg.SelectedPath.EndsWith("\\") ? dlg.SelectedPath : dlg.SelectedPath + "\\";
    }

    private void OnSaveConfig(object? sender, EventArgs e)
    {
        var toSave = new Dictionary<string, string>
        {
            [AppSettings.HttpsHostKey] = _cfgHost.Text.Trim(),
            [AppSettings.AcmeEmailKey] = _cfgEmail.Text.Trim()
        };
        if (!string.IsNullOrWhiteSpace(_cfgVideo.Text)) toSave[AppSettings.VideoRootKey] = _cfgVideo.Text.Trim();
        if (!string.IsNullOrWhiteSpace(_cfgToken.Text)) toSave[AppSettings.CloudflareTokenKey] = _cfgToken.Text.Trim();

        try
        {
            AppSettings.Save(toSave);
            if (toSave.ContainsKey(AppSettings.VideoRootKey))
                _service.SetVideoRoot(toSave[AppSettings.VideoRootKey]);
            _cfgToken.Text = "";
            bool restartNeeded = toSave.ContainsKey(AppSettings.HttpsHostKey)
                || toSave.ContainsKey(AppSettings.CloudflareTokenKey)
                || toSave.ContainsKey(AppSettings.AcmeEmailKey);
            _cfgMsg.ForeColor = Good;
            _cfgMsg.Text = restartNeeded
                ? "Saved. The video folder applies now; click Restart on the Dashboard to apply the HTTPS/host changes."
                : "Saved. Changes applied.";
            Console.WriteLine("[Config] Settings saved from the control panel.");
        }
        catch (Exception ex)
        {
            _cfgMsg.ForeColor = Bad;
            _cfgMsg.Text = "Save failed: " + ex.Message;
        }
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
