using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Text;

namespace AllTemp;

/// <summary>
/// The overlay window. Everything is custom-painted from a flat list of <see cref="Item"/>s that
/// <see cref="RebuildLayout"/> regenerates whenever readings or settings change.
/// </summary>
sealed class OverlayForm : Form
{
    static readonly Color Bg = Color.FromArgb(32, 32, 32);
    static readonly Color BgHover = Color.FromArgb(48, 48, 48);
    static readonly Color Edge = Color.FromArgb(60, 60, 60);
    static readonly Color Divider = Color.FromArgb(44, 44, 44);
    static readonly Color TextPrimary = Color.FromArgb(240, 240, 240);
    static readonly Color TextSecondary = Color.FromArgb(158, 158, 158);
    static readonly Color TagBg = Color.FromArgb(52, 52, 52);
    static readonly Color TagText = Color.FromArgb(200, 200, 200);
    static readonly Color Good = Color.FromArgb(108, 203, 95);
    static readonly Color Warm = Color.FromArgb(247, 201, 72);
    static readonly Color Hot = Color.FromArgb(255, 95, 86);

    static readonly string IconFont =
        new InstalledFontCollection().Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    const int HistoryLength = 60;
    const TextFormatFlags TfLeft = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
    const TextFormatFlags TfRight = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
    const TextFormatFlags TfCenter = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

    enum ItemKind { Title, Button, Device, Sensor, Notice, Message }

    sealed class Item
    {
        public ItemKind Kind;
        public Rectangle Bounds;
        public string Text = "";
        public DeviceReading? Device;
        public SensorReading? Sensor;
        public Action? Click;
        public bool HoverOnly;
        public bool DividerAbove;
    }

    readonly Settings _settings = Settings.Load();
    readonly SensorService _sensors = new();
    readonly Mutex _instance;
    readonly NotifyIcon _tray;
    readonly ContextMenuStrip _menu;
    readonly Dictionary<string, List<float>> _history = new();
    readonly List<Item> _items = [];
    readonly bool _pawnIo = Elevation.PawnIoInstalled;

    IReadOnlyList<DeviceReading>? _devices; // null while LibreHardwareMonitor is still opening
    Item? _hot;
    bool _mouseInside, _mouseDown, _started, _exiting, _firstShow = true, _toldAboutTray;
    bool? _autostart;
    Point _downPos;
    string _trayKey = "";
    Icon? _trayIcon;

    Font _fTitle = null!, _fName = null!, _fValue = null!, _fSub = null!, _fSubValue = null!, _fTag = null!, _fIcon = null!, _fChevron = null!;

    int Px(float v) => (int)Math.Round(v * DeviceDpi / 96f);

    public OverlayForm(Mutex instance, EventWaitHandle showSignal)
    {
        _instance = instance;
        Text = "AllTemp";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Bg;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

        CreateFonts();
        _menu = BuildMenu();
        ContextMenuStrip = _menu;
        _tray = new NotifyIcon { Text = "AllTemp", Icon = SystemIcons.Application, ContextMenuStrip = _menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleVisible(); };

        _sensors.IntervalMs = _settings.IntervalMs;
        _sensors.Updated += snapshot => Post(() => OnReadings(snapshot));

        new Thread(() => { while (showSignal.WaitOne()) Post(ShowWindow); }) { IsBackground = true }.Start();
        new Thread(() => { var on = Autostart.IsEnabled; Post(() => _autostart = on); }) { IsBackground = true }.Start();

        RebuildLayout();
        PlaceWindow();
    }

