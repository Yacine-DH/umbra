namespace Umbra.Linux;

/// <summary>
/// umbra.notify on Linux: org.freedesktop.Notifications on the session bus (GNOME Shell, KDE Plasma, mako, dunst...), the
/// counterpart of <c>ToastService</c>. The checks before a notification (permission, the user's switch, url, rate limit)
/// are the same as on Windows and live in the widget window and <see cref="Notifier"/>. A click on the notification opens
/// its url (already checked against the widget's hosts); a button goes back to the widget (umbra.onNotificationAction).
/// </summary>
sealed class LinuxNotifications
{
    const string Name = "org.freedesktop.Notifications";
    const string Path = "/org/freedesktop/Notifications";

    /// <summary>The button keys we send are this prefix plus the widget's action id; "default" is the body click.</summary>
    public const string ActionPrefix = "a:";

    sealed record Sent(string Instance, string? Tag, string? Url);

    readonly IntPtr _bus;
    readonly Action<string, string?, string> _onAction;
    readonly Dictionary<uint, Sent> _sent = new();
    readonly Dictionary<(string, string), uint> _byTag = new();
    bool _subscribed;
    bool? _bodyMarkup;

    /// <param name="onAction">(instance, tag, action id) when the user clicks one of a widget's buttons.</param>
    public LinuxNotifications(IntPtr bus, Action<string, string?, string> onAction)
    {
        _bus = bus;
        _onAction = onAction;
    }

    /// <summary>The notification's actions: "default" (body click) only with a url, then the widget's buttons.</summary>
    public static string ActionList(bool hasUrl, IReadOnlyList<NotifyAction> actions)
    {
        var list = new List<string>();
        if (hasUrl) { list.Add("default"); list.Add("Open"); }
        foreach (var a in actions) { list.Add(ActionPrefix + a.Id); list.Add(a.Label); }
        return V.Strings(list);
    }

    /// <summary>Servers that render body markup would read &lt;, &gt; and &amp; in widget text as tags; they are escaped.</summary>
    public static string EscapeMarkup(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>The icon the server shows: our own PNG (works before the icon theme is installed), or the theme name.</summary>
    static string Icon()
    {
        var png = System.IO.Path.Combine(AppPaths.AppDir, "icons", "hicolor", "64x64", "apps", DesktopEntry.AppId + ".png");
        return File.Exists(png) ? png : DesktopEntry.AppId;
    }

    void EnsureSubscribed()
    {
        if (_subscribed) return;
        _subscribed = true;
        DBus.Subscribe(_bus, null, Name, "ActionInvoked", Path, (_, v) =>
        {
            using var idV = v[0];
            using var keyV = v[1];
            if (!_sent.TryGetValue(idV.UInt, out var s)) return; // someone else's notification
            var key = keyV.Str;
            if (key == "default") { if (s.Url is not null) WebHost.OpenExternal(s.Url); }
            else if (key.StartsWith(ActionPrefix, StringComparison.Ordinal)) _onAction(s.Instance, s.Tag, key[ActionPrefix.Length..]);
        });
        DBus.Subscribe(_bus, null, Name, "NotificationClosed", Path, (_, v) =>
        {
            using var idV = v[0];
            if (_sent.Remove(idV.UInt, out var s) && s.Tag is not null) _byTag.Remove((s.Instance, s.Tag));
        });
    }

    async Task<bool> BodyMarkupAsync()
    {
        if (_bodyMarkup is { } known) return known;
        try
        {
            using var caps = await DBus.CallAsync(_bus, Name, Path, Name, "GetCapabilities");
            using var list = caps[0];
            var any = false;
            for (var i = 0; i < list.Count; i++) { using var c = list[i]; if (c.Str == "body-markup") any = true; }
            _bodyMarkup = any;
        }
        catch (DBusException) { _bodyMarkup = true; } // assume the strict case: escaping is harmless when not needed
        return _bodyMarkup.Value;
    }

    /// <summary>Shows one notification (the caller already applied the rate limit). A tag replaces the widget's earlier notification with that tag.</summary>
    public async Task ShowAsync(string instance, string widgetName, string title, string body, string? url, string? tag, IReadOnlyList<NotifyAction> actions)
    {
        if (_bus == IntPtr.Zero) { Log.Write($"[{instance}] notify: no session bus"); return; }
        EnsureSubscribed();
        var markup = await BodyMarkupAsync();
        var replaces = tag is not null && _byTag.TryGetValue((instance, tag), out var old) ? old : 0u;
        var hints = V.Dict([("desktop-entry", V.Str(DesktopEntry.AppId)), ("urgency", "byte 1")]);
        var args = $"({V.Str(widgetName)}, {V.UInt(replaces)}, {V.Str(Icon())}, {V.Str(title)}, {V.Str(markup ? EscapeMarkup(body) : body)}, "
            + $"{ActionList(url is not null, actions)}, {hints}, -1)";
        try
        {
            using var reply = await DBus.CallAsync(_bus, Name, Path, Name, "Notify", args);
            using var idV = reply[0];
            var id = idV.UInt;
            _sent[id] = new Sent(instance, tag, url);
            if (tag is not null) _byTag[(instance, tag)] = id;
        }
        catch (DBusException ex) { Log.Write($"[{instance}] notification not shown: {ex.Message}"); }
    }
}
