using Umbra.Linux;

namespace Umbra;

/// <summary>
/// Native Wayland pinning on GNOME (#187) through Umbra's GNOME Shell extension (<c>linux/gnome-extension</c>), which owns
/// <see cref="GnomeShell.BusName"/> on the session bus. Each window gets a random token as its title before it is realized and
/// is registered with <c>AddWindow</c>; the extension then pins it (desktop layer above the desktop icons, every workspace,
/// out of Alt+Tab, the overview and the dock) and moves it on request. Focus gating is the extension's: it hands the focus
/// back unless this host asked with <c>SetFocusable</c> right after a physical press, which it checks again itself.
/// The pointer for the drag, resize and zoom gates comes from the compositor (<c>GetPointer</c>).
/// If the extension goes away while Umbra runs, its windows would become plain windows (above the user's apps, in Alt+Tab,
/// focused when mapped), so they are hidden at once; if it is back within <see cref="ReturnMs"/> (a reload) they are
/// registered and shown again, else <see cref="Lost"/> fires and the host restarts on XWayland.
/// </summary>
sealed class GnomeExtensionPin : IDesktopPin
{
    readonly IntPtr _bus;
    readonly Dictionary<IntPtr, Entry> _windows = new(); // by Gtk.Window handle
    List<GnomeShell.Output> _stage = new();
    Rectangle? _work;
    bool _present = true;
    long _lostAt;
    readonly HashSet<Gtk.Window> _hidden = new();

    /// <summary>How long the extension may be gone (disabled and enabled again) before the host gives up on it.</summary>
    public const uint ReturnMs = 3000;

    /// <summary>The extension stayed away: the host restarts on XWayland (<see cref="App.RestartOnXWayland"/>).</summary>
    public event Action? Lost;

    sealed class Entry(Gtk.Window window, string token, bool overlay)
    {
        public Gtk.Window Window { get; } = window;
        public string Token { get; set; } = token;
        public bool Overlay { get; } = overlay;
        public Point? At { get; set; }
        /// <summary>AddWindow returned (the extension knows the token and the caller's pid): the window may be shown.</summary>
        public bool Registered { get; set; }
    }

    GnomeExtensionPin(IntPtr bus)
    {
        _bus = bus;
        ReadMonitors();
        DBus.Subscribe(bus, GnomeShell.BusName, GnomeShell.Interface, "MonitorsChanged", GnomeShell.ObjectPath, (_, _) => ReadMonitors());
        DBus.Watch(bus, GnomeShell.BusName, present =>
        {
            if (present == _present) return;
            if (!present) { _present = false; OnGone(); return; }
            OnBack();
        });
    }

    bool _lostFired;

    void FireLost(string why)
    {
        if (_lostFired) return;
        _lostFired = true;
        Log.Write($"{why}: restarting on XWayland.");
        Lost?.Invoke();
    }

    /// <summary>
    /// The bus name has an owner again. It counts as the extension only when it answers this host's protocol version and
    /// takes every window back (AddWindow returns once it knows this process's pid); only then are the hidden windows
    /// shown. Any other owner (a process that grabbed the free name) gets nothing: the windows stay hidden, and the host
    /// restarts on XWayland.
    /// </summary>
    void OnBack()
    {
        if (_lostFired) return;
        if (!ProbeVersion(_bus)) { FireLost("The Umbra GNOME Shell bus name came back, but not as this Umbra's extension"); return; }
        _present = true;
        Log.Write("The Umbra GNOME Shell extension is back: registering the windows again.");
        ReadMonitors();
        foreach (var e in _windows.Values) e.Registered = false;
        foreach (var e in _windows.Values.ToList())
        {
            if (!Register(e)) { _present = false; FireLost("The Umbra GNOME Shell extension did not take a window back"); return; }
            if (e.At is { } at) SendMove(e, at);
        }
        foreach (var w in _hidden.ToList()) w.SetVisible(true);
        _hidden.Clear();
    }

