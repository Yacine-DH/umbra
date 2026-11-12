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
