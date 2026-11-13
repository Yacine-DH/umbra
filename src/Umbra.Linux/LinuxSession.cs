using System.Diagnostics;
using System.Net.NetworkInformation;

namespace Umbra.Linux;

/// <summary>
/// What the desktop session tells widgets about, the Linux side of <c>PowerState</c>, <c>WakeRefresh</c>, the time-change
/// handling and <c>WindowsAccent</c>:
/// <list type="bullet">
/// <item>sleep and resume (logind <c>PrepareForSleep</c>), the network coming back and unlocking: widgets refresh now;</item>
/// <item>the screen lock (<c>org.gnome.ScreenSaver</c> / <c>org.freedesktop.ScreenSaver</c> <c>ActiveChanged</c>): widgets pause;</item>
/// <item>the power-saver profile (power-profiles-daemon <c>ActiveProfile</c>): the Energy Saver policy;</item>
/// <item>clock jumps (wall clock against a monotonic clock) and time-zone changes: umbra.onTimeChanged;</item>
/// <item>the desktop accent colour (portal <c>org.freedesktop.appearance accent-color</c>, GNOME 47+, KDE): "Match system accent".</item>
/// </list>
/// Each source is optional: a missing service only means that signal never comes.
/// </summary>
sealed class LinuxSession
{
    public static readonly TimeSpan ResumeDelay = TimeSpan.FromSeconds(3);

    readonly UiThread _ui;
    bool _locked, _saver;
    string? _zone;
    long _lockedAt;

    /// <summary>Widgets should pause (the session is locked).</summary>
    public bool Paused => _locked;
    /// <summary>The power-saver profile is active.</summary>
    public bool EnergySaver => _saver;

    /// <summary>Pausing or the power profile changed.</summary>
    public event Action? PowerChanged;
    /// <summary>Widgets should fetch now (reason for the log).</summary>
    public event Action<string>? RefreshNeeded;
    /// <summary>The clock jumped or the time zone changed.</summary>
    public event Action? TimeChanged;
    /// <summary>The desktop accent colour changed (<see cref="Prefs.SystemAccent"/> is already updated).</summary>
    public event Action? AccentChanged;

    public LinuxSession(UiThread ui) { _ui = ui; }

