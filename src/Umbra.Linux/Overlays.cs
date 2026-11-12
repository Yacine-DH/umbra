namespace Umbra;

/// <summary>
/// Base of the host's own small windows in edit mode and for tab groups (edit pill, snap guides, tab strip): plain GTK
/// windows, not web views, so no widget can draw into them and <c>umbractl capture</c> never includes them. They never take
/// the focus and sit just above the widgets, below normal windows (the pin backend's overlay role).
/// </summary>
abstract class OverlayWindow
{
    protected readonly App App;
    protected readonly Gtk.Window Window = Gtk.Window.New();
    bool _closed;

    protected OverlayWindow(App app, string title, string cssClass)
    {
        App = app;
        Window.SetTitle(title);
        Window.SetDecorated(false);
        Window.SetResizable(false);
        Window.SetFocusable(false);
        Window.AddCssClass(cssClass);
    }

    /// <summary>Call from the subclass constructor once the content is set.</summary>
    protected void Finish()
    {
        ((Gtk.Widget)Window).Realize();
        App.Pin.Prepare(Window, overlay: true);
        Window.OnMap += (_, _) => { App.Pin.Mapped(Window, overlay: true); App.Pin.Move(Window, X, Y); };
    }

    public int X { get; private set; }
    public int Y { get; private set; }
    public bool Visible => !_closed && Window.GetVisible();

    public void MoveTo(int x, int y)
    {
        if (_closed || (x == X && y == Y && Visible)) return;
        X = x; Y = y;
        App.Pin.Move(Window, x, y);
    }

    public void Show() { if (!_closed && !Window.GetVisible()) Window.SetVisible(true); }
    public void Hide() { if (!_closed && Window.GetVisible()) Window.SetVisible(false); }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        Window.Destroy();
    }

    /// <summary>The style of every overlay; loaded once by <c>App</c>. Colours follow the default theme's surfaces and accent.</summary>
    public const string Css = """
        window.umbra-pill, window.umbra-tabs { background: #1c1c22; border-radius: 10px; border: 1px solid rgba(255,255,255,0.12); }
        window.umbra-pill box { padding: 4px 6px 4px 14px; }
        window.umbra-pill label { color: #f2f2f5; font-size: 13px; }
        window.umbra-pill label.title { font-weight: 700; }
        window.umbra-pill label.hint { color: #9a9aa6; }
        window.umbra-pill button, window.umbra-tabs button {
            background: #17171c; color: #f2f2f5; border: 1px solid rgba(255,255,255,0.12); border-radius: 7px;
            box-shadow: none; min-height: 24px; padding: 1px 10px; font-size: 13px; font-weight: 700; }
        window.umbra-pill button:hover, window.umbra-tabs button:hover { background: #24242c; }
        window.umbra-pill button.done { background: #8b93ff; color: #0b0b10; border-color: #8b93ff; padding: 1px 16px; }
        window.umbra-tabs box { padding: 3px; }
        window.umbra-tabs button { min-width: 30px; min-height: 30px; padding: 0; background: transparent; border-color: transparent; }
        window.umbra-tabs button.active { background: rgba(139,147,255,0.16); border-color: #8b93ff; }
        window.umbra-guide { background: #8b93ff; }
        """;
}

/// <summary>
/// The host-drawn "Editing widgets · Snap · Done" pill at the top centre of a monitor while edit mode is on (the Linux
/// <c>EditPill</c>). Its buttons flip snapping and end edit mode; a click never takes the focus from the user's application.
/// </summary>
sealed class EditPill : OverlayWindow
{
    readonly Gtk.Button _snap = Gtk.Button.New();
    readonly Gtk.Label _hint = Gtk.Label.New("");
    const int Width = 430, Height = 36;

    /// <summary>The work area of the monitor this pill sits on.</summary>
    public Rectangle Area { get; }

    public EditPill(App app, Rectangle workArea) : base(app, "Umbra - editing widgets", "umbra-pill")
    {
        Area = workArea;
        var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        var title = Gtk.Label.New("Editing widgets");
        title.AddCssClass("title");
        _hint.AddCssClass("hint");
        var done = Gtk.Button.NewWithLabel("Done");
        done.AddCssClass("done");
        foreach (var b in new[] { _snap, done }) { b.SetFocusable(false); b.SetFocusOnClick(false); }
        _snap.OnClicked += (_, _) => App.ToggleSnap();
        done.OnClicked += (_, _) => App.SetEditMode(false);
        box.Append(title);
        box.Append(_snap);
        box.Append(_hint);
        box.Append(done);
        Window.SetChild(box);
        Window.SetDefaultSize(Width, Height);
        RefreshState();
        Finish();
        MoveTo(workArea.X + (workArea.Width - Width) / 2, workArea.Y + 8);
    }

