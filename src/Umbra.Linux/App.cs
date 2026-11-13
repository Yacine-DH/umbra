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

    /// <summary>Runs widget-declared commands and the user's own scripts behind the approval gate (prompt: <see cref="Dialogs.AskCommandAsync"/>).</summary>
    public CommandService Commands { get; } = new(CommandApprovals.Shared, Dialogs.AskCommandAsync, scripts: UserScripts.Shared);

    /// <summary>The daily, notify-only update check (#197): Settings and the tray say when a newer release exists.</summary>
    public IUpdater Updater => _updater;
    readonly LinuxUpdater _updater;

    /// <summary>Sleep/resume, lock, power profile, clock and accent from the desktop session (#193).</summary>
    public LinuxSession Session { get; private set; } = null!;

    // ---------- notifications (#189) ----------

    readonly Notifier _notifier = new();
    LinuxNotifications? _notifications;

    /// <summary>The click-gated "Allow notifications?" prompt state (umbra.requestNotifications).</summary>
    public NotificationConsent NotificationConsent { get; } = new();

    /// <summary>Shows a widget's notification unless its rate limit is used up (the other checks are done by the window).</summary>
    public bool Notify(string instance, string widgetName, string title, string body, string? url, string? tag, IReadOnlyList<NotifyAction> actions)
    {
        if (!_notifier.TryAcquire(instance, DateTime.UtcNow)) return false;
        Log.Write($"[{instance}] notify: {title}");
        _notifications ??= new LinuxNotifications(DBus.Session, (inst, t, action) => Window(inst)?.NotificationAction(t, action));
        _ = _notifications.ShowAsync(instance, widgetName, title, body, url, tag, actions)
            .ContinueWith(t => Log.Write($"[{instance}] notification failed: {t.Exception?.GetBaseException().Message}"), TaskContinuationOptions.OnlyOnFaulted);
        return true;
    }

    /// <summary>How widget windows are tied to the desktop on this session (X11 hints, or nothing yet on native Wayland).</summary>
    public IDesktopPin Pin { get; }

    string? IUmbraHost.PinBackend => Pin.Name;

    public bool EditMode => !Settings.General.LockAll;

    /// <summary>The widget copy last pressed in edit mode: the one the arrow keys nudge. Null outside edit mode.</summary>
    public string? EditSelection { get; private set; }
    public bool SafeMode { get; private set; }
    public bool WidgetsHidden { get; private set; }
    public bool HiddenByFullscreen => false;
    public bool Peeking => false;
    public bool SettingsOpen => _settings is { IsClosed: false };

    SettingsWindow? _settings;

    public App(Gtk.Application gtk, UiThread ui, bool safe)
    {
        _gtk = gtk;
        _ui = ui;
        SafeMode = safe;
        Settings = Settings.Load();
        try { WidgetTrash.Purge(); } catch (Exception ex) { Log.Write($"Trash purge failed: {ex.Message}"); }
        Prefs.Configure(Settings.General);
        _updater = LinuxUpdater.Create(() => Settings.General.CheckForUpdates, () => _ui.Post(_ => SettingsChangedElsewhere(), null));
        Manifests = WidgetManifest.DiscoverAll();
        WebHost.Manifests = () => Manifests;
        if (!WebHost.SandboxAvailable)
            Log.Write($"Sandbox extension missing ({WebHost.ExtensionDir}): no widget is opened. Error: this build is incomplete.");
        _autostart = LinuxAutostart.Create(Environment.GetEnvironmentVariable, File.Exists, AppPaths.ExePath, AppPaths.IsPortable, portal: null);
        Pin = X11Pin.TryCreate() ?? (IDesktopPin)new NoPin();
        Log.Write($"Umbra started, {Manifests.Count} widget(s) found, desktop pinning: {Pin.Name}");
        if (Pin is NoPin) Log.Write("Widgets are plain windows on this session: native Wayland pinning is not available yet (#186, #187) and no X server was found.");

        // Umbra is dark everywhere: the Settings title bar and the host dialogs follow the dark variant of the GTK theme.
        if (Gtk.Settings.GetDefault() is { } gtkSettings) gtkSettings.GtkApplicationPreferDarkTheme = true;
        var css = Gtk.CssProvider.New();
        css.LoadFromString("window.umbra-widget { background: transparent; }\n" + OverlayWindow.Css);
        if (Gdk.Display.GetDefault() is { } display)
            Gtk.StyleContext.AddProviderForDisplay(display, css, 600 /* GTK_STYLE_PROVIDER_PRIORITY_APPLICATION */);

        Commands.ServicesChanged += id => _ui.Post(_ => { CommandsChanged(id); SettingsChangedElsewhere(); }, null);
        if (Gdk.Display.GetDefault() is { } iconDisplay)
            Gtk.IconTheme.GetForDisplay(iconDisplay).AddSearchPath(Path.Combine(AppPaths.AppDir, "icons"));
        _tray = LinuxTray.TryStart(_ui, TrayItems, () => ShowSettings());
        Session = new LinuxSession(_ui);
        Session.PowerChanged += () => { foreach (var w in _windows.Values) w.PushPower(); };
        Session.RefreshNeeded += reason => { Log.Write($"Refreshing widgets after {reason}"); foreach (var w in _windows.Values) w.RefreshData(); };
        Session.TimeChanged += () => { foreach (var w in _windows.Values) w.NotifyTimeChanged(); };
        Session.AccentChanged += () => { PushAll(); SettingsChangedElsewhere(); };
        Session.Start();
        _updater.Start();
        SyncWindows();
        if (EditMode) _ui.Post(_ => UpdateEditPills(), null); // left unlocked last time: show the Done pill again
        UiThread.Every(250, () => { TickGroups(); return true; });
        // A monitor was added, removed or rearranged: put every widget back where its saved monitor and offset say.
        if (Gdk.Display.GetDefault() is { } d)
            d.GetMonitors().OnItemsChanged += (_, _) => _ui.Post(_ => { foreach (var w in _windows.Values) w.ReapplyPlacement(); }, null);
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
            // GDK 4 reports neither a primary monitor nor work areas: the first monitor stands in, with the window
            // manager's work area (panels left out) when the pin backend can read it.
            var work = bounds;
            if (i == 0 && Pin.PrimaryWorkArea() is { } wa && Rectangle.Intersect(wa, bounds) is { Width: > 0, Height: > 0 } usable) work = usable;
            list.Add(new MonitorInfo(m.GetConnector() ?? $"monitor-{i + 1}", bounds, work, i == 0));
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

    /// <summary>
    /// Opens (or brings forward) the Settings window. <paramref name="background"/> is for captures: when Settings isn't open,
    /// a hidden window is made just for the capture (the caller closes it); an open window is captured as it is.
    /// </summary>
    public ISettingsView ShowSettings(bool background = false)
    {
        if (_settings is { IsClosed: false } open)
        {
            if (!background) open.Present();
            return open;
        }
        var win = new SettingsWindow(this, _gtk, background);
        if (!background)
        {
            _settings = win;
            win.Closed += () => { if (_settings == win) _settings = null; };
        }
        win.Present();
        return win;
    }

    /// <summary>The manifest and instance id for a widget id or an instance id ("sp500#2"), or null when unknown.</summary>
    public (WidgetManifest Manifest, string Instance)? Resolve(string id)
    {
        if (!InstanceId.IsValid(id)) return null;
        var m = Manifests.FirstOrDefault(x => x.Id == InstanceId.WidgetOf(id));
        if (m is null) return null;
        return Settings.Instances(m.Id).Contains(id) ? (m, id) : null;
    }

    public void SaveSettings()
    {
        Prefs.Configure(Settings.General);
        Settings.Save();
        RefreshTray();
    }

    // ---------- tray (#191) ----------

    readonly LinuxTray? _tray;

    /// <summary>True when a tray icon is shown (a StatusNotifierWatcher took it): Settings then talks about the tray.</summary>
    public bool TrayShown => _tray?.Shown == true;

    /// <summary>Rebuilds the tray menu and tooltip (coalesced to one rebuild per main-loop turn).</summary>
    void RefreshTray()
    {
        if (_tray is null) return;
        var on = _windows.Count;
        _tray.SetStatus("Umbra - " + Tray.Tooltip(on, EditMode, SafeMode, Updater.Available is not null, Commands.RunningServices().Count),
            Updater.Available is not null);
        _tray.Invalidate();
    }

    /// <summary>The tray menu: the same items as the Windows tray (Settings, tour, Arrange, safe mode, layouts, widgets, refresh, data folder, restart, quit).</summary>
    IEnumerable<TrayItem> TrayItems()
    {
        if (Updater.Available is { } up)
        {
            yield return new TrayItem { Label = $"Download Umbra v{up.Version}", Click = () => WebHost.OpenExternal(up.PageUrl) };
            yield return TrayItem.Line();
        }
        var scripts = Commands.RunningServices();
        if (scripts.Count > 0)
        {
            yield return new TrayItem
            {
                Label = scripts.Count == 1 ? "1 script running" : $"{scripts.Count} scripts running",
                Children = scripts.Select(s => new TrayItem
                {
                    Label = $"Stop {Tray.MenuText(s.Title)} ({Tray.MenuText(Manifests.FirstOrDefault(x => x.Id == s.WidgetId)?.Name ?? s.WidgetId)})",
                    Click = () => Commands.Stop(s.WidgetId, s.WidgetId, s.ScriptId),
                }).ToList(),
            };
            yield return TrayItem.Line();
        }
        yield return new TrayItem { Label = "Settings...", Click = () => ShowSettings() };
        yield return new TrayItem { Label = "Welcome tour", Click = () => _ = ShowSettings().ShowPageAsync("welcome") };
        yield return new TrayItem { Label = "Arrange widgets (unlock)", Checked = EditMode, Click = () => SetEditMode(!EditMode) };
        yield return new TrayItem { Label = "Hide widgets", Checked = WidgetsHidden, Click = ToggleHidden };
        yield return new TrayItem { Label = "Safe mode (community widgets off)", Checked = SafeMode, Click = () => SetSafeMode(!SafeMode) };
        var layouts = Layouts.Items.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Select(name => new TrayItem { Label = name, Click = () => ApplyLayout(name) }).ToList();
        if (layouts.Count > 0) layouts.Add(TrayItem.Line());
        layouts.Add(new TrayItem
        {
            Label = "Save current layout",
            Click = () =>
            {
                var n = Layouts.Items.Count + 1;
                while (Layouts.Items.ContainsKey($"Layout {n}")) n++;
                try { SaveLayout($"Layout {n}"); SettingsChangedElsewhere(); } catch (Exception ex) { Log.Write($"Save layout failed: {ex.Message}"); }
            },
        });
        yield return new TrayItem { Label = "Layouts", Children = layouts };
        yield return TrayItem.Line();
        foreach (var m in Manifests)
            foreach (var inst in Settings.Instances(m.Id))
            {
                var ws = Settings.For(m, inst);
                yield return new TrayItem
                {
                    Label = InstanceId.Label(m, inst),
                    Checked = ws.Enabled,
                    Click = () => { ws.Enabled = !ws.Enabled; SaveSettings(); SyncWindows(); SettingsChangedElsewhere(); },
                };
            }
        yield return TrayItem.Line();
        yield return new TrayItem { Label = "Refresh all", Click = () => { foreach (var w in _windows.Values) w.RefreshData(); } };
        yield return new TrayItem { Label = "Open data folder", Click = () => OpenWithDesktop(AppPaths.DataDir) };
        yield return new TrayItem { Label = "Restart Umbra", Click = Restart };
        yield return new TrayItem { Label = "Quit", Click = Quit };
    }

    /// <summary>Shows or hides every widget (tray, and the toggle-widgets hotkey once #192 lands).</summary>
    public void ToggleHidden()
    {
        WidgetsHidden = !WidgetsHidden;
        foreach (var (inst, w) in _windows) w.Visible = WantVisible(inst);
        RefreshEditPills();
        RefreshTray();
        SettingsChangedElsewhere();
    }

    /// <summary>Opens a folder or file with the desktop's default app. Fixed argv, no shell.</summary>
    public static void OpenWithDesktop(string path)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("xdg-open") { UseShellExecute = false };
            psi.ArgumentList.Add(path);
            using var p = System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex) { Log.Write($"Open {path} failed: {ex.Message}"); }
    }

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
                    var widgetId = m.Id;
                    win.Closed += () =>
                    {
                        if (_windows.GetValueOrDefault(inst) == created) _windows.Remove(inst);
                        // The widget's last window closed: the scripts it keeps running stop with it.
                        if (!_windows.Values.Any(w => w.Manifest.Id == widgetId)) Commands.StopWidget(widgetId);
                    };
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
        foreach (var (inst, w) in _windows) w.Visible = WantVisible(inst);
        RefreshEditPills();
    }

    /// <summary>Closes one copy (an instance id), or every copy of a widget (its plain id).</summary>
    public void CloseWidget(string id)
    {
        foreach (var key in _windows.Keys.Where(k => k == id || (!id.Contains('#') && InstanceId.WidgetOf(k) == id)).ToList())
        {
            if (EditSelection == key) EditSelection = null;
            var w = _windows[key];
            _windows.Remove(key);
            w.Close();
        }
    }

    /// <summary>Something changed outside the Settings window (umbractl, the edit pill, a script): it re-renders. The tray is #191.</summary>
    public void SettingsChangedElsewhere()
    {
        _settings?.RefreshState();
        RefreshTray();
    }

    /// <summary>Settings on a normal start; the first run also shows the welcome tour once.</summary>
    public void OpenSettingsOnStart(bool firstRun)
    {
        var win = ShowSettings();
        if (!firstRun || Settings.General.TourDone) return;
        Settings.General.TourDone = true;
        SaveSettings();
        _ = win.ShowPageAsync("welcome").ContinueWith(t => Log.Write($"Welcome tour failed: {t.Exception?.GetBaseException().Message}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>Whether some copy of the widget has a window (it is on, and not held back by safe mode).</summary>
    public bool HasOpenWindow(string widgetId) => _windows.Values.Any(w => w.Manifest.Id == widgetId);

    /// <summary>The user saved or deleted a script: tell every open copy of that widget (umbra.onCommandsChange).</summary>
    public void CommandsChanged(string widgetId)
    {
        foreach (var w in _windows.Values) if (w.Manifest.Id == widgetId) w.NotifyCommandsChanged();
    }

    bool HandScaledRecently(string instance) =>
        _handScaledAt.TryGetValue(instance, out var t) && Environment.TickCount64 - t < 2000;

    /// <summary>A save from the Settings page: everything the page owns, while positions, groups, hotkeys and the lock stay the host's.</summary>
    public void ApplySettings(Settings incoming)
    {
        foreach (var id in incoming.Widgets.Keys.Where(k => InstanceId.IsExtra(k) && !Settings.Widgets.ContainsKey(k)).ToList())
            incoming.Widgets.Remove(id);
        foreach (var (id, cur) in Settings.Widgets)
            if (!incoming.Widgets.ContainsKey(id) && InstanceId.IsExtra(id)) incoming.Widgets[id] = cur;
        foreach (var (id, ws) in incoming.Widgets)
            if (Settings.Widgets.TryGetValue(id, out var cur))
            {
                ws.X = cur.X; ws.Y = cur.Y; ws.Monitor = cur.Monitor; ws.RelX = cur.RelX; ws.RelY = cur.RelY;
                if (HandScaledRecently(id)) { ws.Scale = cur.Scale; ws.Width = cur.Width; ws.Height = cur.Height; }
            }
        incoming.Groups = Settings.Groups;
        incoming.Hotkeys = Settings.Hotkeys;
        incoming.General.LastSeenVersion = Settings.General.LastSeenVersion;
        incoming.General.TourDone = Settings.General.TourDone;
        incoming.General.LockAll = Settings.General.LockAll;
        incoming.SettingsPanel = Settings.SettingsPanel;
        Prefs.FollowDefaultScale(incoming);
        Settings = incoming;
        SaveSettings();
        SyncWindows();
        foreach (var w in _windows.Values) w.PushSettings();
        RefreshEditPills();
    }

    /// <summary>Undo of Settings > Remove: brings back the trashed widget with its data, options and position.</summary>
    public WidgetManifest? RestoreWidget(string token)
    {
        string id;
        try { id = WidgetTrash.Restore(Settings, token); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new InvalidOperationException($"Could not restore the widget: {ex.Message}"); }
        SaveSettings();
        ReloadWidgets();
        return Manifests.FirstOrDefault(m => m.Id == id);
    }

    /// <summary>Starts a fresh Umbra that waits for this one to exit, then quits. For settings that only apply on start.</summary>
    public void Restart()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(AppPaths.ExePath) { UseShellExecute = false };
            psi.ArgumentList.Add(Program.RestartArg);
            using var p = System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Write($"Restart failed: {ex.Message}");
            return;
        }
        Log.Write("Restarting");
        QuitSoon();
    }

    /// <summary>"Ubuntu 24.04.1 LTS, GNOME on x11": the system line of a bug report.</summary>
    public static string SystemLine()
    {
        var os = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        try
        {
            if (File.Exists("/etc/os-release"))
                foreach (var line in File.ReadLines("/etc/os-release"))
                    if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal)) { os = line[12..].Trim('"'); break; }
        }
        catch (IOException) { }
        return $"{os}, {Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "unknown desktop"} on {Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "unknown"}";
    }

    // ---------- edit mode: selection, pills, snap guides ----------

    readonly List<EditPill> _pills = new();
    SnapGuides? _snapGuides;
    readonly Dictionary<string, long> _handScaledAt = new();

    /// <summary>A zoom or resize by hand just happened: an in-flight Settings save must not revert it.</summary>
    public void NoteHandScale(string instance) => _handScaledAt[instance] = Environment.TickCount64;

    /// <summary>Marks one widget copy as the edit-mode selection (stronger outline, arrow keys); everyone else is deselected.</summary>
    public void SelectForEdit(string instance)
    {
        if (!EditMode || EditSelection == instance) return;
        EditSelection = instance;
        foreach (var w in _windows.Values) w.ShowEditSelected(w.Instance == instance);
    }

    /// <summary>Draws (or, with none, hides) the alignment guides while a widget is moved in edit mode.</summary>
    public void ShowGuides(IReadOnlyList<Snap.Guide> guides)
    {
        if (guides.Count == 0 && _snapGuides is null) return;
        (_snapGuides ??= new SnapGuides(this)).Set(guides);
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
        var screens = Monitors();
        var areas = _windows.Values.Where(w => w.Visible)
            .Select(w => Placement.Find(screens, w.Bounds)).OfType<MonitorInfo>().Select(m => m.WorkArea).Distinct().ToList();
        if (areas.Count == 0) areas = screens.Where(s => s.Primary).Select(s => s.WorkArea).Take(1).ToList();
        // Only touch pills that must appear or go, so a save or a drag never makes them flicker.
        foreach (var p in _pills.Where(p => !areas.Contains(p.Area)).ToList()) { p.Close(); _pills.Remove(p); }
        foreach (var area in areas.Where(a => _pills.All(p => p.Area != a)))
        {
            var pill = new EditPill(this, area);
            _pills.Add(pill);
            pill.Show();
        }
        foreach (var p in _pills) p.RefreshState();
    }

    /// <summary>Moves the pills to the monitors that show widgets now (a widget was dragged to another monitor, added or removed).</summary>
    public void RefreshEditPills() { if (EditMode) UpdateEditPills(); }

    /// <summary>The pill's snap switch: flips snapping and refreshes the pills.</summary>
    public void ToggleSnap()
    {
        Snap.Toggle(Settings.General);
        SaveSettings();
        foreach (var p in _pills) p.RefreshState();
        SettingsChangedElsewhere();
    }

    // ---------- tab strips of widget groups ----------

    readonly Dictionary<string, GroupTabs> _tabStrips = new();
    readonly Dictionary<string, long> _tabSwitchedAt = new();

    /// <summary>
    /// Every tick: keeps a group's tab strip on its visible widget (hidden with it) and switches groups that have a cycle
    /// interval to their next tab (not while the pointer is over them or in edit mode).
    /// </summary>
    void TickGroups()
    {
        foreach (var id in _tabStrips.Keys.Where(k => !Settings.Groups.TryGetValue(k, out var g) || g.Members.Count < 2).ToList())
        {
            _tabStrips[id].Close();
            _tabStrips.Remove(id);
            _tabSwitchedAt.Remove(id);
        }
        if (Settings.Groups.Count == 0) return;
        var now = Environment.TickCount64;
        var pointer = Pin.Pointer()?.At;
        var screens = Monitors();
        foreach (var (id, g) in Settings.Groups.ToList())
        {
            if (g.Members.Count < 2) continue;
            var active = Groups.ActiveOf(g);
            var win = active is null ? null : Window(active);
            var shown = win is { Visible: true } && !WidgetsHidden;
            _tabStrips.TryGetValue(id, out var strip);
            if (shown)
            {
                if (strip is null) _tabStrips[id] = strip = new GroupTabs(this, id);
                strip.Rebuild();
                var area = Placement.Find(screens, win!.Bounds) is MonitorInfo m ? m.WorkArea : win.Bounds;
                strip.Place(win.Bounds, area);
                strip.Show();
            }
            else strip?.Hide();

            var hover = shown && pointer is { } p && win!.Bounds.Contains(p);
            if (!_tabSwitchedAt.ContainsKey(id)) _tabSwitchedAt[id] = now;
            if (g.CycleSeconds > 0 && shown && !hover && !EditMode && now - _tabSwitchedAt[id] >= g.CycleSeconds * 1000L)
            {
                _tabSwitchedAt[id] = now;
                StepTab(id, 1);
                return; // the settings changed under the loop; the next tick carries on
            }
            if (hover) _tabSwitchedAt[id] = now;
        }
    }

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

    /// <summary>Turns "Arrange widgets" (the global lock off) on or off, saves it and updates the widgets and the pills.</summary>
    public void SetEditMode(bool on)
    {
        if (EditMode == on) return;
        Settings.General.LockAll = !on;
        SaveSettings();
        if (!on) ShowGuides([]);
        EditSelection = null;
        Log.Write($"Edit mode {(on ? "on" : "off")}");
        PushAll();
        UpdateEditPills();
        SettingsChangedElsewhere();
    }

    public void SetSafeMode(bool on)
    {
        if (SafeMode == on) return;
        SafeMode = on;
        Log.Write($"Safe mode {(on ? "on" : "off")}");
        SyncWindows();
        SettingsChangedElsewhere();
    }

    public void Peek() => throw new Control.CommandException("Peek is not available on Linux yet (#183)");

    public void QuitSoon() => _ = Task.Delay(300).ContinueWith(_ => _ui.Post(_ => Quit(), null));

    /// <summary>Closes every widget and ends the main loop.</summary>
    public void Quit()
    {
        foreach (var id in _windows.Keys.ToList()) CloseWidget(id);
        foreach (var p in _pills) p.Close();
        foreach (var t in _tabStrips.Values) t.Close();
        _snapGuides?.Close();
        _settings?.Close();
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
    public void PinLayout(string name, bool pinned) => Layouts.SetPin(name, pinned ? CurrentFingerprint() : null);
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
        // Order matters: the outgoing tab goes first, the incoming one is placed while still hidden, and only then shown,
        // so two tabs are never on screen together.
        foreach (var (inst, w) in _windows) if (w.Visible && !WantVisible(inst)) w.Visible = false;
        foreach (var w in _windows.Values) w.ReapplyPlacement();
        SyncWindows();
        PushAll();
        foreach (var id in _tabStrips.Keys.ToList()) _tabSwitchedAt[id] = Environment.TickCount64;
        TickGroups();
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
        foreach (var strip in _tabStrips.Values) strip.Rebuild();
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
}
