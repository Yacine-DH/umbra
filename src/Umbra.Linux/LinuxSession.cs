using System.Diagnostics;
using System.Globalization;
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
/// <item>the desktop accent colour (portal <c>org.freedesktop.appearance accent-color</c>, GNOME 47+, KDE): "Match system accent";</item>
/// <item>the time format: GNOME's 12/24-hour switch (portal <c>org.gnome.desktop.interface clock-format</c>) and <c>LC_TIME</c>
/// (<see cref="DesktopLocale"/>): <c>settings.hour12</c> and <c>settings.locale</c>.</item>
/// </list>
/// Each source is optional: a missing service only means that signal never comes.
/// </summary>
sealed class LinuxSession
{
    public static readonly TimeSpan ResumeDelay = TimeSpan.FromSeconds(3);

    readonly UiThread _ui;
    bool _locked, _saver, _displayOff, _onBattery;
    string? _zone;
    long _lockedAt;

    /// <summary>Widgets should pause: the session is locked, or the display is off (Windows pauses on both too).</summary>
    public bool Paused => _locked || _displayOff;

    /// <summary>
    /// Mutter's <c>PowerSaveMode</c> (org.gnome.Mutter.DisplayConfig): 0 on, 1 standby, 2 suspend, 3 off, -1 unknown. Anything but
    /// "on" or "unknown" counts as off. Pure, so it is unit tested.
    /// </summary>
    public static bool DisplayOff(long powerSaveMode) => powerSaveMode is 1 or 2 or 3;
    /// <summary>The power-saver profile is active.</summary>
    public bool EnergySaver => _saver;

    /// <summary>On mains power (UPower's <c>OnBattery</c> is false, or UPower is not running: a desktop), for "Show only when on AC".</summary>
    public bool OnAc => !_onBattery;

    /// <summary>The power source changed (mains or battery).</summary>
    public event Action? AcChanged;

    /// <summary>Pausing or the power profile changed.</summary>
    public event Action? PowerChanged;
    /// <summary>Widgets should fetch now (reason for the log).</summary>
    public event Action<string>? RefreshNeeded;
    /// <summary>The clock jumped or the time zone changed.</summary>
    public event Action? TimeChanged;
    /// <summary>The desktop accent colour changed (<see cref="Prefs.SystemAccent"/> is already updated).</summary>
    public event Action? AccentChanged;
    /// <summary>The 12/24-hour setting changed (<see cref="TimeFormat"/> is already updated).</summary>
    public event Action? TimeFormatChanged;

    /// <summary>The locale and 12-hour flag widgets format times with (the desktop's, not only <c>LANG</c>'s; #359).</summary>
    public (string Locale, bool Hour12) TimeFormat { get; private set; } = ComputeTimeFormat(null);

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

