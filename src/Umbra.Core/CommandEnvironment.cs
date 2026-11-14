namespace Umbra;

/// <summary>
/// The environment a widget command's child process gets: an allow-list, never the host's whole environment. Tokens, API keys,
/// <c>UMBRA_*</c>, the session bus address and loader variables (<c>LD_PRELOAD</c>, <c>LD_LIBRARY_PATH</c>) are not on it, so they never reach a widget's program.
/// Pure: the variable source is injected, so the rules are tested on any OS.
/// </summary>
public static class CommandEnvironment
{
    /// <summary>What a Windows child needs to start and find its profile folders.</summary>
    public static readonly IReadOnlyList<string> WindowsAllowList =
    [
        "SystemRoot", "windir", "SystemDrive", "ComSpec", "PATH", "PATHEXT", "TEMP", "TMP", "USERNAME", "USERDOMAIN",
        "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA", "ProgramData", "ProgramFiles", "ProgramFiles(x86)",
        "ProgramW6432", "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432", "NUMBER_OF_PROCESSORS",
        "PROCESSOR_ARCHITECTURE", "OS", "COMPUTERNAME", "PSModulePath",
    ];

    /// <summary>
    /// The Linux counterpart: search path, home and XDG folders (the analogue of APPDATA and friends), locale, time zone and the
    /// runtime dir. No display variables (the Windows list has no display equivalent either) and no bus address.
    /// </summary>
    public static readonly IReadOnlyList<string> LinuxAllowList =
    [
        "PATH", "HOME", "USER", "LOGNAME", "LANG", "LANGUAGE", "LC_ALL", "LC_CTYPE", "LC_MESSAGES", "LC_TIME", "TZ", "TMPDIR",
        "XDG_RUNTIME_DIR", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME", "XDG_STATE_HOME",
    ];

    /// <summary>The allow-list for a platform ("windows" or "linux"); anything else gets an empty list (nothing is passed).</summary>
    public static IReadOnlyList<string> AllowListFor(string platform) => platform switch
    {
        WidgetPlatforms.Windows => WindowsAllowList,
        WidgetPlatforms.Linux => LinuxAllowList,
        _ => [],
    };

    /// <summary>
    /// Builds the child environment: each allow-listed variable that is set and non-empty, plus <c>NO_COLOR=1</c>. Windows names
    /// are case-insensitive, Linux names are not. On Linux the search path keeps only absolute entries, so an empty or relative
    /// entry (which means "the current folder", here the widget's writable data folder) can never find a planted program.
    /// </summary>
    public static Dictionary<string, string> Scrub(string platform, Func<string, string?> get)
    {
        var env = new Dictionary<string, string>(platform == WidgetPlatforms.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var name in AllowListFor(platform))
            if (get(name) is { Length: > 0 } v) env[name] = v;
        if (platform == WidgetPlatforms.Linux && env.TryGetValue("PATH", out var path))
        {
            var clean = SanitizePosixPath(path);
            if (clean.Length > 0) env["PATH"] = clean; else env.Remove("PATH");
        }
        env["NO_COLOR"] = "1";
        return env;
    }

    /// <summary>A colon-separated search path with empty and non-absolute entries removed.</summary>
    public static string SanitizePosixPath(string path) =>
        string.Join(':', path.Split(':').Where(d => d.StartsWith('/')));
}
