using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Umbra.Linux;

/// <summary>
/// Global hotkeys on Linux (#192), the counterpart of the Windows <c>HotkeyService</c>: XGrabKey on an X11 session, the
/// GlobalShortcuts portal on Wayland (KDE Plasma 6, GNOME 48+), and nothing elsewhere (GNOME 46), which Settings shows as
/// "Not supported on this desktop". A pressed hotkey runs its action on the UI thread; it never touches a widget window,
/// so nothing is activated and no widget gets keyboard focus. The rules (backend choice, key mapping, state) are <see cref="HotkeyRules"/>.
/// </summary>
sealed class LinuxHotkeys : IDisposable
{
    readonly IHotkeyBackend? _impl;

    public HotkeyBackend Backend { get; }

    /// <summary>Raised on the UI thread when a state changes without a call to <see cref="Apply"/> (the portal's dialog was answered).</summary>
    public event Action? Changed;

    public LinuxHotkeys(Action<string> run)
    {
        uint portal = 0;
        // The portal is asked only where it would be used: a plain X11 session never wakes xdg-desktop-portal for this.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            portal = PortalHotkeys.Version();
        Backend = HotkeyRules.Choose(Environment.GetEnvironmentVariable, portal);
        void Safe(string action)
        {
            try { run(action); }
            catch (Exception ex) { Log.Write($"Hotkey '{action}' failed: {ex.Message}"); }
        }
        _impl = Backend switch
        {
            HotkeyBackend.X11 => X11Hotkeys.TryCreate(Safe),
            HotkeyBackend.Portal => new PortalHotkeys(Safe, () => Changed?.Invoke()),
            _ => null,
        };
        if (Backend == HotkeyBackend.X11 && _impl is null) Backend = HotkeyBackend.None;
        Log.Write(Backend == HotkeyBackend.None
            ? "Global hotkeys: not supported on this desktop (no GlobalShortcuts portal and not an X11 session)"
            : $"Global hotkeys: {HotkeyRules.Name(Backend)}{(portal > 0 ? $" (GlobalShortcuts portal v{portal})" : "")}");
    }

    /// <summary>Drops every registration and registers the given combinations.</summary>
    public void Apply(IReadOnlyDictionary<string, string> map) => _impl?.Apply(map);

    public JsonArray Describe(IReadOnlyDictionary<string, string> map) =>
        HotkeyRules.Describe(map, Backend, _impl?.States ?? new Dictionary<string, HotkeyRules.ActionState>());

    public void Dispose() => _impl?.Dispose();
}

interface IHotkeyBackend : IDisposable
{
    void Apply(IReadOnlyDictionary<string, string> map);
    IReadOnlyDictionary<string, HotkeyRules.ActionState> States { get; }
}

/// <summary>
/// XGrabKey on the root window, through a separate Xlib connection whose socket is watched by the GLib main loop. Every grab
/// is repeated with Caps Lock and Num Lock, auto-repeat is made detectable so holding the keys runs the action once (like
/// MOD_NOREPEAT on Windows), and a combination another client already grabbed is reported as taken.
/// </summary>
sealed unsafe partial class X11Hotkeys : IHotkeyBackend
{
    const string X11 = "libX11.so.6";
    const string GLibLib = "libglib-2.0.so.0";
    const int KeyPress = 2, KeyRelease = 3, GrabModeAsync = 1, BadAccess = 10;

    [LibraryImport(X11)] private static partial IntPtr XOpenDisplay(IntPtr name);
    [LibraryImport(X11)] private static partial int XCloseDisplay(IntPtr dpy);
    [LibraryImport(X11)] private static partial nuint XDefaultRootWindow(IntPtr dpy);
    [LibraryImport(X11)] private static partial int XConnectionNumber(IntPtr dpy);
    [LibraryImport(X11, StringMarshalling = StringMarshalling.Utf8)] private static partial nuint XStringToKeysym(string name);
    [LibraryImport(X11)] private static partial byte XKeysymToKeycode(IntPtr dpy, nuint keysym);
    [LibraryImport(X11)] private static partial int XGrabKey(IntPtr dpy, int keycode, uint modifiers, nuint window, int ownerEvents, int pointerMode, int keyboardMode);
    [LibraryImport(X11)] private static partial int XUngrabKey(IntPtr dpy, int keycode, uint modifiers, nuint window);
    [LibraryImport(X11)] private static partial int XSync(IntPtr dpy, int discard);
    [LibraryImport(X11)] private static partial int XPending(IntPtr dpy);
    [LibraryImport(X11)] private static partial int XNextEvent(IntPtr dpy, XKeyEvent* ev);
    [LibraryImport(X11)] private static partial IntPtr XSetErrorHandler(IntPtr handler);
    [LibraryImport(X11)] private static partial int XkbSetDetectableAutoRepeat(IntPtr dpy, int detectable, out int supported);
    [LibraryImport(GLibLib)] private static partial uint g_unix_fd_add(int fd, int condition, delegate* unmanaged<int, int, IntPtr, int> func, IntPtr data);
    [LibraryImport(GLibLib)] private static partial int g_source_remove(uint id);

