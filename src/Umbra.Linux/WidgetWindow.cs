using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Umbra.Linux;

namespace Umbra;

/// <summary>
/// One undecorated, transparent GTK window that hosts a widget's page in a WebKitGTK view (the Linux <c>WidgetWindow</c>).
/// Desktop pinning, positioning, edit mode and focus gating arrive with their own backends (#182, #183, #186-#188);
/// until then this is a plain toplevel the compositor places.
/// </summary>
sealed partial class WidgetWindow : IWidgetView
{
    /// <summary>GTK sizes are logical pixels; the toolkit applies the monitor scale itself.</summary>
    const int LogicalDpi = 96;

    static readonly LinuxMetrics Metrics = new(new FileProcSource(), () => Environment.TickCount64);

    readonly App _app;
    readonly Gtk.Window _window;
    readonly WebKit.WebView _view;
    bool _ready, _closed;
    DateTime _renderedAt;
    readonly TaskCompletionSource _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource _renderedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly HashSet<string> _unsupportedLogged = new();

    public WidgetManifest Manifest { get; }

    /// <summary>Which copy of the widget this is: the widget id, or "id#2" and up for extra copies.</summary>
    public string Instance { get; }

    /// <summary>Raised once when the window is gone (closed by the host or the compositor).</summary>
    public event Action? Closed;

    readonly WidgetSettings _detached = new();

    /// <summary>This copy's settings. Never creates an entry: a removed copy gets throwaway defaults instead of coming back in settings.json.</summary>
    WidgetSettings Ws => _app.Settings.Widgets.TryGetValue(Instance, out var ws) ? ws : _detached;

    /// <summary>False once the window is closed or its copy was removed: nothing may be written for it.</summary>
    bool Alive => !_closed && _app.Settings.Widgets.ContainsKey(Instance);

    public WidgetWindow(App app, WidgetManifest manifest, string instance)
    {
        _app = app;
        Manifest = manifest;
        Instance = instance;

        _view = WebHost.NewView(out var content, devTools: Prefs.DeveloperMode, sandbox: manifest, label: instance);
        _view.SetBackgroundColor(new Gdk.RGBA { Red = 0, Green = 0, Blue = 0, Alpha = 0 });
        // The default WebKit menu (Back, Reload, Inspect) never shows; the host-drawn menu comes with edit mode (#183).
        _view.OnContextMenu += (_, _) => true;
        _view.OnWebProcessTerminated += (_, e) => OnProcessFailed(e.Reason);
        content.RegisterScriptMessageHandler("umbra", null);
        content.OnScriptMessageReceived += (_, e) => OnMessage(e.Value.ToJson(0));

        _window = Gtk.Window.New();
        _window.SetTitle($"Umbra - {InstanceId.Label(manifest, instance)}");
        _window.SetDecorated(false);
        _window.SetResizable(false);
        _window.AddCssClass("umbra-widget");
        _window.SetChild(_view);
        _window.OnCloseRequest += (_, _) => { MarkClosed(); return false; };
        InitInput();
        ApplySize();
        // Realize now, so the pin backend can mark the window (type, state, position) before it is first shown.
        ((Gtk.Widget)_window).Realize(); // the widget's realize (creates the surface), not Gtk.Native's
        _app.Pin.Prepare(_window);
        _window.OnMap += (_, _) => { _app.Pin.Mapped(_window); Place(); };
        Place();

        _view.LoadUri(UmbraScheme.FromHttps(manifest.PageUrl));
    }

    Size SavedSize()
    {
        var ws = Ws;
        var k = Prefs.ClampSize(_app.Settings.General.Size);
        return WidgetGeometry.WindowSize(Manifest.Width, Manifest.Height, LogicalDpi, WidgetSettings.ClampScale(ws.Scale * k),
            ws.Width is int w ? (int)Math.Round(w * k) : null, ws.Height is int h ? (int)Math.Round(h * k) : null);
    }

    /// <summary>Sizes the window from this copy's settings and zooms the page to fill it.</summary>
    void ApplySize()
    {
        SetWindowSize(SavedSize());
        _window.SetOpacity(Prefs.EffectiveOpacity(Ws, _app.Settings.General));
    }

    /// <summary>Gives the window this size (the window follows its only child's size request) and returns the page zoom that fills it.</summary>
    double SetWindowSize(Size size)
    {
        Width = size.Width;
        Height = size.Height;
        _window.SetDefaultSize(size.Width, size.Height);
        _view.SetSizeRequest(size.Width, size.Height);
        var zoom = WidgetGeometry.ZoomFor(Manifest.Width, Manifest.Height, LogicalDpi, size.Width, size.Height);
        _view.ZoomLevel = zoom;
        return zoom;
    }

