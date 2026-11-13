namespace Umbra;

/// <summary>The desktop pin backends of the Linux host, in the order <see cref="LayerShellLayout.Choose"/> prefers them.</summary>
enum PinBackend
{
    /// <summary>Native Wayland through wlr-layer-shell (KWin, Sway, Hyprland...): <see cref="LayerShellPin"/>.</summary>
    LayerShell,
    /// <summary>X11 window manager hints, on an X session or under XWayland: <see cref="X11Pin"/>.</summary>
    X11,
    /// <summary>Wayland without layer-shell and without an X server: plain windows (<see cref="NoPin"/>).</summary>
    None,
}

/// <summary>
/// The pure half of <see cref="LayerShellPin"/> (#186): which backend a session gets, where a layer surface goes for a
/// placement, and how a pointer position read inside a moving surface becomes a desktop position. No GTK, no Wayland.
/// </summary>
static class LayerShellLayout
{
    /// <summary>
    /// The backend for this session, decided before GTK opens the display. An explicit <c>GDK_BACKEND</c> wins (x11 means X11,
    /// wayland means layer-shell if the compositor has it). <c>UMBRA_PIN_BACKEND=x11</c> keeps XWayland on a layer-shell
    /// compositor. Otherwise a Wayland session uses layer-shell when the library loaded and the compositor offers
    /// <c>zwlr_layer_shell_v1</c>, else XWayland when there is an X server (GNOME until #187), else nothing.
    /// </summary>
    public static PinBackend Choose(string? gdkBackend, string? pinOverride, bool wayland, bool x11, bool libraryLoaded, bool compositorHasLayerShell)
    {
        var layerShell = wayland && libraryLoaded && compositorHasLayerShell;
        if (!string.IsNullOrEmpty(gdkBackend))
        {
            var first = gdkBackend.Split(',')[0].Trim().ToLowerInvariant();
            if (first == "x11") return x11 ? PinBackend.X11 : PinBackend.None;
            if (first == "wayland") return layerShell && !IsX11(pinOverride) ? PinBackend.LayerShell : PinBackend.None;
        }
        if (layerShell && !IsX11(pinOverride)) return PinBackend.LayerShell;
        return x11 ? PinBackend.X11 : PinBackend.None;
    }

    /// <summary>True for "x11" (any case, spaces ignored).</summary>
    public static bool IsX11(string? value) => string.Equals(value?.Trim(), "x11", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The output and margins that put a layer surface anchored top-left at <paramref name="at"/> (logical desktop
    /// coordinates, like the GDK monitor rectangles): the monitor that holds the most of the window, else the one holding its
    /// top-left corner, else the nearest. Margins are from that output's corner (exclusive zone -1, so panels do not shift it)
    /// and never negative: a layer surface cannot sit partly on a neighbouring output.
    /// </summary>
    public static (int Monitor, int Left, int Top) Place(Point at, Size size, IReadOnlyList<Rectangle> monitors)
    {
        if (monitors.Count == 0) return (-1, Math.Max(0, at.X), Math.Max(0, at.Y));
        var rect = new Rectangle(at, size);
        var best = -1;
        long bestArea = 0;
        for (var i = 0; i < monitors.Count; i++)
        {
            var overlap = Rectangle.Intersect(monitors[i], rect);
            long area = overlap.IsEmpty ? 0 : (long)overlap.Width * overlap.Height;
            if (area > bestArea) { bestArea = area; best = i; }
        }
        if (best < 0) best = Nearest(at, monitors);
        var m = monitors[best];
        return (best, Math.Max(0, at.X - m.X), Math.Max(0, at.Y - m.Y));
    }

    static int Nearest(Point p, IReadOnlyList<Rectangle> monitors)
    {
        var best = 0;
        long bestDist = long.MaxValue;
        for (var i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            long dx = p.X < m.Left ? m.Left - p.X : p.X >= m.Right ? p.X - m.Right + 1 : 0;
            long dy = p.Y < m.Top ? m.Top - p.Y : p.Y >= m.Bottom ? p.Y - m.Bottom + 1 : 0;
            if (dx * dx + dy * dy < bestDist) { bestDist = dx * dx + dy * dy; best = i; }
        }
        return best;
    }
}

/// <summary>
/// Turns the pointer position Wayland gives (relative to the surface under it) into a desktop position. A surface the host
/// just moved reports the old relative position until the compositor sends the next motion, so adding it to the new origin
/// would run away during a drag. A sample is therefore converted once, with the origin it arrived with, and reused until the
/// relative position or the surface changes: a stale sample can overshoot one step, which the next one corrects.
/// </summary>
sealed class PointerLatch
{
    IntPtr _surface;
    double _relX = double.NaN, _relY = double.NaN;
    Point _at;

    /// <summary>The desktop position for this sample; <paramref name="origin"/> is where the host last put that surface.</summary>
    public Point Resolve(IntPtr surface, double relX, double relY, Point origin)
    {
        if (surface != _surface || relX != _relX || relY != _relY)
        {
            _surface = surface; _relX = relX; _relY = relY;
            _at = new Point(origin.X + (int)Math.Floor(relX), origin.Y + (int)Math.Floor(relY));
        }
        return _at;
    }
}