    /// <summary>XKeyEvent, padded to the size of an XEvent (24 longs).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 192)]
    struct XKeyEvent
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(80)] public uint State;
        [FieldOffset(84)] public uint Keycode;
    }

    readonly IntPtr _dpy;
    readonly nuint _root;
    readonly Action<string> _run;
    readonly SynchronizationContext _ui;
    readonly uint _source;
    GCHandle _self;
    readonly Dictionary<(uint Keycode, uint Mask), string> _grabs = new();
    readonly HashSet<uint> _down = new();
    readonly Dictionary<string, HotkeyRules.ActionState> _states = new();
    public IReadOnlyDictionary<string, HotkeyRules.ActionState> States => _states;

    X11Hotkeys(IntPtr dpy, Action<string> run)
    {
        _dpy = dpy;
        _run = run;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        _root = XDefaultRootWindow(dpy);
        XkbSetDetectableAutoRepeat(dpy, 1, out _);
        _self = GCHandle.Alloc(this);
        _source = g_unix_fd_add(XConnectionNumber(dpy), 1 /* G_IO_IN */, &OnReadable, GCHandle.ToIntPtr(_self));
    }

    /// <summary>Null when no X server can be opened.</summary>
    public static X11Hotkeys? TryCreate(Action<string> run)
    {
        var dpy = XOpenDisplay(IntPtr.Zero);
        if (dpy == IntPtr.Zero) { Log.Write("Global hotkeys: could not open the X display"); return null; }
        return new X11Hotkeys(dpy, run);
    }

    [ThreadStatic] static int _lastError;

    [UnmanagedCallersOnly]
    static int OnError(IntPtr dpy, IntPtr ev)
    {
        _lastError = *((byte*)ev + 32); // XErrorEvent.error_code
        return 0;
    }

    void UngrabAll()
    {
        foreach (var (keycode, mask) in _grabs.Keys)
            foreach (var v in HotkeyRules.LockVariants) XUngrabKey(_dpy, (int)keycode, mask | v, _root);
        _grabs.Clear();
        _down.Clear();
        XSync(_dpy, 0);
    }

    public void Apply(IReadOnlyDictionary<string, string> map)
    {
        UngrabAll();
        _states.Clear();
        var problems = Hotkey.Validate(map);
        foreach (var (action, keys) in map)
        {
            if (problems.ContainsKey(action) || HotkeyRules.ToX11(keys) is not { } grab) continue;
            var norm = Hotkey.Normalize(keys);
            var keycode = XKeysymToKeycode(_dpy, XStringToKeysym(grab.Keysym));
            if (keycode == 0) { Fail(action, $"{norm} has no key on this keyboard layout"); continue; }
            // Errors arrive asynchronously; a temporary handler around XSync catches BadAccess (another client holds the grab)
            // without disturbing GTK's own handler, which is back in place before the main loop runs again.
            _lastError = 0;
            var previous = XSetErrorHandler((IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int>)&OnError);
            foreach (var v in HotkeyRules.LockVariants)
                XGrabKey(_dpy, keycode, grab.Mask | v, _root, 0, GrabModeAsync, GrabModeAsync);
            XSync(_dpy, 0);
            var error = _lastError;
            if (error != 0) foreach (var v in HotkeyRules.LockVariants) XUngrabKey(_dpy, keycode, grab.Mask | v, _root);
            XSync(_dpy, 0);
            XSetErrorHandler(previous);
            if (error != 0) { Fail(action, error == BadAccess ? $"{norm} is already in use by another app" : $"The X server refused {norm} (error {error})"); continue; }
            _grabs[(keycode, grab.Mask)] = action;
            _states[action] = new("active");
        }
        Drain();
    }

    void Fail(string action, string why)
    {
        _states[action] = new("failed", why);
        Log.Write($"Hotkey '{action}' not registered: {why}");
    }

    [UnmanagedCallersOnly]
    static int OnReadable(int fd, int condition, IntPtr data)
    {
        try { ((X11Hotkeys)GCHandle.FromIntPtr(data).Target!).Drain(); }
        catch (Exception ex) { Log.Write($"Hotkey event failed: {ex.Message}"); }
        return 1; // G_SOURCE_CONTINUE
    }

    void Drain()
    {
        XKeyEvent ev;
        while (XPending(_dpy) > 0)
        {
            XNextEvent(_dpy, &ev);
            if (ev.Type == KeyRelease) { _down.Remove(ev.Keycode); continue; }
            if (ev.Type != KeyPress || !_down.Add(ev.Keycode)) continue; // held down: auto-repeat, run once
            // Run after this X callback returns: an action can open windows and spin the main loop.
            if (_grabs.TryGetValue((ev.Keycode, HotkeyRules.CleanState(ev.State)), out var action)) _ui.Post(_ => _run(action), null);
        }
    }

    public void Dispose()
    {
        UngrabAll();
        if (_source != 0) g_source_remove(_source);
        XCloseDisplay(_dpy);
        if (_self.IsAllocated) _self.Free();
    }
}