    // GTK 4 cannot place or query a toplevel: the pin backend moves it, and this is where it was last asked to be.
    public int Left { get; private set; }
    public int Top { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    public Rectangle Bounds => new(Left, Top, Width, Height);

    /// <summary>Moves the window to where its settings say (see <see cref="WidgetPlacement.Locate"/>).</summary>
    void Place()
    {
        if (_closed) return;
        var at = WidgetPlacement.Locate(Ws, Manifest.DefaultPosition, new Size(Width, Height), _app.Monitors());
        Left = at.X; Top = at.Y;
        _app.Pin.Move(_window, at.X, at.Y);
    }

    /// <summary>Re-places the window from its saved monitor and offset (monitors were added, removed or rearranged).</summary>
    public void ReapplyPlacement() => Place();

    public bool Visible
    {
        get => _window.GetVisible();
        set { if (!_closed && _window.GetVisible() != value) _window.SetVisible(value); }
    }

    /// <summary>Shows the window without activating it: a widget never takes focus by appearing.</summary>
    public void Show() => _window.SetVisible(true);

    public void Close()
    {
        if (_closed) return;
        MarkClosed();
        _window.Destroy();
    }

    void MarkClosed()
    {
        if (_closed) return;
        _closed = true;
        CrashRecovery.SetStopped(Instance, false);
        Closed?.Invoke();
    }

    public void ResetPosition()
    {
        var ws = Ws;
        ws.X = ws.Y = null;
        ws.Monitor = null; ws.RelX = ws.RelY = null;
        _app.SaveSettings();
        Place();
    }

    /// <summary>Puts the widget on another monitor, keeping its offset from the monitor's corner.</summary>
    public void MoveToMonitor(string monitor)
    {
        var ws = Ws;
        WidgetPlacement.MoveToMonitor(ws, monitor, Bounds, _app.Monitors());
        Place();
        ws.X = Left; ws.Y = Top;
        _app.SaveSettings();
    }

    // ---------- crash recovery (policy in CrashRecovery, shared with Windows) ----------

    readonly CrashRecovery _recovery = new();
    bool _recovering, _stopped;

    /// <summary>What a web process ending means: a crash or a memory kill reloads the page; one we ended ourselves is only logged.</summary>
    internal static CrashReaction Classify(WebKit.WebProcessTerminationReason reason) => reason switch
    {
        WebKit.WebProcessTerminationReason.Crashed or WebKit.WebProcessTerminationReason.ExceededMemoryLimit => CrashReaction.Reload,
        _ => CrashReaction.Ignore,
    };

    /// <summary>
    /// The widget's web process died. Reload the page, at most 3 times per 10 minutes; after that show the host-owned
    /// "This widget stopped working" page, which runs no widget code and has a Reload button.
    /// </summary>
    async void OnProcessFailed(WebKit.WebProcessTerminationReason reason)
    {
        try
        {
            Log.Write($"[{Instance}] {CrashRecovery.LogMarker}: {reason}");
            if (Classify(reason) == CrashReaction.Ignore || _stopped || _recovering || !Alive) return;
            _recovering = true;
            try
            {
                var giveUp = !_recovery.TryAcquire(Environment.TickCount64);
                _ready = false; // the reloaded page says "ready" again
                if (giveUp)
                {
                    _stopped = true;
                    CrashRecovery.SetStopped(Instance, true);
                    Log.Write($"[{Instance}] widget keeps failing ({CrashRecovery.MaxRecoveries} recoveries in {CrashRecovery.Window.TotalMinutes:0} min), not reloading again");
                }
                else Log.Write($"[{Instance}] reloading widget ({_recovery.Used}/{CrashRecovery.MaxRecoveries})");

                if (!giveUp) await Task.Delay(500); // let the dead process finish going away
                if (!Alive) return;
                // A new web process starts for this load; it asks for the sandbox policy again like the first one did.
                _view.LoadUri(giveUp ? UmbraScheme.FromHttps(VirtualHosts.Url("shared/crashed.html")) : UmbraScheme.FromHttps(Manifest.PageUrl));
            }
            finally { _recovering = false; }
        }
        catch (Exception ex)
        {
            Log.Write($"[{Instance}] widget recovery failed: {ex.Message}");
        }
    }

    /// <summary>The Reload button on the "stopped working" page. Only honoured while that page is showing, so a widget cannot reload itself in a loop.</summary>
    void ReloadAfterStop()
    {
        if (!_stopped || _recovering || !Alive) return;
        _stopped = false;
        CrashRecovery.SetStopped(Instance, false);
        _recovery.Reset();
        _ready = false;
        _view.LoadUri(UmbraScheme.FromHttps(Manifest.PageUrl));
    }

    // ---------- JS bridge ----------

    async void OnMessage(string json)
    {
        try
        {
            JsonNode? msg;
            try { msg = JsonNode.Parse(json); } catch { return; }
            if (msg is not JsonObject) return;
            var type = msg["type"]?.GetValue<string>();

            switch (type)
            {
                case "ready":
                    Log.Verbose($"[{Instance}] ready");
                    _ready = true;
                    SendInit();
                    _readyTcs.TrySetResult();
                    break;

                case "rendered":
                    if (!_renderedTcs.Task.IsCompleted) _renderedAt = DateTime.UtcNow;
                    _renderedTcs.TrySetResult();
                    await CaptureIfRequested();
                    break;

                case "reload":
                    ReloadAfterStop();
                    break;

                case "openUrl":
                    if (msg["url"]?.GetValue<string>() is { } url) WebHost.OpenExternal(url);
                    break;

                case "fetchJson":
                case "fetchText":
                    await HandleFetch(msg, type == "fetchText");
                    break;

                case "store":
                    HandleStore(msg);
                    break;

                case "system":
                    HandleSystem(msg);
                    break;

                case "commands":
                    // permissions.commands on Linux is #194: no command is offered until then.
                    Post(new JsonObject { ["type"] = "commandsResult", ["id"] = msg["id"]?.DeepClone(), ["ok"] = true, ["data"] = new JsonArray() });
                    break;

                case null:
                    break;

                default:
                    if (HandleEditMessage(type, msg)) break;
                    // notify (#189), media (#190), run/stop (#194), timelapse, images, tabIcon, ...
                    if (_unsupportedLogged.Add(type)) Log.Write($"[{Instance}] '{type}' is not available on Linux yet");
                    Unsupported(type, msg);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"[{Instance}] message failed: {ex.Message}");
        }
    }

    /// <summary>Requests that expect an answer get a clean failure, so a widget's promise settles instead of hanging.</summary>
    void Unsupported(string type, JsonNode msg)
    {
        var result = type switch
        {
            "notify" => "notifyResult",
            "requestNotifications" => "requestNotificationsResult",
            "media" => "mediaResult",
            "run" or "stop" or "commandOutput" => "runResult",
            "timelapse" => "timelapseResult",
            "images" => "imagesResult",
            _ => null,
        };
        if (result is null || msg["id"] is null) return;
        Post(new JsonObject { ["type"] = result, ["id"] = msg["id"]!.DeepClone(), ["ok"] = false, ["error"] = "Not available on Linux yet" });
    }

    /// <summary>umbra.store: get/set/remove/keys on this widget's own data file, never another widget's.</summary>
    void HandleStore(JsonNode msg)
    {
        var id = msg["id"]?.GetValue<int>() ?? 0;
        var reply = new JsonObject { ["type"] = "storeResult", ["id"] = id };
        if (!Alive) return; // removed copy: don't recreate its data file
        try
        {
            var op = msg["op"]?.GetValue<string>();
            var key = msg["key"]?.GetValue<string>() ?? "";
            switch (op)
            {
                case "get":
                    var (found, value) = WidgetStore.Get(Instance, key);
                    reply["data"] = new JsonObject { ["found"] = found, ["value"] = value };
                    break;
                case "set":
                    WidgetStore.Set(Instance, key, msg["value"]);
                    break;
                case "remove":
                    WidgetStore.Remove(Instance, key);
                    break;
                case "keys":
                    reply["data"] = new JsonArray(WidgetStore.Keys(Instance).Select(k => (JsonNode?)k).ToArray());
                    break;
                default:
                    throw new InvalidOperationException($"Unknown store operation '{op}'");
            }
            reply["ok"] = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            reply["ok"] = false;
            reply["error"] = ex.Message;
            Log.Write($"[{Instance}] store {msg["op"]} failed: {ex.Message}");
        }
        Post(reply);
    }

    /// <summary>Answers umbra.system() with only the metric kinds the manifest declared (none declared: rejected).</summary>
    void HandleSystem(JsonNode msg)
    {
        var reply = new JsonObject { ["type"] = "systemResult", ["id"] = msg["id"]?.DeepClone() };
        if (Manifest.Permissions.System.Count == 0 || !(Alive && Ws.Enabled))
        {
            reply["ok"] = false;
            reply["error"] = "This widget has no permissions.system";
        }
        else
        {
            reply["ok"] = true;
            reply["data"] = Metrics.Read(Manifest.Permissions.System);
        }
        Post(reply);
    }

    async Task HandleFetch(JsonNode msg, bool asText)
    {
        var id = msg["id"]?.GetValue<int>() ?? 0;
        var url = msg["url"]?.GetValue<string>() ?? "";
        var reply = new JsonObject { ["type"] = "fetchResult", ["id"] = id };

        if (!Manifest.AllowsUrl(url))
        {
            reply["ok"] = false;
            reply["error"] = "Host not allowed by widget.json permissions";
            Log.Write($"[{Instance}] blocked fetch: {url}");
        }
        else
        {
            try
            {
                reply["data"] = asText ? JsonValue.Create(await Fetcher.GetTextAsync(url)) : await Fetcher.GetJsonAsync(url);
                reply["ok"] = true;
            }
            catch (Exception ex)
            {
                reply["ok"] = false;
                reply["error"] = ex.Message;
            }
        }
        Post(reply);
    }

    void SendInit() => Post(new JsonObject
    {
        ["type"] = "init",
        ["widget"] = new JsonObject { ["id"] = Manifest.Id, ["instance"] = Instance, ["name"] = Manifest.Name },
        ["settings"] = SettingsPayload(),
    });

    public void PushSettings()
    {
        if (_closed) return;
        ApplySize();
        Place();
        if (_ready) Post(new JsonObject { ["type"] = "settings", ["settings"] = SettingsPayload() });
    }

    public void RefreshData()
    {
        if (_ready) Post(new JsonObject { ["type"] = "refresh" });
    }

    /// <summary>Tells the page that its stored data was changed from outside (umbractl data, MCP).</summary>
    public void StoreChanged(string? key)
    {
        if (_ready) Post(new JsonObject { ["type"] = "storeChanged", ["key"] = key });
    }

    /// <summary>True once the page said it is ready (and, with <paramref name="rendered"/>, drew its first frame); for tests and capture.</summary>
    public Task WhenReady(bool rendered = false) => rendered ? _renderedTcs.Task : _readyTcs.Task;

    /// <summary>
    /// PNG of this widget's web content only (never the screen). Waits for the page to be ready and for its first "rendered"
    /// signal (widgets that never send one get a few seconds), then lets draw-in animations finish. A hidden window is not
    /// drawn by GTK, so it is refused with a clear message instead of returning an empty picture.
    /// </summary>
    public async Task<byte[]> CaptureAsync()
    {
        if (_closed) throw new InvalidOperationException($"'{Instance}' is closed");
        if (!Visible) throw new InvalidOperationException($"'{Instance}' is hidden, so there is nothing to capture; show it first");
        await _readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await Task.WhenAny(_renderedTcs.Task, Task.Delay(3000));
        var settle = _renderedTcs.Task.IsCompleted ? TimeSpan.FromMilliseconds(2500) - (DateTime.UtcNow - _renderedAt) : TimeSpan.Zero;
        if (settle > TimeSpan.Zero) await Task.Delay(settle);
        if (_closed) throw new InvalidOperationException($"'{Instance}' was closed during the capture");
        return await Snapshot.PngAsync(_view);
    }

    /// <summary>Dev hook: with UMBRA_CAPTURE_DIR set, each widget saves a PNG of itself once it rendered (README and preview images).</summary>
    async Task CaptureIfRequested()
    {
        var dir = Environment.GetEnvironmentVariable("UMBRA_CAPTURE_DIR");
        if (string.IsNullOrEmpty(dir) || _closed) return;
        var png = await CaptureAsync();
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, $"{InstanceId.FileName(Instance)}.png"), png);
    }

    JsonObject SettingsPayload()
    {
        var ws = Ws;
        var general = _app.Settings.General;
        return new JsonObject
        {
            // Pausing and Energy Saver come with the power backend (#193).
            ["refreshSeconds"] = Prefs.EffectiveRefresh(ws, general),
            ["paused"] = false,
            ["energySaver"] = false,
            ["animations"] = Prefs.MotionOn(general),
            ["appearanceCss"] = Manifest.Themeable ? Prefs.AppearanceCss(general) : "",
            ["themeCss"] = Manifest.Themeable ? _app.ThemeCss : "",
            ["locked"] = !_app.EditMode,
            ["accent"] = Prefs.ResolveAccent(general, Prefs.SystemAccent),
            ["scale"] = ws.Scale,
            ["locale"] = Prefs.LocaleOf(CultureInfo.CurrentCulture),
            ["hour12"] = Prefs.Hour12Of(CultureInfo.CurrentCulture),
            ["editMode"] = _app.EditMode,
            ["editSelected"] = _app.EditMode && _app.EditSelection == Instance,
            ["options"] = ws.Options.DeepClone(),
        };
    }

    /// <summary>
    /// Host to page: calls <c>window.__umbraReceive</c>, which the bridge in umbra.js defines. It is not a trust boundary:
    /// a page can call it itself, so nothing the host does may depend on it.
    /// </summary>
    void Post(JsonNode node)
    {
        if (_closed) return;
        var script = $"window.__umbraReceive&&window.__umbraReceive({node.ToJsonString()})";
        _ = _view.EvaluateJavascriptAsync(script).ContinueWith(
            t => Debug.WriteLine(t.Exception?.GetBaseException().Message), TaskContinuationOptions.OnlyOnFaulted);
    }
}
