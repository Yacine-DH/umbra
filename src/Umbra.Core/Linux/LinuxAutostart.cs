namespace Umbra.Linux;

/// <summary>"Start with the session" on Linux: the Linux counterpart of the Windows Run key (<c>Startup.cs</c>).</summary>
public interface IAutostart
{
    /// <summary>True when Umbra is set to start at login.</summary>
    bool IsEnabled();

    /// <summary>Turns start at login on or off.</summary>
    void Set(bool enabled);
}

/// <summary>
/// The Background portal (<c>org.freedesktop.portal.Background.RequestBackground</c>), the only way to autostart under Flatpak.
/// NOT RUN ON LINUX YET: the real implementation (GDBus, from the host project) lands with the Linux host.
/// </summary>
public interface IBackgroundPortal
{
    /// <summary>Sends a <c>RequestBackground</c> call with these options; returns whether the user/portal allowed autostart.</summary>
    bool RequestBackground(BackgroundRequest request);
}

/// <summary>The options of a Background portal request: <c>autostart</c>, <c>commandline</c> and the user-visible <c>reason</c>.</summary>
public sealed record BackgroundRequest(bool Autostart, IReadOnlyList<string> CommandLine, string Reason);

/// <summary>Autostart through <c>~/.config/autostart/io.github.aminechr54.Umbra.desktop</c>. Plain file I/O, testable with a temp folder.</summary>
public sealed class DesktopFileAutostart(string path, string launcherPath, IReadOnlyList<string> args) : IAutostart
{
    public bool IsEnabled() => File.Exists(path) && DesktopEntry.IsAutostartEnabled(File.ReadAllText(path));

    public void Set(bool enabled)
    {
        if (enabled)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, DesktopEntry.Autostart(launcherPath, args));
            File.Move(tmp, path, overwrite: true);
        }
        else if (File.Exists(path)) File.Delete(path);
    }
}

/// <summary>
/// Autostart under Flatpak through the Background portal. The portal keeps the on/off state, so the last requested value is
/// remembered here (<see cref="IsEnabled"/> is a best guess until the host persists it in settings).
/// </summary>
public sealed class PortalAutostart(IBackgroundPortal portal, IReadOnlyList<string> commandLine, bool initial = false) : IAutostart
{
    bool _enabled = initial;

    public bool IsEnabled() => _enabled;

    public void Set(bool enabled)
    {
        if (portal.RequestBackground(new BackgroundRequest(enabled, commandLine, "Start Umbra widgets with your session")))
            _enabled = enabled;
    }
}

/// <summary>Picks the autostart backend and applies the portable rule (portable copies never register themselves).</summary>
public static class LinuxAutostart
{
    /// <summary>Stands in when autostart must stay off (portable mode).</summary>
    sealed class Disabled : IAutostart
    {
        public bool IsEnabled() => false;
        public void Set(bool enabled) { }
    }

    /// <summary>
    /// A no-op in portable mode, the Background portal under Flatpak, else the autostart <c>.desktop</c> file.
    /// <paramref name="args"/> defaults to <c>--background</c>, as on Windows.
    /// </summary>
    public static IAutostart Create(Func<string, string?> getEnv, Func<string, bool> fileExists, string launcherPath, bool portable, IBackgroundPortal? portal, IReadOnlyList<string>? args = null)
    {
        if (portable) return new Disabled();
        args ??= ["--background"];
        if (LinuxAppPaths.IsFlatpak(fileExists) && portal != null)
            return new PortalAutostart(portal, [launcherPath, .. args]);
        return new DesktopFileAutostart(DesktopEntry.AutostartPath(LinuxAppPaths.ConfigHome(getEnv)), launcherPath, args);
    }
}
