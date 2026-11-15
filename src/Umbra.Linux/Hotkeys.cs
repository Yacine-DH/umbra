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
    readonly Action<string> _run;
    IHotkeyBackend? _impl;
    bool _disposed;

    public HotkeyBackend Backend { get; private set; }

    /// <summary>Raised on the UI thread when a state changes without a call to <see cref="Apply"/> (the portal's dialog was answered, or the backend became ready).</summary>
    public event Action? Changed;

    /// <summary>
    /// Completes once the backend is chosen and set up. Right away on an X11 session; on Wayland after the portal's version
    /// was read and the app id registered, off the UI thread, so a slow or missing portal never delays startup. Portal calls
    /// that must come after the app id registration (the session's) wait for it.
    /// </summary>
    public Task Ready { get; }

    /// <summary>Call on the UI thread.</summary>
    public LinuxHotkeys(Action<string> run)
    {
        _run = run;
        // The portal is asked only where it would be used: a plain X11 session never wakes xdg-desktop-portal for this.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            Ready = StartAsync();
        else
        {
            Start(0);
            Ready = Task.CompletedTask;
        }
    }

    async Task StartAsync()
    {
        // Both calls block up to 3 s each when the portal hangs: on a worker thread, then back on the UI thread to start.
        var portal = await Task.Run(() =>
        {
            var v = PortalHotkeys.Version();
            // Outside Flatpak the portal learns the app id from the first call that registers it on this connection.
            if (HotkeyRules.Choose(Environment.GetEnvironmentVariable, v) == HotkeyBackend.Portal) PortalHotkeys.Register();
            return v;
        });
        if (_disposed) return;
        Start(portal);
        if (_applied is not null) _impl?.Apply(_applied); // what Apply was given while the backend was not ready yet
        Changed?.Invoke();
    }

    void Start(uint portal)
    {
        Backend = HotkeyRules.Choose(Environment.GetEnvironmentVariable, portal);
        void Safe(string action)
        {
            try { _run(action); }
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

    Dictionary<string, string>? _applied;

    /// <summary>
    /// Drops every registration and registers the given combinations. An unchanged map is skipped: on the portal a re-bind closes
    /// the session and can ask the user again. Before <see cref="Ready"/> the map is kept and applied once the backend is up.
    /// </summary>
    public void Apply(IReadOnlyDictionary<string, string> map)
    {
        if (_applied is not null && _applied.Count == map.Count && !_applied.Except(map).Any()) return;
        _applied = new Dictionary<string, string>(map);
        _impl?.Apply(map);
    }

    /// <summary>True while the portal is still being asked (Wayland, right after startup): nothing is reported unsupported yet.</summary>
    public bool Checking => !Ready.IsCompleted;

    /// <summary>Whether Settings offers hotkeys: a working backend, or one still being checked.</summary>
    public bool Supported => Checking || Backend != HotkeyBackend.None;

    public JsonArray Describe(IReadOnlyDictionary<string, string> map)
    {
        if (!Checking) return HotkeyRules.Describe(map, Backend, _impl?.States ?? new Dictionary<string, HotkeyRules.ActionState>());
        var pending = Hotkey.Actions.ToDictionary(a => a.Id, _ => new HotkeyRules.ActionState("pending", Note: "Checking what your desktop supports"));
        return HotkeyRules.Describe(map, HotkeyBackend.Portal, pending);
    }

    public void Dispose()
    {
        _disposed = true;
        _impl?.Dispose();
    }
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
    const int KeyPress = 2, KeyRelease = 3, MappingNotify = 34, GrabModeAsync = 1, BadAccess = 10;

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
    [LibraryImport(X11)] private static partial XModifierKeymap* XGetModifierMapping(IntPtr dpy);
    [LibraryImport(X11)] private static partial int XFreeModifiermap(XModifierKeymap* map);
    [LibraryImport(X11)] private static partial int XRefreshKeyboardMapping(XKeyEvent* ev);

#pragma warning disable CS0649 // filled in by Xlib
    struct XModifierKeymap { public int MaxKeyPerMod; public byte* ModifierMap; }
#pragma warning restore CS0649
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
    uint[] _variants = HotkeyRules.LockVariants(HotkeyRules.Mod2Mask);
    IReadOnlyDictionary<string, string> _map = new Dictionary<string, string>();
    bool _remapPosted;
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
            foreach (var v in _variants) XUngrabKey(_dpy, (int)keycode, mask | v, _root);
        _grabs.Clear();
        _down.Clear();
        XSync(_dpy, 0);
    }

    /// <summary>The bit Num Lock is on in the current modifier map (Mod2 on most layouts), 0 when it has none.</summary>
    uint NumLockBit()
    {
        var map = XGetModifierMapping(_dpy);
        if (map == null) return HotkeyRules.Mod2Mask;
        try
        {
            var per = map->MaxKeyPerMod;
            return HotkeyRules.ModifierBit(new ReadOnlySpan<byte>(map->ModifierMap, 8 * per), per, XKeysymToKeycode(_dpy, XStringToKeysym("Num_Lock")));
        }
        finally { XFreeModifiermap(map); }
    }

    public void Apply(IReadOnlyDictionary<string, string> map)
    {
        UngrabAll();
        _map = map;
        _variants = HotkeyRules.LockVariants(NumLockBit());
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
            foreach (var v in _variants)
                XGrabKey(_dpy, keycode, grab.Mask | v, _root, 0, GrabModeAsync, GrabModeAsync);
            XSync(_dpy, 0);
            var error = _lastError;
            if (error != 0) foreach (var v in _variants) XUngrabKey(_dpy, keycode, grab.Mask | v, _root);
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
            if (ev.Type == MappingNotify)
            {
                // The keyboard layout or modifier map changed: keycodes and the Num Lock bit may differ now, so grab again
                // (after this callback, since Apply drains events itself).
                XRefreshKeyboardMapping(&ev);
                if (!_remapPosted)
                {
                    _remapPosted = true;
                    _ui.Post(_ => { _remapPosted = false; Log.Verbose("Keyboard mapping changed: hotkeys grabbed again"); Apply(_map); }, null);
                }
                continue;
            }
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
    readonly SynchronizationContext _ui = SynchronizationContext.Current ?? new SynchronizationContext();
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

    /// <summary>Registers Umbra's app id with the portal on the session connection (blocks up to 3 s: call it off the UI thread).</summary>
    public static void Register()
    {
        try
        {
            using var _ = DBus.Call(DBus.Session, Dest, ObjPath, "org.freedesktop.host.portal.Registry", "Register", $"({V.Str(Program.AppId)}, @a{{sv}} {{}})", 3000);
        }
        catch (DBusException ex) { Log.Verbose($"Portal app id not registered: {ex.Message}"); }
    }

    public PortalHotkeys(Action<string> run, Action changed)
    {
        _run = run;
        _changed = changed;
        DBus.Subscribe(_bus, Dest, Iface, "Activated", ObjPath, (_, v) =>
        {
            using var session = v[0];
            using var id = v[1];
            // Only this session's shortcuts, and only those the desktop reported as bound.
            var action = id.Str;
            if (session.Str == _session && _states.TryGetValue(action, out var s) && s.Status == "active") _ui.Post(_ => _run(action), null);
        });
        DBus.Subscribe(_bus, Dest, Iface, "ShortcutsChanged", ObjPath, (_, v) =>
        {
            using var session = v[0];
            if (session.Str != _session) return;
            using var list = v[1];
            var listed = Shortcuts(list).ToDictionary(s => s.Id, s => s.Trigger);
            foreach (var id in _states.Keys.ToList())
                _states[id] = listed.TryGetValue(id, out var trigger) ? Active(trigger) : new("failed", "Removed in your desktop's settings");
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
            if (gen != _generation) { Close(created.Session); return; } // a newer Apply took over: don't leave this one open
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
        void OnResponse(string _, DBus.Value v)
        {
            using var code = v[0];
            using var results = v[1];
            var d = results.Dict();
            var bound = new Dictionary<string, string?>();
            if (d.TryGetValue("shortcuts", out var list)) foreach (var (id, trigger) in Shortcuts(list)) bound[id] = trigger;
            tcs.TrySetResult(new(code.UInt, d.TryGetValue("session_handle", out var s) ? s.AsString : null, bound));
        }
        var sub = DBus.Subscribe(_bus, Dest, "org.freedesktop.portal.Request", "Response", path, OnResponse);
        try
        {
            using (var reply = await DBus.CallAsync(_bus, Dest, ObjPath, Iface, method, args(token), 10000))
            using (var handle = reply[0])
                if (handle.Str != path)
                {
                    // An older portal can pick its own path: follow the handle it returned (the spec's advice).
                    DBus.Unsubscribe(_bus, sub);
                    sub = DBus.Subscribe(_bus, Dest, "org.freedesktop.portal.Request", "Response", handle.Str, OnResponse);
                }
            // The dialog waits for the user; a request nobody answers in 10 minutes is dropped.
            var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromMinutes(10)));
            return done == tcs.Task ? tcs.Task.Result : new Response(2, null, new());
        }
        finally { DBus.Unsubscribe(_bus, sub); }
    }

    void CloseSession()
    {
        var s = _session;
        _session = null;
        Close(s);
    }

    void Close(string? s)
    {
        if (s is null) return;
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