    /// <summary>Re-reads the snap state (it may have changed from the CLI or Settings).</summary>
    public void RefreshState()
    {
        _snap.SetLabel(Snap.Label(App.Settings.General));
        _hint.SetText(Snap.Hint(App.Settings.General));
    }
}

/// <summary>The alignment guides drawn while a widget is moved in edit mode: one thin accent-coloured window per guide line.</summary>
sealed class SnapGuides(App app)
{
    sealed class Line : OverlayWindow
    {
        public Line(App app) : base(app, "Umbra - snap guide", "umbra-guide")
        {
            Window.SetCanTarget(false);
            Finish();
        }

        public void Set(Rectangle r)
        {
            Window.SetDefaultSize(r.Width, r.Height);
            Window.SetSizeRequest(r.Width, r.Height);
            MoveTo(r.X, r.Y);
            Show();
        }
    }

    readonly List<Line> _lines = new();

    /// <summary>Shows exactly these guides (none hides them all).</summary>
    public void Set(IReadOnlyList<Snap.Guide> guides)
    {
        while (_lines.Count < guides.Count) _lines.Add(new Line(app));
        for (var i = 0; i < _lines.Count; i++)
        {
            if (i >= guides.Count) { _lines[i].Hide(); continue; }
            var g = guides[i];
            var length = Math.Max(1, g.End - g.Start);
            _lines[i].Set(g.Vertical ? new Rectangle(g.Pos, g.Start, 1, length) : new Rectangle(g.Start, g.Pos, length, 1));
        }
    }

    public void Close()
    {
        foreach (var l in _lines) l.Close();
        _lines.Clear();
    }
}

/// <summary>
/// The host-drawn tab strip of a widget group: one button per member, attached above the visible widget so it covers none
/// of its content (the Linux <c>GroupTabs</c>). A click shows that member; the mouse wheel steps through them.
/// </summary>
sealed class GroupTabs : OverlayWindow
{
    readonly string _groupId;
    readonly Gtk.Box _box = Gtk.Box.New(Gtk.Orientation.Horizontal, TabStripGeometry.Gap);
    string _shown = "";

    public GroupTabs(App app, string groupId) : base(app, "Umbra - widget tabs", "umbra-tabs")
    {
        _groupId = groupId;
        Window.SetChild(_box);
        var wheel = Gtk.EventControllerScroll.New(Gtk.EventControllerScrollFlags.Vertical | Gtk.EventControllerScrollFlags.Discrete);
        wheel.OnScroll += (_, e) => { App.StepTab(_groupId, e.Dy > 0 ? 1 : -1); return true; };
        Window.AddController(wheel);
        Rebuild();
        Finish();
    }

    /// <summary>Rebuilds the buttons when the members, the active tab or an icon changed.</summary>
    public void Rebuild()
    {
        if (!App.Settings.Groups.TryGetValue(_groupId, out var group)) return;
        var active = Groups.ActiveOf(group);
        var key = string.Join('|', group.Members.Select(m =>
            $"{m}:{(App.Settings.Widgets.TryGetValue(m, out var w) ? Groups.ChooseIcon(w.TabIcon, w.SuggestedTabIcon) : null)}")) + "|" + active;
        if (key == _shown) return;
        _shown = key;
        while (_box.GetFirstChild() is { } child) _box.Remove(child);
        foreach (var member in group.Members)
        {
            var manifest = App.Manifests.FirstOrDefault(m => m.Id == InstanceId.WidgetOf(member));
            var label = manifest is null ? member : InstanceId.Label(manifest, member);
            var button = Gtk.Button.New();
            button.SetFocusable(false);
            button.SetFocusOnClick(false);
            button.SetTooltipText(label);
            var icon = App.Settings.Widgets.TryGetValue(member, out var ws) ? Groups.ChooseIcon(ws.TabIcon, ws.SuggestedTabIcon) : null;
            if (Groups.IconPath(icon) is { } path)
            {
                var image = Gtk.Image.NewFromFile(path);
                image.SetPixelSize(18);
                button.SetChild(image);
            }
            else button.SetLabel(label.Length > 0 ? label[..1].ToUpperInvariant() : "?");
            if (member == active) button.AddCssClass("active");
            var instance = member;
            button.OnClicked += (_, _) => App.ActivateTab(_groupId, instance);
            _box.Append(button);
        }
        var size = TabStripGeometry.SizeFor(group.Members.Count, 1);
        Window.SetDefaultSize(size.Width, size.Height);
    }

    /// <summary>Puts the strip above <paramref name="target"/> (inside its top edge when the screen has no room above).</summary>
    public void Place(Rectangle target, Rectangle workArea)
    {
        var count = App.Settings.Groups.TryGetValue(_groupId, out var g) ? g.Members.Count : 1;
        var r = TabStripGeometry.Place(target, workArea, 1, count, out _);
        MoveTo(r.X, r.Y);
    }
}
