using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Umbra;

/// <summary>The Settings panel: a dark window hosting settings/index.html.</summary>
sealed class SettingsWindow : Form, ISettingsView
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    static readonly Color Surface = Color.FromArgb(0, 0, 0);

    readonly App _app;
    readonly WebView2 _view = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Surface };
    JsonArray _catalog = new();
    readonly Dictionary<string, string?> _previewCache = new();
    readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource _catalogSent = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static readonly string[] Pages = { "home", "widgets", "browse", "themes", "layouts", "hotkeys", "general", "privacy", "about" };

    readonly bool _background;
    string? _page; // last page the panel showed, saved on close
    double _zoom = 1.0; // page zoom (SettingsPlacement.Zoom); background windows stay at 100 % so captures are stable

    /// <summary>Background windows (used for captures) are shown off-screen, outside the taskbar, without taking focus.</summary>
    protected override bool ShowWithoutActivation => _background;

    public SettingsWindow(App app, bool background = false)
    {
        _app = app;
        _background = background;
        Text = "Umbra";
        Icon = App.AppIcon;
        BackColor = Surface;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        Controls.Add(_view);
        ApplyDpiSize();
        if (!background) { RestorePlacement(); _zoom = _app.Settings.SettingsPanel.Zoom; }
        if (background)
        {
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
        }
    }

    /// <summary>Sizes are in logical (96-DPI) pixels; scale them to the monitor the window is on.</summary>
    void ApplyDpiSize()
    {
        float s = DeviceDpi / 96f;
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        MinimumSize = new Size((int)(760 * s), (int)(520 * s));
        ClientSize = new Size(Math.Min((int)(1000 * s), wa.Width - 40), Math.Min((int)(700 * s), wa.Height - 40));
        CenterToScreen();
    }

    /// <summary>Opens where it was last closed if that monitor is still connected; otherwise it stays centered (<see cref="Placement.ResolveWindow"/>).</summary>
    void RestorePlacement()
    {
        var saved = _app.Settings.SettingsPanel;
        static float ScaleOf(MonitorInfo m) => Native.MonitorScale(new Point(m.Bounds.X + m.Bounds.Width / 2, m.Bounds.Y + m.Bounds.Height / 2));
        if (Placement.ResolveWindow(Screens.All(), saved, ScaleOf, new Size(760, 520)) is not { } r) return;
        StartPosition = FormStartPosition.Manual;
        Bounds = r;
        if (saved.Maximized) WindowState = FormWindowState.Maximized;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (_background || e.Cancel || !IsHandleCreated) return;
        // RestoreBounds is the normal rectangle even while maximized or minimized.
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _app.SaveSettingsPlacement(Placement.Capture(Screens.All(), bounds, DeviceDpi / 96f, WindowState == FormWindowState.Maximized, _page, _zoom));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetDwm(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
        Native.SetDwm(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, Native.DWMWCP_ROUND);
        Native.SetDwm(Handle, Native.DWMWA_BORDER_COLOR, Native.ColorRef(30, 31, 36));
        Native.SetDwm(Handle, Native.DWMWA_CAPTION_COLOR, Native.ColorRef(0, 0, 0));
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            await WebHost.InitAsync(_view);
            _view.ZoomFactor = _zoom;
            _view.CoreWebView2.WebMessageReceived += OnMessage;
            _view.CoreWebView2.Navigate(WebHost.Url("settings/index.html"));
        }
        catch (Exception ex)
        {
            Log.Write($"Settings WebView2 init failed: {ex}");
        }
    }

    async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonNode? msg;
        try { msg = JsonNode.Parse(e.WebMessageAsJson); } catch { return; }
        var id = msg?["id"]?.GetValue<string>() ?? "";

        switch (msg?["type"]?.GetValue<string>())
        {
            case "load":
                SendState();
                PostZoom();
                // Reopen on the last page; a page asked for explicitly (umbractl settings <page>) runs after this and wins.
                if (!_background && _app.Settings.SettingsPanel.Page is { } last && last != "home" && Pages.Contains(last))
                    _ = _view.CoreWebView2.ExecuteScriptAsync($"show('{last}')");
                _loaded.TrySetResult();
                break;

            case "page":
                if (msg["page"]?.GetValue<string>() is string pg && Pages.Contains(pg)) _page = pg;
                break;

            case "zoom": // the sidebar's - / % / + buttons, Ctrl+= / Ctrl+- / Ctrl+0 and Ctrl+wheel; -1, 1 or 0 (reset)
                if (_background) break;
                _zoom = SettingsPlacement.StepZoom(_zoom, msg["step"] is JsonValue sv && sv.TryGetValue<int>(out var step) ? step : 0);
                _view.ZoomFactor = _zoom;
                _app.SaveSettingsZoom(_zoom);
                PostZoom();
                break;

            case "close": // Esc in the page
                Close();
                break;

            case "save":
                if (msg["settings"] is JsonNode s) { _app.ApplySettings(Settings.FromJson(s)); SendState(); }
                break;

            case "setStartup":
                Startup.Set(msg["value"]?.GetValue<bool>() == true);
                SendState();
                break;

            case "setEditMode":
                _app.SetEditMode(msg["value"]?.GetValue<bool>() == true); // re-sends the state
                break;

            case "setSafeMode":
                _app.SetSafeMode(msg["value"]?.GetValue<bool>() == true); // re-sends the state
                break;

            case "setHotkey":
                try { _app.SetHotkey(msg["action"]?.GetValue<string>() ?? "", msg["keys"]?.GetValue<string>() ?? ""); }
                catch (ArgumentException ex) { Post(new JsonObject { ["type"] = "commandError", ["error"] = ex.Message }); }
                SendState();
                break;

            case "resetHotkeys":
                _app.ResetHotkeys();
                SendState();
                break;

            case "saveLayout":
            case "applyLayout":
            case "renameLayout":
            case "deleteLayout":
            case "undoDeleteLayout":
            case "pinLayout":
            {
                var name = msg["name"]?.GetValue<string>() ?? "";
                try
                {
                    switch (msg["type"]!.GetValue<string>())
                    {
                        case "saveLayout": _app.SaveLayout(name); break;
                        case "applyLayout":
                            _app.ApplyLayout(name);
                            Post(new JsonObject { ["type"] = "notice", ["text"] = $"Applied layout {name}" });
                            break;
                        case "renameLayout": _app.RenameLayout(name, msg["to"]?.GetValue<string>() ?? ""); break;
                        case "deleteLayout":
                            _app.DeleteLayout(name);
                            Post(new JsonObject { ["type"] = "layoutDeleted", ["name"] = name });
                            break;
                        case "undoDeleteLayout": _app.UndoDeleteLayout(); break;
                        case "pinLayout": _app.PinLayout(name, msg["pinned"]?.GetValue<bool>() == true); break;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
                { Post(new JsonObject { ["type"] = "commandError", ["error"] = ex.Message }); }
                SendState();
                break;
            }

            case "group":
            {
                string? S(string k) => msg[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                try
                {
                    var group = S("group") ?? "";
                    var inst = S("id") ?? "";
                    switch (S("op"))
                    {
                        case "create": _app.CreateGroup(S("name") ?? "Group", inst.Length == 0 ? null : inst); break;
                        case "join": _app.JoinGroup(group, inst); break;
                        case "leave": _app.LeaveGroup(inst); break;
                        case "activate": _app.ActivateTab(group, inst); break;
                        case "reorder": _app.ReorderTab(group, inst, msg["index"]?.GetValue<int>() ?? 0); break;
                        case "configure": _app.ConfigureGroup(group, S("name"), msg["cycleSeconds"]?.GetValue<int>()); break;
                        case "delete": _app.DeleteGroup(group); break;
                        case "icon": _app.SetTabIcon(inst, S("icon")); break;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException)
                { Post(new JsonObject { ["type"] = "commandError", ["error"] = ex.Message }); }
                SendState();
                break;
            }

            case "setMonitor":
                if (msg["monitor"]?.GetValue<string>() is string mon && Placement.Has(Screens.All(), mon))
                    _app.Window(id)?.MoveToMonitor(mon);
                SendState();
                break;

            case "resetPosition":
                _app.ResetPosition(id);
                SendState();
                break;

            case "revokeCamera":
                // Settings can only take the camera away; it is granted by the host prompt after a click on the widget.
                if (_app.Manifests.FirstOrDefault(w => w.Id == (msg["widget"]?.GetValue<string>() ?? "")) is { } cw) _app.RevokeCamera(cw.Id);
                SendState();
                break;

            case "approveCommand":
            case "revokeCommand":
            {
                // The only places a command gets approved are this button (shown with the exact command line) and the run-time prompt.
                var widget = _app.Manifests.FirstOrDefault(w => w.Id == (msg["widget"]?.GetValue<string>() ?? ""));
                var cmdId = msg["command"]?.GetValue<string>() ?? "";
                try
                {
                    if (widget is null) throw new WidgetCommandException("Unknown widget");
                    if (msg["type"]?.GetValue<string>() == "approveCommand")
                        _app.Commands.ApproveFromSettings(widget, cmdId, msg["hash"]?.GetValue<string>() ?? "");
                    else
                        _app.Commands.Approvals.Revoke(widget.Id, cmdId);
                }
                catch (WidgetCommandException ex) { Post(new JsonObject { ["type"] = "commandError", ["error"] = ex.Message }); }
                SendState();
                break;
            }
            case "addInstance":
                if (_app.Manifests.FirstOrDefault(m => m.Id == id) is { } wm)
                {
                    try { _app.AddInstance(wm); }
                    catch (InvalidOperationException ex) { Log.Write($"Add copy of {id} failed: {ex.Message}"); }
                }
                SendState();
                break;

            case "removeInstance":
                try { _app.RemoveInstance(id); }
                catch (InvalidOperationException ex) { Log.Write($"Remove copy {id} failed: {ex.Message}"); }
                SendState();
                break;

            case "checkUpdate":
                await _app.Updater.CheckAsync(manual: true); // re-sends the state
                break;

            case "installUpdate":
                try { await _app.Updater.InstallAsync(); }
                catch (Exception ex)
                {
                    Log.Write($"Update failed: {ex.Message}");
                    Post(new JsonObject { ["type"] = "updateError", ["error"] = ex.Message });
                }
                break;

            case "copyDiagnostics":
                try
                {
                    Clipboard.SetDataObject(Diagnostics.Format(Diagnostics.Collect(_app)), true, 5, 100);
                    Post(new JsonObject { ["type"] = "notice", ["text"] = "Copied, paste it into the issue" });
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or System.Threading.ThreadStateException)
                { Post(new JsonObject { ["type"] = "commandError", ["error"] = $"Couldn't copy: {ex.Message}" }); }
                break;

            case "reportIssue":
            {
                var snap = Diagnostics.Collect(_app);
                WebHost.OpenExternal(Diagnostics.IssueUrl(Community.Repo, snap.Version, Diagnostics.IssueWindows(snap)));
                break;
            }

            case "restart":
                _app.Restart();
                break;

            case "openUrl":
                WebHost.OpenExternal(msg["url"]?.GetValue<string>() ?? "");
                break;

            case "privacy":
                Post(new JsonObject { ["type"] = "privacy", ["widgets"] = Backup.Privacy(_app) });
                break;

            case "deleteData":
            {
                var wid = msg["widget"]?.GetValue<string>() ?? "";
                var name = _app.Manifests.FirstOrDefault(w => w.Id == wid)?.Name;
                if (name is null) break;
                if (!HostDialog.Confirm(this, $"Delete everything {ApprovalDialog.Clean(name, 60)} has saved? This cannot be undone.", yesText: "Delete", noText: "Cancel")) break;
                foreach (var inst in _app.Settings.Instances(wid)) WidgetStore.Delete(inst);
                Post(new JsonObject { ["type"] = "privacy", ["widgets"] = Backup.Privacy(_app) });
                break;
            }

            case "exportBackup":
            {
                using var dlg = new SaveFileDialog { Filter = "Umbra backup (*.json)|*.json", FileName = $"umbra-backup-{DateTime.Now:yyyy-MM-dd}.json" };
                if (dlg.ShowDialog(this) != DialogResult.OK) break;
                try
                {
                    File.WriteAllText(dlg.FileName, Backup.Create(_app.Settings).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    Post(new JsonObject { ["type"] = "notice", ["text"] = "Backup saved" });
                }
                catch (Exception ex) { Post(new JsonObject { ["type"] = "commandError", ["error"] = $"Backup failed: {ex.Message}" }); }
                break;
            }

            case "importBackup":
            {
                using var dlg = new OpenFileDialog { Filter = "Umbra backup (*.json)|*.json" };
                if (dlg.ShowDialog(this) != DialogResult.OK) break;
                try
                {
                    var parsed = Backup.ReadFile(dlg.FileName);
                    if (!HostDialog.Confirm(this, "Replace your current settings, layout and widget data with this backup? Command approvals are not restored.", yesText: "Replace", noText: "Cancel")) break;
                    _app.RestoreBackup(parsed);
                    Post(new JsonObject { ["type"] = "notice", ["text"] = "Backup restored" });
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                { Post(new JsonObject { ["type"] = "commandError", ["error"] = $"Restore failed: {ex.Message}" }); }
                break;
            }

            case "resetAll":
                // Settings, layout and widget data go back to a fresh install; installed widgets, themes and command approvals are kept.
                if (!HostDialog.Confirm(this, "Reset all settings, widget positions and saved widget data to a fresh start? Installed widgets stay.", yesText: "Reset", noText: "Cancel")) break;
                _app.RestoreBackup(new Backup.Parsed(new Settings(), new Dictionary<string, JsonObject>()));
                Post(new JsonObject { ["type"] = "notice", ["text"] = "Reset to defaults" });
                break;

            case "openDataFolder":
                System.Diagnostics.Process.Start("explorer.exe", AppPaths.DataDir);
                break;

            case "openLog":
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(AppPaths.DataDir, "umbra.log")) { UseShellExecute = true }); }
                catch (Exception ex) { Log.Write($"Open log failed: {ex.Message}"); }
                break;

            case "openFolder":
                System.Diagnostics.Process.Start("explorer.exe", AppPaths.CommunityDir);
                break;

            case "catalog":
                await SendCatalog();
                break;

            case "themes":
                await SendThemes();
                break;

            case "setTheme":
                try { _app.SetTheme(id); } catch (InvalidOperationException ex) { Log.Write(ex.Message); }
                await SendThemes(refetch: false);
                break;

            case "installTheme":
                await ThemeJob(id, async () =>
                {
                    var entry = _themeCatalog.FirstOrDefault(t => t?["id"]?.GetValue<string>() == id)
                                ?? throw new InvalidOperationException("Theme not in catalog");
                    await Community.InstallThemeAsync(entry);
                    if (_app.Settings.General.Theme == id) _app.SetTheme(id);
                });
                break;

            case "removeTheme":
                await ThemeJob(id, () =>
                {
                    Community.RemoveTheme(id);
                    if (_app.Settings.General.Theme == id) _app.SetTheme(Theme.DefaultId);
                    return Task.CompletedTask;
                });
                break;

            case "install":
                await RunJob(id, async () =>
                {
                    var entry = _catalog.FirstOrDefault(w => w?["id"]?.GetValue<string>() == id)
                                ?? throw new InvalidOperationException("Widget not in catalog");
                    using var pending = await Community.PrepareAsync(entry);
                    // Host-drawn, so the Settings page (or a widget) can't accept new access on the user's behalf.
                    if (pending.NeedsApproval && !ConfirmNewAccess(pending))
                        throw new InvalidOperationException("Not updated: the new access was declined");
                    _app.CloseWidget(id);
                    pending.Commit();
                });
                break;

            case "installUrl":
            {
                // The address is typed into a host-drawn box, never taken from the page.
                var url = PackageInstall.AskUrl(this);
                if (url is null) break;
                try
                {
                    var done = await PackageInstall.RunAsync(_app, () => Community.PrepareUrlAsync(url), url, this);
                    if (done is not null) Post(new JsonObject { ["type"] = "notice", ["text"] = done });
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log.Write($"Install from URL failed: {ex.Message}");
                    Post(new JsonObject { ["type"] = "commandError", ["error"] = $"Not installed: {ex.Message}" });
                }
                SendState();
                await SendCatalog(refetch: false);
                break;
            }

            case "installLocal":
            {
                // The page only says "file" or "folder": the path comes from a host-drawn picker, never from the page.
                string? path = null;
                if (msg["kind"]?.GetValue<string>() == "folder")
                {
                    using var dlg = new FolderBrowserDialog { Description = "Pick the widget folder (the one with widget.json)", UseDescriptionForTitle = true };
                    if (dlg.ShowDialog(this) == DialogResult.OK) path = dlg.SelectedPath;
                }
                else
                {
                    using var dlg = new OpenFileDialog { Filter = "Umbra widget (*.zip;*.umbrawidget)|*.zip;*.umbrawidget", Title = "Install a widget from file" };
                    if (dlg.ShowDialog(this) == DialogResult.OK) path = dlg.FileName;
                }
                if (path is null) break;
                try
                {
                    // Reading and checking the files runs off the UI thread; the await comes back here for the prompts and the commit.
                    using var pending = await Task.Run(() => Community.PrepareLocal(path));
                    var wid = pending.Manifest.Id;
                    if (_app.Manifests.Any(m => m.Id == wid && m.Source == WidgetSource.BuiltIn))
                        throw new InvalidOperationException($"'{wid}' is the id of a built-in widget");
                    // Host-drawn, so the page can't accept access on the user's behalf. Cancel leaves nothing behind.
                    if (pending.NeedsApproval && !ConfirmNewAccess(pending)) break;
                    if (pending.IsUpdate && !pending.NeedsApproval && !HostDialog.Confirm(this, $"Replace the installed {ApprovalDialog.Clean(pending.Manifest.Name, 60)} with version {pending.Manifest.Version} from this file?", yesText: "Replace", noText: "Cancel")) break;
                    _app.CloseWidget(wid);
                    pending.Commit();
                    _app.ReloadWidgets();
                    _app.SettingsChangedElsewhere();
                    Post(new JsonObject { ["type"] = "notice", ["text"] = $"Installed {pending.Manifest.Name}" });
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Broad on purpose: this runs in an async void message handler, where an escaped exception would end Umbra.
                    Log.Write($"Install from file failed: {ex.Message}");
                    Post(new JsonObject { ["type"] = "commandError", ["error"] = $"Not installed: {ex.Message}" });
                }
                SendState();
                await SendCatalog(refetch: false);
                break;
            }

            case "updateAll":
            {
                UpdateAllResult? result = null;
                string? error = null;
                try
                {
                    result = await _app.UpdateAllWidgetsAsync((i, n, wid) =>
                        Post(new JsonObject { ["type"] = "updateProgress", ["index"] = i, ["total"] = n, ["id"] = wid }));
                }
                catch (Exception ex) { error = ex.Message; Log.Write($"Update all failed: {ex}"); }
                Post(new JsonObject { ["type"] = "updateAllDone", ["error"] = error, ["result"] = result?.ToJson() });
                SendState();
                await SendCatalog(refetch: false);
                break;
            }

            case "remove":
            {
                string? undo = null;
                await RunJob(id, () =>
                {
                    _app.CloseWidget(id); // every copy
                    undo = WidgetTrash.Move(_app.Settings, id); // folder, data and settings of every copy, kept for Undo
                    try
                    {
                        Community.Remove(id);
                        _app.ForgetInstances(id);
                    }
                    catch (Exception ex) when (undo is not null)
                    {
                        // The widget is already in the trash, so it counts as removed; keep the Undo offer instead of failing the job.
                        Log.Write($"Remove of {id} left leftovers after it was trashed: {ex.Message}");
                    }
                    return Task.CompletedTask;
                }, () => undo);
                break;
            }

            case "undoRemove":
            {
                string? error = null;
                string? name = null;
                try { name = _app.RestoreWidget(id)?.Name; } // here id is the undo token from jobDone
                catch (InvalidOperationException ex) { error = ex.Message; Log.Write($"Undo remove failed: {ex.Message}"); }
                Post(new JsonObject { ["type"] = "undoDone", ["name"] = name, ["error"] = error });
                SendState();
                await SendCatalog(refetch: false);
                break;
            }
        }
    }

    /// <summary>"Todo 1.3 wants new access: ..." with the changelog, as a native Yes/No box. No is the default button.</summary>
    bool ConfirmNewAccess(Community.PendingInstall p) => PackageInstall.ConfirmNewAccess(this, p);

    async Task RunJob(string id, Func<Task> job, Func<string?>? undo = null)
    {
        string? error = null;
        try { await job(); }
        catch (Exception ex) { error = ex.Message; Log.Write($"Widget job {id} failed: {ex}"); }
        _app.ReloadWidgets();
        Post(new JsonObject { ["type"] = "jobDone", ["id"] = id, ["error"] = error, ["undo"] = undo?.Invoke() });
        SendState();
        await SendCatalog(refetch: false);
    }

    JsonArray _themeCatalog = new();
    bool _themeCatalogLoaded;
    TaskCompletionSource _themesSent = new(TaskCreationOptions.RunContinuationsAsynchronously);

    async Task ThemeJob(string id, Func<Task> job)
    {
        string? error = null;
        try { await job(); }
        catch (Exception ex) { error = ex.Message; Log.Write($"Theme job {id} failed: {ex}"); }
        Post(new JsonObject { ["type"] = "jobDone", ["id"] = id, ["error"] = error });
        SendState();
        await SendThemes(refetch: false);
    }

    /// <summary>Sends installed + catalog themes (the catalog is fetched once per visit unless refetch).</summary>
    async Task SendThemes(bool refetch = true)
    {
        string? error = null;
        if (refetch || !_themeCatalogLoaded)
        {
            try { _themeCatalog = await Community.FetchThemeCatalogAsync(); _themeCatalogLoaded = true; }
            catch (Exception ex) { error = ex.Message; Log.Write($"Theme catalog fetch failed: {ex.Message}"); }
        }
        Post(new JsonObject { ["type"] = "themes", ["error"] = error, ["themes"] = Theme.Listing(_themeCatalog) });
        _themesSent.TrySetResult();
    }

    void SendState()
    {
        var widgets = new JsonArray();
        foreach (var m in _app.Manifests)
        foreach (var inst in _app.Settings.Instances(m.Id))
        {
            _app.Settings.For(m, inst); // ensure defaults exist
            widgets.Add(new JsonObject
            {
                ["id"] = inst,
                ["widget"] = m.Id,
                ["instance"] = InstanceId.NumberOf(inst),
                ["name"] = InstanceId.Label(m, inst),
                ["monitor"] = _app.Settings.Widgets[inst].Monitor,
                ["description"] = m.Description,
                ["author"] = m.Author,
                ["version"] = m.Version,
                ["source"] = m.IsPermanent ? "builtin" : "community",
                ["private"] = m.Private,
                ["preview"] = m.Preview is null ? null
                    : m.Source == WidgetSource.BuiltIn ? WebHost.Url($"widgets/{m.Folder}/{m.Preview}")
                    : WebHost.CommunityUrl($"{m.Folder}/{m.Preview}"),
                ["hosts"] = new JsonArray(m.Permissions.Hosts.Select(h => (JsonNode?)h).ToArray()),
                ["notifications"] = m.Permissions.Notifications,
                ["camera"] = m.Permissions.Camera,
                ["cameraApproved"] = m.Permissions.Camera && _app.CameraConsent.Approvals.CameraApproved(m.Id),
                ["media"] = new JsonArray(m.Permissions.Media.Select(h => (JsonNode?)h).ToArray()),
                ["system"] = new JsonArray(m.Permissions.System.Select(h => (JsonNode?)h).ToArray()),
                ["commands"] = new JsonArray(m.Permissions.Commands.Select(c => (JsonNode?)new JsonObject
                {
                    ["id"] = c.Id,
                    ["line"] = c.DisplayLine(),
                    ["description"] = c.Description,
                    ["hash"] = c.Hash(),
                    ["status"] = _app.Commands.Approvals.Status(m.Id, c),
                }).ToArray()),
                ["options"] = m.Options.DeepClone(),
                ["optionOrder"] = new JsonArray(m.OptionOrder.Select(k => (JsonNode?)k).ToArray()),
            });
        }

        Post(new JsonObject
        {
            ["type"] = "state",
            ["version"] = AppInfo.Version,
            ["startup"] = Startup.IsEnabled(),
            ["portable"] = AppPaths.IsPortable,
            ["dataDir"] = AppPaths.DataDir,
            ["editMode"] = _app.EditMode,
            ["safeMode"] = _app.SafeMode,
            ["layouts"] = _app.LayoutsJson(),
            ["groups"] = _app.GroupsJson(),
            ["update"] = _app.Updater.ToJson(),
            ["repo"] = $"https://github.com/{Community.Repo}",
            ["settings"] = _app.Settings.ToJson(),
            ["themeCss"] = _app.ThemeCss,
            ["accentCss"] = Prefs.AccentCss(_app.Settings.General),
            ["systemAccent"] = Prefs.SystemAccent?.Accent ?? "",
            ["hotkeys"] = _app.DescribeHotkeys()["hotkeys"]!.DeepClone(),
            ["monitors"] = new JsonArray(Screens.All().Select((m, i) => (JsonNode?)new JsonObject { ["name"] = m.Name, ["label"] = $"Monitor {i + 1}{(m.Primary ? " (primary)" : "")} - {m.Bounds.Width}x{m.Bounds.Height}" }).ToArray()),
            ["widgets"] = widgets,
        });
    }

    async Task SendCatalog(bool refetch = true)
    {
        string? error = null;
        if (refetch || _catalog.Count == 0)
        {
            try { _catalog = await Community.FetchCatalogAsync(); }
            catch (Exception ex) { error = ex.Message; Log.Write($"Catalog fetch failed: {ex.Message}"); }
        }

        var previews = await Task.WhenAll(_catalog.Select(async entry =>
        {
            var wid = entry?["id"]?.GetValue<string>() ?? "";
            if (entry?["preview"]?.GetValue<string>() is not string p) return (wid, (string?)null);
            if (!_previewCache.TryGetValue($"{wid}/{p}", out var url))
                _previewCache[$"{wid}/{p}"] = url = await Community.PreviewDataUrlAsync(wid, p);
            return (wid, url);
        }));

        var list = new JsonArray();
        foreach (var entry in _catalog)
        {
            if (entry is null) continue;
            var copy = entry.DeepClone().AsObject();
            var wid = copy["id"]?.GetValue<string>() ?? "";
            var installed = _app.Manifests.FirstOrDefault(m => m.Id == wid);
            copy["installed"] = installed is not null;
            copy["builtin"] = installed?.IsPermanent == true;
            copy["installedVersion"] = installed?.Version;
            copy["updateAvailable"] = Community.IsUpdate(copy, installed);
            copy["incompatible"] = Community.CatalogIncompatibility(copy); // made for a newer Umbra: greyed out
            copy["local"] = installed is not null && Community.IsLocalInstall(wid); // installed from a file: no catalog updates
            copy["previewUrl"] = previews.FirstOrDefault(x => x.wid == wid).Item2;
            list.Add(copy);
        }
        if (error is null) _app.NoteCatalog(_catalog);
        Post(new JsonObject { ["type"] = "catalog", ["error"] = error, ["widgets"] = list });
        _catalogSent.TrySetResult();
    }

    /// <summary>Re-sends the state after something changed outside this window (tray menu, umbractl).</summary>
    public void RefreshState()
    {
        if (!_loaded.Task.IsCompleted) return;
        SendState();
        if (_themeCatalogLoaded) _ = SendThemes(refetch: false);
        if (_catalog.Count > 0) _ = SendCatalog(refetch: false);
    }

    /// <summary>Switches the panel to one of <see cref="Pages"/> (the page's own show() function).</summary>
    public async Task ShowPageAsync(string page)
    {
        if (page == "welcome" || page.StartsWith("welcome:", StringComparison.Ordinal)) // the welcome dialog, optionally at slide n (captures)
        {
            var slide = page.Length > 8 && int.TryParse(page.AsSpan(8), out var n) ? n : 1;
            await _loaded.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await _view.CoreWebView2.ExecuteScriptAsync($"welcome({slide})");
            return;
        }
        if (!Pages.Contains(page)) throw new ArgumentException($"Unknown Settings page '{page}'. Pages: {string.Join(", ", Pages)}");
        await _loaded.Task.WaitAsync(TimeSpan.FromSeconds(20));
        if (page == "browse") _catalogSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (page == "themes") _themesSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await _view.CoreWebView2.ExecuteScriptAsync($"show('{page}')");
    }

    /// <summary>Shows My widgets scrolled to one copy's card, which is highlighted for a moment (the page's own focusWidget function).</summary>
    public async Task ShowWidgetAsync(string instance)
    {
        if (!InstanceId.IsValid(instance)) throw new ArgumentException($"Invalid widget id '{instance}'");
        await _loaded.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await _view.CoreWebView2.ExecuteScriptAsync($"focusWidget({JsonSerializer.Serialize(instance)})");
    }

    /// <summary>A pixel-exact PNG of one Settings page (only this window's web content, never the screen).</summary>
    public async Task<byte[]> CaptureAsync(string page)
    {
        await ShowPageAsync(page);
        if (page == "browse")
            await Task.WhenAny(_catalogSent.Task, Task.Delay(TimeSpan.FromSeconds(25)));
        if (page == "themes")
            await Task.WhenAny(_themesSent.Task, Task.Delay(TimeSpan.FromSeconds(25)));
        await Task.Delay(page == "browse" ? 1200 : 500); // images decode, transitions settle
        using var ms = new MemoryStream();
        await _view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
        return ms.ToArray();
    }

    void PostZoom() => Post(new JsonObject { ["type"] = "zoom", ["percent"] = (int)Math.Round(_zoom * 100), ["min"] = _zoom <= SettingsPlacement.MinZoom, ["max"] = _zoom >= SettingsPlacement.MaxZoom });

    void Post(JsonNode node)
    {
        if (IsDisposed || _view.CoreWebView2 is null) return;
        _view.CoreWebView2.PostWebMessageAsJson(node.ToJsonString(Json));
    }
}