/// <summary>
/// org.freedesktop.portal.GlobalShortcuts: one session per set of bindings (CreateSession, then BindShortcuts, then the
/// Activated signal). The desktop shows its own dialog where the user confirms or changes each shortcut, so a binding is
/// "pending" until that dialog is answered. A change closes the session and binds the new set in a fresh one. Outside
/// Flatpak the app id is registered first (host Registry portal), so the desktop can name Umbra in its dialog and keep the choice.
/// </summary>
sealed class PortalHotkeys : IHotkeyBackend
{
    const string Dest = "org.freedesktop.portal.Desktop", ObjPath = "/org/freedesktop/portal/desktop", Iface = "org.freedesktop.portal.GlobalShortcuts";

    readonly Action<string> _run;
    readonly Action _changed;
    readonly IntPtr _bus = DBus.Session;
    readonly Dictionary<string, HotkeyRules.ActionState> _states = new();
    string? _session;
    int _generation, _tokens;
    public IReadOnlyDictionary<string, HotkeyRules.ActionState> States => _states;

    /// <summary>The GlobalShortcuts interface version, 0 when the portal or the interface is missing (GNOME 46).</summary>
    public static uint Version()
    {
        try
        {
            using var r = DBus.Call(DBus.Session, Dest, ObjPath, "org.freedesktop.DBus.Properties", "Get", $"({V.Str(Iface)}, {V.Str("version")})", 3000);
            using var v = r[0];
            return (uint)v.Number;
        }
        catch (Exception ex) when (ex is DBusException or InvalidOperationException)
        {
            Log.Verbose($"No GlobalShortcuts portal: {ex.Message}");
            return 0;
        }
    }

    public PortalHotkeys(Action<string> run, Action changed)
    {
        _run = run;
        _changed = changed;
        try
        {
            using var _ = DBus.Call(_bus, Dest, ObjPath, "org.freedesktop.host.portal.Registry", "Register", $"({V.Str(Program.AppId)}, @a{{sv}} {{}})", 3000);
        }
        catch (DBusException ex) { Log.Verbose($"Portal app id not registered: {ex.Message}"); }
        DBus.Subscribe(_bus, Dest, Iface, "Activated", ObjPath, (_, v) =>
        {
            using var session = v[0];
            using var id = v[1];
            // Only this session's shortcuts, and only those the desktop reported as bound.
            if (session.Str == _session && _states.TryGetValue(id.Str, out var s) && s.Status == "active") _run(id.Str);
        });
        DBus.Subscribe(_bus, Dest, Iface, "ShortcutsChanged", ObjPath, (_, v) =>
        {
            using var session = v[0];
            if (session.Str != _session) return;
            using var list = v[1];
            foreach (var (id, trigger) in Shortcuts(list)) if (_states.ContainsKey(id)) _states[id] = Active(trigger);
            _changed();
        });
    }

    public void Apply(IReadOnlyDictionary<string, string> map) => _ = ApplyAsync(map);

