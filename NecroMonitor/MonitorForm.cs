using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace NecroMonitor;

public sealed class MonitorForm : Form
{
    // ── Win32 for always-on-top re-assertion ──
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    // ── WS_EX flags ──
    private const int WS_EX_TOOLWINDOW  = 0x00000080;   // hide from Alt+Tab
    private const int WS_EX_NOACTIVATE  = 0x08000000;   // never steal focus
    private const int WS_EX_LAYERED     = 0x00080000;   // layered window

    // ── State ──
    private readonly HardwareMonitor _hw = null!;    private readonly System.Windows.Forms.Timer _sensorTimer;
    private readonly System.Windows.Forms.Timer _topMostTimer;

    private Point _dragStart;
    private bool _dragging;

    // ── Cached sensor values for paint ──
    private float? _cpuTemp, _gpuTemp;
    private float _cpuLoad, _gpuLoad;

    // ── Colours ──
    private static readonly Color BgColor       = Color.FromArgb(220, 18, 18, 22);
    private static readonly Color BorderColor   = Color.FromArgb(100, 70, 70, 80);
    private static readonly Color TitleColor    = Color.FromArgb(200, 130, 255);
    private static readonly Color LabelColor    = Color.FromArgb(155, 155, 165);
    private static readonly Color DimColor      = Color.FromArgb(75, 75, 85);
    private static readonly Color CpuBarColor   = Color.FromArgb(90, 195, 255);
    private static readonly Color GpuBarColor   = Color.FromArgb(130, 255, 95);
    private static readonly Color TempGreen     = Color.FromArgb(90, 255, 140);
    private static readonly Color TempYellow    = Color.FromArgb(255, 215, 70);
    private static readonly Color TempRed       = Color.FromArgb(255, 75, 75);
    private static readonly Color BarBg         = Color.FromArgb(35, 35, 40);

    // ── Fonts (created once) ──
    private readonly Font _titleFont  = new("Segoe UI", 9f, FontStyle.Bold);
    private readonly Font _labelFont  = new("Segoe UI", 8.5f, FontStyle.Regular);
    private readonly Font _valueFont  = new("Consolas", 11f, FontStyle.Bold);
    private readonly Font _barFont    = new("Segoe UI", 7f, FontStyle.Bold);
    private readonly Font _footerFont = new("Segoe UI", 7f, FontStyle.Regular);

    private const int CornerRadius = 12;
    private const int WidgetWidth  = 230;
    private const int WidgetHeight = 118;

    // ═══════════════════════════════════════════
    //  Construction
    // ═══════════════════════════════════════════
    public MonitorForm()
    {
        // ── Form setup ──
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar   = false;
        TopMost         = true;
        DoubleBuffered  = true;
        Size            = new Size(WidgetWidth, WidgetHeight);
        StartPosition   = FormStartPosition.Manual;
        BackColor       = Color.Black;
        Opacity         = 0.92;

        // Position: top-right of primary screen
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Right - WidgetWidth - 16, wa.Top + 16);

        // ── Mouse drag ──
        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp   += OnMouseUp;

        // ── Context menu ──
        var ctx = new ContextMenuStrip();
        ctx.Items.Add("Reset position", null, (_, _) =>
        {
            var a = Screen.PrimaryScreen!.WorkingArea;
            Location = new Point(a.Right - WidgetWidth - 16, a.Top + 16);
        });
        ctx.Items.Add("Dump sensors to file", null, (_, _) =>
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "sensor_dump.txt");
                File.WriteAllText(path, _hw.DumpSensors());
                MessageBox.Show($"Sensor dump saved to:\n{path}", "NecroMonitor",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to write dump:\n{ex.Message}", "NecroMonitor",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        });
        ctx.Items.Add(new ToolStripSeparator());
        ctx.Items.Add("Exit", null, (_, _) => Application.Exit());
        ContextMenuStrip = ctx;

        // ── Hardware monitor ──
        _hw = new HardwareMonitor();

        // ── Timers ──
        _sensorTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        _sensorTimer.Tick += (_, _) => RefreshSensors();
        _sensorTimer.Start();

        _topMostTimer = new System.Windows.Forms.Timer { Interval = 4000 };
        _topMostTimer.Tick += (_, _) => ReassertTopMost();
        _topMostTimer.Start();

        RefreshSensors(); // initial read
    }

