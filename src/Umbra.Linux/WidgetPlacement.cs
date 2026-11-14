namespace Umbra;

/// <summary>
/// Where a widget window goes: the pure half of <c>WidgetWindow.ApplyBounds</c> on Windows, shared by the Linux pin backends.
/// All values are logical pixels in the coordinate space of the monitor rectangles.
/// </summary>
static class WidgetPlacement
{
    /// <summary>Margin from the work area's edge for a widget without a saved or default position.</summary>
    public const int Margin = 24;

    /// <summary>
    /// The top-left corner for a widget copy: its saved monitor and offset (survives monitor changes), else its saved absolute
    /// spot, else the manifest's default position, else the top-right corner of the primary work area. A spot that no monitor
    /// shows any more (a monitor was unplugged) falls back to that corner, so the widget is never lost off screen.
    /// </summary>
    public static Point Locate(WidgetSettings ws, WidgetManifest.PointSpec? manifestDefault, Size size, IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0) return new Point(ws.X ?? 0, ws.Y ?? 0);
        var primary = monitors.FirstOrDefault(m => m.Primary, monitors[0]).WorkArea;

        Point loc;
        if (ws.RelX is int rx && ws.RelY is int ry && ws.Monitor is not null)
            loc = Placement.Resolve(monitors, ws.Monitor, rx, ry, size);
        else if (ws.X is int x && ws.Y is int y) loc = new Point(x, y);
        else if (manifestDefault is { } d) loc = new Point(primary.Left + d.X, primary.Top + d.Y);
        else loc = new Point(primary.Right - size.Width - Margin, primary.Top + Margin);

        var rect = new Rectangle(loc, size);
        if (!monitors.Any(m => m.WorkArea.IntersectsWith(rect)))
            loc = new Point(primary.Right - size.Width - Margin, primary.Top + Margin);
        return loc;
    }

    /// <summary>Records a position on a copy: the absolute spot plus its monitor and the offset from that monitor's corner.</summary>
    public static void Remember(WidgetSettings ws, Rectangle bounds, IReadOnlyList<MonitorInfo> monitors)
    {
        ws.X = bounds.X; ws.Y = bounds.Y;
        if (Placement.Find(monitors, bounds) is MonitorInfo m)
        {
            ws.Monitor = m.Name;
            ws.RelX = bounds.X - m.Bounds.X;
            ws.RelY = bounds.Y - m.Bounds.Y;
        }
    }

    /// <summary>
    /// Where and how big a copy's window is in logical pixels on the monitors connected now (<see cref="Locate"/> for the
    /// spot). The size is the widget's logical size times its scale and never depends on a monitor's scale factor, so a
    /// monitor that vanished, or a scale that went from 2 to 1, gives the same size on the monitor that is left. The saved
    /// monitor is not touched: when it comes back, the copy goes back to it.
    /// </summary>
    public static Rectangle Layout(WidgetSettings ws, WidgetManifest.PointSpec? manifestDefault, Size logicalSize, IReadOnlyList<MonitorInfo> monitors) =>
        new(Locate(ws, manifestDefault, logicalSize, monitors), logicalSize);

    /// <summary>
    /// A logical rectangle in the display server's device pixels for a surface scale (X11 under GTK: the integer scale
    /// GDK applies; XWayland with native scaling: the scale it was given). Scales below 1 count as 1.
    /// </summary>
    public static Rectangle ToDevice(Rectangle logical, double scale)
    {
        var k = scale >= 1 ? scale : 1;
        int D(int v) => (int)Math.Round(v * k, MidpointRounding.AwayFromZero);
        return new Rectangle(D(logical.X), D(logical.Y), Math.Max(1, D(logical.Width)), Math.Max(1, D(logical.Height)));
    }

    /// <summary>
    /// A key for the display layout: the monitors (names, logical rectangles, primary) and their scale factors. When it
    /// changes (a monitor came or went, a resolution or scale changed, often across a suspend), every widget window and
    /// overlay is placed, sized and drawn again.
    /// </summary>
    public static string LayoutKey(IReadOnlyList<MonitorInfo> monitors, IReadOnlyList<double> scales) =>
        Placement.Fingerprint(monitors) + "|" + string.Join(',', scales.Select(s => s.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>Puts a copy on another monitor, keeping its offset from the monitor's corner (the margin if it had none).</summary>
    public static void MoveToMonitor(WidgetSettings ws, string monitor, Rectangle bounds, IReadOnlyList<MonitorInfo> monitors)
    {
        if (ws.RelX is null || ws.RelY is null)
        {
            var cur = Placement.Find(monitors, bounds);
            ws.RelX = cur is MonitorInfo c ? bounds.X - c.Bounds.X : Margin;
            ws.RelY = cur is MonitorInfo d ? bounds.Y - d.Bounds.Y : Margin;
        }
        ws.Monitor = monitor;
    }
}
