namespace Umbra;

/// <summary>
/// One top-level window as the covered-desktop test sees it (pure data, so the rule can be unit tested).
/// <paramref name="Ignored"/>: Umbra's own windows and the shell's desktop windows (Progman, WorkerW), which never hide widgets.
/// <paramref name="ClickThrough"/>: a layered, mouse-transparent window such as a game or GPU overlay; it draws over the screen but rarely hides it.
/// </summary>
public readonly record struct WindowSnapshot(Rectangle Bounds, bool Visible, bool Minimized, bool Cloaked, bool ClickThrough, bool Ignored);

/// <summary>
/// "Is the desktop under this widget hidden?" (#409). Widgets sit at the bottom of the z-order, so any other window that fills a
/// monitor's work area hides every widget on it: a maximized window, a fullscreen app or game, or a window sized to the screen.
/// Chromium does not notice that occlusion by itself, so the host stops drawing those widgets (their timers keep running).
/// </summary>
public static class Occlusion
{
    /// <summary>Names of the monitors whose desktop is hidden by some window, whether or not it is the foreground one.</summary>
    public static HashSet<string> CoveredMonitors(IReadOnlyList<MonitorInfo> screens, IEnumerable<WindowSnapshot> windows)
    {
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in windows)
        {
            if (!w.Visible || w.Minimized || w.Cloaked || w.ClickThrough || w.Ignored || w.Bounds.Width <= 0 || w.Bounds.Height <= 0) continue;
            foreach (var s in screens)
                if (Covers(w.Bounds, s)) covered.Add(s.Name);
            if (covered.Count == screens.Count) break;
        }
        return covered;
    }

    /// <summary>
    /// A window hides a monitor's desktop when it contains its whole work area. A maximized window overhangs the work area by its
    /// resize border, and a fullscreen one fills the whole monitor, so both match; a window that merely spans two monitors does not.
    /// </summary>
    public static bool Covers(Rectangle window, MonitorInfo screen)
    {
        var area = screen.WorkArea.Width > 0 && screen.WorkArea.Height > 0 ? screen.WorkArea : screen.Bounds;
        return window.Contains(area);
    }

    /// <summary>
    /// Whether a shown widget should draw. A covered widget stops drawing, except in edit mode (the user is arranging widgets),
    /// during Peek (widgets are raised above the covering window) and while a capture needs its pixels.
    /// </summary>
    public static bool ShouldDraw(bool covered, bool editMode, bool peeking, bool capturing) =>
        !covered || editMode || peeking || capturing;
}
