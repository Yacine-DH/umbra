using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Umbra;

public sealed class GeneralSettings
{
    public int RefreshSeconds { get; set; } = 60;
    public bool Animations { get; set; } = true;
    /// <summary>Id of the active theme (see <see cref="Theme"/>); "default" is the built-in look.</summary>
    public string Theme { get; set; } = Umbra.Theme.DefaultId;
    /// <summary>Look for a newer Umbra release on GitHub at start and daily.</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>Send an anonymous +1 for a catalog widget when it is freshly installed (<see cref="InstallStats"/>), so Browse can show install counts.</summary>
    public bool CountInstalls { get; set; } = true;
    /// <summary>Set once the one-time restore of former built-in widgets (sp500) has finished; see <see cref="Restore"/>.</summary>
    public bool LegacyRestoreDone { get; set; }
    /// <summary>Version that last ran, so a newer one shows "What's new" once; "" on a first install. Host-owned: saves and backups never change it.</summary>
    public string LastSeenVersion { get; set; } = "";
    /// <summary>The welcome tour has been shown or offered (#242). Host-owned like <see cref="LastSeenVersion"/>: saves, restores and imports never change it.</summary>
    public bool TourDone { get; set; }
    /// <summary>Hide every widget while a fullscreen app or game is in the foreground, and show them again after.</summary>
    public bool HideOnFullscreen { get; set; }
    /// <summary>How long Peek (hotkey or "peek" command) keeps the widgets above other windows, 1-60 s.</summary>
    public int PeekSeconds { get; set; } = 5;
    /// <summary>Stretch widget intervals 4x while Windows Energy Saver is on (#104). Widgets always pause while the PC is locked or the display is off.</summary>
    public bool SlowOnEnergySaver { get; set; } = true;

    // Appearance (#61): see Prefs.AppearanceCss
    /// <summary>"default", "sharp" or "round": overrides the --radius-* tokens after the theme.</summary>
    public string CornerRadius { get; set; } = "default";
    /// <summary>"normal", "calm" (--motion-scale 2) or "off".</summary>
    public string AnimationSpeed { get; set; } = "normal";
    /// <summary>Allow notifications for every widget that declares them and has no own choice (<see cref="WidgetSettings.Notifications"/> null).</summary>
    public bool DefaultNotifications { get; set; }
    /// <summary>Window opacity 0.2-1 of widgets without their own (<see cref="WidgetSettings.Opacity"/> null); 1 keeps true black and ClearType.</summary>
    public double DefaultOpacity { get; set; } = 1.0;
    /// <summary>Scale of widgets that follow the default (<see cref="WidgetSettings.UseDefaultScale"/>), 50-300 %; new widgets follow it.</summary>
    public double DefaultScale { get; set; } = 0.85;
    /// <summary>Global Size (#233): multiplies every widget's own size, 0.5-1.5; clamped per window to 50-300 % of the manifest size.</summary>
    public double Size { get; set; } = 1.0;

    /// <summary>Draw the hairline border around widget windows (#61).</summary>
    public bool BorderEnabled { get; set; } = true;
    /// <summary>"#rrggbb" border color, or "" for the default.</summary>
    public string BorderColor { get; set; } = "";
    /// <summary>Interface font from <see cref="Prefs.Fonts"/> ("default" = Segoe UI Variable).</summary>
    public string UiFont { get; set; } = "default";
    /// <summary>Interface text size multiplier, 0.8-1.4.</summary>
    public double FontScale { get; set; } = 1.0;
    /// <summary>Solid instead of translucent lines and tints, and fully opaque windows.</summary>
    public bool ReduceTransparency { get; set; }
    /// <summary>"#rrggbb" accent overriding the theme's accent (#13), or "" to follow the theme.</summary>
    public string Accent { get; set; } = "";
    /// <summary>Brighter text tokens for readability on bright wallpapers.</summary>
    public bool LightText { get; set; }

    // Advanced (#65): see Prefs
    /// <summary>"errors", "normal" or "verbose".</summary>
    public string LogLevel { get; set; } = "normal";
    /// <summary>Devtools and a context menu in widget windows.</summary>
    public bool DeveloperMode { get; set; }
    /// <summary>GPU acceleration for WebView2; off passes --disable-gpu (restart needed).</summary>
    public bool HardwareAcceleration { get; set; } = true;
    /// <summary>Seconds to wait after sign-in before starting (only with --background), 0-300.</summary>
    public int StartupDelaySeconds { get; set; }
    /// <summary>"system", "none" or "https://host:port" (restart needed).</summary>
    public string Proxy { get; set; } = "system";
    /// <summary>Grid in physical pixels that widgets snap to while moved in edit mode; 0 = no grid.</summary>
    public int SnapGrid { get; set; }
    /// <summary>"shift" (default, #271): widgets move freely and snap only while Shift is held; "always": snap unless Shift is held. Files without it get the default.</summary>
    public string SnapMode { get; set; } = "shift";
    /// <summary>Snap to screen and other widgets' edges while moving in edit mode (see <see cref="SnapMode"/>).</summary>
    public bool SnapEdges { get; set; } = true;
    /// <summary>Gap in logical pixels (96 DPI) that widgets keep from a neighbour when snapping next to it; 0 = touch edge to edge.</summary>
    public int SnapGap { get; set; } = 12;
    /// <summary>Master switch for snapping (the edit-mode pill toggles it); the grid and edge settings above are kept while it is off.</summary>
    public bool SnapEnabled { get; set; } = true;
    /// <summary>
    /// One global lock (#234): true = widgets can't be moved, resized or zoomed by hand. Turning it off is "Arrange
    /// widgets" (edit mode). Owned by <c>App.SetEditMode</c>, never by a Settings save.
    /// </summary>
    public bool LockAll { get; set; } = true;
    /// <summary>When the monitor setup changes (dock/undock), apply the layout pinned to the new setup after a short pause. Off by default.</summary>
    public bool AutoSwitchLayouts { get; set; }
}

