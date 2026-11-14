namespace Umbra;

/// <summary>Tiny append-only log in %APPDATA%\Umbra\umbra.log (trimmed to its newest half past 256 KB).</summary>
static class Log
{
    static readonly object Gate = new();
    internal const int MaxBytes = 256 * 1024;

    /// <summary>When the file exceeds <paramref name="maxBytes"/>, keeps only the newest ~half, starting at a line boundary.</summary>
    internal static void TrimIfLarge(string path, int maxBytes)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= maxBytes) return;
        var keep = maxBytes / 2;
        byte[] tail;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            fs.Seek(-keep, SeekOrigin.End);
            tail = new byte[keep];
            fs.ReadExactly(tail);
        }
        var start = Array.IndexOf(tail, (byte)'\n') + 1; // drop the partial first line
        File.WriteAllBytes(path, tail[start..]);
    }

    /// <summary>"errors", "normal" or "verbose" (see <see cref="Prefs"/>).</summary>
    public static string Level { get; set; } = "normal";

    static readonly string[] ErrorWords = { "fail", "error", "exception", "could not", "unreadable", "fatal", "blocked" };

    /// <summary>At "errors" only lines that look like problems are kept; "verbose" lines are dropped below "verbose".</summary>
    internal static bool ShouldWrite(string level, string message, bool verbose) =>
        verbose ? level == "verbose"
        : level != "errors" || ErrorWords.Any(w => message.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>Extra detail, written only at the "verbose" level.</summary>
    public static void Verbose(string message) => WriteCore(message, true);

    public static void Write(string message) => WriteCore(message, false);

    static void WriteCore(string message, bool verbose)
    {
        if (!ShouldWrite(Level, message, verbose)) return;
        try
        {
            lock (Gate)
            {
                var path = Path.Combine(AppPaths.DataDir, "umbra.log");
                TrimIfLarge(path, MaxBytes);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never crash the app */ }
    }

    public static string[] Tail(int lines)
    {
        lock (Gate)
        {
            var path = Path.Combine(AppPaths.DataDir, "umbra.log");
            return File.Exists(path) ? File.ReadLines(path).TakeLast(Math.Clamp(lines, 1, 2000)).ToArray() : Array.Empty<string>();
        }
    }
}

static class AppPaths
{
    public static string AppDir => AppContext.BaseDirectory;
    public static string WidgetsDir => Path.Combine(AppDir, "widgets");
    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppDir, "Umbra.exe");

    /// <summary>Community widgets installed from the catalog: %APPDATA%\Umbra\widgets\&lt;id&gt;.</summary>
    public static string CommunityDir
    {
        get
        {
            var d = Path.Combine(DataDir, "widgets");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    /// <summary>True when portable.txt sits next to Umbra.exe: data lives in &lt;AppDir&gt;\data (UMBRA_DATA_DIR still wins).</summary>
    public static bool IsPortable => PortableMode.IsPortable(AppDir);

    /// <summary>%APPDATA%\Umbra, &lt;AppDir&gt;\data in portable mode, or UMBRA_DATA_DIR when set (tests and throwaway dev profiles).</summary>
    public static string DataDir
    {
        get
        {
            var d = PortableMode.DataDir(AppDir);
            Directory.CreateDirectory(d);
            return d;
        }
    }

    /// <summary>
    /// Web engine user data (cache, cookies). Windows: WebView2 under LocalAppData, or inside the data folder in portable mode.
    /// Linux: WebKit under <c>$XDG_CACHE_HOME/umbra</c>, or <c>&lt;data&gt;/cache</c> with UMBRA_DATA_DIR or portable mode.
    /// </summary>
    public static string WebViewDataDir => !OperatingSystem.IsWindows()
        ? Linux.LinuxAppPaths.Resolve(Environment.GetEnvironmentVariable, AppDir, File.Exists,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).CacheDir
        : IsPortable
        ? Path.Combine(DataDir, "WebView2")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Umbra", "WebView2");
}
