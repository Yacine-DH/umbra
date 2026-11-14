using System.Text.Json.Nodes;

namespace Umbra;

static partial class Program
{
    [System.Runtime.InteropServices.LibraryImport("libc", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf8)]
    private static partial int setenv(string name, string value, int overwrite);

    /// <summary>
    /// Picks the desktop pin backend (<see cref="LayerShellLayout.Choose"/>) and the GDK backend it needs. On a Wayland
    /// compositor with layer-shell (KWin, Sway, Hyprland) GTK stays on Wayland and widgets are layer surfaces (#186);
    /// gtk4-layer-shell is loaded first, as it must be before libwayland-client. On a Wayland session without it (GNOME,
    /// until #187) and with XWayland, GTK is told to use X11, whose window manager hints GNOME and KDE honour (#182).
    /// <c>UMBRA_PIN_BACKEND=x11</c> forces the XWayland path. An explicit GDK_BACKEND is respected. Must run before GTK opens
    /// the display, and through libc: GTK reads the C environment.
    /// </summary>
    static void ChooseDisplayBackend()
    {
        var gdk = Environment.GetEnvironmentVariable("GDK_BACKEND");
        var pinOverride = Environment.GetEnvironmentVariable("UMBRA_PIN_BACKEND");
        var wayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        var x = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));
        bool loaded = false, offered = false;
        if (wayland && !LayerShellLayout.IsX11(pinOverride) && !LayerShellLayout.IsX11(gdk?.Split(',')[0]))
        {
            loaded = LayerShellPin.Preload();
            offered = loaded && WaylandProbe.HasLayerShell();
            if (!loaded) Log.Write("gtk4-layer-shell is not installed (libgtk4-layer-shell.so.0): no native Wayland pinning.");
        }
        var choice = LayerShellLayout.Choose(gdk, pinOverride, wayland, x, loaded, offered);
        if (!string.IsNullOrEmpty(gdk)) return;
        if (choice == PinBackend.LayerShell)
        {
            setenv("GDK_BACKEND", "wayland", 1);
            Log.Write("Wayland compositor with layer-shell: widgets are pinned as layer surfaces.");
        }
        else if (choice == PinBackend.X11 && wayland)
        {
            setenv("GDK_BACKEND", "x11", 1);
            Log.Write("Wayland session without layer-shell, with XWayland: using the X11 backend so widgets can be pinned to the desktop.");
        }
    }

    /// <summary>The GApplication id (also the .desktop, metainfo and Flatpak id). The Windows AUMID stays <c>AmineChr54.Umbra</c>.</summary>
    public const string AppId = Linux.DesktopEntry.AppId;

    /// <summary>Passed by Settings > Restart: the new process waits for the old one to let go of the single-instance lock.</summary>
    public const string RestartArg = "--restart";

    /// <summary>
    /// umbra              start (or, if already running, ask the running instance to open Settings)
    /// umbra --background start without opening Settings (the autostart entry uses it)
    /// umbra --safe       start with community widgets off (not saved; the next normal start restores them)
    /// umbra --restart    started by Settings > Restart: waits for the old instance to exit, then starts without opening Settings
    /// Automation: umbractl / umbractl mcp talk to the running instance over the control socket (Protocol.cs).
    /// </summary>
    static int Main(string[] args)
    {
        var background = args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        var safe = args.Contains("--safe", StringComparer.OrdinalIgnoreCase);
        var restart = args.Contains(RestartArg, StringComparer.OrdinalIgnoreCase);

        // One Umbra per user and data folder: the same named mutex and per-user control socket as on Windows.
        using var instance = new SingleInstance(waitForPrevious: restart);
        if (!instance.IsFirst)
        {
            if (safe)
            {
                try { Protocol.SendAsync(Protocol.Request("safe-mode", new JsonObject { ["on"] = true })).GetAwaiter().GetResult(); }
                catch (Exception ex) { Log.Write($"--safe could not reach the running Umbra: {ex.Message}"); }
                return 0;
            }
            if (!background) SingleInstance.SignalFirst("settings");
            return 0;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"Fatal: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Write($"Unobserved: {e.Exception.GetBaseException().Message}"); e.SetObserved(); };

        ChooseDisplayBackend();

        // GirCore resolves the native libraries (libwebkitgtk-6.0.so.4 and what it needs) once these have run.
        Gtk.Module.Initialize();
        WebKit.Module.Initialize();

        // NonUnique: uniqueness is ours (above), so a throwaway UMBRA_DATA_DIR profile can run next to an installed Umbra.
        var gtk = Gtk.Application.New(AppId, Gio.ApplicationFlags.NonUnique);
        App? app = null;
        gtk.OnActivate += (_, _) =>
        {
            if (app is not null) return;
            var ui = new UiThread();
            SynchronizationContext.SetSynchronizationContext(ui);
            var firstRun = !File.Exists(Path.Combine(AppPaths.DataDir, "settings.json"));
            app = new App(gtk, ui, safe);
            app.SaveSettings();
            // No tray yet (#191): a normal start (the app menu, a second launch) opens Settings; autostart and restart stay quiet.
            if (!background && !restart) app.OpenSettingsOnStart(firstRun);
            gtk.Hold(); // widgets are plain windows, not application windows: keep the loop alive until Quit
            instance.Listen(new Control(app).HandleLineAsync);
        };
        return gtk.Run([]);
    }
}
