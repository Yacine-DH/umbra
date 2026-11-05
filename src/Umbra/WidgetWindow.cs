using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Umbra;

/// <summary>A borderless, desktop-pinned window that hosts one widget's HTML in WebView2.</summary>
sealed class WidgetWindow : Form, IWidgetView
{
    readonly App _app;
    WebView2 _view = NewView();
    bool _ready;
    readonly TaskCompletionSource _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource _renderedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    DateTime _renderedAt;
    long _pressedAt = long.MinValue / 2;
    long _leftPressAt = long.MinValue / 2, _menuAt = long.MinValue / 2;
    IntPtr _foregroundAtPress;
    bool _resizing;
    long _lastZoomAt = long.MinValue / 2;

    public WidgetManifest Manifest { get; }

    /// <summary>Which copy of the widget this is: the widget id, or "id#2" and up for extra copies.</summary>
    public string Instance { get; }

    readonly WidgetSettings _detached = new();

    /// <summary>
    /// This copy's settings. Never creates an entry: once the copy was removed, a closing window or a late
    /// message gets throwaway defaults instead of resurrecting it in settings.json.
    /// </summary>
    WidgetSettings Ws => _app.Settings.Widgets.TryGetValue(Instance, out var ws) ? ws : _detached;

    /// <summary>False once the window is disposed or its copy was removed: nothing may be written for it.</summary>
    bool Alive => !IsDisposed && _app.Settings.Widgets.ContainsKey(Instance);

    /// <summary>Moving or changing the widget by hand (drag, corner handle, Ctrl+wheel) needs the global lock off (#234).</summary>
    bool CanArrange => WidgetGeometry.CanArrange(_app.Settings.General);

    /// <summary>The window size for a scale: manifest size (logical pixels) x monitor DPI x scale.</summary>
    Size ScaledSize(double scale) => WidgetGeometry.ScaledSize(Manifest.Width, Manifest.Height, DeviceDpi, scale);

    /// <summary>The window size from this copy's settings: a free size if the user dragged an edge, otherwise manifest x scale.</summary>
    Size SavedSize(WidgetSettings ws)
    {
        var k = GlobalSize;
        return WidgetGeometry.WindowSize(Manifest.Width, Manifest.Height, DeviceDpi, WidgetSettings.ClampScale(ws.Scale * k),
            ws.Width is int w ? (int)Math.Round(w * k) : null, ws.Height is int h ? (int)Math.Round(h * k) : null);
    }

    /// <summary>The global Size multiplier (General), applied on top of the widget's own size.</summary>
    double GlobalSize => Prefs.ClampSize(_app.Settings.General.Size);

    /// <summary>The page zoom for a window size at the current DPI (the smaller stretch; the page lays out at window / zoom).</summary>
    double ZoomFor(Size size) => WidgetGeometry.ZoomFor(Manifest.Width, Manifest.Height, DeviceDpi, size.Width, size.Height);

    public WidgetWindow(App app, WidgetManifest manifest, string instance)
    {
        _app = app;
        Manifest = manifest;
        Instance = instance;

        Text = $"Umbra - {InstanceId.Label(manifest, instance)}";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        AutoScaleMode = AutoScaleMode.None;
        Controls.Add(_view);

        ApplyBounds();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DesktopPin.RoundCorners(Handle);
        DesktopPin.Apply(Handle);
    }