    async Task ApplyAsync(IReadOnlyDictionary<string, string> map)
    {
        var gen = ++_generation;
        CloseSession();
        _states.Clear();
        var shortcuts = HotkeyRules.PortalShortcuts(map);
        foreach (var s in shortcuts) _states[s.Id] = new("pending", Note: "Confirm it in your desktop's shortcut dialog");
        if (shortcuts.Count == 0) return;
        try
        {
            var created = await RequestAsync("CreateSession", t => $"({{'handle_token': <{V.Str(t)}>, 'session_handle_token': <{V.Str("umbra" + t)}>}},)");
            if (gen != _generation) return;
            if (created.Code != 0 || created.Session is null) { FailAll(shortcuts, "The desktop did not open a shortcuts session"); return; }
            _session = created.Session;
            var list = string.Join(", ", shortcuts.Select(s =>
                $"({V.Str(s.Id)}, {{'description': <{V.Str(s.Description)}>, 'preferred_trigger': <{V.Str(s.Trigger)}>}})"));
            var bound = await RequestAsync("BindShortcuts", t => $"({V.Path(_session)}, [{list}], '', {{'handle_token': <{V.Str(t)}>}})");
            if (gen != _generation) return;
            foreach (var s in shortcuts)
                _states[s.Id] = bound.Bound.TryGetValue(s.Id, out var trigger) ? Active(trigger)
                    : bound.Code == 1 ? new("failed", "Not confirmed in your desktop's shortcut dialog")
                    : new("failed", "Your desktop did not bind it");
            Log.Write($"Global hotkeys bound through the portal: {bound.Bound.Count} of {shortcuts.Count} (response {bound.Code})");
        }
        catch (Exception ex) when (ex is DBusException or FormatException or InvalidOperationException)
        {
            if (gen != _generation) return;
            Log.Write($"GlobalShortcuts portal failed: {ex.Message}");
            FailAll(shortcuts, $"Your desktop refused: {ex.Message}");
        }
        finally { if (gen == _generation) _changed(); }
    }

    static HotkeyRules.ActionState Active(string? trigger) =>
        new("active", Note: string.IsNullOrWhiteSpace(trigger) ? null : $"Desktop shortcut: {trigger}");

    void FailAll(IEnumerable<(string Id, string Description, string Trigger)> shortcuts, string why)
    {
        foreach (var s in shortcuts) _states[s.Id] = new("failed", why);
    }

    /// <summary>The (id, trigger description) pairs of an a(sa{sv}).</summary>
    static List<(string Id, string? Trigger)> Shortcuts(DBus.Value list)
    {
        var result = new List<(string, string?)>();
        for (var i = 0; i < list.Count; i++)
        {
            using var item = list[i];
            using var id = item[0];
            using var props = item[1];
            var d = props.Dict();
            result.Add((id.Str, d.TryGetValue("trigger_description", out var t) ? t.AsString : null));
        }
        return result;
    }

    sealed record Response(uint Code, string? Session, Dictionary<string, string?> Bound);

    /// <summary>A portal request: subscribes to its Response first (the path is known from the token), then calls the method.</summary>
    async Task<Response> RequestAsync(string method, Func<string, string> args)
    {
        var token = $"u{Environment.ProcessId}_{++_tokens}";
        var path = HotkeyRules.RequestPath(DBus.UniqueName(_bus) ?? "", token);
        var tcs = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sub = DBus.Subscribe(_bus, Dest, "org.freedesktop.portal.Request", "Response", path, (_, v) =>
        {
            using var code = v[0];
            using var results = v[1];
            var d = results.Dict();
            var bound = new Dictionary<string, string?>();
            if (d.TryGetValue("shortcuts", out var list)) foreach (var (id, trigger) in Shortcuts(list)) bound[id] = trigger;
            tcs.TrySetResult(new(code.UInt, d.TryGetValue("session_handle", out var s) ? s.AsString : null, bound));
        });
        try
        {
            using (await DBus.CallAsync(_bus, Dest, ObjPath, Iface, method, args(token), 10000)) { }
            // The dialog waits for the user; a request nobody answers in 10 minutes is dropped.
            var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromMinutes(10)));
            return done == tcs.Task ? tcs.Task.Result : new Response(2, null, new());
        }
        finally { DBus.Unsubscribe(_bus, sub); }
    }

    void CloseSession()
    {
        if (_session is null) return;
        var s = _session;
        _session = null;
        _ = DBus.CallAsync(_bus, Dest, s, "org.freedesktop.portal.Session", "Close").ContinueWith(t =>
        {
            if (t.IsFaulted) Log.Verbose($"Closing the shortcuts session failed: {t.Exception?.GetBaseException().Message}");
            else t.Result.Dispose();
        });
    }

    public void Dispose()
    {
        _generation++;
        CloseSession();
    }
}
