namespace Umbra;

/// <summary>
/// The host-drawn right-click menu of a widget (#360), the Linux counterpart of the Windows <c>BuildMenu</c>: same items in the
/// same order, drawn by GTK (a <see cref="Gtk.PopoverMenu"/> on a <see cref="Gio.Menu"/>), never by the page. It opens only from
/// WebKit's <c>context-menu</c> signal right after a physical right press on this window (seen by the capture-phase gesture), so a
/// page can neither open nor drive it; a page that calls <c>preventDefault()</c> on <c>contextmenu</c> keeps its own menu.
/// </summary>
sealed partial class WidgetWindow
{
    long _rightPressAt = long.MinValue / 2;
    double _rightX, _rightY;
    Gtk.PopoverMenu? _menu;

    /// <summary>The right press that may open the menu: at most 1.5 s old (the context-menu signal follows the press or its release).</summary>
    bool RecentRightPress() => Environment.TickCount64 - _rightPressAt is >= 0 and <= 1500;

    /// <summary>Called by the press gesture for a physical right press, with its position in the view.</summary>
    void NoteRightPress(double x, double y)
    {
        _rightPressAt = Environment.TickCount64;
        _rightX = x;
        _rightY = y;
    }

    /// <summary>WebKit's context-menu signal: the default menu never shows; ours only after a real right press.</summary>
    bool OnContextMenuRequested()
    {
        if (_closed || !RecentRightPress()) return true;
        _rightPressAt = long.MinValue / 2; // one press, one menu
        try { ShowMenu(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Write($"[{Instance}] widget menu failed: {ex.Message}"); }
        return true;
    }

    void ShowMenu()
    {
        if (!Alive || !Ws.Enabled) return;
        CloseMenu();
        var (model, actions) = BuildMenu();
        _view.InsertActionGroup("menu", actions);
        var popover = Gtk.PopoverMenu.NewFromModel(model);
        popover.SetParent(_view);
        popover.SetHasArrow(false);
        popover.SetPosition(Gtk.PositionType.Bottom);
        popover.SetHalign(Gtk.Align.Start);
        popover.SetPointingTo(new Gdk.Rectangle { X = (int)_rightX, Y = (int)_rightY, Width = 1, Height = 1 });
        popover.OnClosed += (_, _) =>
        {
            // Unparent after the clicked item's action ran (GTK closes the popover first, then activates).
            var closed = popover;
            SynchronizationContext.Current?.Post(_ => { if (_menu == closed) CloseMenu(); }, null);
        };
        _menu = popover;
        Log.Write($"[{Instance}] widget menu opened");
        popover.Popup();
    }

    /// <summary>Removes the menu (also when the window closes).</summary>
    void CloseMenu()
    {
        if (_menu is not { } m) return;
        _menu = null;
        try { m.Popdown(); m.Unparent(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        _view.InsertActionGroup("menu", null);
    }

    /// <summary>Builds the items: name, Refresh, Options..., Arrange widgets, tab group, Hide, Add another copy, Remove copy, Inspect.</summary>
    (Gio.Menu Model, Gio.SimpleActionGroup Actions) BuildMenu()
    {
        var actions = Gio.SimpleActionGroup.New();
        var n = 0;
        string Act(Action run, bool enabled = true, bool? check = null)
        {
            var name = $"a{n++}";
            var act = check is { } c ? Gio.SimpleAction.NewStateful(name, null, GLib.Variant.NewBoolean(c)) : Gio.SimpleAction.New(name, null);
            act.SetEnabled(enabled);
            act.OnActivate += (_, _) =>
            {
                try { run(); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
                { Log.Write($"[{Instance}] menu action failed: {ex.Message}"); }
            };
            actions.AddAction(act);
            return $"menu.{name}";
        }

        var root = Gio.Menu.New();
        var head = Gio.Menu.New();
        head.Append(MenuText(InstanceId.Label(Manifest, Instance)), Act(() => { }, enabled: false));
        root.AppendSection(null, head);

        var main = Gio.Menu.New();
        main.Append("Refresh", Act(RefreshData));
        main.Append("Options...", Act(() => _app.ShowWidgetOptions(Instance)));
        main.Append("Arrange widgets", Act(() => _app.SetEditMode(!_app.EditMode), check: _app.EditMode));
        root.AppendSection(null, main);

        var rest = Gio.Menu.New();
        AddGroupItems(rest, Act);
        rest.Append("Hide", Act(() => _app.DisableWidget(Instance)));
        rest.Append("Add another copy", Act(() => _app.AddInstance(Manifest),
            enabled: _app.Settings.Instances(Manifest.Id).Count < InstanceId.MaxInstances));
        // The first copy can only be hidden here: uninstalling a widget stays in Settings, where nothing can be undone by accident.
        if (InstanceId.IsExtra(Instance)) rest.Append("Remove copy", Act(() => _app.RemoveInstanceWithUndo(Instance)));
        root.AppendSection(null, rest);

        if (Prefs.DeveloperMode)
        {
            var dev = Gio.Menu.New();
            dev.Append("Inspect", Act(() => _view.GetInspector().Show()));
            root.AppendSection(null, dev);
        }
        return (root, actions);
    }

    /// <summary>Tab group entries: switch tabs and leave when grouped, otherwise join a group or start one (as on Windows).</summary>
    void AddGroupItems(Gio.Menu menu, Func<Action, bool, bool?, string> act)
    {
        var s = _app.Settings;
        if (Groups.GroupOf(s, Instance) is { } gid && s.Groups.TryGetValue(gid, out var g))
        {
            var tabs = Gio.Menu.New();
            if (g.Members.Count > 1)
            {
                var step = Gio.Menu.New();
                step.Append("Next tab", act(() => _app.StepTab(gid, 1), true, null));
                step.Append("Previous tab", act(() => _app.StepTab(gid, -1), true, null));
                tabs.AppendSection(null, step);
                var members = Gio.Menu.New();
                foreach (var m in g.Members)
                {
                    var label = _app.Resolve(m) is { } r ? InstanceId.Label(r.Manifest, m) : m;
                    var member = m;
                    members.Append(MenuText(label), act(() => _app.ActivateTab(gid, member), true, m == Instance));
                }
                tabs.AppendSection(null, members);
            }
            var leave = Gio.Menu.New();
            leave.Append("Remove from group", act(() => _app.LeaveGroup(Instance), true, null));
            tabs.AppendSection(null, leave);
            menu.AppendSubmenu($"Tab group: {MenuText(g.Name)}", tabs);
            return;
        }
        var add = Gio.Menu.New();
        if (s.Groups.Count > 0)
        {
            var existing = Gio.Menu.New();
            foreach (var (id, grp) in s.Groups)
            {
                var target = id;
                existing.Append(MenuText(grp.Name), act(() => _app.JoinGroup(target, Instance), true, null));
            }
            add.AppendSection(null, existing);
        }
        var create = Gio.Menu.New();
        create.Append("New group", act(() => _app.CreateGroup(Manifest.Name, Instance), true, null));
        add.AppendSection(null, create);
        menu.AppendSubmenu("Add to tab group", add);
    }

    /// <summary>
    /// Widget-controlled text (names, group names) as a menu label: control characters removed, at most 60 characters, and
    /// underscores doubled, because a <see cref="Gtk.PopoverMenu"/> label treats "_" as a mnemonic.
    /// </summary>
    internal static string MenuText(string s) => Dialogs.Clean(s, 60).Replace("_", "__");
}