    /// <summary>WinForms re-parents a window that is shown with ShowInTaskbar off, which drops the Progman owner set in OnHandleCreated.</summary>
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) DesktopPin.Apply(Handle);
        // Hidden (group tab, hide hotkey, fullscreen app...): a recording pauses with the camera off until the widget is back.
        _app.Timelapse.SetPaused(Instance, !Visible);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == DesktopPin.TaskbarCreated) DesktopPin.Apply(Handle); // Explorer restarted: Progman is a new window
        switch (m.Msg)
        {
            case Native.WM_WINDOWPOSCHANGING:
                DesktopPin.KeepAtBottom(m.LParam);
                break;
            case Native.WM_MOUSEACTIVATE:
                MarkPressed(); // only real clicks on this window send it
                if (ClickGate.IsLeftPress(m.LParam) && !_app.EditMode) _leftPressAt = System.Environment.TickCount64;
                m.Result = Native.MA_NOACTIVATE;
                return;
            case Native.WM_PARENTNOTIFY:
                // A press on the page lands on the WebView's child window, which keeps WM_MOUSEACTIVATE to itself. Windows still
                // tells this ancestor that a press began there and with which button, and a page can't cause this message.
                if (ClickGate.IsLeftPressNotify(m.WParam) && !_app.EditMode) _leftPressAt = System.Environment.TickCount64;
                break;
            case Native.WM_GETMINMAXINFO:
                base.WndProc(ref m);
                LimitTrackSize(m.LParam);
                return;
            case Native.WM_MOVING when CanArrange && !_resizing:
                SnapMove(m.LParam);
                m.Result = 1;
                return;
            case Native.WM_EXITSIZEMOVE:
                _app.ShowGuides(Array.Empty<Snap.Guide>(), DeviceDpi); // the move or resize ended: guides go away
                break;
        }
        base.WndProc(ref m);
    }

    /// <summary>WM_MOVING in edit mode: snap to the grid, edges, centers and an even gap to neighbours while Shift is held (or unless it is, in "always" mode), and draw guides for what it snapped to.</summary>
    unsafe void SnapMove(IntPtr lParam)
    {
        var g = _app.Settings.General;
        if (!Snap.SnapsWhileDragging(g, (ModifierKeys & Keys.Shift) != 0))
        {
            _app.ShowGuides(Array.Empty<Snap.Guide>(), DeviceDpi);
            return;
        }
        var r = (Native.RECT*)lParam;
        var rect = Rectangle.FromLTRB(r->Left, r->Top, r->Right, r->Bottom);
        var areas = Screens.All().Select(s => s.WorkArea).ToList();
        var others = _app.OpenWindows.Where(w => w != this && w.Visible).Select(w => w.Bounds).ToList();
        var gap = (int)Math.Round(Math.Clamp(g.SnapGap, 0, Snap.MaxGap) * DeviceDpi / 96.0);
        var snapped = Snap.Compute(rect, g.SnapGrid, g.SnapEdges, gap, areas, others, (int)(10 * DeviceDpi / 96.0));
        var s2 = snapped.Rect;
        r->Left = s2.Left; r->Top = s2.Top; r->Right = s2.Right; r->Bottom = s2.Bottom;
        _app.ShowGuides(snapped.Guides, DeviceDpi);
    }

    /// <summary>Esc while this widget has keyboard focus (it only gets it after a real press) ends edit mode.</summary>
    void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Escape || !_app.EditMode) return;
        e.Handled = true;
        _app.SetEditMode(false);
    }

    /// <summary>The resize loop may only produce sizes between 50 % and 300 % of the manifest size.</summary>
    unsafe void LimitTrackSize(IntPtr lParam)
    {
        var mmi = (Native.MINMAXINFO*)lParam;
        Size min = ScaledSize(WidgetSettings.MinScale), max = ScaledSize(WidgetSettings.MaxScale);
        mmi->MinTrackSize = new Native.POINT { X = min.Width, Y = min.Height };
        mmi->MaxTrackSize = new Native.POINT { X = max.Width + 1, Y = max.Height + 1 };
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        // While an edge or the corner handle is dragged, the page lays out at window / zoom and zooms with the smaller stretch.
        if (_resizing)
        {
            var scale = ZoomFor(Size);
            ApplyZoom(scale);
            if (_ready) Post(new JsonObject { ["type"] = "editScale", ["scale"] = scale }); // keeps the overlay outline steady
        }
    }

    void ApplyZoom(double zoom)
    {
        if (Math.Abs(_view.ZoomFactor - zoom) > 0.0001) _view.ZoomFactor = zoom;
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            await StartViewAsync(Manifest.PageUrl);
        }
        catch (Exception ex)
        {
            Log.Write($"[{Instance}] WebView2 init failed: {ex}");
        }
    }

    static WebView2 NewView() => new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };

    /// <summary>Initialises the current WebView2 (sandbox, bridge, crash handler) and opens <paramref name="url"/>.</summary>
    async Task StartViewAsync(string url)
    {
        _envGeneration = WebHost.EnvironmentGeneration;
        await WebHost.InitAsync(_view, sandbox: Manifest);
        _view.CoreWebView2.WebMessageReceived += OnMessage;
        _view.CoreWebView2.ProcessFailed += OnProcessFailed;
        _view.CoreWebView2.ContextMenuRequested += OnContextMenuRequested;
        _view.KeyDown += OnViewKeyDown; // WebView2 forwards accelerator keys (Esc included) here
        _view.CoreWebView2.Navigate(url);
    }

    // ---------- crash recovery (policy in CrashRecovery) ----------

    readonly CrashRecovery _recovery = new();
    int _envGeneration;
    bool _recovering, _stopped, _settling;
    CrashReaction _pending = CrashReaction.Ignore; // a crash that arrived while a recovery was running
    Panel? _failedPanel;

    /// <summary>
    /// A WebView2 process died. Renderer gone: reload (at most 3 times per 10 minutes, then the "stopped working"
    /// page). Browser process gone: build a new environment and view first. Other kinds are only logged.
    /// </summary>
    async void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        var reaction = WebView2Crash.Classify(e.ProcessFailedKind);
        Log.Write($"[{Instance}] {CrashRecovery.LogMarker}: {e.ProcessFailedKind} ({e.Reason}, exit code {e.ExitCode})");
        if (reaction == CrashReaction.Ignore || _stopped || !Alive) return;
        if (_recovering)
        {
            // Same incident (several processes report one crash within the settle delay) unless it is a stronger reaction; later is a new crash.
            if (!_settling || reaction > _current) _pending = (CrashReaction)Math.Max((int)_pending, (int)reaction);
            return;
        }
        _recovering = true;
        try
        {
            while (reaction != CrashReaction.Ignore && !_stopped && Alive)
            {
                _current = reaction;
                await RecoverAsync(reaction);
                reaction = _pending;
                _pending = CrashReaction.Ignore;
            }
        }
        finally { _recovering = false; _pending = CrashReaction.Ignore; }
    }

    CrashReaction _current;

    async Task RecoverAsync(CrashReaction reaction)
    {
        try
        {
            var giveUp = !_recovery.TryAcquire(System.Environment.TickCount64);
            _ready = false; // the reloaded page says "ready" again
            if (giveUp)
            {
                _stopped = true;
                CrashRecovery.SetStopped(Instance, true);
                Log.Write($"[{Instance}] widget keeps failing ({CrashRecovery.MaxRecoveries} recoveries in {CrashRecovery.Window.TotalMinutes:0} min), not reloading again");
            }
            else Log.Write($"[{Instance}] reloading widget ({_recovery.Used}/{CrashRecovery.MaxRecoveries})");

            _settling = true;
            try { await Task.Delay(giveUp ? 0 : 500); } // let the dead process finish going away
            finally { _settling = false; }
            if (!Alive) return;
            var url = giveUp ? WebHost.Url("shared/crashed.html") : Manifest.PageUrl;
            if (reaction == CrashReaction.RecreateBrowser) await RecreateViewAsync(url);
            else _view.CoreWebView2.Navigate(url);
        }
        catch (Exception ex)
        {
            Log.Write($"[{Instance}] widget recovery failed: {ex.Message}");
            if (Alive) ShowFailedState();
        }
    }

    async Task RetryRecreateAsync()
    {
        _recovering = true;
        try { _current = CrashReaction.RecreateBrowser; await RecoverAsync(CrashReaction.RecreateBrowser); }
        finally { _recovering = false; }
    }

    /// <summary>Recreating the WebView2 failed, so there is no page to show: stop retrying and show a plain host-drawn message with a Reload button.</summary>
    void ShowFailedState()
    {
        _stopped = true;
        CrashRecovery.SetStopped(Instance, true);
        _ready = false;
        _failedPanel?.Dispose();
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
        var label = new Label
        {
            Text = "This widget stopped working.",
            ForeColor = Color.White, BackColor = Color.Black, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
        };
        var button = new Button { Text = "Reload", Dock = DockStyle.Bottom, Height = 32, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(40, 40, 40), TabStop = false };
        button.Click += (_, _) => ReloadAfterStop();
        panel.Controls.Add(label);
        panel.Controls.Add(button);
        Controls.Add(panel);
        panel.BringToFront();
        _failedPanel = panel;
    }

    /// <summary>The browser process died, so the WebView2 control is unusable: replace the environment (once, however many windows saw it) and the control.</summary>
    async Task RecreateViewAsync(string url)
    {
        WebHost.RecreateEnvironment(_envGeneration);
        var old = _view;
        Controls.Remove(old);
        try { old.Dispose(); } catch (Exception ex) { Log.Write($"[{Instance}] disposing the crashed WebView2 failed: {ex.Message}"); }
        _view = NewView();
        Controls.Add(_view);
        _recordingDot?.BringToFront();
        ApplyZoom(ZoomFor(Size));
        await StartViewAsync(url);
    }

    /// <summary>The Reload button on the "stopped working" page (only honoured while that page is showing).</summary>
    void ReloadAfterStop()
    {
        if (!_stopped || _recovering || !Alive) return;
        _stopped = false;
        CrashRecovery.SetStopped(Instance, false);
        _recovery.Reset();
        if (_failedPanel is not null)
        {
            // The WebView2 itself could not be recreated: try again from scratch.
            _failedPanel.Dispose();
            _failedPanel = null;
            _ = RetryRecreateAsync();
            return;
        }
        if (_view.CoreWebView2 is not null) _view.CoreWebView2.Navigate(Manifest.PageUrl);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        // Keep the spot Windows picked (the window may be mid-drag onto another monitor); only the size follows the new DPI.
        ApplyBounds(e.SuggestedRectangle.Location);
        if (Alive) { RememberPosition(); _app.SaveSettings(); } // else the next PushSettings snaps back to the old spot
    }

    /// <summary>
    /// Manifest size is in logical (96-DPI) pixels: scale it to the monitor's DPI and the user's scale (or use the free
    /// size they dragged), and zoom the page by the smaller stretch, so the content is laid out at window / zoom and
    /// rendered sharply. A widget that keeps its manifest aspect ratio is laid out at exactly the manifest size.
    /// </summary>
    void ApplyBounds(Point? at = null)
    {
        var ws = Ws;
        float dpi = DeviceDpi / 96f;
        var size = SavedSize(ws);

        Point loc;
        if (at is Point p) loc = p;
        else if (ws.RelX is int rx && ws.RelY is int ry && ws.Monitor is not null)
            loc = Placement.Resolve(Screens.All(), ws.Monitor, rx, ry, size);
        else if (ws.X is int x && ws.Y is int y) loc = new Point(x, y);
        else
        {
            var wa = Screen.PrimaryScreen!.WorkingArea;
            var d = Manifest.DefaultPosition;
            loc = d is null
                ? new Point(wa.Right - size.Width - (int)(24 * dpi), wa.Top + (int)(24 * dpi))
                : new Point(wa.Left + (int)(d.X * dpi), wa.Top + (int)(d.Y * dpi));
        }

        // Keep at least part of the widget on a visible screen (monitors may have changed).
        var rect = new Rectangle(loc, size);
        if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(rect)))
        {
            var wa = Screen.PrimaryScreen!.WorkingArea;
            rect.Location = new Point(wa.Right - size.Width - 24, wa.Top + 24);
        }
        Bounds = rect;
        ApplyZoom(ZoomFor(size));
    }

    public void ResetPosition()
    {
        var ws = Ws;
        ws.X = ws.Y = null;
        ws.Monitor = null; ws.RelX = ws.RelY = null;
        _app.SaveSettings();
        ApplyBounds();
    }

    /// <summary>Re-places the window from its saved monitor and offset (monitors were added, removed or rearranged).</summary>
    public void ReapplyPlacement() => ApplyBounds();

    /// <summary>Puts the widget on another monitor, keeping its offset from the monitor's corner (24 px if it had none).</summary>
    public void MoveToMonitor(string monitor)
    {
        var ws = Ws;
        if (ws.RelX is null || ws.RelY is null)
        {
            var cur = Placement.Find(Screens.All(), Bounds);
            ws.RelX = cur is MonitorInfo c ? Left - c.Bounds.X : 24;
            ws.RelY = cur is MonitorInfo d ? Top - d.Bounds.Y : 24;
        }
        ws.Monitor = monitor;
        ApplyBounds();
        ws.X = Left; ws.Y = Top;
        _app.SaveSettings();
    }

    /// <summary>Records where the window is now: absolute spot plus monitor and offset from its corner, so it survives monitor changes.</summary>
    void RememberPosition()
    {
        var ws = Ws;
        ws.X = Left; ws.Y = Top;
        var mon = Placement.Find(Screens.All(), Bounds);
        if (mon is MonitorInfo m)
        {
            ws.Monitor = m.Name;
            ws.RelX = Left - m.Bounds.X;
            ws.RelY = Top - m.Bounds.Y;
        }
    }

    /// <summary>Zooms the widget by hand (Ctrl+wheel): the top-left corner stays where it is. A free size grows or shrinks with it.</summary>
    void SetScaleByHand(double scale)
    {
        var ws = Ws;
        if (ws.Width is int w && ws.Height is int h)
        {
            var factor = scale / Math.Max(0.01, ws.Scale);
            var size = WidgetGeometry.ClampSize(Manifest.Width, Manifest.Height, 96, (int)Math.Round(w * factor), (int)Math.Round(h * factor));
            ws.Width = size.Width; ws.Height = size.Height;
            ws.Scale = WidgetGeometry.ZoomFor(Manifest.Width, Manifest.Height, 96, size.Width, size.Height);
        }
        else ws.Scale = scale;
        FinishHandResize();
    }

    /// <summary>After an edge or corner drag: keep the dragged window size (logical pixels) and the zoom it implies.</summary>
    void SetSizeByHand()
    {
        var ws = Ws;
        var k = GlobalSize; // the window shows own size x global Size; store the own part
        ws.Width = (int)Math.Round(WidgetGeometry.ToLogical(Width, DeviceDpi) / k);
        ws.Height = (int)Math.Round(WidgetGeometry.ToLogical(Height, DeviceDpi) / k);
        ws.Scale = WidgetSettings.ClampScale(ZoomFor(Size) / k);
        FinishHandResize();
    }

    void FinishHandResize()
    {
        _app.NoteHandScale(Instance);
        Ws.UseDefaultScale = false; // a size picked by hand is the widget's own
        RememberPosition();
        PushSettings();
        SaveSoon();
    }

    /// <summary>A wheel notch or a held arrow key can fire many times a second: write settings.json and refresh the tray/Settings once it settles.</summary>
    void SaveSoon()
    {
        _saveTimer ??= new System.Windows.Forms.Timer { Interval = 300 };
        _saveTimer.Tick -= FlushScale;
        _saveTimer.Tick += FlushScale;
        _saveTimer.Stop(); _saveTimer.Start();
    }

    System.Windows.Forms.Timer? _saveTimer;

    void FlushScale(object? sender, EventArgs e)
    {
        _saveTimer?.Stop();
        if (!Alive) return;
        _app.SaveSettings();
        _app.SettingsChangedElsewhere();
    }

    // ---------- host context menu ----------

    ContextMenuStrip? _menu;

    /// <summary>
    /// Right-click on the page. The menu is drawn by the host, so a page can neither spoof it nor open it: the event
    /// comes from real mouse input only. A page that calls preventDefault() on "contextmenu" never gets here and keeps its own.
    /// </summary>
    void OnContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        e.Handled = true; // the Edge menu (back, save as, print...) never shows
        if (IsDisposed) return;
        _menuAt = System.Environment.TickCount64; // a press that opened the host menu never starts the camera
        BeginInvoke(() =>
        {
            // Deferred, so an exception here would reach the UI thread's unhandled handler: log it instead.
            try { ShowMenu(); }
            catch (Exception ex) { Log.Write($"[{Instance}] widget menu failed: {ex.Message}"); }
        });
    }

    /// <summary>
    /// Opens the menu at the cursor. A drop-down never activates this window, which keeps WS_EX_NOACTIVATE intact.
    /// Not verified on a real desktop: whether a click outside the menu or Esc always dismisses it for this non-activating owner
    /// (a ToolStripDropDown normally closes itself on both). Deliberately no Activate/SetForegroundWindow workaround here, because
    /// that would break the "widgets never take focus" invariant; if it proves flaky, close the menu from a mouse hook instead.
    /// </summary>
    void ShowMenu()
    {
        if (!Alive || !Ws.Enabled) return;
        _menu?.Dispose();
        _menu = BuildMenu();
        _menu.Show(CursorOverThisWindow() ? Cursor.Position : new Point(Left + Width / 2, Top + Height / 2));
    }

    /// <summary>Tab group entries: switch tabs and leave when grouped, otherwise join a group or start one.</summary>
    void AddGroupItems(ContextMenuStrip menu)
    {
        var s = _app.Settings;
        void Run(Action a)
        {
            try { a(); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
            { Log.Write($"Group change for {Instance} failed: {ex.Message}"); }
        }
        if (Groups.GroupOf(s, Instance) is { } gid && s.Groups.TryGetValue(gid, out var g))
        {
            var tabs = new ToolStripMenuItem($"Tab group: {g.Name.Replace("&", "&&")}");
            if (g.Members.Count > 1)
            {
                tabs.DropDownItems.Add("Next tab", null, (_, _) => Run(() => _app.StepTab(gid, 1)));
                tabs.DropDownItems.Add("Previous tab", null, (_, _) => Run(() => _app.StepTab(gid, -1)));
                tabs.DropDownItems.Add(new ToolStripSeparator());
                foreach (var m in g.Members)
                {
                    var label = _app.Resolve(m) is { } r ? InstanceId.Label(r.Manifest, m) : m;
                    var item = new ToolStripMenuItem(label.Replace("&", "&&"), null, (_, _) => Run(() => _app.ActivateTab(gid, m))) { Checked = m == Instance };
                    tabs.DropDownItems.Add(item);
                }
                tabs.DropDownItems.Add(new ToolStripSeparator());
            }
            tabs.DropDownItems.Add("Remove from group", null, (_, _) => Run(() => _app.LeaveGroup(Instance)));
            menu.Items.Add(tabs);
            return;
        }
        var add = new ToolStripMenuItem("Add to tab group");
        foreach (var (id, grp) in s.Groups)
        {
            var target = id;
            add.DropDownItems.Add(grp.Name.Replace("&", "&&"), null, (_, _) => Run(() => _app.JoinGroup(target, Instance)));
        }
        if (add.DropDownItems.Count > 0) add.DropDownItems.Add(new ToolStripSeparator());
        add.DropDownItems.Add("New group", null, (_, _) => Run(() => _app.CreateGroup(Manifest.Name, Instance)));
        menu.Items.Add(add);
    }

    ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripLabel(InstanceId.Label(Manifest, Instance).Replace("&", "&&")) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Refresh", null, (_, _) => RefreshData());
        menu.Items.Add("Options...", null, (_, _) => _app.ShowWidgetOptions(Instance));
        menu.Items.Add(new ToolStripMenuItem("Arrange widgets", null, (_, _) => _app.SetEditMode(!_app.EditMode)) { Checked = _app.EditMode });
        menu.Items.Add(new ToolStripSeparator());
        AddGroupItems(menu);
        menu.Items.Add("Hide", null, (_, _) => _app.DisableWidget(Instance));
        var copy = menu.Items.Add("Add another copy", null, (_, _) =>
        {
            try { _app.AddInstance(Manifest); }
            catch (InvalidOperationException ex) { Log.Write($"Add copy of {Manifest.Id} failed: {ex.Message}"); }
        });
        copy.Enabled = _app.Settings.Instances(Manifest.Id).Count < InstanceId.MaxInstances;
        // The first copy can only be hidden here: uninstalling a widget stays in Settings, where nothing can be undone by accident.
        if (InstanceId.IsExtra(Instance))
            menu.Items.Add("Remove copy", null, (_, _) => _app.RemoveInstanceWithUndo(Instance));
        if (_view.CoreWebView2.Settings.AreDevToolsEnabled)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Inspect", null, (_, _) => _view.CoreWebView2.OpenDevToolsWindow());
        }
        return menu;
    }

    // ---------- JS bridge ----------

    async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonNode? msg;
        try { msg = JsonNode.Parse(e.WebMessageAsJson); } catch { return; }
        var type = msg?["type"]?.GetValue<string>();

        switch (type)
        {
            case "ready":
                _ready = true;
                SendInit();
                _readyTcs.TrySetResult();
                break;


            case "reload":
                ReloadAfterStop();
                break;

            case "drag":
                if (!CanArrange || _resizing) return;
                Native.ReleaseCapture();
                Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, Native.HTCAPTION, IntPtr.Zero);
                // SendMessage returns when the move loop ends: persist the new spot.
                _app.ShowGuides(Array.Empty<Snap.Guide>(), DeviceDpi);
                RememberPosition();
                _app.SaveSettings();
                DesktopPin.SendToBottom(Handle);
                _app.RefreshEditPills();
                break;

            case "resize":
                // The edit-mode edge and corner handles ("right", "bottom" or the corner). Only a real press on this window
                // starts the native size loop. Messages still arrive during the modal size loop, so a second "resize" must not nest.
                if (_resizing || !CanArrange || !PressedOnThisWindow()) return;
                var hit = msg?["edge"]?.GetValue<string>() switch
                {
                    "right" => Native.HTRIGHT,
                    "bottom" => Native.HTBOTTOM,
                    _ => Native.HTBOTTOMRIGHT,
                };
                SelectForEdit();
                var before = Size;
                _resizing = true;
                try
                {
                    Native.ReleaseCapture();
                    Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, hit, IntPtr.Zero);
                }
                finally { _resizing = false; }
                // Edit mode may have been turned off (or the widget locked) during the drag: put the saved size back.
                if (!CanArrange) ApplyBounds();
                else if (Size != before) SetSizeByHand();
                DesktopPin.SendToBottom(Handle);
                break;

            case "zoom":
                // Ctrl+wheel in edit mode: one 10 % step per message, at most one every 50 ms.
                // The page can't fake Ctrl being physically down over this window.
                if (_resizing || !CanArrange || !CtrlWheelOverThisWindow()) return;
                if (msg?["steps"] is not JsonNode stepsNode || stepsNode.GetValueKind() != JsonValueKind.Number) return;
                var steps = Math.Sign(stepsNode.GetValue<double>());
                var now = System.Environment.TickCount64;
                if (steps == 0 || now - _lastZoomAt < 50) return;
                _lastZoomAt = now;
                SetScaleByHand(Ws.Scale + 0.1 * steps);
                break;

            case "nudge":
                Nudge(msg?["dir"]?.GetValue<string>());
                break;

            case "rendered":
                if (!_renderedTcs.Task.IsCompleted) _renderedAt = DateTime.UtcNow;
                _renderedTcs.TrySetResult();
                await CaptureIfRequested();
                break;

            case "openUrl":
                var url = msg?["url"]?.GetValue<string>();
                if (url is not null) WebHost.OpenExternal(url);
                break;

            case "fetchJson":
            case "fetchText":
                await HandleFetch(msg!, type == "fetchText");
                break;

            case "store":
                HandleStore(msg!);
                break;

            case "system":
                HandleSystem(msg!);
                break;

            case "notify":
                HandleNotify(msg!);
                break;

            case "media":
                await HandleMedia(msg!);
                break;

            case "run":
                await HandleRun(msg!);
                break;

            case "commands":
                Post(new JsonObject { ["type"] = "commandsResult", ["id"] = msg!["id"]?.DeepClone(), ["ok"] = true, ["data"] = DescribeCommands() });
                break;

            case "timelapse":
                await HandleTimelapse(msg!);
                break;
            case "tabIcon":
                HandleTabIcon(msg!);
                break;

            case "exitEdit":
                // The page saw Esc. Only honoured while this window is the foreground one and Esc is physically down (a page can't fake it).
                if (_app.EditMode && Native.GetForegroundWindow() == Handle && IsKeyDown(Native.VK_ESCAPE)) _app.SetEditMode(false);
                break;

            case "pointer":
                if (PressedOnThisWindow()) { MarkPressed(); SelectForEdit(); }
                break;

            case "focus":
                if (PressedOnThisWindow()) { MarkPressed(); SelectForEdit(); }
                TakeKeyboardFocus();
                break;
        }
    }

    readonly Queue<long> _tabIconCalls = new();

    /// <summary>
    /// umbra.setTabIcon: the page may suggest one name from the bundled shared/tabicons (null or "" clears it). Anything else is ignored and logged.
    /// It only fills in when the user picked no icon for this copy, and changes are rate limited so a page can't churn settings.json.
    /// </summary>
    void HandleTabIcon(JsonNode msg)
    {
        var node = msg["name"];
        string? name = null;
        if (node is not null && node.GetValueKind() != JsonValueKind.Null)
        {
            if (node.GetValueKind() != JsonValueKind.String) { Log.Write($"[{Instance}] tab icon suggestion ignored: not a string"); return; }
            name = node.GetValue<string>();
        }
        string? clean = null;
        if (!string.IsNullOrEmpty(name) && (clean = Groups.CleanSuggestion(name)) is null)
        {
            var shown = new string(name.Take(40).Select(c => char.IsControl(c) ? '?' : c).ToArray());
            Log.Write($"[{Instance}] tab icon suggestion ignored: '{shown}' is not a bundled tab icon");
            return;
        }
        if (clean == Ws.SuggestedTabIcon) return; // nothing changes: repeated calls (say from onSettings) cost nothing
        if (!Groups.AllowSuggestion(_tabIconCalls, System.Environment.TickCount64)) return;
        _app.SuggestTabIcon(Instance, clean);
    }

    /// <summary>A physical press on this widget in edit mode makes it the one the arrow keys move.</summary>
    void SelectForEdit()
    {
        if (CanArrange) _app.SelectForEdit(Instance);
    }

    /// <summary>Redraws the stronger outline when this widget becomes (or stops being) the selected one.</summary>
    public void ShowEditSelected(bool selected)
    {
        if (_ready) Post(new JsonObject { ["type"] = "editSelected", ["selected"] = selected });
    }

    /// <summary>
    /// Arrow-key nudge in edit mode: 1 px, Shift 10 px (one grid step when snapping is on). The page only reports which
    /// arrow it saw; the host moves the window only for the selected, unlocked widget while it holds the foreground
    /// (which a widget gets only after a physical click) and that arrow key is physically down. The widget can't leave
    /// its work area completely, and the new spot is saved like after a drag.
    /// </summary>
    void Nudge(string? dir)
    {
        var (sx, sy, vk) = dir switch
        {
            "left" => (-1, 0, Native.VK_LEFT),
            "right" => (1, 0, Native.VK_RIGHT),
            "up" => (0, -1, Native.VK_UP),
            "down" => (0, 1, Native.VK_DOWN),
            _ => (0, 0, 0),
        };
        if (vk == 0 || _resizing || !CanArrange || !Alive || _app.EditSelection != Instance) return;
        if (Native.GetForegroundWindow() != Handle || !IsKeyDown(vk)) return;

        var general = _app.Settings.General;
        bool big = IsKeyDown(Native.VK_SHIFT);
        var grid = Snap.NudgeGrid(general, big);
        var to = new Point(
            sx == 0 ? Left : WidgetGeometry.NudgeTo(Left, sx, big, grid),
            sy == 0 ? Top : WidgetGeometry.NudgeTo(Top, sy, big, grid));
        var areas = Screens.All().Select(s => s.WorkArea).ToList();
        if (!WidgetGeometry.NudgeAllowed(Bounds, new Rectangle(to, Size), areas, (int)(WidgetGeometry.NudgeKeepVisible * DeviceDpi / 96.0))) return;

        Location = to;
        RememberPosition();
        SaveSoon();
    }

    /// <summary>Remembers a real press on this window, and which window was in front at that moment.</summary>
    void MarkPressed()
    {
        _pressedAt = System.Environment.TickCount64;
        _foregroundAtPress = Native.GetForegroundWindow();
    }

    /// <summary>
    /// Is a mouse button physically down with the cursor over this (visible, uncovered) window?
    /// The page can't fake this, which is what makes the focus gate trustworthy.
    /// </summary>
    bool PressedOnThisWindow() =>
        (IsKeyDown(Native.VK_LBUTTON) || IsKeyDown(Native.VK_RBUTTON)) && CursorOverThisWindow();

    /// <summary>Did the user physically press the mouse on this window in the last two seconds? Gates the first-approval prompt.</summary>
    bool RecentUserPress() => System.Environment.TickCount64 - _pressedAt <= 2000;

    /// <summary>The stricter gate for the camera: a left click outside edit mode in the last 2 s, not the host menu (<see cref="ClickGate"/>).</summary>
    bool RecentLeftClick() => ClickGate.Allows(_leftPressAt, _menuAt, System.Environment.TickCount64, _app.EditMode);

    /// <summary>Cancelled when the window closes (widget disabled or removed): running commands are killed.</summary>
    readonly CancellationTokenSource _closing = new();

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closing.Cancel();
        _app.Timelapse.Stop(Instance); // disabled or removed: the camera goes off and what was captured is rendered
        // Disposed later: this can run inside a click on the menu's own item (Hide, Remove copy).
        if (_menu is { } menu) { _menu = null; SynchronizationContext.Current?.Post(_ => menu.Dispose(), null); }
        CrashRecovery.SetStopped(Instance, false);
        base.OnFormClosed(e);
    }

    /// <summary>Ctrl physically held with the cursor over this window: a wheel zoom the user is really doing.</summary>
    bool CtrlWheelOverThisWindow() => IsKeyDown(Native.VK_CONTROL) && CursorOverThisWindow();

    static bool IsKeyDown(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    public bool CursorOverThisWindow()
    {
        var p = Cursor.Position;
        var hit = Native.WindowFromPoint(new Native.POINT { X = p.X, Y = p.Y });
        return hit != IntPtr.Zero && Native.GetAncestor(hit, Native.GA_ROOT) == Handle;
    }

    /// <summary>
    /// Widgets never take focus on their own (WS_EX_NOACTIVATE), so text fields can't receive typing.
    /// When the user has just pressed the mouse on this widget, the page may ask for the keyboard;
    /// any other request is ignored, so a widget can't pull focus away while the user types elsewhere.
    /// </summary>
    void TakeKeyboardFocus()
    {
        var fg = Native.GetForegroundWindow();
        // Too long since the press, or the user has moved on to another window since: ignore.
        if (System.Environment.TickCount64 - _pressedAt > 1000 || (fg != _foregroundAtPress && fg != Handle)) return;
        if (fg != Handle)
        {
            // The foreground lock only lets the foreground thread hand over focus, so join its input queue briefly.
            uint me = Native.GetCurrentThreadId();
            uint other = fg == IntPtr.Zero ? 0 : Native.GetWindowThreadProcessId(fg, out _);
            bool attached = other != 0 && other != me && Native.AttachThreadInput(me, other, true);
            try { Native.SetForegroundWindow(Handle); }
            finally { if (attached) Native.AttachThreadInput(me, other, false); }
        }
        _view.Focus();
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

    /// <summary>
    /// umbra.run: asks the host to run one of this widget's declared commands (permissions.commands). The page sends
    /// only a command id and string parameters; <see cref="CommandService"/> does the checking and the approval.
    /// </summary>
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
            reply["data"] = WindowsMetrics.Read(Manifest.Permissions.System);
        }
        Post(reply);
    }

    /// <summary>umbra.notify: needs permissions.notifications, the user's switch for this widget, a url allowed by <c>AllowsUrl</c>, and stays within the rate limit.</summary>
    void HandleNotify(JsonNode msg)
    {
        var reply = new JsonObject { ["type"] = "notifyResult", ["id"] = msg["id"]?.DeepClone() };
        string? error = null;
        var title = Notifier.Clean(msg["title"] is JsonValue t && t.GetValueKind() == JsonValueKind.String ? t.GetValue<string>() : null, Notifier.MaxTitle);
        var body = Notifier.Clean(msg["body"] is JsonValue b && b.GetValueKind() == JsonValueKind.String ? b.GetValue<string>() : null, Notifier.MaxBody);
        var url = msg["url"] is JsonValue u && u.GetValueKind() == JsonValueKind.String ? u.GetValue<string>() : null;
        var tag = msg["tag"] is JsonValue g && g.GetValueKind() == JsonValueKind.String ? Notifier.CleanTag(g.GetValue<string>()) : null;
        var actions = Notifier.ParseActions(msg["actions"], out var actionError);
        if (!Manifest.Permissions.Notifications) error = "This widget has no permissions.notifications";
        else if (!(Alive && Ws.Enabled) || !Prefs.EffectiveNotifications(Ws, _app.Settings.General)) error = "Notifications are off for this widget (Settings)";
        else if (title.Length == 0) error = "A title is required";
        else if (url is not null && !Manifest.AllowsUrl(url)) error = "url must be HTTPS on a host in permissions.hosts";
        else if (actions is null) error = actionError;
        else if (!_app.Notify(Instance, InstanceId.Label(Manifest, Instance), title, body, url, tag, actions)) error = "Rate limit reached (5 per 10 minutes)";
        reply["ok"] = error is null;
        if (error is not null) reply["error"] = error;
        Post(reply);
    }

    /// <summary>A button on one of this widget's toasts was clicked: tell the page (umbra.onNotificationAction).</summary>
    public void NotificationAction(string? tag, string action)
    {
        if (!Alive || !Ws.Enabled || !Manifest.Permissions.Notifications) return;
        Post(new JsonObject { ["type"] = "notificationAction", ["action"] = action, ["tag"] = tag });
    }

    /// <summary>umbra.media: "get" needs permissions.media "read"; "control" needs "control" and a recent physical press on the widget.</summary>
    async Task HandleMedia(JsonNode msg)
    {
        var reply = new JsonObject { ["type"] = "mediaResult", ["id"] = msg["id"]?.DeepClone() };
        try
        {
            var media = Manifest.Permissions.Media;
            if (!(Alive && Ws.Enabled)) throw new WidgetCommandException("The widget is disabled");
            var action = msg["action"] is JsonValue a && a.GetValueKind() == JsonValueKind.String ? a.GetValue<string>() : null;
            if (action == "get")
            {
                if (!media.Contains("read")) throw new WidgetCommandException("This widget has no permissions.media \"read\"");
                reply["data"] = await WindowsMedia.NowPlayingAsync();
            }
            else if (action == "control")
            {
                if (!media.Contains("control")) throw new WidgetCommandException("This widget has no permissions.media \"control\"");
                var cmd = msg["command"] is JsonValue c && c.GetValueKind() == JsonValueKind.String ? c.GetValue<string>() : null;
                if (!MediaService.IsCommand(cmd)) throw new WidgetCommandException("Unknown media command");
                if (!RecentUserPress()) throw new WidgetCommandException("Click the widget first: media controls need a physical click");
                reply["data"] = await WindowsMedia.ControlAsync(cmd!);
            }
            else throw new WidgetCommandException("Unknown media action");
            reply["ok"] = true;
        }
        catch (Exception ex)
        {
            reply["ok"] = false;
            reply["error"] = ex is WidgetCommandException ? ex.Message : "Media is not available";
            if (ex is not WidgetCommandException) Log.Write($"[{Manifest.Id}] media failed: {ex.Message}");
        }
        Post(reply);
    }

    /// <summary>
    /// umbra.timelapse: start (needs permissions.camera, an enabled visible widget, a physical left click just before and
    /// the one-time camera approval), stop, cancel, status, and chooseFolder (host folder dialog, also after a click).
    /// The page never gets a frame or a full path: only the video's file name and the folder's name.
    /// </summary>
    async Task HandleTimelapse(JsonNode msg)
    {
        var reply = new JsonObject { ["type"] = "timelapseResult", ["id"] = msg["id"]?.DeepClone() };
        try
        {
            if (!Manifest.Permissions.Camera) throw new TimelapseException("This widget has no permissions.camera");
            var op = msg["op"] is JsonValue o && o.GetValueKind() == JsonValueKind.String ? o.GetValue<string>() : null;
            switch (op)
            {
                case "start":
                    if (!(Alive && Ws.Enabled && Visible)) throw new TimelapseException("The widget is disabled or hidden");
                    var plan = TimelapsePlan.Create(Number(msg, "interval"), Number(msg, "length"), Number(msg, "fps"));
                    await _app.CameraConsent.EnsureAsync(Manifest, RecentLeftClick());
                    if (!(Alive && Ws.Enabled)) throw new TimelapseException("The widget is disabled");
                    reply["data"] = _app.Timelapse.Start(Instance, Manifest.Id, plan, TimelapseFolders.Shared.Get(Manifest.Id));
                    break;
                case "stop":
                    _app.Timelapse.Stop(Instance);
                    reply["data"] = _app.Timelapse.Status(Instance);
                    break;
                case "cancel":
                    _app.Timelapse.Cancel(Instance);
                    reply["data"] = _app.Timelapse.Status(Instance);
                    break;
                case "status":
                    var status = _app.Timelapse.Status(Instance);
                    status["folder"] = TimelapseFolders.Label(TimelapseFolders.Shared.Get(Manifest.Id));
                    status["cameraApproved"] = _app.CameraConsent.Approvals.CameraApproved(Manifest.Id);
                    reply["data"] = status;
                    break;
                case "chooseFolder":
                    if (!(Alive && Ws.Enabled)) throw new TimelapseException("The widget is disabled");
                    if (!RecentLeftClick()) throw new TimelapseException("Click the widget first: the folder dialog opens only from a physical click");
                    reply["data"] = await ChooseTimelapseFolder();
                    break;
                default:
                    throw new TimelapseException("Unknown timelapse operation");
            }
            reply["ok"] = true;
        }
        catch (Exception ex)
        {
            reply["ok"] = false;
            reply["error"] = ex is TimelapseException ? ex.Message : "Timelapse is not available";
            Log.Write($"[{Instance}] timelapse {msg["op"]} failed: {ex.Message}");
        }
        Post(reply);
    }

    static double Number(JsonNode msg, string key) =>
        msg[key] is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : throw new TimelapseException($"{key} must be a number");

    /// <summary>The host's own folder picker; the folder is saved per widget id and the page only learns which one was picked.</summary>
    async Task<JsonObject> ChooseTimelapseFolder()
    {
        await Task.Yield(); // let the message handler return before the modal dialog
        var current = TimelapseFolders.Shared.Get(Manifest.Id);
        using var dlg = new FolderBrowserDialog
        {
            Description = $"Where should \"{ApprovalDialog.Clean(Manifest.Name, 40)}\" save timelapse videos?",
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(current) ? current : System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyVideos),
            ShowNewFolderButton = true,
        };
        if (dlg.ShowDialog() == DialogResult.OK && Path.IsPathFullyQualified(dlg.SelectedPath))
        {
            TimelapseFolders.Shared.Set(Manifest.Id, dlg.SelectedPath);
            return new JsonObject { ["folder"] = TimelapseFolders.Label(dlg.SelectedPath), ["changed"] = true };
        }
        return new JsonObject { ["folder"] = TimelapseFolders.Label(current), ["changed"] = false };
    }

    Panel? _recordingDot;

    /// <summary>
    /// A timelapse of this copy changed: show or hide the host-drawn red dot (a child window over the page, so the page
    /// can't hide or fake it, and widget screenshots don't include it) and tell the page (umbra.timelapse.onChange).
    /// </summary>
    public void TimelapseChanged(JsonObject state)
    {
        if (IsDisposed) return;
        var recording = state["state"]?.GetValue<string>() == "recording";
        if (recording && _recordingDot is null)
        {
            var d = Math.Max(8, 10 * DeviceDpi / 96);
            var m = Math.Max(4, 6 * DeviceDpi / 96);
            _recordingDot = new Panel { Size = new Size(d, d), BackColor = Tray.RecordingColor, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            _recordingDot.Location = new Point(ClientSize.Width - d - m, m);
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                path.AddEllipse(0, 0, d, d);
                _recordingDot.Region = new Region(path);
            }
            Controls.Add(_recordingDot);
            _recordingDot.BringToFront();
        }
        else if (!recording && _recordingDot is not null)
        {
            Controls.Remove(_recordingDot);
            _recordingDot.Dispose();
            _recordingDot = null;
        }
        if (_ready) Post(new JsonObject { ["type"] = "timelapseState", ["state"] = state.DeepClone() });
    }

    async Task HandleRun(JsonNode msg)
    {
        var reply = new JsonObject { ["type"] = "runResult", ["id"] = msg["id"]?.DeepClone() };
        try
        {
            if (msg["command"] is not JsonValue c || c.GetValueKind() != JsonValueKind.String)
                throw new WidgetCommandException("A command id is required");
            if (msg["params"] is not null && msg["params"] is not JsonObject)
                throw new WidgetCommandException("params must be an object of strings");
            reply["data"] = await _app.Commands.RunAsync(Manifest, Alive && Ws.Enabled, RecentUserPress(),
                c.GetValue<string>(), msg["params"] as JsonObject, _closing.Token, Instance);
            reply["ok"] = true;
        }
        catch (Exception ex)
        {
            reply["ok"] = false;
            reply["error"] = ex is WidgetCommandException ? ex.Message : "The command failed";
            if (ex is not WidgetCommandException) Log.Write($"[{Manifest.Id}] run failed: {ex}");
        }
        Post(reply);
    }

    /// <summary>
    /// umbra.commands(): what this widget can run, as ids and names only. The program, arguments and paths stay in the host,
    /// so the page learns nothing it could not already do with umbra.run.
    /// </summary>
    JsonArray DescribeCommands() => new(_app.Commands.All(Manifest).Select(c => (JsonNode?)new JsonObject
    {
        ["id"] = c.Id,
        ["title"] = c.Title ?? c.Id,
        ["description"] = c.IsUserScript && c.Description == c.Title ? "" : c.Description, // a script without one borrows its name
        ["user"] = c.IsUserScript,
    }).ToArray());

    /// <summary>The user added, changed or deleted one of this widget's scripts in Settings.</summary>
    public void NotifyCommandsChanged()
    {
        if (_ready) Post(new JsonObject { ["type"] = "commandsChanged" });
    }

    /// <summary>Tells the page that its stored data was changed from outside (umbractl data, MCP).</summary>
    public void StoreChanged(string? key)
    {
        if (_ready) Post(new JsonObject { ["type"] = "storeChanged", ["key"] = key });
    }

    /// <summary>
    /// Dev hook: with UMBRA_CAPTURE_DIR set, each widget saves a pixel-exact PNG of itself
    /// (used for README screenshots and catalog preview.png files). umbractl capture does the same on demand.
    /// </summary>
    async Task CaptureIfRequested()
    {
        var dir = System.Environment.GetEnvironmentVariable("UMBRA_CAPTURE_DIR");
        if (string.IsNullOrEmpty(dir) || _view.CoreWebView2 is null) return;
        var png = await CaptureAsync();
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, $"{InstanceId.FileName(Instance)}.png"), png);
    }

    /// <summary>
    /// PNG of this widget's web content. Waits for the page to be ready and for its first "rendered"
    /// signal (widgets that never send one get a few seconds), then lets draw-in animations finish.
    /// </summary>
    public async Task<byte[]> CaptureAsync()
    {
        await _readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await Task.WhenAny(_renderedTcs.Task, Task.Delay(3000));
        var settle = _renderedTcs.Task.IsCompleted ? TimeSpan.FromMilliseconds(2500) - (DateTime.UtcNow - _renderedAt) : TimeSpan.Zero;
        if (settle > TimeSpan.Zero) await Task.Delay(settle);
        using var ms = new MemoryStream();
        await _view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
        return ms.ToArray();
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
        ApplyBounds();
        Opacity = Prefs.EffectiveOpacity(Ws, _app.Settings.General);
        if (_ready) Post(new JsonObject { ["type"] = "settings", ["settings"] = SettingsPayload() });
    }

    /// <summary>Tells the page that pausing or Energy Saver changed (#104); unlike <see cref="PushSettings"/> it leaves the window alone.</summary>
    public void PushPower()
    {
        if (_ready) Post(new JsonObject { ["type"] = "settings", ["settings"] = SettingsPayload() });
    }

    public void RefreshData()
    {
        if (_ready) Post(new JsonObject { ["type"] = "refresh" });
    }

    /// <summary>The system clock or time zone changed: the SDK runs the page's onTimeChanged handlers, or its refresh handlers when it has none.</summary>
    public void NotifyTimeChanged()
    {
        if (_ready) Post(TimeChange.Message());
    }

    JsonObject SettingsPayload()
    {
        var ws = Ws;
        var saver = _app.Power.EnergySaver && _app.Settings.General.SlowOnEnergySaver; // true = intervals are stretched
        return new JsonObject
        {
            ["refreshSeconds"] = PowerPolicy.Refresh(Prefs.EffectiveRefresh(ws, _app.Settings.General), saver, true),
            ["paused"] = _app.Power.Paused,
            ["energySaver"] = saver,
            ["animations"] = Prefs.MotionOn(_app.Settings.General),
            ["appearanceCss"] = Manifest.Themeable ? Prefs.AppearanceCss(_app.Settings.General) : "",
            ["themeCss"] = Manifest.Themeable ? _app.ThemeCss : "",
            ["locked"] = !_app.EditMode, // the global lock (#234)
            ["accent"] = Prefs.ResolveAccent(_app.Settings.General, Prefs.SystemAccent),
            ["scale"] = ws.Scale,
            ["locale"] = Prefs.LocaleOf(CultureInfo.CurrentCulture),
            ["hour12"] = Prefs.Hour12Of(CultureInfo.CurrentCulture),
            ["editMode"] = _app.EditMode,
            ["editSelected"] = _app.EditMode && _app.EditSelection == Instance,
            ["options"] = ws.Options.DeepClone(),
        };
    }

    void Post(JsonNode node)
    {
        if (IsDisposed || _view.CoreWebView2 is null) return;
        _view.CoreWebView2.PostWebMessageAsJson(node.ToJsonString());
    }

}
