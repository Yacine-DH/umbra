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
        // GitHub's latest-release JSON is a few KB; anything over 1 MB is refused unread.
        var c = new HttpClient(Prefs.CreateHandler()) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = MaxFeedBytes };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Umbra/{AppInfo.Version}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    });

    /// <summary>The largest feed read.</summary>
    public const int MaxFeedBytes = 1024 * 1024;

    readonly Action<string> _log;
    readonly Func<bool> _enabled;
    readonly Action _changed;
    readonly Fetcher _fetch;
    readonly string _feed;
    readonly string _current;
    readonly string? _arch;
    Task? _inflight;
    DateTime? _lastAttemptUtc;
    bool _lastFailed;

    public LinuxUpdater(Func<bool> enabled, Action changed, bool flatpak, Fetcher? fetch = null, string? feed = null,
        string? current = null, string? arch = null, Action<string>? log = null)
    {
        _enabled = enabled;
        _changed = changed;
        Flatpak = flatpak;
        _fetch = fetch ?? ((url, ct) => Http.Value.GetStringAsync(url, ct));
        _feed = feed ?? Updates.ReleasesApi;
        _current = current ?? AppInfo.Version;
        _arch = arch ?? Updates.LinuxArch(RuntimeInformation.OSArchitecture);
        _log = log ?? Log.Write;
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

    /// <summary>
    /// Starts the daily check: the first one after the desktop settled, then an hourly tick that checks once the last
    /// attempt is 24 hours old by the wall clock (GLib timers stop while the machine sleeps, so a 24 h timer could wait days).
    /// </summary>
    public void Start()
    {
        if (Flatpak) { _log("Update check skipped: Flatpak updates Umbra"); return; }
        UiThread.Every(8_000, () => { _ = CheckAsync(manual: false); return false; });
        UiThread.Every((uint)TimeSpan.FromHours(1).TotalMilliseconds, () => { CheckIfDue(DateTime.UtcNow); return true; });
    }

    /// <summary>Runs an automatic check when <see cref="IsDue"/> says so (the hourly tick).</summary>
    public void CheckIfDue(DateTime utcNow)
    {
        if (IsDue(_lastAttemptUtc, _lastFailed, utcNow)) _ = CheckAsync(manual: false);
    }

    /// <summary>Due 24 hours after the last attempt, or 1 hour after a failed one; never before the first attempt (the start check does that).</summary>
    public static bool IsDue(DateTime? lastAttemptUtc, bool lastFailed, DateTime utcNow) =>
        lastAttemptUtc is { } last && utcNow - last >= (lastFailed ? TimeSpan.FromHours(1) : TimeSpan.FromHours(24));

    /// <summary>
    /// Queries the feed. An automatic check does nothing when the user turned update checks off; a manual one (Settings,
    /// <c>umbractl update check</c>) always asks. <paramref name="download"/> is ignored: Linux never downloads an update.
    /// </summary>
    public Task CheckAsync(bool manual, bool download = false)
    {
        if (Flatpak) return Task.CompletedTask;
        // A check already running answers this one too, so a manual check never returns the state from before it.
        if (_inflight is { IsCompleted: false } running) return running;
        if (!manual && !_enabled()) return Task.CompletedTask;
        return _inflight = RunAsync();
    }

    async Task RunAsync()
    {
        _lastAttemptUtc = DateTime.UtcNow;
        try
        {
            if (_arch is null) throw new PlatformNotSupportedException($"No Linux build is published for {RuntimeInformation.OSArchitecture}");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            // Off the UI thread: the first request loads the HTTP and TLS stack synchronously, long enough to stall the control socket.
            var json = await Task.Run(() => _fetch(_feed, cts.Token), cts.Token);
            if (json.Length > MaxFeedBytes) throw new InvalidDataException("The release feed is larger than 1 MB");
            var rel = Updates.ParseLinuxNotice(json, _arch);
            Available = rel is not null && Updates.IsNewer(_current, rel.Version) ? rel : null;
            Error = null;
            _lastFailed = false;
            if (Available is not null) _log($"Update available: {Available.Version} ({Available.PageUrl})");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = ex.Message;
            _lastFailed = true;
            _log($"Update check failed: {ex.Message}");
        }
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
