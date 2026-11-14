using Umbra.Linux;

namespace Umbra;

/// <summary>
/// Native Wayland pinning on GNOME (#187) through Umbra's GNOME Shell extension (<c>linux/gnome-extension</c>), which owns
/// <see cref="GnomeShell.BusName"/> on the session bus. Each window gets a random token as its title before it is realized and
/// is registered with <c>AddWindow</c>; the extension then pins it (desktop layer above the desktop icons, every workspace,
/// out of Alt+Tab, the overview and the dock) and moves it on request. Focus gating is the extension's: it hands the focus
/// back unless this host asked with <c>SetFocusable</c> right after a physical press, which it checks again itself.
/// The pointer for the drag, resize and zoom gates comes from the compositor (<c>GetPointer</c>).
/// If the extension goes away while Umbra runs, the windows become plain windows until it comes back (they are then
/// registered again) or Umbra restarts on XWayland.
/// </summary>
sealed class GnomeExtensionPin : IDesktopPin
{
    readonly IntPtr _bus;
    readonly Dictionary<IntPtr, Entry> _windows = new(); // by Gtk.Window handle
    List<Rectangle> _stage = new();
    Rectangle? _work;
    bool _present = true;

    sealed record Entry(Gtk.Window Window, string Token, bool Overlay)
    {
        public Point? At { get; set; }
    }

    GnomeExtensionPin(IntPtr bus)
    {
        _bus = bus;
        ReadMonitors();
        DBus.Subscribe(bus, GnomeShell.BusName, GnomeShell.Interface, "MonitorsChanged", GnomeShell.ObjectPath, (_, _) => ReadMonitors());
        DBus.Watch(bus, GnomeShell.BusName, present =>
        {
            if (present == _present) return;
            _present = present;
            if (!present) { Log.Write("The Umbra GNOME Shell extension went away: widgets are plain windows until it is back."); return; }
            Log.Write("The Umbra GNOME Shell extension is back: registering the windows again.");
            ReadMonitors();
            foreach (var e in _windows.Values.ToList()) { Register(e); if (e.At is { } at) SendMove(e, at); }
        });
    }

    public string Name => "gnome-extension";

    /// <summary>
    /// True when the extension answers <c>GetVersion</c> with this host's protocol version. Runs before GTK opens the
    /// display (it decides between native Wayland and XWayland), so it is one short blocking call.
    /// </summary>
    public static bool Probe()
    {
        var bus = DBus.Session;
        if (bus == IntPtr.Zero) return false;
        try
        {
            using var reply = DBus.Call(bus, GnomeShell.BusName, GnomeShell.ObjectPath, GnomeShell.Interface, "GetVersion", null, 1000);
            using var v = reply[0];
            var ok = GnomeShell.Compatible(v.UInt);
            if (!ok) Log.Write($"The Umbra GNOME Shell extension speaks protocol {v.UInt}, this Umbra {GnomeShell.Version}: not used.");
            return ok;
        }
        catch (DBusException) { return false; } // not installed, disabled, or not GNOME
    }

    /// <summary>The backend when <see cref="Program"/> chose it and GDK really runs on Wayland, else null.</summary>
    public static GnomeExtensionPin? TryCreate()
    {
        if (Program.Backend != PinBackend.GnomeExtension || Gdk.Display.GetDefault() is null) return null; // chosen means GDK is on Wayland
        return DBus.Session is var bus && bus != IntPtr.Zero ? new GnomeExtensionPin(bus) : null;
    }

    static IntPtr H(Gtk.Window window) => window.Handle.DangerousGetHandle();

    public void Init(Gtk.Window window, bool overlay = false)
    {
        var entry = new Entry(window, GnomeShell.NewToken(), overlay);
        _windows[H(window)] = entry;
        // The title is how the extension recognises the window; it is set before realize, so the first title it sees.
        window.SetTitle(GnomeShell.Title(entry.Token));
        Register(entry);
        window.OnUnrealize += (_, _) => Forget(window);
    }

    void Register(Entry e) => Send("AddWindow", GnomeShell.AddArgs(e.Token, e.Overlay));

