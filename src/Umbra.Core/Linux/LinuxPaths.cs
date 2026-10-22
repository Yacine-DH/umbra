namespace Umbra.Linux;

/// <summary>
/// Where Umbra keeps its files on Linux. Same internal layout as <c>%APPDATA%\Umbra</c> on Windows, so Backup, Trash and
/// Restore work unchanged. Pure: the environment, the home folder and the file check are passed in, so every rule is
/// testable on any OS. Paths are always built with <c>/</c>.
/// </summary>
public sealed record LinuxAppPaths(
    string DataDir, string CacheDir, bool IsPortable,
    string SettingsFile, string WidgetsDir, string StoreDir, string ThemesDir, string TrashDir, string LogFile)
{
    /// <summary>The Flatpak marker file; its presence means we run inside a Flatpak sandbox.</summary>
    public const string FlatpakInfo = "/.flatpak-info";

    /// <summary>Joins two path parts with a single <c>/</c>.</summary>
    internal static string Join(string left, string right) => left.TrimEnd('/') + "/" + right.TrimStart('/');

    /// <summary>
    /// Resolves the folders. Data dir: <c>UMBRA_DATA_DIR</c> wins, then <c>&lt;appDir&gt;/data</c> when <c>portable.txt</c> sits
    /// in <paramref name="appDir"/>, then <c>$XDG_DATA_HOME/umbra</c> (default <c>~/.local/share/umbra</c>). The WebKit cache goes to
    /// <c>$XDG_CACHE_HOME/umbra</c> (default <c>~/.cache/umbra</c>); with <c>UMBRA_DATA_DIR</c> or portable mode it stays inside the
    /// data folder (<c>cache</c>), so a throwaway profile never touches the real cache. XDG values that are empty or not absolute
    /// are ignored, as the XDG spec says.
    /// </summary>
    /// <param name="getEnv">Reads an environment variable (null when unset).</param>
    /// <param name="appDir">The folder of the umbra binary (for portable mode).</param>
    /// <param name="fileExists">Checks that a file exists (for <c>portable.txt</c>).</param>
    /// <param name="homeFallback">Used when <c>HOME</c> is unset or empty.</param>
    public static LinuxAppPaths Resolve(Func<string, string?> getEnv, string appDir, Func<string, bool> fileExists, string homeFallback = "/")
    {
        var portable = fileExists(Join(appDir, PortableMode.Marker));
        string data, cache;
        if (getEnv("UMBRA_DATA_DIR") is { Length: > 0 } custom)
        {
            data = custom;
            cache = Join(data, "cache");
        }
        else if (portable)
        {
            data = Join(appDir, "data");
            cache = Join(data, "cache");
        }
        else
        {
            data = Join(XdgBase(getEnv, "XDG_DATA_HOME", ".local/share", homeFallback), "umbra");
            cache = Join(XdgBase(getEnv, "XDG_CACHE_HOME", ".cache", homeFallback), "umbra");
        }
        return new LinuxAppPaths(data, cache, portable,
            Join(data, "settings.json"), Join(data, "widgets"), Join(data, "data"), Join(data, "themes"), Join(data, "trash"), Join(data, "umbra.log"));
    }

    /// <summary>The user's XDG base dir: the variable when it is an absolute path, else <c>$HOME/&lt;fallback&gt;</c>.</summary>
    public static string XdgBase(Func<string, string?> getEnv, string variable, string fallbackUnderHome, string homeFallback = "/")
    {
        if (getEnv(variable) is { } set && set.StartsWith('/')) return set;
        var home = getEnv("HOME") is { } h && h.StartsWith('/') ? h : homeFallback;
        return Join(home, fallbackUnderHome);
    }

    /// <summary><c>$XDG_CONFIG_HOME</c> (default <c>~/.config</c>).</summary>
    public static string ConfigHome(Func<string, string?> getEnv, string homeFallback = "/") => XdgBase(getEnv, "XDG_CONFIG_HOME", ".config", homeFallback);

    /// <summary>True under Flatpak (<c>/.flatpak-info</c> exists).</summary>
    public static bool IsFlatpak(Func<string, bool> fileExists) => fileExists(FlatpakInfo);
}