    public string Name => "gnome-extension";

    /// <summary>Hides every window at once, then gives the extension <see cref="ReturnMs"/> to come back.</summary>
    void OnGone()
    {
        Log.Write("The Umbra GNOME Shell extension went away: widgets are hidden until it is back.");
        foreach (var e in _windows.Values) Hide(e.Window);
        var lostAt = _lostAt = Environment.TickCount64;
        UiThread.Every(ReturnMs, () =>
        {
            if (!_present && _lostAt == lostAt) FireLost("The Umbra GNOME Shell extension did not come back");
            return false;
        });
    }

    void Hide(Gtk.Window w)
    {
        if (!w.GetVisible()) return;
        _hidden.Add(w);
        w.SetVisible(false);
    }

    /// <summary>
    /// True when the extension answers <c>GetVersion</c> with this host's protocol version. Runs before GTK opens the
    /// display (it decides between native Wayland and XWayland), so it is one short blocking call.
    /// </summary>
    public static bool Probe() => DBus.Session is var bus && bus != IntPtr.Zero && ProbeVersion(bus);

    static bool ProbeVersion(IntPtr bus)
    {
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

    /// <summary>
    /// Shows a window only while the extension is there and has taken it (so no frame of it is ever a plain window the
    /// compositor could focus); otherwise it is remembered and shown when the extension takes it. Hiding is always done,
    /// and a window the host hides on purpose is not shown again when the extension comes back.
    /// </summary>
    public void SetVisible(Gtk.Window window, bool visible)
    {
        if (!visible) { _hidden.Remove(window); window.SetVisible(false); return; }
        if (_windows.TryGetValue(H(window), out var e) && (!_present || (!e.Registered && !Register(e))))
        {
            _hidden.Add(window);
            return;
        }
        window.SetVisible(true);
    }

    /// <summary>
    /// Registers the window's token. A token another caller already holds (it registered it while this host was away, e.g.
    /// after an extension reload) is replaced by a fresh one and a new title.
    /// </summary>
    bool Register(Entry e)
    {
        for (var attempt = 0; attempt < 3 && _present; attempt++)
        {
            try
            {
                DBus.Call(_bus, GnomeShell.BusName, GnomeShell.ObjectPath, GnomeShell.Interface, "AddWindow", GnomeShell.AddArgs(e.Token, e.Overlay), 3000).Dispose();
                return e.Registered = true;
            }
            catch (DBusException ex) when (GnomeShell.TokenTaken(ex.Message))
            {
                Log.Write("A window token was already registered by someone else: using a new one.");
                e.Token = GnomeShell.NewToken();
                e.Window.SetTitle(GnomeShell.Title(e.Token));
            }
            catch (DBusException ex) { Log.Write($"GNOME extension AddWindow failed: {ex.Message}"); return false; }
        }
        return false;
    }

    void Forget(Gtk.Window window)
    {
        _hidden.Remove(window);
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
        if (!_present) { Hide(window); return; } // a window shown around the pin (should not happen): never a plain window
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
            using var connector = m[8];
            monitors.Add(new GnomeShell.Monitor(new Rectangle(F(0), F(1), F(2), F(3)), new Rectangle(F(4), F(5), F(6), F(7)), connector.Str));
        }
        _stage = monitors.Select(m => new GnomeShell.Output(m.Bounds, m.Connector)).ToList();
        _work = GnomeShell.PrimaryWorkArea(primary.Int, monitors);
    }

    static List<GnomeShell.Output> GdkMonitors()
    {
        var list = new List<GnomeShell.Output>();
        if (Gdk.Display.GetDefault() is not { } display) return list;
        var monitors = display.GetMonitors();
        for (uint i = 0; i < monitors.GetNItems(); i++)
            if (monitors.GetObject(i) is Gdk.Monitor m) { m.GetGeometry(out var g); list.Add(new(new Rectangle(g.X, g.Y, g.Width, g.Height), m.GetConnector())); }
        return list;
    }
}
