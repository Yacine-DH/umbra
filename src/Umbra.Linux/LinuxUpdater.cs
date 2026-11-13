using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Umbra.Linux;

/// <summary>
/// The update check on Linux (#197): once shortly after start and then every 24 hours it reads GitHub's latest release
/// (<see cref="Updates.ParseLinuxNotice"/>) and, when it is newer and has this machine's tarball, says so in Settings and
/// in the tray (the -attention icon and a menu item that opens the release page). It only ever notifies: nothing is
/// downloaded or run, whatever the caller asks (the self-update rules in CLAUDE.md are Windows-only). Under Flatpak it
/// never checks, because Flatpak (Flathub) updates the app.
/// </summary>
sealed class LinuxUpdater : IUpdater
{
    /// <summary>Fetches the feed text; injected so tests never touch the network.</summary>
    public delegate Task<string> Fetcher(string url, CancellationToken cancel);

    static readonly Lazy<HttpClient> Http = new(() =>
    {
        var c = new HttpClient(Prefs.CreateHandler()) { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Umbra/{AppInfo.Version}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    });

    readonly Func<bool> _enabled;
    readonly Action _changed;
    readonly Fetcher _fetch;
    readonly string _feed;
    readonly string _current;
    readonly string? _arch;
    bool _checking;

    public LinuxUpdater(Func<bool> enabled, Action changed, bool flatpak, Fetcher? fetch = null, string? feed = null,
        string? current = null, string? arch = null)
    {
        _enabled = enabled;
        _changed = changed;
        Flatpak = flatpak;
        _fetch = fetch ?? ((url, ct) => Http.Value.GetStringAsync(url, ct));
        _feed = feed ?? Updates.ReleasesApi;
        _current = current ?? AppInfo.Version;
        _arch = arch ?? Updates.LinuxArch(RuntimeInformation.OSArchitecture);
    }

    /// <summary>The real one: GitHub's API, or in Debug builds only <c>UMBRA_RELEASE_URL</c> (an https URL, or a file path for the headless test).</summary>
    public static LinuxUpdater Create(Func<bool> enabled, Action changed)
    {
        var flatpak = LinuxAppPaths.IsFlatpak(File.Exists);
#if DEBUG
        var hook = Environment.GetEnvironmentVariable("UMBRA_RELEASE_URL"); // test hook, never in release builds
        if (!string.IsNullOrEmpty(hook))
            return hook.StartsWith('/')
                ? new LinuxUpdater(enabled, changed, flatpak, (path, ct) => File.ReadAllTextAsync(path, ct), hook)
                : new LinuxUpdater(enabled, changed, flatpak, feed: hook);
#endif
        return new LinuxUpdater(enabled, changed, flatpak);
    }

    /// <summary>True under Flatpak: no check at all, Settings says Flatpak keeps Umbra current.</summary>
    public bool Flatpak { get; }
    public ReleaseInfo? Available { get; private set; }
    public DateTime? LastChecked { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Starts the daily check: the first one after the desktop settled, then every 24 hours.</summary>
    public void Start()
    {
        if (Flatpak) { Log.Write("Update check skipped: Flatpak updates Umbra"); return; }
        UiThread.Every(8_000, () => { _ = CheckAsync(manual: false); return false; });
        UiThread.Every((uint)TimeSpan.FromHours(24).TotalMilliseconds, () => { _ = CheckAsync(manual: false); return true; });
    }

    /// <summary>
    /// Queries the feed. An automatic check does nothing when the user turned update checks off; a manual one (Settings,
    /// <c>umbractl update check</c>) always asks. <paramref name="download"/> is ignored: Linux never downloads an update.
    /// </summary>
    public async Task CheckAsync(bool manual, bool download = false)
    {
        if (Flatpak || _checking) return;
        if (!manual && !_enabled()) return;
        _checking = true;
        try
        {
            if (_arch is null) throw new PlatformNotSupportedException($"No Linux build is published for {RuntimeInformation.OSArchitecture}");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            // Off the UI thread: the first request loads the HTTP and TLS stack synchronously, long enough to stall the control socket.
            var json = await Task.Run(() => _fetch(_feed, cts.Token), cts.Token);
            var rel = Updates.ParseLinuxNotice(json, _arch);
            Available = rel is not null && Updates.IsNewer(_current, rel.Version) ? rel : null;
            Error = null;
            if (Available is not null) Log.Write($"Update available: {Available.Version} ({Available.PageUrl})");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = ex.Message;
            Log.Write($"Update check failed: {ex.Message}");
        }
        finally { _checking = false; }
        LastChecked = DateTime.Now;
        _changed();
    }

    /// <summary>The same shape as the Windows updater's state, always in its notify-only form (no download, progress or restart).</summary>
    public JsonObject ToJson() => new()
    {
        ["enabled"] = _enabled(),
        ["supported"] = !Flatpak,
        ["flatpak"] = Flatpak,
        ["portable"] = AppPaths.IsPortable,
        ["current"] = _current,
        ["available"] = Available is null ? null : new JsonObject
        {
            ["version"] = Available.Version, ["url"] = Available.PageUrl, ["asset"] = Available.AssetName, ["notes"] = Available.Notes,
        },
        ["whatsNew"] = null,
        ["ready"] = false,
        ["button"] = Updates.ButtonState(Available is not null, ready: false, installing: false, progress: null, portable: true),
        ["installing"] = false,
        ["progress"] = null,
        ["lastChecked"] = LastChecked?.ToString("o"),
        ["error"] = Error,
    };
}