        // Display off (GNOME): Mutter's PowerSaveMode, read once and followed. KDE has no stable D-Bus property for it yet, so
        // there only the lock pauses (most KDE setups lock before blanking).
        _ = ReadDisplayAsync(session);
        DBus.Subscribe(session, "org.gnome.Mutter.DisplayConfig", "org.freedesktop.DBus.Properties", "PropertiesChanged", "/org/gnome/Mutter/DisplayConfig", (_, v) =>
        {
            using var changed = v[1];
            var d = changed.Dict();
            if (d.TryGetValue("PowerSaveMode", out var m)) SetDisplayOff(DisplayOff(NumberOf(m)));
            foreach (var x in d.Values) x.Dispose();
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

        // Mains or battery (UPower): the "on AC" show-when rule.
        _ = ReadOnBatteryAsync(system);
        DBus.Subscribe(system, "org.freedesktop.UPower", "org.freedesktop.DBus.Properties", "PropertiesChanged", "/org/freedesktop/UPower", (_, v) =>
        {
            using var changed = v[1];
            var d = changed.Dict();
            if (d.TryGetValue("OnBattery", out var b)) SetOnBattery(BoolOf(b));
            foreach (var x in d.Values) x.Dispose();
        });

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
        // GNOME's 12/24-hour switch, through the same portal (also inside a Flatpak, where gsettings can't see the host's).
        _ = ReadClockFormatAsync(session);
        DBus.Subscribe(session, null, "org.freedesktop.portal.Settings", "SettingChanged", "/org/freedesktop/portal/desktop", (_, v) =>
        {
            using var ns = v[0];
            using var key = v[1];
            using var value = v[2];
            using var inner = value.Type == "v" ? value.Unbox : null;
            if (ns.Str == "org.freedesktop.appearance" && key.Str == "accent-color") SetAccent(ParseAccent(inner ?? value));
            else if (ns.Str == "org.gnome.desktop.interface" && key.Str == "clock-format") SetClockFormat((inner ?? value).AsString);
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

    static long NumberOf(DBus.Value v)
    {
        if (v.Type != "v") return v.Number;
        using var inner = v.Unbox;
        return inner.Number;
    }

    void SetDisplayOff(bool off)
    {
        if (off == _displayOff) return;
        _displayOff = off;
        Log.Write(off ? "Display off: widgets pause" : "Display on");
        PowerChanged?.Invoke();
    }

    async Task ReadDisplayAsync(IntPtr session)
    {
        try
        {
            using var r = await DBus.CallAsync(session, "org.gnome.Mutter.DisplayConfig", "/org/gnome/Mutter/DisplayConfig", "org.freedesktop.DBus.Properties", "Get",
                $"({V.Str("org.gnome.Mutter.DisplayConfig")}, {V.Str("PowerSaveMode")})", 2000);
            using var v = r[0];
            var off = DisplayOff(NumberOf(v));
            _ui.Post(_ => SetDisplayOff(off), null);
        }
        catch (DBusException) { } // not GNOME
    }

    static bool BoolOf(DBus.Value v)
    {
        if (v.Type == "b") return v.Bool;
        if (v.Type != "v") return false;
        using var inner = v.Unbox;
        return inner.Type == "b" && inner.Bool;
    }

    void SetOnBattery(bool on)
    {
        if (on == _onBattery) return;
        _onBattery = on;
        Log.Write(on ? "On battery power" : "On mains power");
        AcChanged?.Invoke();
    }

    async Task ReadOnBatteryAsync(IntPtr system)
    {
        try
        {
            using var r = await DBus.CallAsync(system, "org.freedesktop.UPower", "/org/freedesktop/UPower", "org.freedesktop.DBus.Properties", "Get",
                $"({V.Str("org.freedesktop.UPower")}, {V.Str("OnBattery")})", 2000);
            using var v = r[0];
            var on = BoolOf(v);
            _ui.Post(_ => SetOnBattery(on), null);
        }
        catch (DBusException) { } // no UPower: treated as mains power
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

    async Task ReadClockFormatAsync(IntPtr session)
    {
        try
        {
            using var r = await DBus.CallAsync(session, "org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop",
                "org.freedesktop.portal.Settings", "ReadOne", $"({V.Str("org.gnome.desktop.interface")}, {V.Str("clock-format")})", 3000);
            using var v = r[0];
            var format = v.AsString; // read here: the reply is freed when this method returns
            _ui.Post(_ => SetClockFormat(format), null);
        }
        catch (DBusException ex) { Log.Verbose($"No clock format from the portal: {ex.Message}"); }
    }

    void SetClockFormat(string? format)
    {
        var next = ComputeTimeFormat(format);
        if (next == TimeFormat) return;
        TimeFormat = next;
        Log.Verbose($"Time format: {next.Locale}, {(next.Hour12 ? "12" : "24")}-hour");
        TimeFormatChanged?.Invoke();
    }

    static (string Locale, bool Hour12) ComputeTimeFormat(string? clockFormat) =>
        DesktopLocale.Resolve(Environment.GetEnvironmentVariable, ReadPlasmaLocale(), clockFormat, CultureInfo.CurrentCulture);

    /// <summary>KDE's saved region formats (<c>~/.config/plasma-localerc</c>); null without KDE or when it can't be read.</summary>
    static string? ReadPlasmaLocale()
    {
        try
        {
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(config) || !Path.IsPathRooted(config))
                config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            var file = new FileInfo(Path.Combine(config, "plasma-localerc"));
            return file.Exists && file.Length < 64 * 1024 ? File.ReadAllText(file.FullName) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
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
