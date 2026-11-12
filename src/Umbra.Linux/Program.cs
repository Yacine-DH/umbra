using System.Text.Json.Nodes;

namespace Umbra;

static class Program
{
    /// <summary>The GApplication id (also the .desktop, metainfo and Flatpak id). The Windows AUMID stays <c>AmineChr54.Umbra</c>.</summary>
    public const string AppId = Linux.DesktopEntry.AppId;

    /// <summary>
    /// umbra              start (or, if already running, ask the running instance to open Settings)
    /// umbra --background start without opening Settings (the autostart entry uses it)
    /// umbra --safe       start with community widgets off (not saved; the next normal start restores them)
    /// Automation: umbractl / umbractl mcp talk to the running instance over the control socket (Protocol.cs).
    /// </summary>
    static int Main(string[] args)
    {
        var background = args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        var safe = args.Contains("--safe", StringComparer.OrdinalIgnoreCase);

        // One Umbra per user and data folder: the same named mutex and per-user control socket as on Windows.
        using var instance = new SingleInstance();
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
            app = new App(gtk, ui, safe);
            app.SaveSettings();
            gtk.Hold(); // widgets are plain windows, not application windows: keep the loop alive until Quit
            instance.Listen(new Control(app).HandleLineAsync);
        };
        return gtk.Run([]);
    }
}
