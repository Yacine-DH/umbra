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

    /// <summary>How long a widget must stay hidden for a suspendable reason before the host suspends its WebView (#409).</summary>
    public static readonly TimeSpan SuspendDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether a hidden widget's reason for being hidden allows suspending it (#409): background group tabs and hide-on-fullscreen
    /// suspend after <see cref="SuspendDelay"/>; widgets hidden only by the hotkey, start-hidden or show-when rules stay live.
    /// </summary>
    public static bool CanSuspendWhenHidden(bool isShownTab, bool hiddenByFullscreen, bool widgetsHidden = false, bool startHidden = false, bool rulesAllow = true) =>
        !isShownTab || (hiddenByFullscreen && !widgetsHidden && !startHidden && rulesAllow);

    /// <summary>
    /// Whether a widget is eligible to be (or stay) suspended once <see cref="SuspendDelay"/> has elapsed: hidden for a
    /// suspendable reason (<see cref="CanSuspendWhenHidden"/>), not kept alive by <c>umbra.keepAlive(true)</c>, and not being captured.
    /// </summary>
    public static bool CanSuspend(bool visible, bool canSuspendWhenHidden, bool keepAlive, bool capturing) =>
        !visible && canSuspendWhenHidden && !keepAlive && !capturing;

    /// <summary>
    /// Whether a widget should be suspended now: <see cref="CanSuspend"/> holds and it has been eligible for at least <see cref="SuspendDelay"/>.
    /// </summary>
    public static bool ShouldSuspend(bool visible, bool canSuspendWhenHidden, bool keepAlive, bool capturing, TimeSpan hiddenFor) =>
        CanSuspend(visible, canSuspendWhenHidden, keepAlive, capturing) && hiddenFor >= SuspendDelay;
}
