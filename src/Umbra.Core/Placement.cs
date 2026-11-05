namespace Umbra;

/// <summary>One monitor as placement sees it (pure data, so the maths can be unit tested).</summary>
public readonly record struct MonitorInfo(string Name, Rectangle Bounds, Rectangle WorkArea, bool Primary);

/// <summary>Per-monitor placement and the fullscreen test. Pure functions; each host reads the live monitors (Windows: <c>Screens</c>).</summary>
public static class Placement
{
    /// <summary>The monitor a window mostly sits on (the one containing its centre, else the largest overlap, else primary).</summary>
    public static MonitorInfo? Find(IReadOnlyList<MonitorInfo> screens, Rectangle rect)
    {
        var centre = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        foreach (var s in screens) if (s.Bounds.Contains(centre)) return s;
        MonitorInfo? best = null;
        long bestArea = 0;
        foreach (var s in screens)
        {
            var i = Rectangle.Intersect(s.Bounds, rect);
            long area = (long)i.Width * i.Height;
            if (i.Width > 0 && i.Height > 0 && area > bestArea) { best = s; bestArea = area; }
        }
        return best ?? screens.Where(s => s.Primary).Select(s => (MonitorInfo?)s).FirstOrDefault();
    }

    /// <summary>
    /// A stable text for a monitor setup: each monitor's device name, resolution and top-left position (so the arrangement
    /// counts), primary marked with "*", sorted by name so the order Windows lists them in doesn't matter. Work areas are
    /// left out on purpose (moving the taskbar is not a new setup). Empty when there are no monitors.
    /// </summary>
    public static string Fingerprint(IReadOnlyList<MonitorInfo> screens) =>
        string.Join(';', screens.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => $"{s.Name.ToUpperInvariant()}={s.Bounds.Width}x{s.Bounds.Height}@{s.Bounds.X},{s.Bounds.Y}{(s.Primary ? "*" : "")}"));

    /// <summary>How many monitors a <see cref="Fingerprint"/> describes.</summary>
    public static int FingerprintMonitors(string fingerprint) =>
        string.IsNullOrEmpty(fingerprint) ? 0 : fingerprint.Split(';').Length;

    /// <summary>True when the device name belongs to a connected monitor.</summary>
    public static bool Has(IReadOnlyList<MonitorInfo> screens, string name) =>
        screens.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Where the window goes: the saved monitor plus its offset from that monitor's top-left corner. A monitor that is
    /// gone falls back to the primary one with the same offset. The result is pulled inside the work area, so the
    /// widget always survives a monitor change or unplug.
    /// </summary>
    public static Point Resolve(IReadOnlyList<MonitorInfo> screens, string? monitor, int relX, int relY, Size size)
    {
        if (screens.Count == 0) return new Point(relX, relY);
        var m = screens.FirstOrDefault(s => monitor is not null && string.Equals(s.Name, monitor, StringComparison.OrdinalIgnoreCase));
        if (m.Name is null) m = screens.FirstOrDefault(s => s.Primary);
        if (m.Name is null) m = screens[0];
        var w = m.WorkArea;
        var x = m.Bounds.X + relX;
        var y = m.Bounds.Y + relY;
        x = Math.Max(w.Left, Math.Min(x, w.Right - size.Width));
        y = Math.Max(w.Top, Math.Min(y, w.Bottom - size.Height));
        return new Point(x, y);
    }

    /// <summary>
    /// Records a window's normal bounds (physical px) as a <see cref="SettingsPlacement"/>: the monitor it mostly sits on,
    /// its offset from that monitor's corner and its size, both in logical px (divided by <paramref name="scale"/>).
    /// </summary>
    public static SettingsPlacement Capture(IReadOnlyList<MonitorInfo> screens, Rectangle bounds, float scale, bool maximized, string? page, double zoom = 1.0)
    {
        var p = new SettingsPlacement { Maximized = maximized, Page = page, Zoom = zoom };
        if (scale <= 0 || Find(screens, bounds) is not { } m) return p;
        p.Monitor = m.Name;
        p.X = (int)Math.Round((bounds.X - m.Bounds.X) / scale);
        p.Y = (int)Math.Round((bounds.Y - m.Bounds.Y) / scale);
        p.Width = (int)Math.Round(bounds.Width / scale);
        p.Height = (int)Math.Round(bounds.Height / scale);
        return p;
    }

    /// <summary>
    /// The physical bounds to open the window at, or null (centre it) when nothing usable was saved or that monitor is
    /// gone. <paramref name="scaleOf"/> gives the DPI scale of the saved monitor. The size is raised to the minimum and
    /// the rectangle is pulled inside the work area, so a smaller or rearranged monitor still shows all of it.
    /// </summary>
    public static Rectangle? ResolveWindow(IReadOnlyList<MonitorInfo> screens, SettingsPlacement p, Func<MonitorInfo, float> scaleOf, Size minLogical)
    {
        if (p.Monitor is null || p.Width <= 0 || p.Height <= 0) return null;
        var found = screens.Where(s => string.Equals(s.Name, p.Monitor, StringComparison.OrdinalIgnoreCase)).Select(s => (MonitorInfo?)s).FirstOrDefault();
        if (found is not { } m) return null;
        var scale = scaleOf(m);
        if (scale <= 0) scale = 1f;
        var wa = m.WorkArea;
        var w = Math.Min(Math.Max((int)Math.Round(p.Width * scale), (int)Math.Round(minLogical.Width * scale)), wa.Width);
        var h = Math.Min(Math.Max((int)Math.Round(p.Height * scale), (int)Math.Round(minLogical.Height * scale)), wa.Height);
        var x = Math.Max(wa.Left, Math.Min(m.Bounds.X + (int)Math.Round(p.X * scale), wa.Right - w));
        var y = Math.Max(wa.Top, Math.Min(m.Bounds.Y + (int)Math.Round(p.Y * scale), wa.Bottom - h));
        return new Rectangle(x, y, w, h);
    }

    /// <summary>
    /// SHQueryUserNotificationState: 2 = busy (a fullscreen app), 3 = Direct3D fullscreen (a game),
    /// 4 = presentation mode. The other states (screensaver, quiet time, normal) are not a fullscreen app.
    /// </summary>
    public static bool IsFullscreenState(int state) => state is 2 or 3 or 4;
}
