using System.Runtime.InteropServices;
using System.Text;

namespace Umbra;

/// <summary>The live Windows monitors, fullscreen state and top-level windows, as the pure <see cref="Placement"/> and <see cref="Occlusion"/> maths expect them.</summary>
static class Screens
{
    public static List<MonitorInfo> All() =>
        Screen.AllScreens.Select(s => new MonitorInfo(s.DeviceName, s.Bounds, s.WorkingArea, s.Primary)).ToList();

    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);

    /// <summary>True while a fullscreen app or game is in the foreground (false if Windows can't say).</summary>
    public static bool FullscreenAppActive() =>
        SHQueryUserNotificationState(out var s) == 0 && Placement.IsFullscreenState(s);

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out Native.RECT rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    const int DWMWA_CLOAKED = 14;

    static readonly uint OwnPid = (uint)Environment.ProcessId;

    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_SYSTEM_MINIMIZESTART = 0x0016, EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    const uint WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;

    /// <summary>
    /// Calls <paramref name="changed"/> on this (UI) thread when another app's window comes to the front or is minimized or
    /// restored, including Show desktop, so a covered check can run at once instead of on the next poll. Dispose to unhook.
    /// </summary>
    public static IDisposable WatchWindowChanges(Action changed) => new WindowWatch(changed);

    sealed class WindowWatch : IDisposable
    {
        readonly WinEventProc _proc; // kept alive: Windows calls it for as long as the hooks exist
        readonly IntPtr[] _hooks;

        public WindowWatch(Action changed)
        {
            _proc = (_, _, _, idObject, _, _, _) => { if (idObject == 0 /* OBJID_WINDOW */) changed(); };
            const uint flags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;
            _hooks = new[]
            {
                SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, flags),
                SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _proc, 0, 0, flags),
            };
        }

        public void Dispose()
        {
            foreach (var h in _hooks) if (h != IntPtr.Zero) UnhookWinEvent(h);
        }
    }

    /// <summary>
    /// Every visible top-level window for <see cref="Occlusion.CoveredMonitors"/>. Umbra's own windows and the shell's desktop
    /// (Progman, WorkerW) are marked ignored. Hidden windows are dropped before any other call, so a poll stays cheap.
    /// </summary>
    public static List<WindowSnapshot> TopLevelWindows()
    {
        var list = new List<WindowSnapshot>();
        var cls = new StringBuilder(32);
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || !GetWindowRect(hwnd, out var r)) return true;
            var bounds = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            if (bounds.Width <= 0 || bounds.Height <= 0) return true;
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            cls.Clear();
            GetClassName(hwnd, cls, cls.Capacity);
            var name = cls.ToString();
            var ex = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
            var clickThrough = (ex & Native.WS_EX_TRANSPARENT) != 0 && (ex & Native.WS_EX_LAYERED) != 0;
            var cloaked = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var c, sizeof(int)) == 0 && c != 0;
            list.Add(new WindowSnapshot(bounds, Visible: true, IsIconic(hwnd), cloaked, clickThrough,
                Ignored: pid == OwnPid || name is "Progman" or "WorkerW"));
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
