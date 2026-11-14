namespace Umbra;

/// <summary>
/// Host-drawn GTK dialogs for the Settings window (the Linux <c>HostDialog</c>, <c>PackageInstall</c> boxes and file pickers).
/// They are native windows on the UI thread, never HTML: a page can ask for one, but only the user's click answers it.
/// Only one is open at a time, so nothing can stack prompts on the user.
/// </summary>
static class Dialogs
{
    static bool _open;

    /// <summary>Widget-controlled text for a dialog: control characters removed, at most <paramref name="max"/> characters.</summary>
    public static string Clean(string? s, int max)
    {
        var t = new string((s ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return t.Length > max ? t[..max] + "..." : t;
    }

    /// <summary>A Yes/No box. True only when the user picked <paramref name="yes"/>; Esc, the close box and No are false. No is the default.</summary>
    public static async Task<bool> ConfirmAsync(Gtk.Window? parent, string message, string? detail = null, string yes = "Yes", string no = "Cancel")
    {
        if (_open) { Log.Write("A host dialog is already open; request ignored"); return false; }
        _open = true;
        try
        {
            var dlg = Gtk.AlertDialog.NewWithProperties([]);
            dlg.SetMessage(message);
            if (!string.IsNullOrEmpty(detail)) dlg.SetDetail(detail);
            dlg.SetButtons([no, yes]);
            dlg.SetCancelButton(0);
            dlg.SetDefaultButton(0);
            dlg.SetModal(true);
            return await dlg.ChooseAsync(parent!) == 1;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; } // dismissed
        finally { _open = false; }
    }

    /// <summary>"Todo 1.3 wants new access: ..." with the list and the changelog. Cancel is the default button.</summary>
    public static Task<bool> ConfirmNewAccessAsync(Gtk.Window? parent, Community.PendingInstall p, string? source = null)
    {
        var name = Clean(p.Manifest.Name, 60);
        var detail = string.Join('\n', p.Added.Describe().Select(l => "• " + Clean(l, 200)))
            + (source is null ? "" : $"\n\nFrom: {Clean(source, 200)}")
            + (WidgetManifest.SanitizeChangelog(p.Manifest.Changelog) is { } cl ? "\n\nWhat's new: " + cl : "")
            + (p.IsUpdate ? "\n\nUpdate and allow this?" : "\n\nInstall and allow this?");
        return ConfirmAsync(parent, $"{name} {Clean(p.Manifest.Version, 30)} wants {(p.IsUpdate ? "new " : "")}access:", detail,
            p.IsUpdate ? "Update" : "Install", "Cancel");
    }

    /// <summary>The review text for a widget that asks for no access (same wording as Windows).</summary>
    public static string PlainReview(Community.PendingInstall p, string source) =>
        $"{(p.IsUpdate ? "Replace the installed" : "Install")} {Clean(p.Manifest.Name, 60)} {Clean(p.Manifest.Version, 30)}"
        + (p.Manifest.Author.Length > 0 ? $" by {Clean(p.Manifest.Author, 60)}" : "")
        + $"?\n\nFrom {Clean(source, 200)}. It asks for no access to websites, system info, media, notifications or commands.";

    /// <summary>A small host-drawn box asking for an https:// address. Returns the checked address, or null (cancelled, or a bad address, which is explained).</summary>
    public static async Task<string?> AskUrlAsync(Gtk.Window? parent)
    {
        if (_open) return null;
        _open = true;
        string? url = null;
        try
        {
            var tcs = new TaskCompletionSource<bool>();
            var win = Gtk.Window.New();
            win.SetTitle("Install a widget from a web address");
            win.SetModal(true);
            win.SetResizable(false);
            if (parent is not null) win.SetTransientFor(parent);
            win.SetDefaultSize(520, -1);
            var box = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
            box.SetMarginTop(16); box.SetMarginBottom(16); box.SetMarginStart(16); box.SetMarginEnd(16);
            var label = Gtk.Label.New("Address of a .umbrawidget (or .zip) file, https:// only:");
            label.SetXalign(0);
            var entry = Gtk.Entry.New();
            entry.SetHexpand(true);
            var buttons = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
            buttons.SetHalign(Gtk.Align.End);
            var cancel = Gtk.Button.NewWithLabel("Cancel");
            var ok = Gtk.Button.NewWithLabel("Download and review");
            ok.AddCssClass("suggested-action");
            buttons.Append(cancel);
            buttons.Append(ok);
            box.Append(label);
            box.Append(entry);
            box.Append(buttons);
            win.SetChild(box);
            cancel.OnClicked += (_, _) => { tcs.TrySetResult(false); win.Destroy(); };
            ok.OnClicked += (_, _) => { url = entry.GetText().Trim(); tcs.TrySetResult(true); win.Destroy(); };
            entry.OnActivate += (_, _) => { url = entry.GetText().Trim(); tcs.TrySetResult(true); win.Destroy(); };
            win.OnCloseRequest += (_, _) => { tcs.TrySetResult(false); return false; };
            var keys = Gtk.EventControllerKey.New();
            keys.OnKeyPressed += (_, e) =>
            {
                if (e.Keyval != 0xff1b /* Escape */) return false;
                tcs.TrySetResult(false); win.Destroy(); return true;
            };
            win.AddController(keys);
            win.Present();
            if (!await tcs.Task) return null;
        }
        finally { _open = false; }
        if (string.IsNullOrEmpty(url)) return null;
        if (Community.CheckDownloadUrl(url) is { } bad) { await InfoAsync(parent, bad); return null; }
        return url;
    }

    /// <summary>
    /// The "Allow notifications?" prompt for a widget (the Linux <c>ApprovalDialog.AskNotifications</c>). Null when it closed
    /// without an answer. Not now is the default and Esc.
    /// </summary>
    public static async Task<NotifyChoice?> AskNotificationsAsync(string widgetName)
    {
        if (_open) return null;
        _open = true;
        try
        {
            var tcs = new TaskCompletionSource<NotifyChoice?>();
            var win = Gtk.Window.New();
            win.SetTitle("Umbra");
            win.SetResizable(false);
            win.SetDefaultSize(420, -1);
            var box = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
            box.SetMarginTop(18); box.SetMarginBottom(16); box.SetMarginStart(18); box.SetMarginEnd(18);
            var heading = Gtk.Label.New($"Allow notifications from {Clean(widgetName, 60)}?");
            heading.AddCssClass("title-4");
            heading.SetXalign(0);
            heading.SetWrap(true);
            var detail = Gtk.Label.New("The widget can then show desktop notifications (at most 5 every 10 minutes). You can change this any time in Settings > My widgets.");
            detail.SetXalign(0);
            detail.SetWrap(true);
            detail.SetMaxWidthChars(52);
            var buttons = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
            buttons.SetHalign(Gtk.Align.End);
            buttons.SetMarginTop(6);
            void Add(string label, NotifyChoice choice, bool primary = false)
            {
                var b = Gtk.Button.NewWithLabel(label);
                if (primary) b.AddCssClass("suggested-action");
                b.OnClicked += (_, _) => { tcs.TrySetResult(choice); win.Destroy(); };
                buttons.Append(b);
            }
            Add("Don't ask again", NotifyChoice.Never);
            Add("Not now", NotifyChoice.NotNow);
            Add("Allow", NotifyChoice.Allow, primary: true);
            box.Append(heading);
            box.Append(detail);
            box.Append(buttons);
            win.SetChild(box);
            win.OnCloseRequest += (_, _) => { tcs.TrySetResult(NotifyChoice.NotNow); return false; };
            var keys = Gtk.EventControllerKey.New();
            keys.OnKeyPressed += (_, e) =>
            {
                if (e.Keyval != 0xff1b /* Escape */) return false;
                tcs.TrySetResult(NotifyChoice.NotNow); win.Destroy(); return true;
            };
            win.AddController(keys);
            win.Present();
            // Like Windows: no answer within 2 minutes counts as none, and the box goes away.
            var answered = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromMinutes(2)));
            if (answered != tcs.Task) { win.Destroy(); return null; }
            return await tcs.Task;
        }
        finally { _open = false; }
    }

