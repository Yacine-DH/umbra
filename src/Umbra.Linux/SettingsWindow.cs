using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Umbra;

/// <summary>
/// The Settings panel on Linux: a normal (decorated, activatable) GTK window hosting the same <c>settings/index.html</c>
/// as Windows over the bridge shim, with the message protocol of the Windows <c>SettingsWindow</c>. Every confirmation
/// (new access, delete, replace, reset) is a native GTK dialog from <see cref="Dialogs"/>, never HTML, so the page can't
/// approve anything on its own.
/// </summary>
sealed class SettingsWindow : ISettingsView
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static readonly string[] Pages = { "home", "widgets", "browse", "themes", "layouts", "hotkeys", "general", "privacy", "about" };

    readonly App _app;
    readonly Gtk.Window _window;
    readonly WebKit.WebView _view;
    readonly bool _background;
    bool _closed;
    string? _page;
    double _zoom = 1.0;
    JsonArray _catalog = new();
    readonly Dictionary<string, string?> _previewCache = new();
    readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource _catalogSent = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Raised once when the window is gone.</summary>
    public event Action? Closed;

    public bool IsClosed => _closed;

    /// <param name="background">For captures: a window kept off the taskbar and out of sight (desktop layer, transparent), at 100 % zoom.</param>
    public SettingsWindow(App app, Gtk.Application gtk, bool background = false)
    {
        _app = app;
        _background = background;
        _view = WebHost.NewView(out var content, devTools: Prefs.DeveloperMode, sandbox: null, label: "settings");
        _view.SetBackgroundColor(new Gdk.RGBA { Red = 0, Green = 0, Blue = 0, Alpha = 1 });
        _view.OnContextMenu += (_, _) => !Prefs.DeveloperMode; // Inspect only in developer mode
        _view.OnWebProcessTerminated += (_, e) => { Log.Write($"Settings web process ended ({e.Reason}); reloading"); if (!_closed) _view.Reload(); };
        content.RegisterScriptMessageHandler("umbra", null);
        content.OnScriptMessageReceived += (_, e) => OnMessage(e.Value.ToJson(0));

        _window = Gtk.Window.New();
        _window.SetTitle("Umbra");
        _window.SetIconName(Program.AppId);
        _window.SetChild(_view);
        var saved = _app.Settings.SettingsPanel;
        _window.SetSizeRequest(760, 520);
        _window.SetDefaultSize(saved.Width >= 760 ? saved.Width : 1000, saved.Height >= 520 ? saved.Height : 700);
        if (!background)
        {
            gtk.AddWindow(_window);
            if (saved.Maximized) _window.Maximize();
            _zoom = saved.Zoom;
        }
        else
        {
            // A capture window: never in the taskbar or Alt+Tab, below every window, invisible. The pin backend gives it the
            // same desktop-layer hints as a widget; it never takes focus.
            _window.SetDecorated(false);
            _window.SetOpacity(0.01);
            _window.SetCanFocus(false);
            ((Gtk.Widget)_window).Realize();
            _app.Pin.Prepare(_window);
            _window.OnMap += (_, _) => _app.Pin.Mapped(_window);
        }
        _window.OnCloseRequest += (_, _) => { OnClosing(); return false; };
        _view.ZoomLevel = _zoom;
        _view.LoadUri(UmbraScheme.FromHttps(VirtualHosts.Url("settings/index.html")));
    }

    /// <summary>Shows the window and brings it to the front (a normal window: it may take focus).</summary>
    public void Present()
    {
        if (_closed) return;
        if (_background) _window.SetVisible(true);
        else _window.Present();
    }

    void OnClosing()
    {
        if (_closed) return;
        _closed = true;
        if (!_background)
        {
            // GTK 4 can't read a window's position; size, maximized, page and zoom are kept (the compositor places the window).
            var p = _app.Settings.SettingsPanel;
            p.Maximized = _window.IsMaximized();
            if (!p.Maximized) { p.Width = _window.GetWidth(); p.Height = _window.GetHeight(); }
            p.Page = _page ?? p.Page;
            p.Zoom = _zoom;
            _app.Settings.Save();
        }
        Closed?.Invoke();
    }

    public void Close()
    {
        if (_closed) return;
        OnClosing();
        _window.Destroy();
    }

    async void OnMessage(string json)
    {
        JsonNode? msg;
        try { msg = JsonNode.Parse(json); } catch { return; }
        if (msg is not JsonObject) return;
        try { await Handle(msg); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // async void: an escaped exception would end Umbra.
            Log.Write($"Settings message failed: {ex}");
            Post(new JsonObject { ["type"] = "commandError", ["error"] = ex.Message });
        }
    }

    WidgetManifest? ManifestOf(JsonNode msg, string key = "widget") => _app.Manifests.FirstOrDefault(w => w.Id == (S(msg, key) ?? ""));

    static string? S(JsonNode msg, string k) => msg[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    static bool B(JsonNode msg, string k) => msg[k] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    async Task Handle(JsonNode msg)
    {
        var id = S(msg, "id") ?? "";
        switch (S(msg, "type"))
        {
            case "load":
                SendState();
                PostZoom();
                if (!_background && _app.Settings.SettingsPanel.Page is { } last && last != "home" && Pages.Contains(last))
                    _ = Eval($"show('{last}')");
                _loaded.TrySetResult();
                break;

            case "page":
                if (S(msg, "page") is string pg && Pages.Contains(pg)) _page = pg;
                break;

            case "zoom":
                if (_background) break;
                _zoom = SettingsPlacement.StepZoom(_zoom, msg["step"] is JsonValue sv && sv.TryGetValue<int>(out var step) ? step : 0);
                _view.ZoomLevel = _zoom;
                _app.Settings.SettingsPanel.Zoom = _zoom;
                _app.Settings.Save();
                PostZoom();
                break;

            case "close":
                Close();
                break;

            case "save":
                if (msg["settings"] is JsonNode s) { _app.ApplySettings(Settings.FromJson(s)); SendState(); }
                break;

            case "setStartup":
                try { _app.StartupEnabled = B(msg, "value"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                { Post(new JsonObject { ["type"] = "commandError", ["error"] = $"Couldn't change start on login: {ex.Message}" }); }
                SendState();
                break;

            case "setEditMode":
                _app.SetEditMode(B(msg, "value"));
                break;

            case "setSafeMode":
                _app.SetSafeMode(B(msg, "value"));
                SendState();
                break;

            case "setHotkey":
                try { _app.SetHotkey(S(msg, "action") ?? "", S(msg, "keys") ?? ""); }
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
                var name = S(msg, "name") ?? "";
                try
                {
                    switch (S(msg, "type"))
                    {
                        case "saveLayout": _app.SaveLayout(name); break;
                        case "applyLayout":
                            _app.ApplyLayout(name);
                            Post(new JsonObject { ["type"] = "notice", ["text"] = $"Applied layout {name}" });
                            break;
                        case "renameLayout": _app.RenameLayout(name, S(msg, "to") ?? ""); break;
                        case "deleteLayout":
                            _app.DeleteLayout(name);
                            Post(new JsonObject { ["type"] = "layoutDeleted", ["name"] = name });
                            break;
                        case "undoDeleteLayout": _app.Layouts.UndoDelete(); break;
                        case "pinLayout": _app.PinLayout(name, B(msg, "pinned")); break;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
                { Post(new JsonObject { ["type"] = "commandError", ["error"] = ex.Message }); }
                SendState();
                break;
            }

            case "group":
            {
                try
                {
                    var group = S(msg, "group") ?? "";
                    var inst = S(msg, "id") ?? "";
                    switch (S(msg, "op"))
                    {
                        case "create": _app.CreateGroup(S(msg, "name") ?? "Group", inst.Length == 0 ? null : inst); break;
                        case "join": _app.JoinGroup(group, inst); break;
                        case "leave": _app.LeaveGroup(inst); break;
                        case "activate": _app.ActivateTab(group, inst); break;
                        case "reorder": _app.ReorderTab(group, inst, msg["index"]?.GetValue<int>() ?? 0); break;
                        case "configure": _app.ConfigureGroup(group, S(msg, "name"), msg["cycleSeconds"]?.GetValue<int>()); break;
                        case "delete": _app.DeleteGroup(group); break;
                        case "icon": _app.SetTabIcon(inst, S(msg, "icon")); break;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException)
                { Post(new JsonObject { ["type"] = "commandError", ["error"] = ex.Message }); }
                SendState();
                break;
            }

            case "setMonitor":
                if (S(msg, "monitor") is string mon && Placement.Has(_app.Monitors(), mon)) _app.Window(id)?.MoveToMonitor(mon);
                SendState();
                break;

            case "resetPosition":
                _app.ResetPosition(id);
                SendState();
                break;

            case "revokeCamera":
                SendState(); // no camera on Linux: nothing is ever approved
                break;

            case "approveCommand":
            case "revokeCommand":
            {
                // The only places a command gets approved are this button (shown with the exact command line) and the run-time prompt.
                var widget = ManifestOf(msg);
                var cmdId = S(msg, "command") ?? "";
                try
                {
                    if (widget is null) throw new WidgetCommandException("Unknown widget");
                    if (S(msg, "type") == "approveCommand") _app.Commands.ApproveFromSettings(widget, cmdId, S(msg, "hash") ?? "");
                    else _app.Commands.Approvals.Revoke(widget.Id, cmdId);
                }
                catch (WidgetCommandException ex) { Post(new JsonObject { ["type"] = "commandError", ["error"] = ex.Message }); }
                SendState();
                break;
            }

            case "saveScript":
            case "deleteScript":
            {
                // The user's own scripts (permissions.userScripts). This page is the only writer; no control command, umbractl or MCP tool.
                var widget = ManifestOf(msg);
                try
                {
                    if (widget is null) throw new WidgetCommandException("Unknown widget");
                    if (S(msg, "type") == "deleteScript")
                    {
                        var sid = S(msg, "script") ?? "";
                        var name = _app.Commands.UserScriptsOf(widget).FirstOrDefault(s => s.Id == sid)?.Title ?? sid;
                        if (!await Dialogs.ConfirmAsync(_window, $"Delete the script {Dialogs.Clean(name, 60)}?", yes: "Delete")) break;
                        _app.Commands.DeleteUserScript(widget, sid);
                        Post(new JsonObject { ["type"] = "notice", ["text"] = $"Deleted {name}" });
                    }
                    else
                    {
                        var input = msg["script"] as JsonObject ?? throw new WidgetCommandException("Nothing to save");
                        var script = new UserScript
                        {
                            Id = S(input, "id") ?? "", Title = S(input, "title") ?? "", Description = S(input, "description") ?? "",
                            Kind = S(input, "kind") ?? "", Mode = S(input, "mode") ?? "", Path = S(input, "path") ?? "",
                            Code = S(input, "code") ?? "", Args = S(input, "args") ?? "",
                            TimeoutSeconds = input["timeoutSeconds"] is JsonValue t && t.TryGetValue<int>(out var n) ? n : CommandSpec.DefaultTimeoutSeconds,
                            StartIn = S(input, "startIn") ?? "", KeepsRunning = B(input, "keepsRunning"), AutoStart = B(input, "autoStart"),
                            Restart = B(input, "restart"),
                            RunAsAdmin = false, // Windows UAC only; there is no elevation path on Linux
                        };
                        var (saved, approved) = _app.Commands.SaveUserScript(widget, script);
                        Post(new JsonObject { ["type"] = "scriptSaved", ["widget"] = widget.Id, ["script"] = saved.Id });
                        Post(approved
                            ? new JsonObject { ["type"] = "notice", ["text"] = $"Saved {saved.Title}" }
                            : new JsonObject { ["type"] = "commandError", ["error"] = $"Saved {saved.Title}, but its program wasn't found on this PC. Install it (or use a full path), then press Approve." });
                    }
                    _app.CommandsChanged(widget.Id);
                }
                catch (WidgetCommandException ex) { Post(new JsonObject { ["type"] = "commandError", ["error"] = ex.Message }); }
                SendState();
                break;
            }

            case "startScript":
            case "stopScript":
            {
                var widget = ManifestOf(msg);
                var sid = S(msg, "script") ?? "";
                try
                {
                    if (widget is null) throw new WidgetCommandException("Unknown widget");
                    if (S(msg, "type") == "stopScript") _app.Commands.Stop(widget.Id, widget.Id, sid);
                    else if (!_app.HasOpenWindow(widget.Id)) throw new WidgetCommandException($"Turn {widget.Name} on first: a script that keeps running stops when its widget is off");
                    else _app.Commands.StartFromSettings(widget, sid);
                }
                catch (WidgetCommandException ex) { Post(new JsonObject { ["type"] = "commandError", ["error"] = ex.Message }); }
                SendState();
                break;
            }

            case "pickScriptFolder":
            {
                var path = await Dialogs.PickFolderAsync(_window, "Pick the folder the script starts in", S(msg, "path"));
                if (path is null) break;
                Post(new JsonObject { ["type"] = "scriptFolder", ["ref"] = msg["ref"]?.DeepClone(), ["path"] = path });
                break;
            }

            case "pickScriptFile":
            {
                // A host file dialog, so the path comes from the user's pick.
                var kind = S(msg, "kind") ?? "";
                var (filter, patterns) = kind switch
                {
                    "python" => ("Python script", new[] { "*.py" }),
                    "bash" => ("Shell script", new[] { "*.sh" }),
                    "powershell" => ("PowerShell script", new[] { "*.ps1" }),
                    _ => ((string?)null, (string[]?)null),
                };
                var path = await Dialogs.OpenFileAsync(_window, kind == "program" ? "Pick the program to run" : "Pick the script to run", filter, patterns);
                if (path is null) break;
                Post(new JsonObject { ["type"] = "scriptFile", ["ref"] = msg["ref"]?.DeepClone(), ["path"] = path });
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
                await _app.Updater.CheckAsync(manual: true);
                SendState();
                break;

            case "installUpdate":
                // Linux updates are notify-only: the button opens the release page, nothing is installed by Umbra.
                if (_app.Updater.Available is { } rel) WebHost.OpenExternal(rel.PageUrl);
                break;

            case "copyDiagnostics":
                if (Gdk.Display.GetDefault() is { } display)
                {
                    display.GetClipboard().SetText(_app.Diagnostics());
                    Post(new JsonObject { ["type"] = "notice", ["text"] = "Copied, paste it into the issue" });
                }
                break;

            case "reportIssue":
                WebHost.OpenExternal($"https://github.com/{Community.Repo}/issues/new?template=bug.yml&version={Uri.EscapeDataString(AppInfo.Version)}"
                    + $"&windows={Uri.EscapeDataString(App.SystemLine())}");
                break;

            case "restart":
                _app.Restart();
                break;

            case "openUrl":
                WebHost.OpenExternal(S(msg, "url") ?? "");
                break;

            case "privacy":
                Post(new JsonObject { ["type"] = "privacy", ["widgets"] = Backup.Privacy(_app) });
                break;

            case "deleteData":
            {
                var wid = S(msg, "widget") ?? "";
                var name = _app.Manifests.FirstOrDefault(w => w.Id == wid)?.Name;
                if (name is null) break;
                if (!await Dialogs.ConfirmAsync(_window, $"Delete everything {Dialogs.Clean(name, 60)} has saved?", "This cannot be undone.", "Delete")) break;
                foreach (var inst in _app.Settings.Instances(wid)) WidgetStore.Delete(inst);
                Post(new JsonObject { ["type"] = "privacy", ["widgets"] = Backup.Privacy(_app) });
                break;
            }

            case "exportBackup":
            {
                var path = await Dialogs.SaveFileAsync(_window, "Save an Umbra backup", $"umbra-backup-{DateTime.Now:yyyy-MM-dd}.json", "Umbra backup", ["*.json"]);
                if (path is null) break;
                try
                {
                    File.WriteAllText(path, Backup.Create(_app.Settings).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    Post(new JsonObject { ["type"] = "notice", ["text"] = "Backup saved" });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { Post(new JsonObject { ["type"] = "commandError", ["error"] = $"Backup failed: {ex.Message}" }); }
                break;
            }

            case "importBackup":
            {
                var path = await Dialogs.OpenFileAsync(_window, "Restore an Umbra backup", "Umbra backup", ["*.json"]);
                if (path is null) break;
                try
                {
                    var parsed = Backup.ReadFile(path);
                    if (!await Dialogs.ConfirmAsync(_window, "Replace your current settings, layout and widget data with this backup?", "Command approvals are not restored.", "Replace")) break;
                    _app.RestoreBackup(parsed);
                    Post(new JsonObject { ["type"] = "notice", ["text"] = "Backup restored" });
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                { Post(new JsonObject { ["type"] = "commandError", ["error"] = $"Restore failed: {ex.Message}" }); }
                SendState();
                break;
            }

            case "resetAll":
                if (!await Dialogs.ConfirmAsync(_window, "Reset all settings, widget positions and saved widget data to a fresh start?", "Installed widgets stay.", "Reset")) break;
                _app.RestoreBackup(new Backup.Parsed(new Settings(), new Dictionary<string, JsonObject>()));
                Post(new JsonObject { ["type"] = "notice", ["text"] = "Reset to defaults" });
                SendState();
                break;

            case "openDataFolder":
                OpenFolder(AppPaths.DataDir);
                break;

            case "openLog":
                OpenFolder(Path.Combine(AppPaths.DataDir, "umbra.log"));
                break;

            case "openFolder":
                Directory.CreateDirectory(AppPaths.CommunityDir);
                OpenFolder(AppPaths.CommunityDir);
                break;

            case "catalog":
                await SendCatalog();
                break;

            case "themes":
                await SendThemes();
                break;

            case "setTheme":
                try { _app.SetTheme(id); } catch (InvalidOperationException ex) { Log.Write(ex.Message); }
                SendState();
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
                    if (pending.NeedsApproval && !await Dialogs.ConfirmNewAccessAsync(_window, pending))
                        throw new InvalidOperationException("Not updated: the new access was declined");
                    _app.CloseWidget(id);
                    pending.Commit();
                });
                break;

            case "installUrl":
            {
                // The address is typed into a host-drawn box, never taken from the page.
                var url = await Dialogs.AskUrlAsync(_window);
                if (url is null) break;
                await InstallPackage(() => Community.PrepareUrlAsync(url), url);
                break;
            }

            case "installLocal":
            {
                // The page only says "file" or "folder": the path comes from a host-drawn picker, never from the page.
                var path = S(msg, "kind") == "folder"
                    ? await Dialogs.PickFolderAsync(_window, "Pick the widget folder (the one with widget.json)")
                    : await Dialogs.OpenFileAsync(_window, "Install a widget from file", "Umbra widget", ["*.zip", "*.umbrawidget"]);
                if (path is null) break;
                await InstallPackage(() => Task.Run(() => Community.PrepareLocal(path)), path);
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
                catch (Exception ex) when (ex is not OutOfMemoryException) { error = ex.Message; Log.Write($"Update all failed: {ex}"); }
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
                    _app.CloseWidget(id);
                    undo = WidgetTrash.Move(_app.Settings, id);
                    try
                    {
                        Community.Remove(id);
                        _app.ForgetInstances(id);
                    }
                    catch (Exception ex) when (undo is not null)
                    {
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
                try { name = _app.RestoreWidget(id)?.Name; }
                catch (InvalidOperationException ex) { error = ex.Message; Log.Write($"Undo remove failed: {ex.Message}"); }
                Post(new JsonObject { ["type"] = "undoDone", ["name"] = name, ["error"] = error });
                SendState();
                await SendCatalog(refetch: false);
                break;
            }
        }
    }

    /// <summary>Install from a file, folder or URL: the shared checks, then a host-drawn review; nothing is written before the user says yes.</summary>
    async Task InstallPackage(Func<Task<Community.PendingInstall>> prepare, string source)
    {
        try
        {
            using var pending = await prepare();
            var wid = pending.Manifest.Id;
            if (_app.Manifests.Any(m => m.Id == wid && m.Source == WidgetSource.BuiltIn))
                throw new InvalidOperationException($"'{wid}' is the id of a built-in widget");
            var accepted = pending.NeedsApproval
                ? await Dialogs.ConfirmNewAccessAsync(_window, pending, source)
                : await Dialogs.ConfirmAsync(_window, Dialogs.PlainReview(pending, source), yes: pending.IsUpdate ? "Replace" : "Install");
            if (!accepted) return;
            _app.CloseWidget(wid);
            pending.Commit();
            _app.ReloadWidgets();
            Post(new JsonObject { ["type"] = "notice", ["text"] = $"Installed {pending.Manifest.Name}" });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Write($"Install from {source} failed: {ex.Message}");
            Post(new JsonObject { ["type"] = "commandError", ["error"] = $"Not installed: {ex.Message}" });
        }
        SendState();
        await SendCatalog(refetch: false);
    }

    static void OpenFolder(string path) => App.OpenWithDesktop(path);

    async Task RunJob(string id, Func<Task> job, Func<string?>? undo = null)
    {
        string? error = null;
        try { await job(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { error = ex.Message; Log.Write($"Widget job {id} failed: {ex.Message}"); }
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
        catch (Exception ex) when (ex is not OutOfMemoryException) { error = ex.Message; Log.Write($"Theme job {id} failed: {ex.Message}"); }
        Post(new JsonObject { ["type"] = "jobDone", ["id"] = id, ["error"] = error });
        SendState();
        await SendThemes(refetch: false);
    }

    async Task SendThemes(bool refetch = true)
    {
        string? error = null;
        if (refetch || !_themeCatalogLoaded)
        {
            try { _themeCatalog = await Community.FetchThemeCatalogAsync(); _themeCatalogLoaded = true; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { error = ex.Message; Log.Write($"Theme catalog fetch failed: {ex.Message}"); }
        }
        Post(new JsonObject { ["type"] = "themes", ["error"] = error, ["themes"] = Theme.Listing(_themeCatalog) });
        _themesSent.TrySetResult();
    }

    JsonArray ScriptsJson(WidgetManifest m)
    {
        var specs = _app.Commands.All(m).Where(c => c.IsUserScript).ToDictionary(c => c.Id);
        return new JsonArray(_app.Commands.UserScriptsOf(m).Select(s =>
        {
            var spec = specs.GetValueOrDefault(s.Id);
            var state = _app.Commands.State(m.Id, s.Id);
            return (JsonNode?)new JsonObject
            {
                ["id"] = s.Id, ["title"] = s.Title, ["description"] = s.Description, ["kind"] = s.Kind, ["mode"] = s.Mode,
                ["path"] = s.Path, ["code"] = s.Code, ["args"] = s.Args, ["timeoutSeconds"] = s.TimeoutSeconds,
                ["keepsRunning"] = s.KeepsRunning, ["startIn"] = s.StartIn, ["autoStart"] = s.AutoStart, ["restart"] = s.Restart,
                ["runAsAdmin"] = s.RunAsAdmin, ["running"] = state.Running,
                ["since"] = state.Since is { } since ? new DateTimeOffset(since).ToUnixTimeMilliseconds() : null,
                ["restarting"] = state.Restarting, ["lastExit"] = state.LastExit,
                ["line"] = spec?.DisplayLine() ?? "", ["hash"] = spec?.Hash() ?? "",
                ["status"] = spec is null ? "invalid" : _app.Commands.Approvals.Status(m.Id, spec),
            };
        }).ToArray());
    }

    void SendState()
    {
        var widgets = new JsonArray();
        foreach (var m in _app.Manifests)
        foreach (var inst in _app.Settings.Instances(m.Id))
        {
            _app.Settings.For(m, inst);
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
                    : m.Source == WidgetSource.BuiltIn ? VirtualHosts.Url($"widgets/{m.Folder}/{m.Preview}")
                    : VirtualHosts.CommunityUrl($"{m.Folder}/{m.Preview}"),
                ["hosts"] = new JsonArray(m.Permissions.Hosts.Select(h => (JsonNode?)h).ToArray()),
                ["notifications"] = m.Permissions.Notifications,
                ["camera"] = m.Permissions.Camera,
                ["cameraApproved"] = false,
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
                ["userScripts"] = _app.Commands.TakesUserScripts(m),
                ["scripts"] = ScriptsJson(m),
                ["options"] = m.Options.DeepClone(),
                ["optionOrder"] = new JsonArray(m.OptionOrder.Select(k => (JsonNode?)k).ToArray()),
            });
        }

        var monitors = _app.Monitors();
        Post(new JsonObject
        {
            ["type"] = "state",
            ["platform"] = "linux",
            // What this host can do; settings.js hides or rewords what it can't (Windows sends none of this).
            ["capabilities"] = new JsonObject
            {
                ["tray"] = _app.TrayShown,
                ["hotkeys"] = false,
                ["autostart"] = !AppPaths.IsPortable,
                ["updates"] = "notify",
                ["accent"] = Prefs.SystemAccent is not null,
                ["energySaver"] = true,
                ["runAsAdmin"] = false,
                ["camera"] = false,
                ["fullscreen"] = false,
            },
            ["version"] = AppInfo.Version,
            ["startup"] = _app.StartupEnabled,
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
            ["monitors"] = new JsonArray(monitors.Select((m, i) => (JsonNode?)new JsonObject { ["name"] = m.Name, ["label"] = $"Monitor {i + 1}{(m.Primary ? " (primary)" : "")} - {m.Bounds.Width}x{m.Bounds.Height}" }).ToArray()),
            ["widgets"] = widgets,
        });
    }

    async Task SendCatalog(bool refetch = true)
    {
        string? error = null;
        if (refetch || _catalog.Count == 0)
        {
            try { _catalog = await Community.FetchCatalogAsync(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { error = ex.Message; Log.Write($"Catalog fetch failed: {ex.Message}"); }
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
            copy["incompatible"] = Community.CatalogIncompatibility(copy);
            copy["local"] = installed is not null && Community.IsLocalInstall(wid);
            copy["previewUrl"] = previews.FirstOrDefault(x => x.wid == wid).Item2;
            list.Add(copy);
        }
        Post(new JsonObject { ["type"] = "catalog", ["error"] = error, ["widgets"] = list });
        _catalogSent.TrySetResult();
    }

    /// <summary>Re-sends the state after something changed outside this window (umbractl, edit pill, a script).</summary>
    public void RefreshState()
    {
        if (!_loaded.Task.IsCompleted || _closed) return;
        SendState();
        if (_themeCatalogLoaded) _ = SendThemes(refetch: false);
        if (_catalog.Count > 0) _ = SendCatalog(refetch: false);
    }

    public async Task ShowPageAsync(string page)
    {
        if (page == "welcome" || page.StartsWith("welcome:", StringComparison.Ordinal))
        {
            var slide = page.Length > 8 && int.TryParse(page.AsSpan(8), out var n) ? n : 1;
            await _loaded.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await Eval($"welcome({slide})");
            return;
        }
        if (!Pages.Contains(page)) throw new Control.CommandException($"Unknown Settings page '{page}'. Pages: {string.Join(", ", Pages)}");
        await _loaded.Task.WaitAsync(TimeSpan.FromSeconds(20));
        if (page == "browse") _catalogSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (page == "themes") _themesSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await Eval($"show('{page}')");
    }

    public async Task<byte[]> CaptureAsync(string page)
    {
        await ShowPageAsync(page);
        if (page == "browse") await Task.WhenAny(_catalogSent.Task, Task.Delay(TimeSpan.FromSeconds(25)));
        if (page == "themes") await Task.WhenAny(_themesSent.Task, Task.Delay(TimeSpan.FromSeconds(25)));
        await Task.Delay(page == "browse" ? 1200 : 500); // images decode, transitions settle
        return await Snapshot.PngAsync(_view);
    }

    void PostZoom() => Post(new JsonObject
    {
        ["type"] = "zoom", ["percent"] = (int)Math.Round(_zoom * 100),
        ["min"] = _zoom <= SettingsPlacement.MinZoom, ["max"] = _zoom >= SettingsPlacement.MaxZoom,
    });

    Task Eval(string script) => _closed ? Task.CompletedTask : _view.EvaluateJavascriptAsync(script);

    /// <summary>Host to page through the bridge (<c>window.__umbraReceive</c>).</summary>
    void Post(JsonNode node)
    {
        if (_closed) return;
        var script = $"window.__umbraReceive&&window.__umbraReceive({node.ToJsonString(Json)})";
        _ = _view.EvaluateJavascriptAsync(script).ContinueWith(
            t => Debug.WriteLine(t.Exception?.GetBaseException().Message), TaskContinuationOptions.OnlyOnFaulted);
    }
}