    void Forget(Gtk.Window window)
    {
        if (!_windows.Remove(H(window), out var e)) return;
        Send("RemoveWindow", GnomeShell.TokenArgs(e.Token));
    }

    /// <summary>One call to the extension; a failure is logged, never thrown (the extension may be gone).</summary>
    DBus.Value? Send(string method, string? args, int timeoutMs = 1000)
    {
        if (!_present) return null;
        try { return DBus.Call(_bus, GnomeShell.BusName, GnomeShell.ObjectPath, GnomeShell.Interface, method, args, timeoutMs); }
        catch (DBusException ex) { Log.Verbose($"GNOME extension {method} failed: {ex.Message}"); return null; }
    }

    public void Prepare(Gtk.Window window, bool overlay = false) { }

    /// <summary>The extension places a window when it is shown; this repeats the position in case it was not known then.</summary>
    public void Mapped(Gtk.Window window, bool overlay = false)
    {
        if (_windows.TryGetValue(H(window), out var e) && e.At is { } at) SendMove(e, at);
    }

    public void Move(Gtk.Window window, int x, int y)
    {
        if (!_windows.TryGetValue(H(window), out var e)) return;
        e.At = new Point(x, y);
        SendMove(e, e.At.Value);
    }

    void SendMove(Entry e, Point at) => Send("MoveWindow", GnomeShell.MoveArgs(e.Token, GnomeShell.Map(at, GdkMonitors(), _stage)))?.Dispose();

    public PointerState? Pointer()
    {
        using var r = Send("GetPointer", null, 300);
        if (r is null) return null;
        using var known = r[0];
        using var x = r[1];
        using var y = r[2];
        using var mask = r[3];
        var at = GnomeShell.Map(new Point(x.Int, y.Int), _stage, GdkMonitors());
        return GnomeShell.Pointer(known.Bool, at, mask.UInt);
    }

    /// <summary>
    /// Asks the extension to let the window take the focus (it also gives it the focus), or never again on its own. The
    /// host asks only right after a physical press on the window; the extension checks that press again itself and turns
    /// the grant back when the window loses the focus or never got it.
    /// </summary>
    public void SetFocusable(Gtk.Window window, bool on)
    {
        if (!_windows.TryGetValue(H(window), out var e)) return;
        using var r = Send("SetFocusable", GnomeShell.FocusArgs(e.Token, on));
        if (on && r is not null)
        {
            using var granted = r[0];
            if (!granted.Bool) Log.Verbose("The GNOME extension refused the focus (no press it saw on that window).");
        }
    }

    /// <summary>The grant above already focused the window.</summary>
    public void Focus(Gtk.Window window) { }

    public Rectangle? PrimaryWorkArea()
    {
        if (_work is not { } w) return null;
        var gdk = GdkMonitors();
        var a = GnomeShell.Map(w.Location, _stage, gdk);
        var b = GnomeShell.Map(new Point(w.Right, w.Bottom), _stage, gdk);
        return Rectangle.FromLTRB(a.X, a.Y, b.X, b.Y);
    }

    void ReadMonitors()
    {
        using var r = Send("GetMonitors", null);
        if (r is null) return;
        using var primary = r[0];
        using var list = r[1];
        var monitors = new List<GnomeShell.Monitor>();
        for (var i = 0; i < list.Count; i++)
        {
            using var m = list[i];
            int F(int k) { using var v = m[k]; return v.Int; }
            monitors.Add(new GnomeShell.Monitor(new Rectangle(F(0), F(1), F(2), F(3)), new Rectangle(F(4), F(5), F(6), F(7))));
        }
        _stage = monitors.Select(m => m.Bounds).ToList();
        _work = GnomeShell.PrimaryWorkArea(primary.Int, monitors);
    }

    static List<Rectangle> GdkMonitors()
    {
        var list = new List<Rectangle>();
        if (Gdk.Display.GetDefault() is not { } display) return list;
        var monitors = display.GetMonitors();
        for (uint i = 0; i < monitors.GetNItems(); i++)
            if (monitors.GetObject(i) is Gdk.Monitor m) { m.GetGeometry(out var g); list.Add(new Rectangle(g.X, g.Y, g.Width, g.Height)); }
        return list;
    }
}
