using System.Runtime.InteropServices;

namespace Umbra;

/// <summary>
/// How widget windows are tied to the desktop on this session: placed at a position, kept below normal windows, out of the
/// taskbar and on every workspace. The Linux counterpart of <c>DesktopPin.cs</c> on Windows (Progman owner, <c>HWND_BOTTOM</c>).
/// </summary>
interface IDesktopPin
{
    /// <summary>Short name for the log and <c>umbractl status</c>: "layer-shell", "x11" or "none".</summary>
    string Name { get; }

    /// <summary>
    /// Call once per window before it is realized: a backend that turns the window into another kind of surface (layer-shell)
    /// has to do it before GTK creates the surface. <paramref name="overlay"/> as for <see cref="Prepare"/>.
    /// </summary>
    void Init(Gtk.Window window, bool overlay = false) { }

    /// <summary>
    /// Call once per window after it was realized and before it is shown. <paramref name="overlay"/> is for the host's own
    /// small windows (edit pill, snap guides, tab strip): just above the widgets, still below normal windows.
    /// </summary>
    void Prepare(Gtk.Window window, bool overlay = false);

    /// <summary>Call each time the window was mapped: (re)asserts the hints a window manager only reads from a mapped window.</summary>
    void Mapped(Gtk.Window window, bool overlay = false);

    /// <summary>
    /// Where the pointer is and which buttons and modifier keys are physically down, straight from the display server, or null
    /// when the session cannot say. This is what a page cannot fake: the gates for drag, resize, zoom and nudge read it.
    /// </summary>
    PointerState? Pointer();

    /// <summary>Lets the window take keyboard focus (true, only right after a physical press) or never on its own (false, the normal state).</summary>
    void SetFocusable(Gtk.Window window, bool on);

    /// <summary>Gives the window the keyboard focus now.</summary>
    void Focus(Gtk.Window window);

    /// <summary>Moves the window so its top-left corner is at this point (logical pixels, like the GDK monitor rectangles).</summary>
    void Move(Gtk.Window window, int x, int y);

    /// <summary>The usable area of the primary monitor (without panels), or null when the session does not say.</summary>
    Rectangle? PrimaryWorkArea();
}

/// <summary>The pointer as the display server reports it (logical pixels).</summary>
readonly record struct PointerState(Point At, bool ButtonDown, bool Shift, bool Ctrl);

/// <summary>No pinning: plain windows the compositor places (a Wayland session without layer-shell and without XWayland).</summary>
sealed class NoPin : IDesktopPin
{
    public string Name => "none";
    public void Prepare(Gtk.Window window, bool overlay = false) { }
    public void Mapped(Gtk.Window window, bool overlay = false) { }
    public PointerState? Pointer() => null;
    public void SetFocusable(Gtk.Window window, bool on) { }
    public void Focus(Gtk.Window window) { }
    public void Move(Gtk.Window window, int x, int y) { }
    public Rectangle? PrimaryWorkArea() => null;
}

/// <summary>
/// Pinning through the X11 window manager hints (EWMH), which GNOME (Mutter) and KDE (KWin) also honour for XWayland windows:
/// window type DESKTOP (or UTILITY kept BELOW), sticky, skipped by taskbar and pager, never focused on its own.
/// </summary>
sealed unsafe partial class X11Pin : IDesktopPin
{
    const string X11 = "libX11.so.6";
    const string Gtk4 = "libgtk-4.so.1";

    [LibraryImport(Gtk4)] private static partial IntPtr gdk_x11_display_get_xdisplay(IntPtr display);
    [LibraryImport(Gtk4)] private static partial nuint gdk_x11_surface_get_xid(IntPtr surface);
    [LibraryImport(Gtk4)] private static partial nuint gdk_x11_display_get_xrootwindow(IntPtr display);
    [LibraryImport(Gtk4)] private static partial nuint gdk_x11_display_get_type();
    [LibraryImport("libgobject-2.0.so.0")] private static partial int g_type_check_instance_is_a(IntPtr instance, nuint type);

    [LibraryImport(X11, StringMarshalling = StringMarshalling.Utf8)] private static partial nuint XInternAtom(IntPtr display, string name, int onlyIfExists);
    [LibraryImport(X11)] private static partial int XChangeProperty(IntPtr display, nuint window, nuint property, nuint type, int format, int mode, nuint* data, int count);
    [LibraryImport(X11)] private static partial int XMoveWindow(IntPtr display, nuint window, int x, int y);
    [LibraryImport(X11)] private static partial int XLowerWindow(IntPtr display, nuint window);
    [LibraryImport(X11)] private static partial int XFlush(IntPtr display);
    [LibraryImport(X11)] private static partial int XSendEvent(IntPtr display, nuint window, int propagate, nint mask, XClientMessageEvent* e);
    [LibraryImport(X11)] private static partial int XSetWMHints(IntPtr display, nuint window, XWMHints* hints);
    [LibraryImport(X11)] private static partial int XSetWMNormalHints(IntPtr display, nuint window, XSizeHints* hints);
    [LibraryImport(X11)] private static partial int XGetWindowProperty(IntPtr display, nuint window, nuint property, nint offset, nint length, int delete,
        nuint reqType, out nuint actualType, out int actualFormat, out nuint items, out nuint bytesAfter, out IntPtr data);
    [LibraryImport(X11)] private static partial int XFree(IntPtr data);
    [LibraryImport(X11)] private static partial int XRaiseWindow(IntPtr display, nuint window);
    [LibraryImport(X11)] private static partial int XSetInputFocus(IntPtr display, nuint window, int revertTo, nuint time);
    [LibraryImport(X11)] private static partial int XQueryPointer(IntPtr display, nuint window, out nuint root, out nuint child,
        out int rootX, out int rootY, out int winX, out int winY, out uint mask);