    /// <summary>
    /// "Allow this widget to run a program?" (the Linux <c>ApprovalDialog.Ask</c>): the exact command line, the program, what the
    /// widget says it is for. Deny is the default and Esc. Allow works only 900 ms after the box appeared and once the pointer,
    /// having been off the button since then, is on it: a click already in flight or aimed at where the button will appear can't
    /// approve. No answer in 2 minutes is a refusal. True only for a click on Allow.
    /// </summary>
    public static async Task<bool> AskCommandAsync(ApprovalRequest r)
    {
        if (_open) return false;
        _open = true;
        try
        {
            var tcs = new TaskCompletionSource<bool>();
            var win = Gtk.Window.New();
            win.SetTitle("Umbra: allow this widget to run a program?");
            win.SetResizable(false);
            win.SetDefaultSize(560, -1);
            var box = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
            box.SetMarginTop(18); box.SetMarginBottom(16); box.SetMarginStart(18); box.SetMarginEnd(18);
            Gtk.Label L(string text, bool wrap = true)
            {
                var l = Gtk.Label.New(text);
                l.SetXalign(0);
                l.SetWrap(wrap);
                l.SetMaxWidthChars(64);
                l.SetSelectable(false);
                return l;
            }
            var heading = L($"The widget \"{Clean(r.WidgetName, 40)}\" wants to run this on your computer:");
            heading.AddCssClass("title-4");
            var line = Gtk.TextView.New();
            line.SetEditable(false);
            line.SetCursorVisible(false);
            line.SetWrapMode(Gtk.WrapMode.Char);
            line.SetMonospace(true);
            line.GetBuffer().SetText(r.CommandLine, -1);
            var frame = Gtk.Frame.New(null);
            frame.SetChild(line);
            var where = L($"Program: {r.ExePath}");
            where.SetEllipsize(Pango.EllipsizeMode.Middle);
            var note = L((r.HasParams ? "Parts written ${like this} are filled in by the widget each time (as single arguments). " : "")
                + "It runs as you, with no window, and is stopped after a time limit. You can revoke this any time in Settings > My widgets.");
            var said = L("What the widget says it is for (not verified): " + (string.IsNullOrWhiteSpace(r.Description) ? "(no description)" : Clean(r.Description, 200)));
            box.Append(heading);
            box.Append(frame);
            box.Append(where);
            box.Append(note);
            box.Append(said);
            if (r.IsInterpreter)
            {
                var warn = L("This program can run anything: whoever controls this widget controls what it does.");
                warn.AddCssClass("error");
                box.Append(warn);
            }
            var buttons = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
            buttons.SetHalign(Gtk.Align.End);
            buttons.SetMarginTop(6);
            var allow = Gtk.Button.NewWithLabel("Allow");
            var deny = Gtk.Button.NewWithLabel("Deny");
            allow.SetSensitive(false);
            buttons.Append(allow);
            buttons.Append(deny);
            box.Append(buttons);
            win.SetChild(box);

            var shownAt = Environment.TickCount64;
            bool inside = false, sawOutside = false;
            // Tracked on the window, not the button: a disabled (insensitive) GTK button gets no pointer events at all.
            void Track(double x, double y)
            {
                inside = allow.ComputeBounds(win, out var b) && x >= b.GetX() && y >= b.GetY()
                    && x < b.GetX() + b.GetWidth() && y < b.GetY() + b.GetHeight();
                if (!inside) sawOutside = true;
            }
            var winMotion = Gtk.EventControllerMotion.New();
            winMotion.OnEnter += (_, e) => Track(e.X, e.Y);
            winMotion.OnMotion += (_, e) => Track(e.X, e.Y);
            // No "leave" handler: pressing the button makes GTK grab the pointer, which sends a crossing event between press and
            // release; reacting to it would disarm Allow in the middle of the user's click. Position alone decides.
            winMotion.OnLeave += (_, _) => sawOutside = true;
            win.AddController(winMotion);
            UiThread.Every(40, () =>
            {
                if (tcs.Task.IsCompleted) return false;
                // Once armed it stays armed: the press itself makes GTK send crossing and motion events that must not disarm it mid-click.
                if (!allow.GetSensitive()) allow.SetSensitive(sawOutside && inside && Environment.TickCount64 - shownAt >= 900);
                if (Environment.TickCount64 - shownAt >= 120_000) { tcs.TrySetResult(false); win.Destroy(); return false; }
                return true;
            });
            allow.OnClicked += (_, _) =>
            {
                // GTK only emits clicked for a sensitive button, and only the arming tick above makes it sensitive.
                if (!allow.GetSensitive()) return;
                tcs.TrySetResult(true); win.Destroy();
            };
            deny.OnClicked += (_, _) => { tcs.TrySetResult(false); win.Destroy(); };
            win.OnCloseRequest += (_, _) => { tcs.TrySetResult(false); return false; };
            var keys = Gtk.EventControllerKey.New();
            keys.OnKeyPressed += (_, e) =>
            {
                if (e.Keyval != 0xff1b /* Escape */) return false;
                tcs.TrySetResult(false); win.Destroy(); return true;
            };
            win.AddController(keys);
            win.SetDefaultWidget(deny);
            win.Present();
            deny.GrabFocus();
            var allowed = await tcs.Task;
            Log.Write($"Command prompt for {r.WidgetId}/{r.CommandId}: {(allowed ? "allowed" : "denied")}");
            return allowed;
        }
        finally { _open = false; }
    }

