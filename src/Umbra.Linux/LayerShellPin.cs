using System.Runtime.InteropServices;

namespace Umbra;

/// <summary>
/// Native Wayland pinning through gtk4-layer-shell (#186), for compositors with wlr-layer-shell (KWin on Plasma 6, Sway,
/// Hyprland): each widget is a layer surface on the BOTTOM layer (below normal windows, never in the task manager, on
/// every virtual desktop), anchored top-left on its monitor with the position as margins, keyboard mode NONE until a
/// physical press lends it the focus (ON_DEMAND). The host's overlays (edit pill, snap guides, tab strip) use the same
/// layer, mapped after the widgets so they sit above them, like the X11 backend's "below" overlays.
/// GNOME has no layer-shell; it keeps XWayland (#187).
/// </summary>
sealed unsafe partial class LayerShellPin : IDesktopPin
{
    const string Lib = "libgtk4-layer-shell.so.0";
    const string Gtk4 = "libgtk-4.so.1";

    [LibraryImport(Lib)] private static partial int gtk_layer_is_supported();
    [LibraryImport(Lib)] private static partial void gtk_layer_init_for_window(IntPtr window);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] private static partial void gtk_layer_set_namespace(IntPtr window, string name);
    [LibraryImport(Lib)] private static partial void gtk_layer_set_layer(IntPtr window, int layer);
    [LibraryImport(Lib)] private static partial void gtk_layer_set_monitor(IntPtr window, IntPtr monitor);
    [LibraryImport(Lib)] private static partial void gtk_layer_set_anchor(IntPtr window, int edge, int anchor);
    [LibraryImport(Lib)] private static partial void gtk_layer_set_margin(IntPtr window, int edge, int margin);
    [LibraryImport(Lib)] private static partial void gtk_layer_set_exclusive_zone(IntPtr window, int zone);
    [LibraryImport(Lib)] private static partial void gtk_layer_set_keyboard_mode(IntPtr window, int mode);

    [LibraryImport(Gtk4)] private static partial nuint gdk_wayland_display_get_type();
    [LibraryImport(Gtk4)] private static partial int gdk_surface_get_device_position(IntPtr surface, IntPtr device, out double x, out double y, out uint mask);
    [LibraryImport("libgobject-2.0.so.0")] private static partial int g_type_check_instance_is_a(IntPtr instance, nuint type);
    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr dlopen(string file, int mode);

    const int LayerBottom = 1;
    const int EdgeLeft = 0, EdgeTop = 2;
    const int KeyboardNone = 0, KeyboardOnDemand = 2;

    static bool _loaded;

    /// <summary>Where the host last put each surface (the real origin, after clamping to its monitor), by GdkSurface handle.</summary>
    readonly Dictionary<IntPtr, Point> _origins = new();
    readonly Dictionary<IntPtr, int> _monitorOf = new();
    readonly PointerLatch _latch = new();

    LayerShellPin() { }

    public string Name => "layer-shell";

    /// <summary>
    /// Loads gtk4-layer-shell into the global symbol scope before GTK and libwayland-client are loaded, which it needs to
    /// wrap the surfaces GTK creates (what <c>LD_PRELOAD=libgtk4-layer-shell.so.0</c> does; then this is a no-op). Must run
    /// before <c>Gtk.Module.Initialize</c>. False when the library is not installed.
    /// </summary>
    public static bool Preload()
    {
        if (_loaded) return true;
        try { _loaded = dlopen(Lib, 0x2 /* RTLD_NOW */ | 0x100 /* RTLD_GLOBAL */) != IntPtr.Zero; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { _loaded = false; }
        return _loaded;
    }

    /// <summary>The layer-shell backend when GDK runs on Wayland, the library was preloaded and the compositor supports it, else null.</summary>
    public static LayerShellPin? TryCreate()
    {
        if (!_loaded || Gdk.Display.GetDefault() is not { } display) return null;
        try
        {
            if (g_type_check_instance_is_a(display.Handle.DangerousGetHandle(), gdk_wayland_display_get_type()) == 0) return null;
            return gtk_layer_is_supported() != 0 ? new LayerShellPin() : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    static IntPtr H(Gtk.Window window) => window.Handle.DangerousGetHandle();
    static IntPtr SurfaceOf(Gtk.Window window) => window.GetSurface()?.Handle.DangerousGetHandle() ?? IntPtr.Zero;

    public void Init(Gtk.Window window, bool overlay = false)
    {
        var w = H(window);
        gtk_layer_init_for_window(w);
        gtk_layer_set_namespace(w, overlay ? "umbra-overlay" : "umbra");
        gtk_layer_set_layer(w, LayerBottom);
        gtk_layer_set_anchor(w, EdgeTop, 1);
        gtk_layer_set_anchor(w, EdgeLeft, 1);
        // -1: not pushed aside by panels, so the margins are exactly the offset from the output's corner (the GDK monitor
        // rectangle the placement uses); nothing is reserved either.
        gtk_layer_set_exclusive_zone(w, -1);
        gtk_layer_set_keyboard_mode(w, KeyboardNone);
        window.OnUnrealize += (_, _) => Forget(window);
    }

    public void Prepare(Gtk.Window window, bool overlay = false) { }

    public void Mapped(Gtk.Window window, bool overlay = false)
    {
        // The GdkSurface exists now; carry over the origin recorded under the window before it had one.
        if (_pending.Remove(H(window), out var at) && SurfaceOf(window) is var s && s != IntPtr.Zero) _origins[s] = at;
    }

    readonly Dictionary<IntPtr, Point> _pending = new();

    static List<Gdk.Monitor> GdkMonitors()
    {
        var list = new List<Gdk.Monitor>();
        if (Gdk.Display.GetDefault() is not { } display) return list;
        var monitors = display.GetMonitors();
        for (uint i = 0; i < monitors.GetNItems(); i++)
            if (monitors.GetObject(i) is Gdk.Monitor m) list.Add(m);
        return list;
    }

    public void Move(Gtk.Window window, int x, int y)
    {
        var monitors = GdkMonitors();
        var rects = monitors.Select(m => { m.GetGeometry(out var g); return new Rectangle(g.X, g.Y, g.Width, g.Height); }).ToList();
        var size = new Size(Math.Max(1, window.GetWidth()), Math.Max(1, window.GetHeight()));
        var (index, left, top) = LayerShellLayout.Place(new Point(x, y), size, rects);
        var w = H(window);
        // Moving to another monitor rebinds the surface to that output; the margins are then from its corner.
        if (index >= 0 && (!_monitorOf.TryGetValue(w, out var was) || was != index))
        {
            gtk_layer_set_monitor(w, monitors[index].Handle.DangerousGetHandle());
            _monitorOf[w] = index;
        }
        gtk_layer_set_margin(w, EdgeLeft, left);
        gtk_layer_set_margin(w, EdgeTop, top);
        var origin = index >= 0 ? new Point(rects[index].X + left, rects[index].Y + top) : new Point(left, top);
        if (SurfaceOf(window) is var s && s != IntPtr.Zero) _origins[s] = origin;
        else _pending[w] = origin;
    }

    public PointerState? Pointer()
    {
        // Wayland has no global pointer: GDK knows the position inside the surface under the pointer (kept during the
        // implicit grab of a press), and the buttons and modifiers its seat saw. Only a press on one of our surfaces counts.
        if (Gdk.Display.GetDefault()?.GetDefaultSeat()?.GetPointer() is not { } device) return null;
        var surface = device.GetSurfaceAtPosition(out _, out _);
        if (surface is null) return null;
        var handle = surface.Handle.DangerousGetHandle();
        if (!_origins.TryGetValue(handle, out var origin)) return null;
        gdk_surface_get_device_position(handle, device.Handle.DangerousGetHandle(), out var rx, out var ry, out var mask);
        var at = _latch.Resolve(handle, rx, ry, origin);
        const uint ShiftMask = 1, ControlMask = 4, Buttons = (1 << 8) | (1 << 9) | (1 << 10); // GdkModifierType
        return new PointerState(at, (mask & Buttons) != 0, (mask & ShiftMask) != 0, (mask & ControlMask) != 0);
    }

    /// <summary>
    /// NONE (never focused) or ON_DEMAND (the compositor may give it the keyboard on a click), only right after a physical
    /// press. The edit code turns it back to NONE once the window loses the focus. Fine-tuning per compositor is #188.
    /// </summary>
    public void SetFocusable(Gtk.Window window, bool on) => gtk_layer_set_keyboard_mode(H(window), on ? KeyboardOnDemand : KeyboardNone);

    /// <summary>A layer surface cannot ask for the focus; with ON_DEMAND the compositor gives it on the user's click.</summary>
    public void Focus(Gtk.Window window) { }

    /// <summary>Wayland tells clients no work area: the monitor rectangles stand for it.</summary>
    public Rectangle? PrimaryWorkArea() => null;

    /// <summary>Forgets a window whose surface goes away (its handles may be reused).</summary>
    void Forget(Gtk.Window window)
    {
        var w = H(window);
        _monitorOf.Remove(w);
        _pending.Remove(w);
        if (SurfaceOf(window) is var s && s != IntPtr.Zero) _origins.Remove(s);
    }
}

/// <summary>
/// Asks the Wayland compositor whether it offers <c>zwlr_layer_shell_v1</c>, before GTK opens the display (so the choice
/// between native Wayland and XWayland can still be made). One registry round trip on a connection of its own.
/// </summary>
static unsafe partial class WaylandProbe
{
    const string Lib = "libwayland-client.so.0";

    [LibraryImport(Lib)] private static partial IntPtr wl_display_connect(IntPtr name);
    [LibraryImport(Lib)] private static partial void wl_display_disconnect(IntPtr display);
    [LibraryImport(Lib)] private static partial int wl_display_roundtrip(IntPtr display);
    [LibraryImport(Lib)] private static partial IntPtr wl_proxy_marshal_array_constructor(IntPtr proxy, uint opcode, IntPtr* args, IntPtr iface);
    [LibraryImport(Lib)] private static partial int wl_proxy_add_listener(IntPtr proxy, IntPtr* listener, IntPtr data);
    [LibraryImport(Lib)] private static partial void wl_proxy_destroy(IntPtr proxy);

    static bool _found;

    [UnmanagedCallersOnly]
    static void Global(IntPtr data, IntPtr registry, uint name, IntPtr iface, uint version)
    {
        if (Marshal.PtrToStringUTF8(iface) == "zwlr_layer_shell_v1") _found = true;
    }

    [UnmanagedCallersOnly]
    static void GlobalRemove(IntPtr data, IntPtr registry, uint name) { }

    /// <summary>True when the compositor of <c>WAYLAND_DISPLAY</c> lists the layer-shell global; false on any failure.</summary>
    public static bool HasLayerShell()
    {
        try
        {
            if (!NativeLibrary.TryLoad(Lib, out var lib)) return false;
            var registryInterface = NativeLibrary.GetExport(lib, "wl_registry_interface");
            var display = wl_display_connect(IntPtr.Zero);
            if (display == IntPtr.Zero) return false;
            try
            {
                IntPtr* args = stackalloc IntPtr[1];
                args[0] = IntPtr.Zero; // new_id
                var registry = wl_proxy_marshal_array_constructor(display, 1 /* wl_display.get_registry */, args, registryInterface);
                if (registry == IntPtr.Zero) return false;
                IntPtr* listener = stackalloc IntPtr[2];
                listener[0] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, uint, IntPtr, uint, void>)&Global;
                listener[1] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, uint, void>)&GlobalRemove;
                _found = false;
                wl_proxy_add_listener(registry, listener, IntPtr.Zero);
                wl_display_roundtrip(display);
                wl_proxy_destroy(registry);
                return _found;
            }
            finally { wl_display_disconnect(display); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }
}