    [StructLayout(LayoutKind.Sequential)]
    struct XClientMessageEvent
    {
        public int Type; public nuint Serial; public int SendEvent; public IntPtr Display; public nuint Window;
        public nuint MessageType; public int Format; public nint L0, L1, L2, L3, L4;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XWMHints
    {
        public nint Flags; public int Input; public int InitialState; public nuint IconPixmap, IconWindow;
        public int IconX, IconY; public nuint IconMask, WindowGroup;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XSizeHints
    {
        public nint Flags; public int X, Y, Width, Height, MinWidth, MinHeight, MaxWidth, MaxHeight, WidthInc, HeightInc;
        public int MinAspectX, MinAspectY, MaxAspectX, MaxAspectY, BaseWidth, BaseHeight, WinGravity;
    }

    const int PropModeReplace = 0, ClientMessage = 33;
    const nint SubstructureRedirectMask = 1 << 20, SubstructureNotifyMask = 1 << 19;
    const nint InputHint = 1, USPosition = 1, PPosition = 4;
    const nuint XaAtom = 4, XaCardinal = 6;
    const nuint AllDesktops = 0xFFFFFFFF;

    readonly IntPtr _display;
    readonly nuint _root;
    readonly bool _utility;

    X11Pin(IntPtr display, nuint root, bool utility) { _display = display; _root = root; _utility = utility; }

    public string Name => _utility ? "x11 (utility, below)" : "x11";

    /// <summary>
    /// The X11 backend when GDK runs on X11 (a real X session or XWayland), else null.
    /// <c>UMBRA_X11_WINDOW_TYPE=utility</c> uses a UTILITY window kept BELOW instead of the DESKTOP type, for a window manager
    /// that draws DESKTOP windows behind its wallpaper or desktop icons.
    /// </summary>
    public static X11Pin? TryCreate()
    {
        if (Gdk.Display.GetDefault() is not { } display) return null;
        var handle = display.Handle.DangerousGetHandle();
        try { if (g_type_check_instance_is_a(handle, gdk_x11_display_get_type()) == 0) return null; }
        catch (EntryPointNotFoundException) { return null; } // a GTK built without the X11 backend
        var utility = string.Equals(Environment.GetEnvironmentVariable("UMBRA_X11_WINDOW_TYPE"), "utility", StringComparison.OrdinalIgnoreCase);
        return new X11Pin(gdk_x11_display_get_xdisplay(handle), gdk_x11_display_get_xrootwindow(handle), utility);
    }

    nuint Atom(string name) => XInternAtom(_display, name, 0);

    static nuint Xid(Gtk.Window window) =>
        window.GetSurface() is { } surface ? gdk_x11_surface_get_xid(surface.Handle.DangerousGetHandle()) : 0;

    /// <summary>The integer scale GDK applies on X11: X11 coordinates are device pixels, GTK's are logical.</summary>
    static int Scale(Gtk.Window window) => Math.Max(1, window.GetSurface()?.GetScaleFactor() ?? 1);

    /// <summary>A widget is a DESKTOP window (or UTILITY kept BELOW); an overlay is always UTILITY kept BELOW, so it is above the desktop layer.</summary>
    string[] StatesFor(bool overlay) => _utility || overlay
        ? ["_NET_WM_STATE_BELOW", "_NET_WM_STATE_STICKY", "_NET_WM_STATE_SKIP_TASKBAR", "_NET_WM_STATE_SKIP_PAGER"]
        : ["_NET_WM_STATE_STICKY", "_NET_WM_STATE_SKIP_TASKBAR", "_NET_WM_STATE_SKIP_PAGER"];

    public void Prepare(Gtk.Window window, bool overlay = false)
    {
        var xid = Xid(window);
        if (xid == 0) return;
        // Before the first map a window manager reads these as properties.
        SetType(xid, overlay);
        var states = StatesFor(overlay).Select(Atom).ToArray();
        fixed (nuint* p = states) XChangeProperty(_display, xid, Atom("_NET_WM_STATE"), XaAtom, 32, PropModeReplace, p, states.Length);
        var all = AllDesktops;
        XChangeProperty(_display, xid, Atom("_NET_WM_DESKTOP"), XaCardinal, 32, PropModeReplace, &all, 1);
        NoInput(xid);
        XFlush(_display);
    }

    void SetType(nuint xid, bool overlay)
    {
        var type = Atom(_utility || overlay ? "_NET_WM_WINDOW_TYPE_UTILITY" : "_NET_WM_WINDOW_TYPE_DESKTOP");
        XChangeProperty(_display, xid, Atom("_NET_WM_WINDOW_TYPE"), XaAtom, 32, PropModeReplace, &type, 1);
    }

    /// <summary>The window never takes keyboard focus on its own (the Linux <c>WS_EX_NOACTIVATE</c>); focus after a real click is #183.</summary>
    void NoInput(nuint xid)
    {
        var hints = new XWMHints { Flags = InputHint, Input = 0 };
        XSetWMHints(_display, xid, &hints);
    }

    public void Mapped(Gtk.Window window, bool overlay = false)
    {
        var xid = Xid(window);
        if (xid == 0) return;
        // GTK wrote its own window type (NORMAL) and WM_HINTS just before it asked for the map. The window manager only reads
        // them when it handles that request, which is after this runs, so ours are the ones it sees.
        SetType(xid, overlay);
        // Once mapped, state changes go through the window manager as client messages (EWMH).
        var add = StatesFor(overlay).Select(Atom).ToArray();
        for (var i = 0; i < add.Length; i += 2)
            SendRoot(xid, "_NET_WM_STATE", 1 /* _NET_WM_STATE_ADD */, (nint)add[i], i + 1 < add.Length ? (nint)add[i + 1] : 0, 1);
        SendRoot(xid, "_NET_WM_DESKTOP", unchecked((nint)AllDesktops), 1, 0, 0);
        NoInput(xid);
        if (overlay) XRaiseWindow(_display, xid); // above the widgets when both are in the "below" layer
        else XLowerWindow(_display, xid);
        XFlush(_display);
    }

    public PointerState? Pointer()
    {
        if (XQueryPointer(_display, _root, out _, out _, out var x, out var y, out _, out _, out var mask) == 0) return null;
        var k = Math.Max(1, (Gdk.Display.GetDefault()?.GetMonitors().GetObject(0) as Gdk.Monitor)?.GetScaleFactor() ?? 1);
        const uint ShiftMask = 1, ControlMask = 4, Buttons = (1 << 8) | (1 << 9) | (1 << 10);
        return new PointerState(new Point(x / k, y / k), (mask & Buttons) != 0, (mask & ShiftMask) != 0, (mask & ControlMask) != 0);
    }

    public void SetFocusable(Gtk.Window window, bool on)
    {
        var xid = Xid(window);
        if (xid == 0) return;
        var hints = new XWMHints { Flags = InputHint, Input = on ? 1 : 0 };
        XSetWMHints(_display, xid, &hints);
        XFlush(_display);
    }

    public void Focus(Gtk.Window window)
    {
        var xid = Xid(window);
        if (xid == 0) return;
        XSetInputFocus(_display, xid, 2 /* RevertToParent */, 0 /* CurrentTime */);
        XFlush(_display);
    }

    void SendRoot(nuint xid, string message, nint l0, nint l1, nint l2, nint l3)
    {
        var e = new XClientMessageEvent
        {
            Type = ClientMessage, Display = _display, Window = xid, MessageType = Atom(message), Format = 32,
            L0 = l0, L1 = l1, L2 = l2, L3 = l3,
        };
        XSendEvent(_display, _root, 0, SubstructureRedirectMask | SubstructureNotifyMask, &e);
    }

    public void Move(Gtk.Window window, int x, int y)
    {
        var xid = Xid(window);
        if (xid == 0) return;
        var k = Scale(window);
        // "The user chose this position": without it a window manager may place the window itself when it maps.
        var hints = new XSizeHints { Flags = USPosition | PPosition, X = x * k, Y = y * k };
        XSetWMNormalHints(_display, xid, &hints);
        XMoveWindow(_display, xid, x * k, y * k);
        XFlush(_display);
    }

    public Rectangle? PrimaryWorkArea()
    {
        // _NET_WORKAREA: x, y, width, height per desktop, in device pixels. The first desktop's stands for all.
        if (XGetWindowProperty(_display, _root, Atom("_NET_WORKAREA"), 0, 4, 0, XaCardinal, out _, out var format, out var items, out _, out var data) != 0
            || data == IntPtr.Zero) return null;
        try
        {
            if (format != 32 || items < 4) return null;
            var v = (nuint*)data; // format 32 arrives as C longs
            var k = Math.Max(1, (Gdk.Display.GetDefault()?.GetMonitors().GetObject(0) as Gdk.Monitor)?.GetScaleFactor() ?? 1);
            var area = new Rectangle((int)v[0] / k, (int)v[1] / k, (int)v[2] / k, (int)v[3] / k);
            return area.Width > 0 && area.Height > 0 ? area : null;
        }
        finally { XFree(data); }
    }
}