/// <summary>"Show only when" conditions of a widget; every set field must hold.</summary>
public sealed class ShowRules
{
    /// <summary>"HH:mm" start of the daily window (with <see cref="To"/>; wraps past midnight).</summary>
    public string? From { get; set; }
    public string? To { get; set; }
    /// <summary>true = only on AC power, false = only on battery.</summary>
    public bool? OnAc { get; set; }
    /// <summary>Only while this monitor (device name) is connected.</summary>
    public string? Monitor { get; set; }
}

public sealed class WidgetSettings
{
    public const double MinScale = 0.5, MaxScale = 3.0;

    double _scale = 1.0;

    public bool Enabled { get; set; } = true;
    public int? X { get; set; }
    public int? Y { get; set; }
    /// <summary>Device name of the monitor the widget lives on (e.g. \\.\DISPLAY2); null = not recorded yet, X/Y apply as they are.</summary>
    public string? Monitor { get; set; }
    /// <summary>Offset from that monitor's top-left corner, so the widget keeps its place when monitors change.</summary>
    public int? RelX { get; set; }
    public int? RelY { get; set; }
    /// <summary>Legacy per-widget lock, replaced by <see cref="GeneralSettings.LockAll"/> (#234): still read from old files, ignored and never written.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Locked { get => false; set { } }
    /// <summary>The user allows (true) or blocks (false) notifications (umbra.notify) for this widget; null = <see cref="GeneralSettings.DefaultNotifications"/>.</summary>
    public bool? Notifications { get; set; }
    /// <summary>Tab-strip icon chosen by the user (a name from shared/tabicons); null = the widget's own icon or a letter.</summary>
    public string? TabIcon { get; set; }
    /// <summary>Tab-strip icon the widget copy itself suggested with <c>umbra.setTabIcon</c> (validated by <see cref="Groups.CleanSuggestion"/>); only used while <see cref="TabIcon"/> is unset or unusable.</summary>
    public string? SuggestedTabIcon { get; set; }

    /// <summary>Size multiplier for the window and the page zoom, kept within 50-300 % and rounded to whole percent.</summary>
    public double Scale
    {
        get => _scale;
        set => _scale = ClampScale(value);
    }

    /// <summary>
    /// Free window size in logical (96-DPI) pixels after dragging an edge; both null = manifest size x <see cref="Scale"/>.
    /// <see cref="Scale"/> then holds the page zoom (the smaller stretch). Setting a scale clears them.
    /// </summary>
    public int? Width { get; set; }
    public int? Height { get; set; }
    /// <summary>
    /// <see cref="Scale"/> follows <see cref="GeneralSettings.DefaultScale"/> (#232): set for new widgets, cleared by any size the
    /// user picks. Missing in older files, so their stored scales stay as the user's own.
    /// </summary>
    public bool UseDefaultScale { get; set; }

    /// <summary>Own refresh interval in seconds (30-600) overriding the global one; null = global.</summary>
    public int? RefreshSeconds { get; set; }
    /// <summary>Own window opacity 0.2-1; null = <see cref="GeneralSettings.DefaultOpacity"/> (fully opaque unless changed).</summary>
    public double? Opacity { get; set; }
    /// <summary>Not shown at launch; revealed with the toggle-widgets hotkey or by configure startHidden=false (#66).</summary>
    public bool StartHidden { get; set; }
    /// <summary>Show only while these conditions hold (#66); null = always.</summary>
    public ShowRules? ShowWhen { get; set; }

    public JsonObject Options { get; set; } = new();

    /// <summary>Clamps to <see cref="MinScale"/>-<see cref="MaxScale"/> and rounds to 0.01; anything not a number becomes 1.</summary>
    public static double ClampScale(double value) =>
        double.IsFinite(value) ? Math.Round(Math.Clamp(value, MinScale, MaxScale), 2) : 1.0;
}

/// <summary>Where the Settings window was when it was last closed, so it opens there again (#111). Sizes are logical (96-DPI) pixels.</summary>
public sealed class SettingsPlacement
{
    /// <summary>Device name of the monitor the window was on; null = never saved.</summary>
    public string? Monitor { get; set; }
    /// <summary>Offset of the window's top-left corner from that monitor's top-left corner.</summary>
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>The window was maximized (X/Y/Width/Height are then the restored bounds).</summary>
    public bool Maximized { get; set; }
    /// <summary>Last open page (one of <see cref="SettingsWindow.Pages"/>); null = General.</summary>
    public string? Page { get; set; }

