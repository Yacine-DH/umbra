using System.Text.Json.Nodes;

namespace Umbra;

/// <summary>Owns the settings, the widget windows, the tray icon and the Settings window.</summary>
sealed class App : ApplicationContext, IUmbraHost
{
    public static readonly Icon AppIcon = LoadIcon();

    readonly Dictionary<string, WidgetWindow> _windows = new();
    readonly NotifyIcon _tray;
    readonly Notifier _notifier = new();
    Action? _balloonClick; // what a click on the current tray balloon does; set only by ShowBalloon (toasts carry their own URL)
    ToastService? _toasts;
    bool _toastsTried;
    readonly SynchronizationContext _ui;
    SettingsWindow? _settingsWindow;
    public UpdateService Updater { get; }

    /// <summary>Runs widget-declared local commands and the user's own scripts (Settings) behind the approval gate.</summary>
    public CommandService Commands { get; } = new(CommandApprovals.Shared, req => Task.FromResult(ApprovalDialog.Ask(req)), scripts: UserScripts.Shared);

    /// <summary>A script that keeps running started, ended or waits to restart: the widget, Settings and the tray show it.</summary>
    void OnServicesChanged(string widgetId)
    {
        CommandsChanged(widgetId);
        if (_settingsWindow is { IsDisposed: false }) _settingsWindow.ScriptsChanged();
        UpdateTrayStatus();
    }

    /// <summary>Whether some copy of the widget has a window (it is on, and not held back by safe mode).</summary>
    public bool HasOpenWindow(string widgetId) => _windows.Values.Any(w => w.Manifest.Id == widgetId && !w.IsDisposed);

    /// <summary>A window of the widget closed: when it was the last one, the scripts it keeps running stop with it.</summary>
    void WidgetWindowGone(string widgetId)
    {
        if (!_windows.Values.Any(w => w.Manifest.Id == widgetId && !w.IsDisposed)) Commands.StopWidget(widgetId);
    }

    /// <summary>The user saved or deleted a script: tell every open copy of that widget (umbra.onCommandsChange).</summary>
    public void CommandsChanged(string widgetId)
    {
        foreach (var w in _windows.Values) if (w.Manifest.Id == widgetId) w.NotifyCommandsChanged();
    }

    /// <summary>Webcam timelapse recordings (umbra.timelapse, permissions.camera). Started only from a widget click, never by automation.</summary>
    public TimelapseService Timelapse { get; } = new(TimelapseService.DefaultRoot);

    /// <summary>The one-time camera approval per widget (host prompt after a click; stored with the command approvals).</summary>
    public CameraConsent CameraConsent { get; } = new(CommandApprovals.Shared, name => Task.FromResult(ApprovalDialog.AskCamera(name)));
    /// <summary>The click-gated "Allow notifications?" prompt state (umbra.requestNotifications).</summary>
    public NotificationConsent NotificationConsent { get; } = new();

    public Settings Settings { get; private set; }
    public List<WidgetManifest> Manifests { get; private set; }

    /// <summary>
    /// App-wide "Arrange widgets" state: the global lock (<see cref="GeneralSettings.LockAll"/>, #234) is off, so
    /// widgets show an outline and a corner handle and can be moved, resized and zoomed. Locked by default.
    /// </summary>
    public bool EditMode => !Settings.General.LockAll;

    /// <summary>The widget copy last pressed in edit mode: the one the arrow keys nudge. Null outside edit mode.</summary>
    public string? EditSelection { get; private set; }

    /// <summary>All widgets temporarily hidden by the "toggle-widgets" hotkey. Not saved.</summary>
    public bool WidgetsHidden { get; private set; }

    readonly HotkeyService _hotkeys;
    readonly WindowsAccent _windowsAccent;

    /// <summary>Locked / display off / Energy Saver state (#104); widgets pause and slow down from it.</summary>
    public PowerState Power { get; }

    /// <summary>True while a fullscreen app hides the widgets (only with General.HideOnFullscreen). Not saved.</summary>
    public bool HiddenByFullscreen { get; private set; }
    public bool Peeking { get; private set; }
    readonly System.Windows.Forms.Timer _fullscreenTimer = new() { Interval = 750 };
    System.Windows.Forms.Timer? _peekTimer;

    bool ShouldHide => WidgetsHidden || HiddenByFullscreen;

    /// <summary>Instances with "start hidden" that the user has not revealed yet (#66). Not saved.</summary>
    readonly HashSet<string> _startHidden = new();
    readonly System.Windows.Forms.Timer _rulesTimer = new() { Interval = 15000 };

