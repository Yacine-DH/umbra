using System.Text.Json.Nodes;
using Umbra.Linux;

namespace Umbra;

/// <summary>
/// The Linux host: owns settings, the widget windows and the pieces automation talks to (<see cref="IUmbraHost"/>).
/// The skeleton from #179: widgets load and talk to the host. Pinning, edit mode, Settings, tray, hotkeys, notifications
/// and capture are separate issues of epic #172, and say so in the log when asked for.
/// </summary>
sealed class App : IUmbraHost
{
    readonly Gtk.Application _gtk;
    readonly UiThread _ui;
    readonly Dictionary<string, WidgetWindow> _windows = new();
    readonly IAutostart _autostart;

    public Settings Settings { get; private set; }
    public List<WidgetManifest> Manifests { get; private set; }
    public Layouts Layouts { get; } = Layouts.Load();

    /// <summary>No host-drawn approval prompt exists on Linux yet (#194), so every request is refused: nothing runs unapproved.</summary>
    public CommandService Commands { get; } = new(CommandApprovals.Shared, _ => Task.FromResult(false), scripts: UserScripts.Shared);

    public IUpdater Updater { get; } = new LinuxUpdater();

    public bool EditMode => !Settings.General.LockAll;
    public bool SafeMode { get; private set; }
    public bool WidgetsHidden { get; private set; }
    public bool HiddenByFullscreen => false;
    public bool Peeking => false;
    public bool SettingsOpen => false;

    public App(Gtk.Application gtk, UiThread ui, bool safe)
    {
        _gtk = gtk;
        _ui = ui;
        SafeMode = safe;
        Settings = Settings.Load();
        try { WidgetTrash.Purge(); } catch (Exception ex) { Log.Write($"Trash purge failed: {ex.Message}"); }
        Prefs.Configure(Settings.General);
        Manifests = WidgetManifest.DiscoverAll();
        WebHost.Manifests = () => Manifests;
        if (!WebHost.SandboxAvailable)
            Log.Write($"Sandbox extension missing ({WebHost.ExtensionDir}): no widget is opened. Error: this build is incomplete.");
        _autostart = LinuxAutostart.Create(Environment.GetEnvironmentVariable, File.Exists, AppPaths.ExePath, AppPaths.IsPortable, portal: null);
        Log.Write($"Umbra started, {Manifests.Count} widget(s) found");

        var css = Gtk.CssProvider.New();
        css.LoadFromString("window.umbra-widget { background: transparent; }");
        if (Gdk.Display.GetDefault() is { } display)
            Gtk.StyleContext.AddProviderForDisplay(display, css, 600 /* GTK_STYLE_PROVIDER_PRIORITY_APPLICATION */);

        Commands.ServicesChanged += _ => _ui.Post(_ => SettingsChangedElsewhere(), null);
        SyncWindows();
    }