    /// <summary>A message with only an OK button.</summary>
    public static async Task InfoAsync(Gtk.Window? parent, string message)
    {
        try
        {
            var dlg = Gtk.AlertDialog.NewWithProperties([]);
            dlg.SetMessage(message);
            dlg.SetButtons(["OK"]);
            dlg.SetModal(true);
            await dlg.ChooseAsync(parent!);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    /// <summary>The GTK file chooser (the desktop portal's under Flatpak). Null when cancelled.</summary>
    public static async Task<string?> OpenFileAsync(Gtk.Window? parent, string title, string? filterName = null, string[]? patterns = null, string? initialFolder = null)
    {
        var dlg = NewFileDialog(title, filterName, patterns, initialFolder);
        try { return (await dlg.OpenAsync(parent!))?.GetPath(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    /// <summary>The GTK folder chooser. Null when cancelled.</summary>
    public static async Task<string?> PickFolderAsync(Gtk.Window? parent, string title, string? initialFolder = null)
    {
        var dlg = NewFileDialog(title, null, null, initialFolder);
        try { return (await dlg.SelectFolderAsync(parent!))?.GetPath(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    /// <summary>The GTK save dialog. Null when cancelled.</summary>
    public static async Task<string?> SaveFileAsync(Gtk.Window? parent, string title, string initialName, string? filterName = null, string[]? patterns = null)
    {
        var dlg = NewFileDialog(title, filterName, patterns, null);
        dlg.SetInitialName(initialName);
        try { return (await dlg.SaveAsync(parent!))?.GetPath(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    static Gtk.FileDialog NewFileDialog(string title, string? filterName, string[]? patterns, string? initialFolder)
    {
        var dlg = Gtk.FileDialog.New();
        dlg.SetTitle(title);
        dlg.SetModal(true);
        if (patterns is { Length: > 0 })
        {
            var filter = Gtk.FileFilter.New();
            filter.SetName(filterName ?? title);
            foreach (var p in patterns) filter.AddPattern(p);
            var list = Gio.ListStore.New(Gtk.FileFilter.GetGType());
            list.Append(filter);
            var all = Gtk.FileFilter.New();
            all.SetName("All files");
            all.AddPattern("*");
            list.Append(all);
            dlg.SetFilters(list);
            dlg.SetDefaultFilter(filter);
        }
        if (initialFolder is { Length: > 0 } && Directory.Exists(initialFolder)) dlg.SetInitialFolder(Gio.FileHelper.NewForPath(initialFolder));
        return dlg;
    }
}