    /// <summary>Whether this widget copy may be on screen: global hide states, start-hidden and its "show only when" rules.</summary>
    bool WantVisible(string inst)
    {
        if (ShouldHide || _startHidden.Contains(inst) || !Groups.IsShownTab(Settings, inst)) return false;
        var rules = Settings.Widgets.TryGetValue(inst, out var ws) ? ws.ShowWhen : null;
        if (rules is null) return true;
        return Prefs.RulesAllow(rules, DateTime.Now.TimeOfDay,
            SystemInformation.PowerStatus.PowerLineStatus != PowerLineStatus.Offline,
            name => Screen.AllScreens.Any(s => s.DeviceName.Equals(name, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Shows or hides every widget window from the hotkey and fullscreen states. Only changes windows whose state differs, so nothing flickers.</summary>
    void ApplyVisibility()
    {
        foreach (var (inst, w) in _windows)
        {
            var want = WantVisible(inst);
            if (w.Visible != want) w.Visible = want;
        }
    }

    void CheckFullscreen()
    {
        var hide = Settings.General.HideOnFullscreen && Screens.FullscreenAppActive();
        if (hide == HiddenByFullscreen) return;
        HiddenByFullscreen = hide;
        if (hide) EndPeek();
        ApplyVisibility();
    }

    /// <summary>Raises the widgets above other windows for General.PeekSeconds (never activating them), then returns them to the bottom.</summary>
    public void Peek()
    {
        if (ShouldHide) return;
        DesktopPin.Peeking = true;
        Peeking = true;
        foreach (var w in _windows.Values) if (w.IsHandleCreated) DesktopPin.Raise(w.Handle);
        _peekTimer ??= new System.Windows.Forms.Timer();
        _peekTimer.Stop();
        _peekTimer.Interval = Math.Clamp(Settings.General.PeekSeconds, 1, 60) * 1000;
        _peekTimer.Tick -= PeekTick;
        _peekTimer.Tick += PeekTick;
        _peekTimer.Start();
    }

    void PeekTick(object? s, EventArgs e) => EndPeek();

    void EndPeek()
    {
        _peekTimer?.Stop();
        if (!Peeking) return;
        Peeking = false;
        DesktopPin.Peeking = false;
        foreach (var w in _windows.Values) if (w.IsHandleCreated) DesktopPin.Lower(w.Handle);
    }

    WakeRefresh? _wakeRefresh;

    /// <summary>Resume / network back / unlock (#102): widgets fetch now instead of waiting for their next tick.</summary>
    void RefreshAfterWake(string trigger)
    {
        Log.Verbose($"Refreshing widgets after {trigger}");
        foreach (var w in _windows.Values) w.RefreshData();
    }

    // SystemEvents raises these on its own thread, and a clock change often sends several in a row: coalesce them on the UI thread.
    readonly System.Windows.Forms.Timer _timeTimer = new() { Interval = 300 };
    string? _timeSignature;

    void OnTimeChanged(object? s, EventArgs e) => _ui.Post(_ => { _timeTimer.Stop(); _timeTimer.Start(); }, null);

    /// <summary>Settings broadcasts (WM_SETTINGCHANGE) are frequent; only a different time zone counts.</summary>
    void OnPreferenceChanged(object? s, Microsoft.Win32.UserPreferenceChangedEventArgs e) => _ui.Post(_ =>
    {
        if (!TimeChange.ZoneChanged(_timeSignature, TimeChange.Signature())) return;
        _timeTimer.Stop();
        _timeTimer.Start();
    }, null);

    void TimeTimerTick(object? s, EventArgs e)
    {
        _timeTimer.Stop();
        _timeSignature = TimeChange.Signature();
        foreach (var w in _windows.Values) w.NotifyTimeChanged();
    }

    void OnDisplayChanged(object? s, EventArgs e) => _ui.Post(_ =>
    {
        foreach (var w in _windows.Values) w.ReapplyPlacement();
        UpdateEditPills();
        // Windows sends several of these while monitors come and go; wait until it has settled.
        _layoutTimer.Stop();
        _layoutTimer.Start();
    }, null);

    /// <summary>Monitor setup the last time it settled; a layout is only switched when this changes.</summary>
    /// <remarks>Read and written only on the UI thread: <c>_layoutTimer</c> is a WinForms timer and <see cref="SwitchLayoutForSetup"/> runs from its Tick.</remarks>
    // Not computed in the constructor: touching Screen before the message loop runs makes WebView2 fail later with RPC_E_CHANGED_MODE.
    string? _knownSetup;
    readonly System.Windows.Forms.Timer _layoutTimer = new() { Interval = 2000 };

    /// <summary>After a monitor change settles: with the option on, applies the layout pinned to the new setup and says so in a balloon.</summary>
    void SwitchLayoutForSetup()
    {
        _layoutTimer.Stop();
        var setup = CurrentFingerprint();
        var first = _knownSetup is null;
        var previous = _knownSetup;
        _knownSetup = setup;
        if (first || setup == previous) return;
        if (!Settings.General.AutoSwitchLayouts || Layouts.FindPinned(setup) is not { } name) return;
        try
        {
            ApplyLayout(name);
            Log.Write($"Monitors changed, applied layout '{name}'");
            ShowBalloon(4000, "Umbra", $"Applied layout {name}", ToolTipIcon.None, null);
        }
        catch (Exception ex) { Log.Write($"Auto-switch to layout '{name}' failed: {ex.Message}"); }
    }

    /// <summary>Runs a hotkey action. Never activates a widget window; only Settings is brought forward, on request.</summary>
    void RunHotkey(string action)
    {
        switch (action)
        {
            case "edit-mode": SetEditMode(!EditMode); break;
            case "refresh-all": foreach (var w in _windows.Values) w.RefreshData(); break;
            case "toggle-widgets":
                WidgetsHidden = !WidgetsHidden;
                if (!WidgetsHidden) _startHidden.Clear();
                ApplyVisibility();
                break;
            case "peek": Peek(); break;
            case "open-settings": ShowSettings(); break;
        }
    }

    public System.Text.Json.Nodes.JsonObject DescribeHotkeys() => new()
    {
        ["hotkeys"] = _hotkeys.Describe(Settings.Hotkeys),
        ["backend"] = "win32",
        ["supported"] = true,
        ["widgetsHidden"] = WidgetsHidden,
    };

    /// <summary>Sets (or with empty keys clears) one hotkey, rejecting invalid or duplicate combinations; other apps' conflicts show as a failed status.</summary>
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
        ApplyHotkeys();
    }

    public void ResetHotkeys()
    {
        Settings.Hotkeys = new();
        ApplyHotkeys();
    }

    void ApplyHotkeys()
    {
        _hotkeys.Apply(Settings.Hotkeys);
        SaveSettings();
        SettingsChangedElsewhere();
    }

    /// <summary>Safe mode: community widgets stay closed (nothing is deleted or disabled in settings). Not saved, so the next normal start restores everything.</summary>
    public bool SafeMode { get; private set; }

    public Layouts Layouts { get; } = Layouts.Load();

    public void SetSafeMode(bool on)
    {
        if (SafeMode == on) return;
        SafeMode = on;
        Log.Write($"Safe mode {(on ? "on" : "off")}");
        SyncWindows();
        SettingsChangedElsewhere();
    }

    /// <summary>The monitor setup in use right now (see <see cref="Placement.Fingerprint"/>).</summary>
    static string CurrentFingerprint() => Placement.Fingerprint(Screens.All());

    /// <summary>Saves the current positions, sizes and enabled widgets under a name, pinned to the monitor setup in use.</summary>
    public void SaveLayout(string name)
    {
        Layouts.Capture(name, Settings, CurrentFingerprint());
        SettingsChangedElsewhere();
    }

    /// <summary>The saved layouts as Settings and umbractl list them, with the one pinned to the current monitors marked.</summary>
    public System.Text.Json.Nodes.JsonArray LayoutsJson() => Layouts.ToJson(CurrentFingerprint());

    public void RenameLayout(string name, string newName)
    {
        Layouts.Rename(name, newName);
        BuildTrayMenu();
        SettingsChangedElsewhere();
    }

    /// <summary>Pins a layout to the monitor setup in use, or unpins it.</summary>
    public void PinLayout(string name, bool pinned)
    {
        Layouts.SetPin(name, pinned ? CurrentFingerprint() : null);
        SettingsChangedElsewhere();
    }

    /// <summary>Restores the layout deleted last; returns its name or null.</summary>
    public string? UndoDeleteLayout()
    {
        var name = Layouts.UndoDelete();
        if (name is not null) SettingsChangedElsewhere();
        return name;
    }

    /// <summary>Switches to a saved layout live; returns how many widget copies it changed.</summary>
    public int ApplyLayout(string name)
    {
        var n = Layouts.Apply(name, Settings);
        SaveSettings();
        SyncWindows();
        foreach (var w in _windows.Values) w.PushSettings();
        SettingsChangedElsewhere();
        return n;
    }

    /// <summary>Runs a change to the tab groups, then saves, re-places the windows (the incoming tab takes the slot) and refreshes Settings.</summary>
    void ChangeGroups(Action<Settings> change)
    {
        change(Settings);
        Groups.Prune(Settings);
        SaveSettings();
        // Order matters: the outgoing tab goes first, the incoming one is moved and sized while still hidden, and only then shown,
        // so two tabs are never on screen together and the new one never flashes at its old bounds.
        foreach (var (inst, w) in _windows) if (w.Visible && !WantVisible(inst)) w.Visible = false;
        foreach (var w in _windows.Values) w.ReapplyPlacement();
        SyncWindows();
        foreach (var w in _windows.Values) w.PushSettings();
        LowerWidgets();
        foreach (var id in _tabStrips.Keys.ToList()) _tabSwitchedAt[id] = System.Environment.TickCount64;
        TickGroups();
        SettingsChangedElsewhere();
    }

    /// <summary>The groups for Settings and umbractl: id, name, members (instance ids in tab order), active tab and cycle interval.</summary>
    public System.Text.Json.Nodes.JsonArray GroupsJson() => new(Settings.Groups.Select(kv => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
    {
        ["id"] = kv.Key,
        ["name"] = kv.Value.Name,
        ["members"] = new System.Text.Json.Nodes.JsonArray(kv.Value.Members.Select(m => (System.Text.Json.Nodes.JsonNode)System.Text.Json.Nodes.JsonValue.Create(m)!).ToArray()),
        ["active"] = Groups.ActiveOf(kv.Value),
        ["cycleSeconds"] = kv.Value.CycleSeconds,
        ["icons"] = new System.Text.Json.Nodes.JsonObject(kv.Value.Members
            .Where(m => Settings.Widgets.TryGetValue(m, out var w) && w.TabIcon is not null)
            .Select(m => KeyValuePair.Create(m, (System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(Settings.Widgets[m].TabIcon)))),
        ["suggestedIcons"] = new System.Text.Json.Nodes.JsonObject(kv.Value.Members
            .Where(m => Settings.Widgets.TryGetValue(m, out var w) && w.SuggestedTabIcon is not null)
            .Select(m => KeyValuePair.Create(m, (System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(Settings.Widgets[m].SuggestedTabIcon)))),
    }).ToArray());

    /// <summary>Sets (or with null/empty clears) the tab icon of a widget copy; the strip redraws at once.</summary>
    public void SetTabIcon(string instance, string? icon)
    {
        if (!Settings.Widgets.TryGetValue(instance, out var ws)) throw new KeyNotFoundException($"No widget copy '{instance}'");
        if (string.IsNullOrEmpty(icon)) icon = null;
        else if (Groups.IconPath(icon) is null) throw new ArgumentException($"Unknown tab icon '{icon}'");
        ws.TabIcon = icon;
        SaveSettings();
        foreach (var id in _tabStrips.Keys.ToList()) _tabStrips[id].Redraw();
        SettingsChangedElsewhere();
    }

    /// <summary>
    /// Stores the tab icon a widget copy suggested (already validated; null clears it). It never touches the user's own choice, which wins in the strip.
    /// Saved only when it changed, then the strip redraws at once.
    /// </summary>
    public void SuggestTabIcon(string instance, string? icon)
    {
        if (!Settings.Widgets.TryGetValue(instance, out var ws) || ws.SuggestedTabIcon == icon) return;
        ws.SuggestedTabIcon = icon;
        SaveSettings();
        foreach (var id in _tabStrips.Keys.ToList()) _tabStrips[id].Redraw();
        SettingsChangedElsewhere();
    }

    /// <summary>Starts a group with one widget copy; returns the group id.</summary>
    public string CreateGroup(string name, string? instance = null)
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

    public void DeleteGroup(string groupId) => ChangeGroups(s =>
    {
        if (!s.Groups.Remove(groupId)) throw new KeyNotFoundException($"No group '{groupId}'");
    });

    public void ConfigureGroup(string groupId, string? name, int? cycleSeconds) => ChangeGroups(s =>
    {
        if (!s.Groups.TryGetValue(groupId, out var g)) throw new KeyNotFoundException($"No group '{groupId}'");
        if (name is not null) g.Name = Groups.CleanName(name);
        if (cycleSeconds is { } c) g.CycleSeconds = c <= 0 ? 0 : c;
    });

    public bool DeleteLayout(string name)
    {
        var ok = Layouts.Delete(name);
        SettingsChangedElsewhere();
        return ok;
    }

    /// <summary>
    /// The context that puts work on the UI thread. A plain SynchronizationContext can already be current at startup, and it posts
    /// to the thread pool: everything marshalled through it would then touch windows and WebViews off the UI thread.
    /// </summary>
    internal static SynchronizationContext UiContext(SynchronizationContext? current) =>
        current as WindowsFormsSynchronizationContext ?? new WindowsFormsSynchronizationContext();

    public App(bool openSettings, bool background = false, bool safe = false)
    {
        SafeMode = safe;
        _ui = UiContext(SynchronizationContext.Current);
        Settings = Settings.Load();
        try { WidgetTrash.Purge(); } catch (Exception ex) { Log.Write($"Trash purge failed: {ex.Message}"); }
        Prefs.Configure(Settings.General);
        _windowsAccent = new WindowsAccent(() => _ui.Post(_ => OnSystemAccentChanged(), null));
        Prefs.SystemAccent = _windowsAccent.Read();
        WebHost.WebSettings = Settings.General;
        Manifests = WidgetManifest.DiscoverAll();
        Log.Write($"Umbra started, {Manifests.Count} widget(s) found");

        Updater = new UpdateService(this);
        _ = Task.Run(Timelapse.PurgeLeftovers);
        DropStaleCameraApprovals();
        Timelapse.Changed += (inst, state) => _ui.Post(_ => OnTimelapseChanged(inst, state), null);
        Commands.ServicesChanged += widgetId => _ui.Post(_ => OnServicesChanged(widgetId), null);
        _tray = new NotifyIcon { Icon = AppIcon, Text = "Umbra", Visible = true };
        // One left click, like OneDrive and PowerToys. ShowSettings only focuses an open window, so a double-click never opens two.
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowSettings(); };
        _tray.ContextMenuStrip = new ContextMenuStrip();
        _tray.ContextMenuStrip.Opening += (_, _) => BuildTrayMenu();
        BuildTrayMenu();
        UpdateTrayStatus();
        _tray.BalloonTipClicked += (_, _) => _balloonClick?.Invoke();
        var announced = AnnounceUpdate();

        foreach (var (k, ws) in Settings.Widgets) if (ws.StartHidden) _startHidden.Add(k);
        Power = new PowerState(_ui); // before the windows: their settings payload reads it
        Power.Changed += () => { foreach (var w in _windows.Values) w.PushPower(); };
        SyncWindows();
        _hotkeys = new HotkeyService(RunHotkey);
        _hotkeys.Apply(Settings.Hotkeys);
        _fullscreenTimer.Tick += (_, _) => CheckFullscreen();
        _fullscreenTimer.Start();
        _tabTimer.Tick += (_, _) => TickGroups();
        _tabTimer.Start();
        _rulesTimer.Tick += (_, _) => ApplyVisibility();
        _rulesTimer.Start();
        _layoutTimer.Tick += (_, _) => SwitchLayoutForSetup();
        _ui.Post(_ => _knownSetup ??= CurrentFingerprint(), null); // first reading once the message loop runs
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        _timeSignature = TimeChange.Signature();
        _timeTimer.Tick += TimeTimerTick;
        Microsoft.Win32.SystemEvents.TimeChanged += OnTimeChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        _wakeRefresh = new WakeRefresh(_ui, RefreshAfterWake, () => Settings.General.RefreshSeconds);

        if (EditMode) _ui.Post(_ => UpdateEditPills(), null); // left unlocked last time: show the Done pill again

        // Former built-in widgets are fetched again once; a fresh install starts on Home.
        var restore = Restore.ToRestore(Settings, Manifests);
        if (restore.Count > 0)
        {
            _restoring.UnionWith(restore);
            _ = RestoreMissingAsync();
        }
        else if (!Settings.General.LegacyRestoreDone) Settings.General.LegacyRestoreDone = true;
        if (openSettings) ShowSettings(); // Home is the first page; with no widgets yet it offers a few to start with
        switch (Tour.Decide(Settings.General.TourDone, openSettings, announced || SafeMode))
        {
            case Tour.Plan.ShowNow:
                Settings.General.TourDone = true;
                SaveSettings();
                _ = ShowPageAsync(ShowSettings(), "welcome");
                break;
            case Tour.Plan.Offer: // an existing profile: ask once, clicking runs it, ignoring it counts as declining
                Settings.General.TourDone = true;
                SaveSettings();
                ShowBalloon(8000, "Umbra", "New: take a quick tour of Umbra?", ToolTipIcon.None, ReplayTour);
                break;
        }
    }

    static async Task ShowPageAsync(SettingsWindow win, string page)
    {
        try { await win.ShowPageAsync(page); }
        catch (Exception ex) { Log.Write($"Could not open {page}: {ex.Message}"); }
    }

    /// <summary>
    /// After a self-update, shows one balloon "Umbra updated to x.y" that opens About, where the release notes are.
    /// The version is recorded first, so it shows once per version; a first install shows nothing.
    /// </summary>
    bool AnnounceUpdate()
    {
        var current = UpdateService.CurrentVersion;
        var announce = Updates.ShouldAnnounce(Settings.General.LastSeenVersion, current);
        var next = Updates.NextLastSeen(Settings.General.LastSeenVersion, current);
        if (next != Settings.General.LastSeenVersion) { Settings.General.LastSeenVersion = next; SaveSettings(); }
        if (!announce) return false;
        Log.Write($"Updated to {current}");
        _ = ObserveAsync(Updater.LoadWhatsNewAsync(current), "Release notes");
        ShowBalloon(6000, "Umbra", $"Umbra updated to {current}. See what's new", ToolTipIcon.None, () => _ = ShowPageAsync(ShowSettings(), "about"));
        return true;
    }

    /// <summary>Opens Settings with the welcome tour on top (tray menu, About, the one-time offer).</summary>
    public void ReplayTour() => _ = ShowPageAsync(ShowSettings(), "welcome");

    /// <summary>Legacy widget ids being restored right now; their settings and data must survive <see cref="ForgetOrphans"/>.</summary>
    readonly HashSet<string> _restoring = new();

    /// <summary>One attempt per start, never blocks startup; the marker is only set when it finished, so offline retries next start.</summary>
    async Task RestoreMissingAsync()
    {
        try
        {
            var r = await Restore.RunAsync(
                () => OnUi(() => Task.FromResult(Restore.ToRestore(Settings, Manifests))),
                Community.FetchCatalogAsync, e => Community.InstallAsync(e), Log.Write);
            await OnUi(() =>
            {
                _restoring.Clear();
                if (r.Installed.Count > 0) { ReloadWidgets(); SettingsChangedElsewhere(); }
                if (r.Done) { Settings.General.LegacyRestoreDone = true; SaveSettings(); }
                return Task.FromResult(0);
            });
            Log.Write($"Restore finished: installed [{string.Join(", ", r.Installed)}], done={r.Done}");
        }
        catch (Exception ex)
        {
            Log.Write($"Restore failed: {ex.Message}");
            await OnUi(() => { _restoring.Clear(); return Task.FromResult(0); });
        }
    }

    // ---------- IUmbraHost: the surface Control (umbractl, MCP, tests) automates ----------

    IUpdater IUmbraHost.Updater => Updater;
    IEnumerable<IWidgetView> IUmbraHost.OpenWindows => OpenWindows;
    IWidgetView? IUmbraHost.Window(string instance) => Window(instance);
    ISettingsView IUmbraHost.ShowSettings(bool background) => ShowSettings(background);
    List<MonitorInfo> IUmbraHost.Monitors() => Screens.All();
    bool IUmbraHost.StartupEnabled { get => Startup.IsEnabled(); set => Startup.Set(value); }
    string IUmbraHost.Diagnostics() => Umbra.Diagnostics.Format(Umbra.Diagnostics.Collect(this));

    /// <summary>Runs work on the UI thread (pipe requests arrive on worker threads) and returns its result.</summary>
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

    /// <summary>
    /// Opens a double-clicked .umbrawidget: the full install checks, then the host-drawn review. Only an existing, fully qualified
    /// path ending in .umbrawidget is accepted. Runs on the UI thread.
    /// </summary>
    public bool CameraApproved(string widgetId) => CameraConsent.Approvals.CameraApproved(widgetId);

    public async Task<bool> OpenPackageAsync(string path)
    {
        path = path.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(path) || !path.EndsWith(Community.PackageExtension, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            Log.Write($"Ignored open-package request for '{path}'");
            return false;
        }
        try
        {
            var done = await PackageInstall.RunAsync(this, () => Task.Run(() => Community.PrepareLocal(path)), $"the file {Path.GetFileName(path)}");
            if (done is not null) _tray?.ShowBalloonTip(3000, "Umbra", done, ToolTipIcon.Info);
            return done is not null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Write($"Open package failed: {ex.Message}");
            MessageBox.Show($"Not installed: {ex.Message}", "Umbra", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    public WidgetWindow? Window(string id) => _windows.GetValueOrDefault(id);

    public IEnumerable<WidgetWindow> OpenWindows => _windows.Values;

    /// <summary>Tells an open Settings window to re-render after a change made elsewhere (tray, umbractl).</summary>
    public void SettingsChangedElsewhere()
    {
        if (_settingsWindow is { IsDisposed: false }) _settingsWindow.RefreshState();
        BuildTrayMenu();
        UpdateTrayStatus();
    }

    Icon? _badgedIcon;
    Color? _badge;

    /// <summary>
    /// Refreshes the tray tooltip (widget count, edit/safe mode, update, recording) and the dot: red while a timelapse
    /// records (the camera is on), otherwise orange while an Umbra update is ready.
    /// </summary>
    void UpdateTrayStatus()
    {
        _tray.Text = Tray.Tooltip(_windows.Count, EditMode, SafeMode, Updater.Available is not null, Timelapse.TrayLabel, Commands.RunningServices().Count);
        Color? badge = Timelapse.Recording ? Tray.RecordingColor : Updater.Available is not null ? Tray.UpdateColor : null;
        if (badge == _badge) return;
        _badge = badge;
        var old = _badgedIcon;
        _badgedIcon = null;
        _tray.Icon = AppIcon;
        old?.Dispose();
        if (badge is { } color)
        {
            try { _badgedIcon = Tray.WithBadge(AppIcon, SystemInformation.SmallIconSize, color); _tray.Icon = _badgedIcon; }
            catch (Exception ex) { Log.Write($"Tray badge failed: {ex.Message}"); _badge = null; }
        }
    }

    /// <summary>A timelapse started, took a still, is rendering or ended: tell its widget (indicator, events) and the tray.</summary>
    void OnTimelapseChanged(string inst, JsonObject state)
    {
        if (_windows.TryGetValue(inst, out var w)) w.TimelapseChanged(state);
        UpdateTrayStatus();
        if (state["state"]?.GetValue<string>() == "idle" && state["error"]?.GetValue<string>() is { } error)
            ShowTrayMessage($"Timelapse stopped: {error}", ToolTipIcon.Warning);
    }

    /// <summary>Starts a fresh Umbra that waits for this one to exit, then exits. For settings that only apply on start (GPU, proxy).</summary>
    public void Restart()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.ExePath, Tray.RestartArg) { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Write($"Restart failed: {ex.Message}");
            ShowBalloon(4000, "Umbra", $"Could not restart: {ex.Message}", ToolTipIcon.Error, null);
            return;
        }
        Log.Write("Restarting");
        QuitSoon();
    }

    /// <summary>Exits shortly after the current pipe reply has been written.</summary>
    public void QuitSoon() => _ = Task.Delay(300).ContinueWith(_ => _ui.Post(_ => ExitThread(), null));

    Font? _trayBold; // one shared bold font for the tray menu (a new one per rebuild would leak)

    void BuildTrayMenu()
    {
        var menu = _tray.ContextMenuStrip!;
        menu.Items.Clear();
        _tray.Text = Updater.Progress is { } dl ? $"Umbra - downloading update {dl}%"
            : Updater.Ready && Updater.Available is { } ready ? $"Umbra - restart to update to v{ready.Version}" : "Umbra";
        if (Updater.Available is { } up)
        {
            var label = Updater.Installing ? $"Updating to v{up.Version}..."
                : Updater.Progress is { } pct ? $"Downloading update v{up.Version} ({pct}%)"
                : Updater.Ready ? $"Restart to update to v{up.Version}" : $"Update Umbra to v{up.Version}";
            var item = menu.Items.Add(label, null, async (_, _) =>
            {
                try { await Updater.InstallAsync(); }
                catch (Exception ex)
                {
                    Log.Write($"Update failed: {ex.Message}");
                    ShowTrayMessage($"Update failed: {ex.Message}", ToolTipIcon.Error);
                }
            });
            item.Font = _trayBold ??= new Font(menu.Font, FontStyle.Bold);
            item.Enabled = !Updater.Installing;
            menu.Items.Add(new ToolStripSeparator());
        }
        if (Settings.General.CheckForUpdates && WidgetUpdateCount is > 0 and var pending)
        {
            menu.Items.Add(pending == 1 ? "Update 1 widget" : $"Update {pending} widgets", null, async (_, _) =>
            {
                try
                {
                    var r = await UpdateAllWidgetsAsync();
                    var text = r.Failed.Count == 0 ? $"Updated {r.Updated.Count} widget(s)"
                        : $"Updated {r.Updated.Count}, failed: {string.Join(", ", r.Failed.Select(f => f.Id))}";
                    ShowTrayMessage(text, r.Failed.Count == 0 ? ToolTipIcon.Info : ToolTipIcon.Warning);
                }
                catch (Exception ex)
                {
                    Log.Write($"Widget update failed: {ex.Message}");
                    ShowTrayMessage($"Widget update failed: {ex.Message}", ToolTipIcon.Error);
                }
            });
        }
        if (Timelapse.ActiveInstance is { } recorder)
        {
            UpdateTrayStatus(); // the tooltip's one source while a timelapse runs
            var label = Manifests.FirstOrDefault(m => m.Id == InstanceId.WidgetOf(recorder)) is { } rm ? InstanceId.Label(rm, recorder) : recorder;
            if (Timelapse.TrayLabel != "Making timelapse video")
                menu.Items.Add($"Stop timelapse ({label})", null, (_, _) => Timelapse.Stop(recorder)).Font = _trayBold ??= new Font(menu.Font, FontStyle.Bold);
            menu.Items.Add($"Cancel timelapse ({label})", null, (_, _) => Timelapse.Cancel(recorder));
            menu.Items.Add(new ToolStripSeparator());
        }
        var scripts = Commands.RunningServices();
        if (scripts.Count > 0)
        {
            // Scripts that keep running (a tunnel, a server) die with their widget; this stops one without opening it.
            var sub = new ToolStripMenuItem(scripts.Count == 1 ? "1 script running" : $"{scripts.Count} scripts running");
            foreach (var (widgetId, scriptId, title) in scripts)
            {
                var owner = Manifests.FirstOrDefault(x => x.Id == widgetId)?.Name ?? widgetId;
                sub.DropDownItems.Add($"Stop {Tray.MenuText(title)} ({Tray.MenuText(owner)})", null, (_, _) => Commands.Stop(widgetId, widgetId, scriptId));
            }
            menu.Items.Add(sub);
            menu.Items.Add(new ToolStripSeparator());
        }
        if (UndoRemoveLabel is { } undo)
        {
            menu.Items.Add($"Undo remove {undo}", null, (_, _) => UndoRemove());
            menu.Items.Add(new ToolStripSeparator());
        }
        menu.Items.Add("Settings...", null, (_, _) => ShowSettings()).Font = _trayBold ??= new Font(menu.Font, FontStyle.Bold);
        menu.Items.Add("Welcome tour", null, (_, _) => ReplayTour());
        var edit = new ToolStripMenuItem("Arrange widgets (unlock)") { Checked = EditMode };
        edit.Click += (_, _) => SetEditMode(!EditMode);
        menu.Items.Add(edit);
        var safe = new ToolStripMenuItem("Safe mode (community widgets off)") { Checked = SafeMode };
        safe.Click += (_, _) => SetSafeMode(!SafeMode);
        menu.Items.Add(safe);
        var layouts = new ToolStripMenuItem("Layouts");
        foreach (var name in Layouts.Items.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            layouts.DropDownItems.Add(name, null, (_, _) => ApplyLayout(name));
        if (layouts.DropDownItems.Count > 0) layouts.DropDownItems.Add(new ToolStripSeparator());
        layouts.DropDownItems.Add("Save current layout", null, (_, _) =>
        {
            var n = Layouts.Items.Count + 1;
            while (Layouts.Items.ContainsKey($"Layout {n}")) n++;
            try { SaveLayout($"Layout {n}"); } catch (Exception ex) { Log.Write($"Save layout failed: {ex.Message}"); }
        });
        menu.Items.Add(layouts);
        menu.Items.Add(new ToolStripSeparator());
        foreach (var m in Manifests)
            foreach (var inst in Settings.Instances(m.Id))
            {
                var ws = Settings.For(m, inst);
                var item = new ToolStripMenuItem(InstanceId.Label(m, inst)) { Checked = ws.Enabled, CheckOnClick = true };
                item.Click += (_, _) => { ws.Enabled = item.Checked; SaveSettings(); SyncWindows(); };
                menu.Items.Add(item);
            }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Refresh all", null, (_, _) => { foreach (var w in _windows.Values) w.RefreshData(); });
        menu.Items.Add("Open data folder", null, (_, _) =>
        {
            try { System.Diagnostics.Process.Start("explorer.exe", $"\"{AppPaths.DataDir}\""); }
            catch (Exception ex) { Log.Write($"Open data folder failed: {ex.Message}"); }
        });
        menu.Items.Add("Restart Umbra", null, (_, _) => Restart());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
    }

    /// <summary>A balloon from Umbra itself (not a widget), e.g. a failed update.</summary>
    public void ShowTrayMessage(string text, ToolTipIcon icon)
    {
        ShowBalloon(5000, "Umbra", text, icon, null);
    }

    /// <summary>The one place a tray balloon is shown: resets the click target first, so clicking an unrelated balloon never opens an earlier balloon's URL.</summary>
    void ShowBalloon(int ms, string title, string text, ToolTipIcon icon, Action? onClick)
    {
        _balloonClick = onClick;
        _tray.ShowBalloonTip(ms, title, text, icon);
    }

    /// <summary>Awaits a fire-and-forget task so an exception is logged instead of going unobserved.</summary>
    static async Task ObserveAsync(Task task, string what)
    {
        try { await task; }
        catch (Exception ex) { Log.Write($"{what} failed: {ex.Message}"); }
    }

    /// <summary>"Name" for the first copy of a widget, "Name (2)" for the others.</summary>
    /// <summary>
    /// Shows a widget's notification as a Windows toast (clicking opens <paramref name="url"/>; buttons go back to the widget) unless its
    /// rate limit is used up. Falls back to a tray balloon when toasts can't be created or shown.
    /// </summary>
    public bool Notify(string instance, string widgetName, string title, string body, string? url, string? tag = null, IReadOnlyList<NotifyAction>? actions = null)
    {
        if (!_notifier.TryAcquire(instance, DateTime.UtcNow)) return false;
        Log.Write($"[{instance}] notify: {title}");
        if (!_toastsTried) { _toastsTried = true; _toasts = ToastService.TryCreate(); }
        if (_toasts is { } toasts)
        {
            try
            {
                toasts.Show(instance, widgetName, title, body, url, tag, actions ?? [],
                    u => _ui.Post(_ => WebHost.OpenExternal(u), null),
                    id => _ui.Post(_ => Window(instance)?.NotificationAction(tag, id), null));
                return true;
            }
            catch (Exception ex) { Log.Write($"Toast failed, using a balloon: {ex.Message}"); }
        }
        ShowBalloon(5000, widgetName, body.Length == 0 ? title : $"{title}\n{body}", ToolTipIcon.None, url is null ? null : () => WebHost.OpenExternal(url));
        return true;
    }

    /// <summary>The manifest and instance id for a widget id or an instance id ("sp500#2"), or null when unknown.</summary>
    public (WidgetManifest Manifest, string Instance)? Resolve(string id)
    {
        if (!InstanceId.IsValid(id)) return null;
        var m = Manifests.FirstOrDefault(x => x.Id == InstanceId.WidgetOf(id));
        if (m is null) return null;
        return Settings.Instances(m.Id).Contains(id) ? (m, id) : null;
    }

    /// <summary>Opens a window for each enabled widget copy and closes the ones that were disabled or removed.</summary>
    public void SyncWindows()
    {
        Groups.Prune(Settings);
        foreach (var m in Manifests)
            foreach (var inst in Settings.Instances(m.Id))
            {
                var enabled = Settings.For(m, inst).Enabled && !(SafeMode && m.Source != WidgetSource.BuiltIn);
                var open = _windows.TryGetValue(inst, out var win);
                if (enabled && !open)
                {
                    win = new WidgetWindow(this, m, inst);
                    win.FormClosed += (_, _) => { _windows.Remove(inst); WidgetWindowGone(m.Id); };
                    _windows[inst] = win;
                    if (WantVisible(inst)) win.Show();
                    Commands.AutoStart(m); // scripts the user set to start whenever the widget is on
                }
                else if (!enabled && open)
                {
                    win!.Close();
                }
            }
        var live = Manifests.SelectMany(m => Settings.Instances(m.Id)).ToHashSet();
        foreach (var inst in _windows.Keys.Where(k => !live.Contains(k)).ToList()) CloseWidget(inst);
        foreach (var inst in _startHidden.Where(k => !Settings.Widgets.TryGetValue(k, out var s) || !s.StartHidden).ToList()) _startHidden.Remove(inst);
        ApplyVisibility();
        UpdateTrayStatus();
        RefreshEditPills();
    }

    /// <summary>Closes one copy (an instance id), or every copy of a widget (its plain id).</summary>
    public void CloseWidget(string id)
    {
        foreach (var key in _windows.Keys.Where(k => k == id || (!id.Contains('#') && InstanceId.WidgetOf(k) == id)).ToList())
        {
            if (EditSelection == key) EditSelection = null;
            _windows[key].Close();
            _windows.Remove(key);
            WidgetWindowGone(InstanceId.WidgetOf(key));
        }
    }

    /// <summary>Adds another copy of a widget (new position next to the first one) and returns its instance id.</summary>
    public string AddInstance(WidgetManifest m)
    {
        var inst = Settings.AddInstance(m) ?? throw new InvalidOperationException($"A widget can have at most {InstanceId.MaxInstances} copies");
        if (Window(m.Id) is { } first)
        {
            var offset = (int)(32 * first.DeviceDpi / 96.0);
            var ws = Settings.For(m, inst);
            ws.X = first.Left + offset * (InstanceId.NumberOf(inst) - 1);
            ws.Y = first.Top + offset * (InstanceId.NumberOf(inst) - 1);
        }
        SaveSettings();
        SyncWindows();
        SettingsChangedElsewhere();
        return inst;
    }

    /// <summary>Removes an extra copy with its settings and stored data. The first copy can only be disabled.</summary>
    public void RemoveInstance(string instance)
    {
        if (!InstanceId.IsExtra(instance) || !Settings.Widgets.ContainsKey(instance))
            throw new InvalidOperationException($"'{instance}' is not an extra copy of a widget");
        var m = Manifests.FirstOrDefault(x => x.Id == InstanceId.WidgetOf(instance));
        _removed = new RemovedCopy(instance, m is null ? instance : InstanceId.Label(m, instance), Settings.Widgets[instance], WidgetStore.All(instance));
        CloseWidget(instance);
        Settings.Widgets.Remove(instance);
        WidgetStore.Delete(instance);
        SaveSettings();
        SettingsChangedElsewhere();
    }

    RemovedCopy? _removed;

    /// <summary>Label of the copy that "Remove" can still bring back, or null when there is nothing to undo.</summary>
    public string? UndoRemoveLabel =>
        _removed is { } r && r.CanRestore(Settings) && Manifests.Any(m => m.Id == InstanceId.WidgetOf(r.Instance)) ? r.Label : null;

    /// <summary>Removes an extra copy from the widget menu and says how to get it back (tray menu, or a click on the balloon).</summary>
    public void RemoveInstanceWithUndo(string instance)
    {
        RemoveInstance(instance);
        if (_removed is not { } r) return;
        ShowBalloon(5000, "Umbra", $"{r.Label} removed. Click to undo.", ToolTipIcon.None, UndoRemove);
    }

    /// <summary>Brings back the last removed copy with its settings, position and stored data.</summary>
    public void UndoRemove()
    {
        if (UndoRemoveLabel is null || _removed is not { } r) return;
        r.Restore(Settings);
        if (r.Data.Count > 0) WidgetStore.Replace(r.Instance, r.Data);
        _removed = null;
        SaveSettings();
        SyncWindows();
        SettingsChangedElsewhere();
    }

    /// <summary>Hides a widget copy (turns it off in Settings); the widget menu's "Hide".</summary>
    public void DisableWidget(string instance)
    {
        if (!Settings.Widgets.TryGetValue(instance, out var ws)) return;
        ws.Enabled = false;
        SaveSettings();
        SyncWindows();
        SettingsChangedElsewhere();
    }

    /// <summary>Opens (or focuses) Settings on My widgets, scrolled to this copy's card and highlighted.</summary>
    public async void ShowWidgetOptions(string instance)
    {
        try { await ShowSettings().ShowWidgetAsync(instance); }
        catch (Exception ex) { Log.Write($"Could not open options for {instance}: {ex.Message}"); }
    }

    /// <summary>Forgets all extra copies of an uninstalled widget (settings; the data files go with the widget).</summary>
    public void ForgetInstances(string widgetId)
    {
        Settings.RemoveExtraInstances(widgetId);
        SaveSettings();
    }

    /// <summary>Undo of Settings > Remove: brings back the trashed widget with its data, options and position. Returns its manifest.</summary>
    public WidgetManifest? RestoreWidget(string token)
    {
        string id;
        try { id = WidgetTrash.Restore(Settings, token); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new InvalidOperationException($"Could not restore the widget: {ex.Message}"); }
        SaveSettings();
        ReloadWidgets();
        SettingsChangedElsewhere();
        return Manifests.FirstOrDefault(m => m.Id == id);
    }

    /// <summary>
    /// Extra copies of a widget whose folder vanished (deleted outside Umbra) are forgotten with their data.
    /// A missing first copy keeps its settings, as before.
    /// </summary>
    void ForgetOrphans()
    {
        // A widget skipped because it needs a newer Umbra is still on disk: its copies, settings and data must survive.
        var known = Manifests.Select(m => m.Id).Concat(WidgetManifest.Skipped.Select(s => s.Id)).ToList();
        foreach (var key in Restore.Orphans(Settings.Widgets.Keys.Where(InstanceId.IsExtra), known, _restoring))
        {
            CloseWidget(key);
            Settings.Widgets.Remove(key);
        }
        foreach (var inst in Restore.Orphans(WidgetStore.CopyIds(), known, _restoring))
            WidgetStore.Delete(inst);
    }

    /// <summary>A widget that no longer declares permissions.camera loses its camera approval, so declaring it again asks again.</summary>
    void DropStaleCameraApprovals()
    {
        CameraConsent.Approvals.RevokeCameraExcept(Manifests.Where(m => m.Permissions.Camera).Select(m => m.Id));
        // same for the picture folder: a widget that stops declaring permissions.saveImages forgets it
        ImageFolders.Shared.ClearExcept(Manifests.Where(m => m.Permissions.SaveImages).Select(m => m.Id));
    }
    /// <summary>Settings revoked the camera for a widget: drop the approval and cancel its recording.</summary>
    public void RevokeCamera(string widgetId)
    {
        CameraConsent.Approvals.RevokeCamera(widgetId);
        if (Timelapse.ActiveInstance is { } inst && InstanceId.WidgetOf(inst) == widgetId) Timelapse.Cancel(inst);
    }

    /// <summary>Re-scans widget folders after an install/remove and opens/closes windows to match.</summary>
    public void ReloadWidgets()
    {
        Manifests = WidgetManifest.DiscoverAll();
        DropStaleCameraApprovals();
        ForgetOrphans();
        foreach (var id in _windows.Keys.Where(id => Manifests.All(m => m.Id != InstanceId.WidgetOf(id))).ToList())
            CloseWidget(id);
        SaveSettings();
        SyncWindows();
        BuildTrayMenu();
        UpdateTrayStatus();
    }

    /// <summary>The community catalog as last fetched (daily check, Browse, update all). Null until the first fetch.</summary>
    JsonArray? _catalogSeen;
    bool _updatingAll;

    /// <summary>Remember a freshly fetched catalog so the tray can offer "Update n widgets" without another request.</summary>
    public void NoteCatalog(JsonArray catalog)
    {
        _catalogSeen = catalog;
        BuildTrayMenu();
    }

    /// <summary>How many installed community widgets have a newer catalog version, judged against the last fetched catalog.</summary>
    public int WidgetUpdateCount => _catalogSeen is null ? 0 : Community.Outdated(_catalogSeen, Manifests).Count;

    /// <summary>The automatic check: refreshes the catalog quietly (offline is fine) so the tray count stays current.</summary>
    public async Task CheckWidgetUpdatesAsync()
    {
        try { _catalogSeen = await Community.FetchCatalogAsync(); }
        catch (Exception ex) { Log.Write($"Widget update check failed: {ex.Message}"); }
    }

    /// <summary>
    /// Installs every available widget update, one after another; a failure doesn't stop the rest. An update that adds
    /// permissions is never applied here: it is reported in <c>Failed</c> as skipped, with the new access listed.
    /// <paramref name="progress"/> gets (index, total, id) before each widget.
    /// </summary>
    public async Task<UpdateAllResult> UpdateAllWidgetsAsync(Action<int, int, string>? progress = null)
    {
        if (_updatingAll) throw new InvalidOperationException("Widgets are already being updated");
        _updatingAll = true;
        try
        {
            _catalogSeen = await Community.FetchCatalogAsync();
            var result = await Community.UpdateAllAsync(Community.Outdated(_catalogSeen, Manifests), entry =>
                // Never silently accepts new access: such an update is reported as skipped and the widget keeps running.
                Community.UpdateWithoutNewAccessAsync(entry, CloseWidget), progress);
            ReloadWidgets(); // also reopens a widget whose update failed
            SettingsChangedElsewhere();
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
        s.General.LastSeenVersion = Settings.General.LastSeenVersion; // an old backup must not announce an update again
        s.General.TourDone = Settings.General.TourDone; // nor re-offer or reset the tour
        s.SettingsPanel = Settings.SettingsPanel; // where this window sits is not part of a backup
        Settings = s;
        SaveSettings();
        _hotkeys.Apply(Settings.Hotkeys);
        SyncWindows();
        BuildTrayMenu();
        SettingsChangedElsewhere();
    }

    readonly Dictionary<string, long> _handScaledAt = new();

    /// <summary>A zoom by hand (handle, Ctrl+wheel) just happened: an in-flight Settings save must not revert it.</summary>
    public void NoteHandScale(string instance) => _handScaledAt[instance] = System.Environment.TickCount64;

    bool HandScaledRecently(string instance) =>
        _handScaledAt.TryGetValue(instance, out var t) && System.Environment.TickCount64 - t < 2000;

    public void ApplySettings(Settings incoming)
    {
        // Positions are owned by the widget windows (dragging), so keep the current ones.
        // Copies are only added and removed through AddInstance/RemoveInstance, never by a (possibly stale) save.
        foreach (var id in incoming.Widgets.Keys.Where(k => InstanceId.IsExtra(k) && !Settings.Widgets.ContainsKey(k)).ToList())
            incoming.Widgets.Remove(id);
        foreach (var (id, cur) in Settings.Widgets)
            if (!incoming.Widgets.ContainsKey(id) && InstanceId.IsExtra(id)) incoming.Widgets[id] = cur;
        foreach (var (id, ws) in incoming.Widgets)
            if (Settings.Widgets.TryGetValue(id, out var cur)) { ws.X = cur.X; ws.Y = cur.Y; ws.Monitor = cur.Monitor; ws.RelX = cur.RelX; ws.RelY = cur.RelY; if (HandScaledRecently(id)) { ws.Scale = cur.Scale; ws.Width = cur.Width; ws.Height = cur.Height; } }
        incoming.Groups = Settings.Groups; // owned by the group methods, like positions
        incoming.Hotkeys = Settings.Hotkeys; // owned by SetHotkey, never by a (possibly stale) save
        incoming.General.LastSeenVersion = Settings.General.LastSeenVersion; // owned by AnnounceUpdate
        incoming.General.TourDone = Settings.General.TourDone; // owned by the tour logic in the constructor
        incoming.General.LockAll = Settings.General.LockAll; // owned by SetEditMode
        incoming.SettingsPanel = Settings.SettingsPanel; // owned by the Settings window
        Prefs.FollowDefaultScale(incoming);
        Settings = incoming;
        SaveSettings();
        SyncWindows();
        foreach (var w in _windows.Values) w.PushSettings();
        RefreshEditPills(); // the chip and hint encode the snap mode
    }

    /// <summary>Turns "Arrange widgets" (the global lock off) on or off, saves it and updates the widgets, tray and Settings.</summary>
    public void SetEditMode(bool on)
    {
        if (EditMode == on) return;
        Settings.General.LockAll = !on;
        SaveSettings();
        if (!on) ShowGuides(Array.Empty<Snap.Guide>(), 96);
        EditSelection = null;
        Log.Write($"Edit mode {(on ? "on" : "off")}");
        foreach (var w in _windows.Values) w.PushSettings();
        UpdateEditPills();
        SettingsChangedElsewhere();
    }

    SnapGuides? _snapGuides;

    /// <summary>Draws (or, with none, hides) the alignment guides while a widget is moved in edit mode.</summary>
    public void ShowGuides(IReadOnlyList<Snap.Guide> guides, int dpi)
    {
        if (guides.Count == 0 && _snapGuides is null) return;
        _snapGuides ??= new SnapGuides();
        _snapGuides.SetGuides(guides, Settings.General.Accent, dpi);
    }

    readonly List<EditPill> _pills = new();

    readonly Dictionary<string, GroupTabs> _tabStrips = new();
    readonly Dictionary<string, long> _tabSwitchedAt = new();
    readonly System.Windows.Forms.Timer _tabTimer = new() { Interval = 250 };

    /// <summary>
    /// Every tick: keeps a group's tab strip on top of its visible widget (hidden with it),
    /// and switches groups that have a cycle interval to their next tab (not while the pointer is over them or in edit mode).
    /// </summary>
    void TickGroups()
    {
        foreach (var id in _tabStrips.Keys.Where(k => !Settings.Groups.TryGetValue(k, out var g) || g.Members.Count < 2).ToList())
        {
            _tabStrips[id].Close();
            _tabStrips.Remove(id);
            _tabSwitchedAt.Remove(id);
        }
        var now = System.Environment.TickCount64;
        var raise = false;
        foreach (var (id, g) in Settings.Groups.ToList())
        {
            if (g.Members.Count < 2) continue;
            var active = Groups.ActiveOf(g);
            var win = active is null ? null : Window(active);
            var shown = win is { IsHandleCreated: true, Visible: true } && !ShouldHide;
            _tabStrips.TryGetValue(id, out var strip);
            var hover = shown && (win!.CursorOverThisWindow() || (strip is { Visible: true } && IsOver(strip)));
            if (shown)
            {
                if (strip is null) { strip = new GroupTabs(this, id, win!.Bounds); _tabStrips[id] = strip; }
                var changed = strip.Place(win!.Bounds);
                if (!strip.Visible) { strip.Show(); raise = true; }
                else if (changed) raise = true;
            }
            else if (strip is { Visible: true }) strip.Hide();

            if (!_tabSwitchedAt.ContainsKey(id)) _tabSwitchedAt[id] = now;
            if (g.CycleSeconds > 0 && shown && !hover && !EditMode && now - _tabSwitchedAt[id] >= g.CycleSeconds * 1000L)
            {
                _tabSwitchedAt[id] = now;
                StepTab(id, 1);
                return; // the settings changed under the loop; the next tick carries on
            }
            if (hover) _tabSwitchedAt[id] = now;
        }
        if (raise) LowerWidgets();
    }

    static bool IsOver(Form f)
    {
        var p = Cursor.Position;
        var hit = Native.WindowFromPoint(new Native.POINT { X = p.X, Y = p.Y });
        return hit != IntPtr.Zero && Native.GetAncestor(hit, Native.GA_ROOT) == f.Handle;
    }

    /// <summary>The "Done" pills: one per monitor that shows a widget (the primary one if none does) while edit mode is on, none otherwise.</summary>
    void UpdateEditPills()
    {
        if (!EditMode)
        {
            foreach (var p in _pills) p.Close();
            _pills.Clear();
            return;
        }
        var screens = Screens.All();
        var areas = _windows.Values.Where(w => w.IsHandleCreated && w.Visible)
            .Select(w => Placement.Find(screens, w.Bounds)).OfType<MonitorInfo>().Select(m => m.WorkArea).Distinct().ToList();
        if (areas.Count == 0) areas = screens.Where(s => s.Primary).Select(s => s.WorkArea).Take(1).ToList();
        // Only touch pills that must appear or go, so a save or a drag never makes them flicker.
        foreach (var p in _pills.Where(p => !areas.Contains(p.Area)).ToList()) { p.Close(); _pills.Remove(p); }
        var added = false;
        foreach (var area in areas.Where(a => _pills.All(p => p.Area != a)))
        {
            var pill = new EditPill(this, area);
            _pills.Add(pill);
            pill.Show();
            added = true;
        }
        foreach (var p in _pills) p.RefreshState();
        if (added) LowerWidgets();
    }

    /// <summary>Moves the pills to the monitors that show widgets now (a widget was dragged to another monitor, added or removed).</summary>
    public void RefreshEditPills() { if (EditMode) UpdateEditPills(); }

    /// <summary>Puts every widget back at the bottom, under the edit pills (a pill is shown at the bottom too, so the widgets go under it).</summary>
    public void LowerWidgets()
    {
        if (Peeking) return;
        foreach (var w in _windows.Values) if (w.IsHandleCreated) DesktopPin.SendToBottom(w.Handle);
    }

    /// <summary>The pill's snap switch: flips snapping and refreshes the pills, tray and Settings.</summary>
    public void ToggleSnap()
    {
        Snap.Toggle(Settings.General);
        SaveSettings();
        foreach (var p in _pills) p.RefreshState();
        SettingsChangedElsewhere();
    }

    /// <summary>Marks one widget copy as the edit-mode selection (stronger outline, arrow keys); everyone else is deselected.</summary>
    public void SelectForEdit(string instance)
    {
        if (!EditMode || EditSelection == instance) return;
        EditSelection = instance;
        foreach (var w in _windows.Values) w.ShowEditSelected(w.Instance == instance);
    }

    public void ResetPosition(string id)
    {
        if (_windows.TryGetValue(id, out var w)) w.ResetPosition();
        else if (Settings.Widgets.TryGetValue(id, out var ws)) { ws.X = ws.Y = null; SaveSettings(); }
    }

    /// <summary>Remembers where the Settings window was closed (see <see cref="SettingsPlacement"/>).</summary>
    public void SaveSettingsPlacement(SettingsPlacement p) { Settings.SettingsPanel = p; Settings.Save(); }

    /// <summary>The Settings window's zoom changed: keep it at once, not only when the window closes.</summary>
    public void SaveSettingsZoom(double zoom) { Settings.SettingsPanel.Zoom = zoom; Settings.Save(); }

    public void SaveSettings() { Prefs.Configure(Settings.General); Settings.Save(); foreach (var w in _windows.Values) if (w.IsHandleCreated) DesktopPin.RoundCorners(w.Handle); }

    /// <summary>
    /// Windows reported new colors: re-read the accent and, when "Match Windows accent" is on, re-tint widgets and Settings at once.
    /// Windows can fire the event just before the value is readable, so it is checked once more shortly after.
    /// </summary>
    void OnSystemAccentChanged(bool recheck = true)
    {
        var now = _windowsAccent.Read();
        if (now != Prefs.SystemAccent)
        {
            Prefs.SystemAccent = now;
            if (Prefs.IsWindowsAccent(Settings.General.Accent))
            {
                foreach (var w in _windows.Values) w.PushSettings();
                SettingsChangedElsewhere();
            }
        }
        if (recheck) _ = Task.Delay(500).ContinueWith(_ => _ui.Post(_ => OnSystemAccentChanged(false), null));
    }

    /// <summary>Generated :root override of the active theme (empty for the default look).</summary>
    public string ThemeCss => Theme.CssFor(Settings.General.Theme);

    /// <summary>Switches the theme live: widgets and Settings re-apply it at once. Unknown ids are an error.</summary>
    public void SetTheme(string id)
    {
        if (Theme.DiscoverAll().All(t => t.Id != id)) throw new InvalidOperationException($"No installed theme '{id}'");
        Settings.General.Theme = id;
        SaveSettings();
        foreach (var w in _windows.Values) w.PushSettings();
        SettingsChangedElsewhere();
    }

    /// <summary>Opens (or focuses) Settings. With <paramref name="background"/> an unopened window is created off-screen and never activated, and an open one is left alone.</summary>
    public SettingsWindow ShowSettings(bool background = false)
    {
        if (_settingsWindow is { IsDisposed: false })
        {
            if (background) return _settingsWindow;
            if (_settingsWindow.WindowState == FormWindowState.Minimized) _settingsWindow.WindowState = FormWindowState.Normal;
            _settingsWindow.Activate();
            return _settingsWindow;
        }
        _settingsWindow = new SettingsWindow(this, background);
        _settingsWindow.Show();
        if (!background) _settingsWindow.Activate();
        return _settingsWindow;
    }

    public bool SettingsOpen => _settingsWindow is { IsDisposed: false };

    protected override void ExitThreadCore()
    {
        Timelapse.Dispose(); // camera off, stills deleted, nothing rendered
        _tray.Visible = false;
        _tray.Dispose();
        _badgedIcon?.Dispose();
        Updater.Dispose();
        _windowsAccent.Dispose();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        Microsoft.Win32.SystemEvents.TimeChanged -= OnTimeChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
        _timeTimer.Dispose();
        _wakeRefresh?.Dispose();
        _fullscreenTimer.Dispose();
        _tabTimer.Dispose();
        foreach (var t in _tabStrips.Values) t.Close();
        _layoutTimer.Dispose();
        _peekTimer?.Dispose();
        _hotkeys.Dispose();
        _snapGuides?.Dispose();
        foreach (var p in _pills) p.Close();
        Power.Dispose();
        foreach (var w in _windows.Values.ToList()) w.Close();
        _settingsWindow?.Close();
        base.ExitThreadCore();
    }

    static Icon LoadIcon()
    {
        using var s = typeof(App).Assembly.GetManifestResourceStream("umbra.ico");
        return s is null ? SystemIcons.Application : new Icon(s);
    }
}