    /// <summary>The accent from the portal's (r, g, b) in 0-1, made readable on black like the Windows accent; null when unset (out of range).</summary>
    public static Prefs.AccentPair? AccentFromPortal(double r, double g, double b)
    {
        if (r is < 0 or > 1 || g is < 0 or > 1 || b is < 0 or > 1 || double.IsNaN(r + g + b)) return null;
        var c = ((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
        return Prefs.FromWindows(c, c);
    }

    /// <summary>True when the wall clock moved more than 2 s away from the monotonic clock between two ticks (someone set the time, NTP stepped it).</summary>
    public static bool ClockJumped(TimeSpan wallElapsed, TimeSpan monotonicElapsed) =>
        Math.Abs((wallElapsed - monotonicElapsed).TotalSeconds) > 2;

    public void Start()
    {
        var system = DBus.SystemBus;
        var session = DBus.Session;

        // Sleep and resume.
        DBus.Subscribe(system, "org.freedesktop.login1", "org.freedesktop.login1.Manager", "PrepareForSleep", "/org/freedesktop/login1", (signal, v) =>
        {
            using var going = v[0];
            if (going.Bool) { Log.Write("Going to sleep"); return; }
            Log.Write("Resumed from sleep");
            _ = Task.Delay(ResumeDelay).ContinueWith(_ => _ui.Post(_ => RefreshNeeded?.Invoke("resume"), null));
        });

        // Screen lock: GNOME and KDE both send ActiveChanged(bool) on their screensaver interface.
        foreach (var iface in new[] { "org.gnome.ScreenSaver", "org.freedesktop.ScreenSaver" })
            DBus.Subscribe(session, null, iface, "ActiveChanged", null, (_, v) =>
            {
                using var active = v[0];
                SetLocked(active.Bool);
            });

        // Power profile: read once, then follow changes (both the old and the new bus name of power-profiles-daemon).
        foreach (var (name, path) in new[] { ("net.hadess.PowerProfiles", "/net/hadess/PowerProfiles"), ("org.freedesktop.UPower.PowerProfiles", "/org/freedesktop/UPower/PowerProfiles") })
        {
            _ = ReadProfileAsync(system, name, path);
            DBus.Subscribe(system, name, "org.freedesktop.DBus.Properties", "PropertiesChanged", path, (_, v) =>
            {
                using var changed = v[1];
                var d = changed.Dict();
                if (d.TryGetValue("ActiveProfile", out var p)) SetSaver(p.AsString == "power-saver");
                foreach (var x in d.Values) x.Dispose();
            });
        }

        // Time zone: timedated says so at once; the poll below also catches it (and clock jumps) where it isn't running.
        DBus.Subscribe(system, "org.freedesktop.timedate1", "org.freedesktop.DBus.Properties", "PropertiesChanged", "/org/freedesktop/timedate1",
            (_, _) => CheckZone());
        _zone = TimeChange.Signature();
        var wall = DateTime.UtcNow;
        var mono = Stopwatch.StartNew();
        UiThread.Every(5000, () =>
        {
            var nowWall = DateTime.UtcNow;
            var jumped = ClockJumped(nowWall - wall, mono.Elapsed);
            wall = nowWall;
            mono.Restart();
            if (jumped) { Log.Write("The system clock changed"); TimeChanged?.Invoke(); _zone = TimeChange.Signature(); }
            else CheckZone();
            return true;
        });

        // The network coming back (NetworkManager or systemd-networkd: .NET watches the kernel's netlink socket).
        NetworkChange.NetworkAvailabilityChanged += (_, e) =>
        {
            if (e.IsAvailable) _ui.Post(_ => RefreshNeeded?.Invoke("network"), null);
        };

        // Accent colour from the desktop portal.
        _ = ReadAccentAsync(session);
        DBus.Subscribe(session, null, "org.freedesktop.portal.Settings", "SettingChanged", "/org/freedesktop/portal/desktop", (_, v) =>
        {
            using var ns = v[0];
            using var key = v[1];
            if (ns.Str != "org.freedesktop.appearance" || key.Str != "accent-color") return;
            using var value = v[2];
            using var inner = value.Type == "v" ? value.Unbox : null;
            SetAccent(ParseAccent(inner ?? value));
        });
    }

    void SetLocked(bool locked)
    {
        if (locked == _locked) return;
        _locked = locked;
        Log.Write(locked ? "Session locked: widgets pause" : "Session unlocked");
        if (locked) _lockedAt = Environment.TickCount64;
        PowerChanged?.Invoke();
        // Back after a while: fetch now instead of waiting for the next tick.
        if (!locked && Environment.TickCount64 - _lockedAt > 60_000) RefreshNeeded?.Invoke("unlock");
    }

    void SetSaver(bool on)
    {
        if (on == _saver) return;
        _saver = on;
        Log.Write(on ? "Power saver is on" : "Power saver is off");
        PowerChanged?.Invoke();
    }

    async Task ReadProfileAsync(IntPtr system, string name, string path)
    {
        try
        {
            using var r = await DBus.CallAsync(system, name, path, "org.freedesktop.DBus.Properties", "Get", $"({V.Str(name)}, {V.Str("ActiveProfile")})", 2000);
            using var v = r[0];
            var on = v.AsString == "power-saver";
            _ui.Post(_ => SetSaver(on), null);
        }
        catch (DBusException) { } // not running, or not under this name
    }

    void CheckZone()
    {
        var now = TimeChange.Signature();
        if (!TimeChange.ZoneChanged(_zone, now)) return;
        _zone = now;
        Log.Write("The time zone changed");
        TimeChanged?.Invoke();
    }

    async Task ReadAccentAsync(IntPtr session)
    {
        try
        {
            using var r = await DBus.CallAsync(session, "org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop",
                "org.freedesktop.portal.Settings", "ReadOne", $"({V.Str("org.freedesktop.appearance")}, {V.Str("accent-color")})", 3000);
            using var v = r[0];
            using var inner = v.Type == "v" ? v.Unbox : null;
            var accent = ParseAccent(inner ?? v); // read here: the reply is freed when this method returns
            _ui.Post(_ => SetAccent(accent), null);
        }
        catch (DBusException ex) { Log.Verbose($"No accent colour from the portal: {ex.Message}"); }
    }

    static Prefs.AccentPair? ParseAccent(DBus.Value v)
    {
        if (v.Type != "(ddd)") return null;
        using var r = v[0];
        using var g = v[1];
        using var b = v[2];
        return AccentFromPortal(r.Double, g.Double, b.Double);
    }

    void SetAccent(Prefs.AccentPair? accent)
    {
        if (accent == Prefs.SystemAccent) return;
        Prefs.SystemAccent = accent;
        Log.Verbose($"Desktop accent: {accent?.Accent ?? "none"}");
        AccentChanged?.Invoke();
    }
}