    public const double MinZoom = 0.5, MaxZoom = 2.0, ZoomStep = 0.05;
    double _zoom = 1.0;

    /// <summary>Page zoom of the Settings window, 50-200 % in 5 % steps like a browser (Ctrl+= / Ctrl+- / Ctrl+0, Ctrl+wheel).</summary>
    public double Zoom { get => _zoom; set => _zoom = ClampZoom(value); }

    /// <summary>Snaps to the 5 % grid within <see cref="MinZoom"/>-<see cref="MaxZoom"/>; zero, negative or not a number becomes 100 %.</summary>
    public static double ClampZoom(double value)
    {
        if (!double.IsFinite(value) || value <= 0) return 1.0;
        return Math.Round(Math.Clamp(Math.Round(value / ZoomStep) * ZoomStep, MinZoom, MaxZoom), 2);
    }

    /// <summary>One zoom step: <paramref name="direction"/> &gt; 0 zooms in, &lt; 0 out, 0 resets to 100 %.</summary>
    public static double StepZoom(double current, int direction) =>
        direction == 0 ? 1.0 : ClampZoom(ClampZoom(current) + Math.Sign(direction) * ZoomStep);
}

/// <summary>User settings persisted to %APPDATA%\Umbra\settings.json.</summary>
public sealed class Settings
{
    public GeneralSettings General { get; set; } = new();
    public Dictionary<string, WidgetSettings> Widgets { get; set; } = new();
    /// <summary>Global hotkeys by action id (see <see cref="Hotkey.Actions"/>), e.g. "edit-mode": "Ctrl+Alt+E". No entry = off.</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = new();
    /// <summary>Tab groups by id (see <see cref="WidgetGroup"/>); host-owned like positions, so a stale Settings save keeps them.</summary>
    public Dictionary<string, WidgetGroup> Groups { get; set; } = new();

    /// <summary>Last placement and page of the Settings window; owned by the window, never by a (possibly stale) Settings save or a backup.</summary>
    public SettingsPlacement SettingsPanel { get => _settingsPanel; set => _settingsPanel = value ?? new(); } // a null in the file means defaults
    SettingsPlacement _settingsPanel = new();

    static string FilePath => Path.Combine(AppPaths.DataDir, "settings.json");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (Exception ex)
        {
            Log.Write($"settings.json unreadable, using defaults: {ex.Message}");
        }
        return new();
    }

    public void Save()
    {
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write($"Could not save settings: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the settings of one copy of the widget (<paramref name="instance"/> = its <see cref="InstanceId"/>,
    /// null = the first copy), creating them and filling any missing option with its manifest default.
    /// </summary>
    public WidgetSettings For(WidgetManifest m, string? instance = null)
    {
        instance ??= m.Id;
        if (InstanceId.WidgetOf(instance) != m.Id) throw new ArgumentException($"'{instance}' is not a copy of '{m.Id}'");
        if (!Widgets.TryGetValue(instance, out var ws))
            Widgets[instance] = ws = new WidgetSettings { Scale = General.DefaultScale, UseDefaultScale = true };
        foreach (var (key, value) in m.DefaultOptions())
            if (!ws.Options.ContainsKey(key)) ws.Options[key] = value?.DeepClone();
        General.RefreshSeconds = Math.Clamp(General.RefreshSeconds, 30, 600);
        return ws;
    }

    /// <summary>Instance ids of a widget: the first copy always, then the extra ones in order. Old files only have the first.</summary>
    public List<string> Instances(string widgetId) =>
        Widgets.Keys.Where(k => k != widgetId && InstanceId.IsValid(k) && InstanceId.WidgetOf(k) == widgetId && InstanceId.IsExtra(k))
            .OrderBy(InstanceId.NumberOf).Prepend(widgetId).ToList();

    /// <summary>Creates the next free extra copy and returns its instance id, or null at <see cref="InstanceId.MaxInstances"/>.</summary>
    public string? AddInstance(WidgetManifest m)
    {
        if (Instances(m.Id).Count >= InstanceId.MaxInstances) return null;
        var n = 2;
        while (Widgets.ContainsKey(InstanceId.Make(m.Id, n))) n++;
        var id = InstanceId.Make(m.Id, n);
        For(m, id);
        return id;
    }

    /// <summary>Forgets every extra copy of a widget (when it is uninstalled); returns their ids.</summary>
    public List<string> RemoveExtraInstances(string widgetId)
    {
        var extras = Instances(widgetId).Skip(1).ToList();
        foreach (var e in extras) Widgets.Remove(e);
        Widgets.Remove(widgetId); // removed means gone: no stale entry that a restore could bring back
        return extras;
    }

    public JsonNode ToJson() => JsonSerializer.SerializeToNode(this, Json)!;

    public static Settings FromJson(JsonNode node) => node.Deserialize<Settings>(Json) ?? new();
}