    /// <summary>Runs work on the UI thread (socket requests arrive on worker threads) and returns its result.</summary>
    public Task<T> OnUi<T>(Func<Task<T>> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(async _ =>
        {
            try { tcs.SetResult(await work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }, null);
        return tcs.Task;
    }

    /// <summary>Logs once that a feature has no Linux implementation yet, naming the issue that brings it.</summary>
    static void NotYet(string what, int issue) => Log.Write($"{what} is not available on Linux yet (#{issue})");

    // ---------- monitors, startup, diagnostics ----------

    public List<MonitorInfo> Monitors()
    {
        var list = new List<MonitorInfo>();
        if (Gdk.Display.GetDefault() is not { } display) return list;
        var monitors = display.GetMonitors();
        for (uint i = 0; i < monitors.GetNItems(); i++)
        {
            if (monitors.GetObject(i) is not Gdk.Monitor m) continue;
            m.GetGeometry(out var g);
            var bounds = new Rectangle(g.X, g.Y, g.Width, g.Height);
            // GDK 4 does not report a work area or a primary monitor; the first one stands in.
            list.Add(new MonitorInfo(m.GetConnector() ?? $"monitor-{i + 1}", bounds, bounds, i == 0));
        }
        return list;
    }

    public bool StartupEnabled { get => _autostart.IsEnabled(); set => _autostart.Set(value); }

    public string Diagnostics()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"- Umbra {AppInfo.Version} (Linux host){(AppPaths.IsPortable ? ", portable" : "")}{(SafeMode ? ", safe mode" : "")}");
        sb.AppendLine($"- System: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"- Session: {Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "unknown"}, desktop {Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "unknown"}");
        sb.AppendLine($"- GTK {Gtk.Functions.GetMajorVersion()}.{Gtk.Functions.GetMinorVersion()}.{Gtk.Functions.GetMicroVersion()}, WebKitGTK {WebKit.Functions.GetMajorVersion()}.{WebKit.Functions.GetMinorVersion()}.{WebKit.Functions.GetMicroVersion()}");
        sb.AppendLine($"- Monitors: {string.Join(", ", Monitors().Select(m => $"{m.Bounds.Width}x{m.Bounds.Height}"))}");
        var enabled = Manifests.Where(m => Settings.Instances(m.Id).Any(i => Settings.For(m, i).Enabled)).Select(m => $"{m.Id} {m.Version}");
        sb.AppendLine($"- Widgets on: {string.Join(", ", enabled)}");
        return sb.ToString();
    }

    // ---------- windows ----------

    IEnumerable<IWidgetView> IUmbraHost.OpenWindows => _windows.Values;
    IWidgetView? IUmbraHost.Window(string instance) => Window(instance);
    public WidgetWindow? Window(string instance) => _windows.GetValueOrDefault(instance);
    public IEnumerable<WidgetWindow> OpenWindows => _windows.Values;

    /// <summary>No Settings window yet (#184): automation gets the plain message, and so does the log.</summary>
    public ISettingsView ShowSettings(bool background = false)
    {
        NotYet("The Settings window", 184);
        throw new Control.CommandException(NoSettingsView.Why);
    }

    /// <summary>The manifest and instance id for a widget id or an instance id ("sp500#2"), or null when unknown.</summary>
    public (WidgetManifest Manifest, string Instance)? Resolve(string id)
    {
        if (!InstanceId.IsValid(id)) return null;
        var m = Manifests.FirstOrDefault(x => x.Id == InstanceId.WidgetOf(id));
        if (m is null) return null;
        return Settings.Instances(m.Id).Contains(id) ? (m, id) : null;
    }

    public void SaveSettings() { Prefs.Configure(Settings.General); Settings.Save(); }

    bool WantVisible(string inst) => !WidgetsHidden && Groups.IsShownTab(Settings, inst);

    /// <summary>Opens a window for each enabled widget copy and closes the ones that were disabled or removed.</summary>
    public void SyncWindows()
    {
        Groups.Prune(Settings);
        foreach (var m in Manifests)
            foreach (var inst in Settings.Instances(m.Id))
            {
                // Without the sandbox extension a widget page would be unrestricted, so none is opened (fail closed).
                var enabled = Settings.For(m, inst).Enabled && !(SafeMode && m.Source != WidgetSource.BuiltIn) && WebHost.SandboxAvailable;
                var open = _windows.TryGetValue(inst, out var win);
                if (enabled && !open)
                {
                    win = new WidgetWindow(this, m, inst);
                    var created = win;
                    win.Closed += () => { if (_windows.GetValueOrDefault(inst) == created) _windows.Remove(inst); };
                    _windows[inst] = win;
                    if (WantVisible(inst)) win.Show();
                }
                else if (!enabled && open)
                {
                    win!.Close();
                }
            }
        var live = Manifests.SelectMany(m => Settings.Instances(m.Id)).ToHashSet();
        foreach (var inst in _windows.Keys.Where(k => !live.Contains(k)).ToList()) CloseWidget(inst);
        foreach (var (inst, w) in _windows) w.Visible = WantVisible(inst);
    }

    /// <summary>Closes one copy (an instance id), or every copy of a widget (its plain id).</summary>
    public void CloseWidget(string id)
    {
        foreach (var key in _windows.Keys.Where(k => k == id || (!id.Contains('#') && InstanceId.WidgetOf(k) == id)).ToList())
        {
            var w = _windows[key];
            _windows.Remove(key);
            w.Close();
        }
    }

    public void SettingsChangedElsewhere() { } // the Settings window and tray re-render here once they exist (#184, #191)
    public void RefreshEditPills() { }         // the edit pill is #183

    /// <summary>Re-scans widget folders after an install/remove and opens/closes windows to match.</summary>
    public void ReloadWidgets()
    {
        Manifests = WidgetManifest.DiscoverAll();
        foreach (var id in _windows.Keys.Where(id => Manifests.All(m => m.Id != InstanceId.WidgetOf(id))).ToList())
            CloseWidget(id);
        SaveSettings();
        SyncWindows();
    }

    /// <summary>Generated :root override of the active theme (empty for the default look).</summary>
    public string ThemeCss => Theme.CssFor(Settings.General.Theme);

    public void SetTheme(string id)
    {
        if (Theme.DiscoverAll().All(t => t.Id != id)) throw new InvalidOperationException($"No installed theme '{id}'");
        Settings.General.Theme = id;
        SaveSettings();
        PushAll();
    }

    void PushAll() { foreach (var w in _windows.Values) w.PushSettings(); }

    /// <summary>Turns "Arrange widgets" on or off and tells the pages; moving and resizing by hand arrive with #183.</summary>
    public void SetEditMode(bool on)
    {
        if (EditMode == on) return;
        Settings.General.LockAll = !on;
        SaveSettings();
        Log.Write($"Edit mode {(on ? "on" : "off")}");
        PushAll();
    }

    public void SetSafeMode(bool on)
    {
        if (SafeMode == on) return;
        SafeMode = on;
        Log.Write($"Safe mode {(on ? "on" : "off")}");
        SyncWindows();
    }

    public void Peek() => throw new Control.CommandException("Peek is not available on Linux yet (#182)");

    public void QuitSoon() => _ = Task.Delay(300).ContinueWith(_ => _ui.Post(_ => Quit(), null));

    /// <summary>Closes every widget and ends the main loop.</summary>
    public void Quit()
    {
        foreach (var id in _windows.Keys.ToList()) CloseWidget(id);
        _gtk.Quit();
    }

    // ---------- hotkeys (stored and validated; registering them is #192) ----------

    public JsonObject DescribeHotkeys() => new()
    {
        ["hotkeys"] = new JsonArray(Hotkey.Actions.Select(a => (JsonNode)new JsonObject
        {
            ["action"] = a.Id,
            ["label"] = a.Label,
            ["keys"] = Settings.Hotkeys.GetValueOrDefault(a.Id, ""),
            ["status"] = Settings.Hotkeys.ContainsKey(a.Id) ? "failed" : "off",
            ["error"] = Settings.Hotkeys.ContainsKey(a.Id) ? "Global hotkeys are not available on Linux yet" : null,
        }).ToArray()),
        ["widgetsHidden"] = WidgetsHidden,
    };

    public void SetHotkey(string action, string keys)
    {
        if (!Hotkey.IsAction(action)) throw new ArgumentException($"Unknown hotkey action '{action}'. Actions: {string.Join(", ", Hotkey.Actions.Select(a => a.Id))}");
        var next = new Dictionary<string, string>(Settings.Hotkeys);
        if (string.IsNullOrWhiteSpace(keys)) next.Remove(action);
        else
        {
            if (!Hotkey.TryParse(keys, out _, out _, out var err)) throw new ArgumentException(err);
            next[action] = Hotkey.Normalize(keys)!;
            if (Hotkey.Validate(next).TryGetValue(action, out var conflict)) throw new ArgumentException(conflict);
        }
        Settings.Hotkeys = next;
        SaveSettings();
    }

    public void ResetHotkeys()
    {
        Settings.Hotkeys = new();
        SaveSettings();
    }

    // ---------- layouts ----------

    string CurrentFingerprint() => Placement.Fingerprint(Monitors());

    public JsonArray LayoutsJson() => Layouts.ToJson(CurrentFingerprint());
    public void SaveLayout(string name) => Layouts.Capture(name, Settings, CurrentFingerprint());
    public void RenameLayout(string name, string newName) => Layouts.Rename(name, newName);
    public bool DeleteLayout(string name) => Layouts.Delete(name);

    public int ApplyLayout(string name)
    {
        var n = Layouts.Apply(name, Settings);
        SaveSettings();
        SyncWindows();
        PushAll();
        return n;
    }

    // ---------- packages, camera ----------

    public Task<bool> OpenPackageAsync(string path)
    {
        NotYet("Installing a .umbrawidget package with its review", 184);
        return Task.FromResult(false);
    }

    /// <summary>The camera has no Linux backend, so no widget is ever approved for it.</summary>
    public bool CameraApproved(string widgetId) => false;

    // ---------- tab groups (the model; the tab strip is #183) ----------

    void ChangeGroups(Action<Settings> change)
    {
        change(Settings);
        Groups.Prune(Settings);
        SaveSettings();
        SyncWindows();
        PushAll();
    }

    public JsonArray GroupsJson() => new(Settings.Groups.Select(kv => (JsonNode)new JsonObject
    {
        ["id"] = kv.Key,
        ["name"] = kv.Value.Name,
        ["members"] = new JsonArray(kv.Value.Members.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()),
        ["active"] = Groups.ActiveOf(kv.Value),
        ["cycleSeconds"] = kv.Value.CycleSeconds,
        ["icons"] = new JsonObject(kv.Value.Members
            .Where(m => Settings.Widgets.TryGetValue(m, out var w) && w.TabIcon is not null)
            .Select(m => KeyValuePair.Create(m, (JsonNode?)JsonValue.Create(Settings.Widgets[m].TabIcon)))),
        ["suggestedIcons"] = new JsonObject(kv.Value.Members
            .Where(m => Settings.Widgets.TryGetValue(m, out var w) && w.SuggestedTabIcon is not null)
            .Select(m => KeyValuePair.Create(m, (JsonNode?)JsonValue.Create(Settings.Widgets[m].SuggestedTabIcon)))),
    }).ToArray());

    public string CreateGroup(string name, string? instance)
    {
        var id = "";
        ChangeGroups(s => id = Groups.Create(s, name, instance));
        return id;
    }

    public void JoinGroup(string groupId, string instance) => ChangeGroups(s => Groups.Join(s, groupId, instance));
    public void LeaveGroup(string instance) => ChangeGroups(s => Groups.Leave(s, instance));
    public void ActivateTab(string groupId, string instance) => ChangeGroups(s => Groups.Activate(s, groupId, instance));
    public void StepTab(string groupId, int direction) => ChangeGroups(s => Groups.Step(s, groupId, direction));
    public void ReorderTab(string groupId, string instance, int index) => ChangeGroups(s => Groups.Reorder(s, groupId, instance, index));

    public void ConfigureGroup(string groupId, string? name, int? cycleSeconds) => ChangeGroups(s =>
    {
        if (!s.Groups.TryGetValue(groupId, out var g)) throw new KeyNotFoundException($"No group '{groupId}'");
        if (name is not null) g.Name = Groups.CleanName(name);
        if (cycleSeconds is { } c) g.CycleSeconds = c <= 0 ? 0 : c;
    });

    public void DeleteGroup(string groupId) => ChangeGroups(s =>
    {
        if (!s.Groups.Remove(groupId)) throw new KeyNotFoundException($"No group '{groupId}'");
    });

    public void SetTabIcon(string instance, string? icon)
    {
        if (!Settings.Widgets.TryGetValue(instance, out var ws)) throw new KeyNotFoundException($"No widget copy '{instance}'");
        if (string.IsNullOrEmpty(icon)) icon = null;
        else if (Groups.IconPath(icon) is null) throw new ArgumentException($"Unknown tab icon '{icon}'");
        ws.TabIcon = icon;
        SaveSettings();
    }

    // ---------- copies ----------

    public void ResetPosition(string instance)
    {
        if (_windows.TryGetValue(instance, out var w)) w.ResetPosition();
        else if (Settings.Widgets.TryGetValue(instance, out var ws)) { ws.X = ws.Y = null; SaveSettings(); }
    }

    /// <summary>Adds another copy of a widget and returns its instance id.</summary>
    public string AddInstance(WidgetManifest m)
    {
        var inst = Settings.AddInstance(m) ?? throw new InvalidOperationException($"A widget can have at most {InstanceId.MaxInstances} copies");
        SaveSettings();
        SyncWindows();
        return inst;
    }

    /// <summary>Removes an extra copy with its settings and stored data. The first copy can only be disabled.</summary>
    public void RemoveInstance(string instance)
    {
        if (!InstanceId.IsExtra(instance) || !Settings.Widgets.ContainsKey(instance))
            throw new InvalidOperationException($"'{instance}' is not an extra copy of a widget");
        CloseWidget(instance);
        Settings.Widgets.Remove(instance);
        WidgetStore.Delete(instance);
        SaveSettings();
    }

    public void ForgetInstances(string widgetId)
    {
        Settings.RemoveExtraInstances(widgetId);
        SaveSettings();
    }

    bool _updatingAll;

    public async Task<UpdateAllResult> UpdateAllWidgetsAsync(Action<int, int, string>? progress = null)
    {
        if (_updatingAll) throw new InvalidOperationException("Widgets are already being updated");
        _updatingAll = true;
        try
        {
            var catalog = await Community.FetchCatalogAsync();
            // Never silently accepts new access: such an update is reported as skipped and the widget keeps running.
            var result = await Community.UpdateAllAsync(Community.Outdated(catalog, Manifests),
                entry => Community.UpdateWithoutNewAccessAsync(entry, CloseWidget), progress);
            ReloadWidgets(); // also reopens a widget whose update failed
            return result;
        }
        finally { _updatingAll = false; }
    }

    /// <summary>Replaces settings (positions included) and widget data from a validated backup; approvals are untouched.</summary>
    public void RestoreBackup(Backup.Parsed backup)
    {
        Backup.WriteData(backup, WidgetStore.CopyIds().Concat(Settings.Widgets.Keys));
        foreach (var id in _windows.Keys.ToList()) CloseWidget(id);
        var s = backup.Settings;
        if (Theme.DiscoverAll().All(t => t.Id != s.General.Theme)) s.General.Theme = Theme.DefaultId;
        s.General.LegacyRestoreDone = true;
        s.General.LastSeenVersion = Settings.General.LastSeenVersion;
        s.General.TourDone = Settings.General.TourDone;
        s.SettingsPanel = Settings.SettingsPanel;
        Settings = s;
        SaveSettings();
        SyncWindows();
    }

    /// <summary>Stands in for the Settings window until #184: every call fails with a clear message instead of a crash.</summary>
    sealed class NoSettingsView : ISettingsView
    {
        public const string Why = "The Settings window is not available on Linux yet (#184)";
        // CommandException: reported to the caller as the plain message, without a stack trace in the log.
        public Task ShowPageAsync(string page) => Task.FromException(new Control.CommandException(Why));
        public Task<byte[]> CaptureAsync(string page) => Task.FromException<byte[]>(new Control.CommandException(Why));
        public void Close() { }
    }
}

/// <summary>The update check on Linux: nothing yet. It will only ever notify (open the release page), never install (#197).</summary>
sealed class LinuxUpdater : IUpdater
{
    public ReleaseInfo? Available => null;
    public Task CheckAsync(bool manual, bool download = false) => Task.CompletedTask;
    public JsonObject ToJson() => new() { ["current"] = AppInfo.Version, ["available"] = null, ["supported"] = false };
}