    void Post(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch (InvalidOperationException) { /* closing */ }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW; // keep out of Alt+Tab; the tray icon is the way back
            return cp;
        }
    }

    protected override void SetVisibleCore(bool value)
    {
        if (_firstShow)
        {
            _firstShow = false;
            if (_settings.Hidden) { CreateHandle(); value = false; }
        }
        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.RoundCorners(Handle);
        ApplyPinState();
        if (!_started) { _started = true; _sensors.Start(); }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        CreateFonts();
        RebuildLayout();
    }

    void CreateFonts()
    {
        foreach (var f in new[] { _fTitle, _fName, _fValue, _fSub, _fSubValue, _fTag, _fIcon, _fChevron }) f?.Dispose();
        Font F(string family, float pt) => new(family, pt * DeviceDpi / 72f, FontStyle.Regular, GraphicsUnit.Pixel);
        _fTitle = F("Segoe UI Semibold", 10.5f);
        _fName = F("Segoe UI", 9.5f);
        _fValue = F("Segoe UI Semibold", 11f);
        _fSub = F("Segoe UI", 8.75f);
        _fSubValue = F("Segoe UI Semibold", 9f);
        _fTag = F("Segoe UI Semibold", 7f);
        _fIcon = F(IconFont, 9.5f);
        _fChevron = F(IconFont, 6.5f);
    }

    // ───────────────────────────── data ─────────────────────────────

    void OnReadings(IReadOnlyList<DeviceReading> devices)
    {
        _devices = devices;
        foreach (var d in devices)
        {
            if (!_history.TryGetValue(d.Id, out var h)) _history[d.Id] = h = new List<float>(HistoryLength);
            if (h.Count == HistoryLength) h.RemoveAt(0);
            h.Add(d.Primary.Value);
        }
        RebuildLayout();
        UpdateTray();
    }

    static (float warm, float hot) Limits(DeviceKind kind, string sensor) => kind switch
    {
        DeviceKind.Cpu => (70, 85),
        DeviceKind.Gpu when sensor.Contains("Hot Spot") || sensor.Contains("Memory") || sensor.Contains("Junction") => (85, 100),
        DeviceKind.Gpu => (75, 85),
        DeviceKind.Storage => (55, 70),
        DeviceKind.Memory or DeviceKind.Motherboard => (60, 80),
        DeviceKind.Cooler => (40, 50),
        _ => (70, 90),
    };

    static Color StatusColor(DeviceKind kind, SensorReading s)
    {
        var (warm, hot) = Limits(kind, s.Name);
        return s.Value >= hot ? Hot : s.Value >= warm ? Warm : Good;
    }

    string Fmt(float celsius, bool unit = true)
    {
        var v = _settings.Fahrenheit ? celsius * 9 / 5 + 32 : celsius;
        return $"{Math.Round(v):0}°" + (unit ? (_settings.Fahrenheit ? "F" : "C") : "");
    }

    static string KindTag(DeviceKind k) => k switch
    {
        DeviceKind.Cpu => "CPU",
        DeviceKind.Gpu => "GPU",
        DeviceKind.Memory => "RAM",
        DeviceKind.Motherboard => "BOARD",
        DeviceKind.Storage => "DISK",
        DeviceKind.Cooler => "COOL",
        _ => "SYS",
    };

    // ───────────────────────────── layout ─────────────────────────────

    void RebuildLayout()
    {
        _items.Clear();
        int w = Px(340), y;
        bool pinned = _settings.Pinned;

        if (!pinned)
        {
            int h = Px(40), bs = Px(30), by = (h - bs) / 2, bx = w - Px(6) - bs;
            _items.Add(new Item { Kind = ItemKind.Title, Bounds = new(0, 0, w, h), Text = "Temperatures" });
            _items.Add(new Item { Kind = ItemKind.Button, Bounds = new(bx, by, bs, bs), Text = "", Click = HideToTray });
            bx -= bs + Px(2);
            _items.Add(new Item { Kind = ItemKind.Button, Bounds = new(bx, by, bs, bs), Text = "", Click = TogglePin });
            bx -= bs + Px(2);
            _items.Add(new Item { Kind = ItemKind.Button, Bounds = new(bx, by, bs, bs), Text = "", Click = () => _menu.Show(Cursor.Position) });
            y = h;
        }
        else y = Px(4);

        if (_devices is null || _devices.Count == 0)
        {
            var msg = _devices is null ? "Detecting sensors…" : "No temperature sensors found.";
            _items.Add(new Item { Kind = ItemKind.Message, Bounds = new(0, y, w, Px(40)), Text = msg });
            y += Px(40);
        }
        else
        {
            bool first = true;
            foreach (var d in _devices)
            {
                bool expandable = d.Sensors.Count > 1;
                _items.Add(new Item
                {
                    Kind = ItemKind.Device,
                    Bounds = new(0, y, w, Px(34)),
                    Device = d,
                    DividerAbove = !first,
                    Click = expandable ? () => ToggleExpand(d.Id) : null,
                });
                y += Px(34);
                first = false;
                if (!expandable || !_settings.Expanded.Contains(d.Id)) continue;
                foreach (var s in d.Sensors)
                {
                    _items.Add(new Item { Kind = ItemKind.Sensor, Bounds = new(0, y, w, Px(22)), Device = d, Sensor = s });
                    y += Px(22);
                }
                y += Px(6);
            }
        }

        if (!pinned)
        {
            string? notice = null;
            Action? fix = null;
            if (!Elevation.IsAdmin) notice = "Not running as administrator, so CPU, disk and RAM sensors are hidden.";
            else if (!_pawnIo) (notice, fix) = ("CPU, RAM and motherboard temps need the free PawnIO driver. Click here to install it.", InstallPawnIo);
            if (notice != null)
            {
                int th = TextRenderer.MeasureText(notice, _fSub, new Size(w - Px(28), 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
                _items.Add(new Item { Kind = ItemKind.Notice, Bounds = new(0, y, w, th + Px(18)), Text = notice, Click = fix, DividerAbove = true });
                y += th + Px(18);
            }
        }
        else
        {
            // Unpin control floats over the top-right corner and only appears on hover.
            _items.Add(new Item { Kind = ItemKind.Button, Bounds = new(w - Px(32), Px(4), Px(28), Px(28)), Text = "", Click = TogglePin, HoverOnly = true });
        }

        y += Px(6);
        var size = new Size(w, y);
        if (ClientSize != size) ClientSize = size;
        _hot = _mouseInside ? HitTest(PointToClient(MousePosition)) : null;
        if (Visible) Invalidate();
    }

    void PlaceWindow()
    {
        var p = new Point(_settings.X, _settings.Y);
        bool onScreen = _settings.X != int.MinValue &&
            Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(p, new Size(Px(120), Px(40)))));
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = onScreen ? p : new Point(wa.Right - Width - Px(24), wa.Top + Px(24));
    }

    // ───────────────────────────── painting ─────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Bg);
        foreach (var it in _items)
        {
            switch (it.Kind)
            {
                case ItemKind.Title:
                    TextRenderer.DrawText(g, it.Text, _fTitle, Inset(it.Bounds, Px(14), 0), TextPrimary, TfLeft);
                    break;
                case ItemKind.Button:
                    DrawButton(g, it);
                    break;
                case ItemKind.Device:
                    DrawDevice(g, it);
                    break;
                case ItemKind.Sensor:
                    DrawSensor(g, it);
                    break;
                case ItemKind.Notice:
                    if (it.DividerAbove) DrawDivider(g, it.Bounds.Top);
                    if (it == _hot) Fill(g, it.Bounds, BgHover);
                    TextRenderer.DrawText(g, it.Text, _fSub, Inset(it.Bounds, Px(14), Px(9)), Warm,
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
                    break;
                case ItemKind.Message:
                    TextRenderer.DrawText(g, it.Text, _fName, it.Bounds, TextSecondary, TfCenter);
                    break;
            }
        }
        using var edge = new Pen(Edge);
        g.DrawRectangle(edge, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    void DrawButton(Graphics g, Item it)
    {
        if (it.HoverOnly && !_mouseInside) return;
        if (it.HoverOnly || it == _hot)
            FillRound(g, it.Bounds, Px(4), it == _hot ? Color.FromArgb(70, 70, 70) : Color.FromArgb(52, 52, 52));
        var glyph = it.Text == "" && _settings.Pinned ? "" : it.Text;
        TextRenderer.DrawText(g, glyph, _fIcon, it.Bounds, TextPrimary, TfCenter);
    }

    void DrawDevice(Graphics g, Item it)
    {
        var d = it.Device!;
        var r = it.Bounds;
        if (it.DividerAbove) DrawDivider(g, r.Top);
        if (it == _hot) Fill(g, r, BgHover);

        int x = Px(8), cy = r.Top + r.Height / 2;
        if (d.Sensors.Count > 1)
        {
            var chevron = _settings.Expanded.Contains(d.Id) ? "" : "";
            TextRenderer.DrawText(g, chevron, _fChevron, new Rectangle(x, r.Top, Px(14), r.Height), TextSecondary, TfCenter);
        }
        x += Px(16);

        var tagRect = new Rectangle(x, cy - Px(9), Px(40), Px(18));
        FillRound(g, tagRect, Px(4), TagBg);
        TextRenderer.DrawText(g, KindTag(d.Kind), _fTag, tagRect, TagText, TfCenter);
        x += Px(48);

        int right = r.Right - Px(14);
        var valueRect = new Rectangle(right - Px(56), r.Top, Px(56), r.Height);
        var color = StatusColor(d.Kind, d.Primary);
        int nameRight = valueRect.Left - Px(6);
        if (_settings.ShowGraphs)
        {
            var spark = new Rectangle(valueRect.Left - Px(58), cy - Px(9), Px(54), Px(18));
            DrawSparkline(g, spark, d.Id, color);
            nameRight = spark.Left - Px(10);
        }

        if (d.Detail != null)
        {
            var dw = TextRenderer.MeasureText(g, d.Detail, _fSubValue, Size.Empty, TextFormatFlags.NoPadding).Width;
            TextRenderer.DrawText(g, d.Detail, _fSubValue, new Rectangle(x, r.Top, Math.Min(dw, nameRight - x), r.Height), TextPrimary, TfLeft);
            x += dw + Px(6);
        }
        if (nameRight > x)
            TextRenderer.DrawText(g, d.Name, _fName, new Rectangle(x, r.Top, nameRight - x, r.Height),
                d.Detail != null ? TextSecondary : TextPrimary, TfLeft);

        TextRenderer.DrawText(g, Fmt(d.Primary.Value), _fValue, valueRect, color, TfRight);
    }

    void DrawSensor(Graphics g, Item it)
    {
        var r = it.Bounds;
        var s = it.Sensor!;
        int x = Px(72), right = r.Right - Px(14);
        bool isPrimary = s.Id == it.Device!.Primary.Id;
        TextRenderer.DrawText(g, s.Name, _fSub, new Rectangle(x, r.Top, right - Px(50) - x, r.Height),
            isPrimary ? TextPrimary : TextSecondary, TfLeft);
        TextRenderer.DrawText(g, Fmt(s.Value, unit: false), _fSubValue, new Rectangle(right - Px(48), r.Top, Px(48), r.Height),
            StatusColor(it.Device.Kind, s), TfRight);
    }

    void DrawSparkline(Graphics g, Rectangle r, string id, Color color)
    {
        if (!_history.TryGetValue(id, out var h) || h.Count < 2) return;
        float min = h.Min(), max = h.Max();
        if (max - min < 8) { float mid = (max + min) / 2; min = mid - 4; max = mid + 4; }
        float step = r.Width / (float)(HistoryLength - 1);
        float x0 = r.Right - step * (h.Count - 1);
        var pts = new PointF[h.Count];
        for (int i = 0; i < h.Count; i++)
            pts[i] = new PointF(x0 + i * step, r.Bottom - (h[i] - min) / (max - min) * r.Height);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var area = new GraphicsPath())
        {
            area.AddLines(pts);
            area.AddLine(pts[^1].X, pts[^1].Y, pts[^1].X, r.Bottom);
            area.AddLine(pts[^1].X, r.Bottom, pts[0].X, r.Bottom);
            area.CloseFigure();
            using var fill = new SolidBrush(Color.FromArgb(40, color));
            g.FillPath(fill, area);
        }
        using (var pen = new Pen(Color.FromArgb(220, color), Math.Max(1f, DeviceDpi / 96f * 1.25f)))
            g.DrawLines(pen, pts);
        g.SmoothingMode = SmoothingMode.None;
    }

    void DrawDivider(Graphics g, int y)
    {
        using var pen = new Pen(Divider);
        g.DrawLine(pen, Px(12), y, ClientSize.Width - Px(12), y);
    }

    static Rectangle Inset(Rectangle r, int dx, int dy) => new(r.X + dx, r.Y + dy, r.Width - 2 * dx, r.Height - 2 * dy);

    static void Fill(Graphics g, Rectangle r, Color c)
    {
        using var b = new SolidBrush(c);
        g.FillRectangle(b, r);
    }

    static void FillRound(Graphics g, Rectangle r, int radius, Color c)
    {
        using var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        using var b = new SolidBrush(c);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.FillPath(b, path);
        g.SmoothingMode = SmoothingMode.None;
    }

    // ───────────────────────────── mouse ─────────────────────────────

    Item? HitTest(Point p)
    {
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            var it = _items[i];
            if (it.Click != null && (!it.HoverOnly || _mouseInside) && it.Bounds.Contains(p)) return it;
        }
        return null;
    }

    void SetHot(Item? it)
    {
        if (it == _hot) return;
        _hot = it;
        Cursor = it != null ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _mouseDown = true;
        _downPos = e.Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_mouseDown && (Math.Abs(e.X - _downPos.X) > Px(4) || Math.Abs(e.Y - _downPos.Y) > Px(4)))
        {
            _mouseDown = false;
            Native.ReleaseCapture();
            Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, Native.HTCAPTION, IntPtr.Zero); // modal move loop
            _settings.X = Left;
            _settings.Y = Top;
            _settings.Save();
            return;
        }
        if (!_mouseInside) { _mouseInside = true; Invalidate(); }
        SetHot(HitTest(e.Location));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_mouseDown || e.Button != MouseButtons.Left) return;
        _mouseDown = false;
        HitTest(e.Location)?.Click?.Invoke();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _mouseInside = false;
        _mouseDown = false;
        SetHot(null);
        Invalidate();
    }

    // ───────────────────────────── actions ─────────────────────────────

    void ToggleExpand(string id)
    {
        if (!_settings.Expanded.Remove(id)) _settings.Expanded.Add(id);
        _settings.Save();
        RebuildLayout();
    }

    void TogglePin()
    {
        _settings.Pinned = !_settings.Pinned;
        _settings.Save();
        ApplyPinState();
    }

    void ApplyPinState()
    {
        bool clickThrough = _settings.Pinned && _settings.ClickThrough;
        double opacity = _settings.Pinned ? _settings.PinnedOpacity / 100.0 : 1.0;
        if (clickThrough) opacity = Math.Min(opacity, 0.99); // WS_EX_TRANSPARENT only passes clicks through on layered windows
        TopMost = _settings.Pinned;
        Opacity = opacity;
        if (IsHandleCreated) Native.SetExStyle(Handle, Native.WS_EX_TRANSPARENT, clickThrough);
        RebuildLayout();
    }

    void ToggleVisible()
    {
        if (Visible) HideToTray();
        else ShowWindow();
    }

    void ShowWindow()
    {
        _settings.Hidden = false;
        _settings.Save();
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        if (!TopMost) { TopMost = true; TopMost = false; } // bring above other windows once
    }

    void HideToTray()
    {
        Hide();
        _settings.Hidden = true;
        _settings.Save();
        if (!_toldAboutTray)
        {
            _toldAboutTray = true;
            _tray.ShowBalloonTip(2500, "AllTemp is still running", "Click the tray icon to bring the window back.", ToolTipIcon.None);
        }
    }

    void InstallPawnIo()
    {
        var answer = MessageBox.Show(this,
            "CPU, RAM and motherboard sensors are read through PawnIO, a free signed driver by namazso " +
            "(the same one LibreHardwareMonitor and FanControl use).\n\n" +
            "Install it now with winget? A console window shows the progress, and AllTemp restarts when it finishes.",
            "Install PawnIO driver", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;

        Process installer;
        try
        {
            installer = Process.Start(new ProcessStartInfo("cmd.exe",
                "/c winget install -e --id namazso.PawnIO --accept-package-agreements --accept-source-agreements & timeout /t 4")
            { UseShellExecute = true })!;
        }
        catch
        {
            Process.Start(new ProcessStartInfo("https://pawnio.eu") { UseShellExecute = true });
            return;
        }

        new Thread(() =>
        {
            installer.WaitForExit();
            Post(() =>
            {
                if (Elevation.PawnIoInstalled) Restart();
                else if (MessageBox.Show(this, "PawnIO doesn't appear to be installed yet. Open pawnio.eu to download it manually?",
                             "Install PawnIO driver", MessageBoxButtons.YesNo) == DialogResult.Yes)
                    Process.Start(new ProcessStartInfo("https://pawnio.eu") { UseShellExecute = true });
            });
        }) { IsBackground = true }.Start();
    }

    void Restart()
    {
        _instance.ReleaseMutex();
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true });
        ExitApp();
    }

    void ExitApp()
    {
        _exiting = true;
        Application.Exit();
    }

    string ReadingsText()
    {
        var sb = new StringBuilder();
        foreach (var d in _devices ?? [])
        {
            sb.AppendLine($"{KindTag(d.Kind)}  {(d.Detail != null ? d.Detail + "  " : "")}{d.Name}");
            foreach (var s in d.Sensors) sb.AppendLine($"    {s.Name,-28} {Fmt(s.Value)}");
        }
        return sb.ToString();
    }

    // ───────────────────────────── tray + menu ─────────────────────────────

    void UpdateTray()
    {
        if (_devices is null || _devices.Count == 0) return;
        var main = _devices.FirstOrDefault(d => d.Kind == DeviceKind.Cpu) ?? _devices.MaxBy(d => d.Primary.Value)!;
        var color = StatusColor(main.Kind, main.Primary);
        var number = Fmt(main.Primary.Value, unit: false).TrimEnd('°');

        var key = number + color.ToArgb();
        if (key != _trayKey)
        {
            _trayKey = key;
            var old = _trayIcon;
            _trayIcon = TrayIcon.Render(number, color);
            _tray.Icon = _trayIcon;
            old?.Dispose();
        }

        var tip = new StringBuilder("AllTemp");
        foreach (var d in _devices)
        {
            var line = $"\n{KindTag(d.Kind)} {(d.Detail ?? Shorten(d.Name))}: {Fmt(d.Primary.Value)}";
            if (tip.Length + line.Length > 127) break;
            tip.Append(line);
        }
        _tray.Text = tip.ToString();
    }

    static string Shorten(string name) => name.Length <= 18 ? name : name[..17] + "…";

    ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false };

        var show = new ToolStripMenuItem("Hide window", null, (_, _) => ToggleVisible());
        var pin = new ToolStripMenuItem("Pin on top", null, (_, _) => TogglePin());
        var opacity = new ToolStripMenuItem("Pinned opacity");
        foreach (var pct in new[] { 100, 90, 80, 70, 60, 50, 40 })
            opacity.DropDownItems.Add(new ToolStripMenuItem($"{pct}%", null, (_, _) =>
            {
                _settings.PinnedOpacity = pct;
                _settings.Save();
                ApplyPinState();
            }) { Tag = pct });
        var clickThrough = new ToolStripMenuItem("Click-through while pinned", null, (_, _) =>
        {
            _settings.ClickThrough = !_settings.ClickThrough;
            _settings.Save();
            ApplyPinState();
        });

        var expandAll = new ToolStripMenuItem("Expand all", null, (_, _) =>
        {
            foreach (var d in _devices ?? []) if (d.Sensors.Count > 1) _settings.Expanded.Add(d.Id);
            _settings.Save();
            RebuildLayout();
        });
        var collapseAll = new ToolStripMenuItem("Collapse all", null, (_, _) =>
        {
            _settings.Expanded.Clear();
            _settings.Save();
            RebuildLayout();
        });
        var graphs = new ToolStripMenuItem("Show graphs", null, (_, _) =>
        {
            _settings.ShowGraphs = !_settings.ShowGraphs;
            _settings.Save();
            RebuildLayout();
        });
        var fahrenheit = new ToolStripMenuItem("Fahrenheit (°F)", null, (_, _) =>
        {
            _settings.Fahrenheit = !_settings.Fahrenheit;
            _settings.Save();
            _trayKey = "";
            RebuildLayout();
            UpdateTray();
        });
        var interval = new ToolStripMenuItem("Refresh every");
        foreach (var ms in new[] { 500, 1000, 2000, 5000 })
            interval.DropDownItems.Add(new ToolStripMenuItem(ms < 1000 ? $"{ms / 1000.0:0.#} s" : $"{ms / 1000} s", null, (_, _) =>
            {
                _settings.IntervalMs = ms;
                _settings.Save();
                _sensors.IntervalMs = ms;
            }) { Tag = ms });

        var autostart = new ToolStripMenuItem("Start with Windows", null, (_, _) =>
        {
            bool want = _autostart != true;
            if (Autostart.Set(want)) _autostart = want;
            else MessageBox.Show(this, "Couldn't update the startup task.", "AllTemp", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        });
        var pawn = new ToolStripMenuItem("Install PawnIO driver…", null, (_, _) => InstallPawnIo()) { Visible = !_pawnIo && Elevation.IsAdmin };
        var copy = new ToolStripMenuItem("Copy readings", null, (_, _) =>
        {
            var text = ReadingsText();
            if (text.Length > 0) Clipboard.SetText(text);
        });

        m.Items.AddRange(new ToolStripItem[] {
            show, pin, opacity, clickThrough, new ToolStripSeparator(),
            expandAll, collapseAll, graphs, fahrenheit, interval, new ToolStripSeparator(),
            autostart, pawn, copy, new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitApp()),
        });

        m.Opening += (_, _) =>
        {
            show.Text = Visible ? "Hide window" : "Show window";
            pin.Checked = _settings.Pinned;
            clickThrough.Checked = _settings.ClickThrough;
            graphs.Checked = _settings.ShowGraphs;
            fahrenheit.Checked = _settings.Fahrenheit;
            autostart.Checked = _autostart == true;
            autostart.Enabled = _autostart != null;
            foreach (ToolStripMenuItem i in opacity.DropDownItems) i.Checked = (int)i.Tag! == _settings.PinnedOpacity;
            foreach (ToolStripMenuItem i in interval.DropDownItems) i.Checked = (int)i.Tag! == _settings.IntervalMs;
        };
        return m;
    }

    // ───────────────────────────── shutdown ─────────────────────────────

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true; // Alt+F4 hides to tray; Exit lives in the menu
            HideToTray();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (Visible) { _settings.X = Left; _settings.Y = Top; }
        _settings.Save();
        _tray.Visible = false;
        _tray.Dispose();
        _trayIcon?.Dispose();
        _sensors.Dispose();
        base.OnFormClosed(e);
    }
}