    // ═══════════════════════════════════════════
    //  Window style overrides
    // ═══════════════════════════════════════════
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW;   // hides from Alt+Tab
            cp.ExStyle |= WS_EX_NOACTIVATE;   // never steals focus
            return cp;
        }
    }

    // ═══════════════════════════════════════════
    //  Rounded-corner region clipping
    // ═══════════════════════════════════════════
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        using var path = MakeRoundedRect(new Rectangle(0, 0, Width, Height), CornerRadius);
        Region = new Region(path);
    }

    // ═══════════════════════════════════════════
    //  Sensor refresh
    // ═══════════════════════════════════════════
    private void RefreshSensors()
    {
        _hw.Update();
        _cpuTemp = _hw.CpuTemp;
        _gpuTemp = _hw.GpuTemp;
        _cpuLoad = _hw.CpuLoad;
        _gpuLoad = _hw.GpuLoad;
        Invalidate();   // trigger repaint
    }

    private void ReassertTopMost()
    {
        if (!IsDisposed && IsHandleCreated)
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    // ═══════════════════════════════════════════
    //  Paint
    // ═══════════════════════════════════════════
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode     = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);

        // ── Background + border ──
        using (var path = MakeRoundedRect(bounds, CornerRadius))
        {
            using var bgBrush = new SolidBrush(BgColor);
            g.FillPath(bgBrush, path);
            using var pen = new Pen(BorderColor, 1f);
            g.DrawPath(pen, path);
        }

        // ── Title ──
        using (var b = new SolidBrush(TitleColor))
            g.DrawString("\u2620  NECROMONITOR", _titleFont, b, 10, 7);

        // ── Separator ──
        using (var pen = new Pen(DimColor))
            g.DrawLine(pen, 10, 27, Width - 10, 27);

        // ── CPU row ──
        int y1 = 34;
        DrawRow(g, "CPU", _cpuTemp, _cpuLoad, CpuBarColor, y1);

        // ── GPU row ──
        int y2 = 62;
        DrawRow(g, "GPU", _gpuTemp, _gpuLoad, GpuBarColor, y2);

        // ── Footer ──
        using (var b = new SolidBrush(DimColor))
            g.DrawString(_hw.IsAvailable ? "drag to move  \u2022  right-click \u2192 exit"
                                         : "\u26A0 run as admin for sensor access",
                _footerFont, b, 10, Height - 19);
    }

    private void DrawRow(Graphics g, string label, float? temp, float load, Color barColor, int y)
    {
        // Label
        using (var b = new SolidBrush(LabelColor))
            g.DrawString(label, _labelFont, b, 12, y + 1);

        // Temperature
        DrawTemp(g, temp, 58, y - 1);

        // Load bar
        DrawLoadBar(g, load, 130, y + 3, 72, 14, barColor);
    }

    private void DrawTemp(Graphics g, float? temp, int x, int y)
    {
        if (temp is not float t)
        {
            using var grayBrush = new SolidBrush(Color.Gray);
            g.DrawString("N/A", _valueFont, grayBrush, x, y);
            return;
        }

        Color c = t switch
        {
            < 60  => TempGreen,
            < 85  => TempYellow,
            _     => TempRed
        };
        using var brush = new SolidBrush(c);
        g.DrawString($"{t:F0}°C", _valueFont, brush, x, y);
    }

    private void DrawLoadBar(Graphics g, float load, int x, int y, int w, int h, Color color)
    {
        // Background
        using (var bgBrush = new SolidBrush(BarBg))
        using (var path = MakeRoundedRect(new Rectangle(x, y, w, h), 5))
            g.FillPath(bgBrush, path);

        // Fill
        int fw = (int)(w * Math.Clamp(load, 0, 100) / 100f);
        if (fw > 0)
        {
            using var fill = new SolidBrush(Color.FromArgb(170, color.R, color.G, color.B));
            using var path = MakeRoundedRect(new Rectangle(x, y, Math.Max(fw, 6), h), 5);
            g.FillPath(fill, path);
        }

        // Percentage text centered in bar
        string pct = $"{load:F0}%";
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(pct, _barFont, Brushes.White, new RectangleF(x, y, w, h), sf);
    }

    // ═══════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════
    private static GraphicsPath MakeRoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ── Drag support ──
    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _dragging = true;
            _dragStart = e.Location;
        }
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (_dragging)
            Location = new Point(Location.X + e.X - _dragStart.X,
                                 Location.Y + e.Y - _dragStart.Y);
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        _dragging = false;
    }

    // ── Cleanup ──
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _sensorTimer.Stop();
        _topMostTimer.Stop();
        _hw.Dispose();
        _titleFont.Dispose();
        _labelFont.Dispose();
        _valueFont.Dispose();
        _barFont.Dispose();
        _footerFont.Dispose();
        base.OnFormClosing(e);
    }
}
