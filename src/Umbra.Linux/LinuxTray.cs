namespace Umbra.Linux;

/// <summary>
/// The tray icon on Linux: a StatusNotifierItem with a com.canonical.dbusmenu menu on the session bus, registered with
/// org.kde.StatusNotifierWatcher. KDE Plasma has the watcher built in; Ubuntu GNOME has it through the AppIndicator
/// extension (on by default). Without a watcher there is no tray: Umbra runs on, and launching it again opens Settings.
/// A left click opens Settings, like Windows; the menu has the same items as the Windows tray.
/// </summary>
sealed class LinuxTray
{
    const string ItemPath = "/StatusNotifierItem";
    const string MenuPath = "/MenuBar";
    const string ItemIface = "org.kde.StatusNotifierItem";
    const string MenuIface = "com.canonical.dbusmenu";
    const string Watcher = "org.kde.StatusNotifierWatcher";

    const string ItemXml = """
        <node><interface name="org.kde.StatusNotifierItem">
          <property name="Category" type="s" access="read"/>
          <property name="Id" type="s" access="read"/>
          <property name="Title" type="s" access="read"/>
          <property name="Status" type="s" access="read"/>
          <property name="WindowId" type="i" access="read"/>
          <property name="IconThemePath" type="s" access="read"/>
          <property name="IconName" type="s" access="read"/>
          <property name="IconPixmap" type="a(iiay)" access="read"/>
          <property name="OverlayIconName" type="s" access="read"/>
          <property name="OverlayIconPixmap" type="a(iiay)" access="read"/>
          <property name="AttentionIconName" type="s" access="read"/>
          <property name="AttentionIconPixmap" type="a(iiay)" access="read"/>
          <property name="AttentionMovieName" type="s" access="read"/>
          <property name="ToolTip" type="(sa(iiay)ss)" access="read"/>
          <property name="ItemIsMenu" type="b" access="read"/>
          <property name="Menu" type="o" access="read"/>
          <method name="ContextMenu"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
          <method name="Activate"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
          <method name="SecondaryActivate"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
          <method name="Scroll"><arg name="delta" type="i" direction="in"/><arg name="orientation" type="s" direction="in"/></method>
          <signal name="NewTitle"/><signal name="NewIcon"/><signal name="NewAttentionIcon"/><signal name="NewOverlayIcon"/>
          <signal name="NewToolTip"/><signal name="NewStatus"><arg name="status" type="s"/></signal>
        </interface></node>
        """;

    const string MenuXml = """
        <node><interface name="com.canonical.dbusmenu">
          <property name="Version" type="u" access="read"/>
          <property name="TextDirection" type="s" access="read"/>
          <property name="Status" type="s" access="read"/>
          <property name="IconThemePath" type="as" access="read"/>
          <method name="GetLayout">
            <arg type="i" name="parentId" direction="in"/><arg type="i" name="recursionDepth" direction="in"/>
            <arg type="as" name="propertyNames" direction="in"/>
            <arg type="u" name="revision" direction="out"/><arg type="(ia{sv}av)" name="layout" direction="out"/>
          </method>
          <method name="GetGroupProperties">
            <arg type="ai" name="ids" direction="in"/><arg type="as" name="propertyNames" direction="in"/>
            <arg type="a(ia{sv})" name="properties" direction="out"/>
          </method>
          <method name="GetProperty">
            <arg type="i" name="id" direction="in"/><arg type="s" name="name" direction="in"/><arg type="v" name="value" direction="out"/>
          </method>
          <method name="Event">
            <arg type="i" name="id" direction="in"/><arg type="s" name="eventId" direction="in"/>
            <arg type="v" name="data" direction="in"/><arg type="u" name="timestamp" direction="in"/>
          </method>
          <method name="EventGroup">
            <arg type="a(isvu)" name="events" direction="in"/><arg type="ai" name="idErrors" direction="out"/>
          </method>
          <method name="AboutToShow"><arg type="i" name="id" direction="in"/><arg type="b" name="needUpdate" direction="out"/></method>
          <method name="AboutToShowGroup">
            <arg type="ai" name="ids" direction="in"/><arg type="ai" name="updatesNeeded" direction="out"/><arg type="ai" name="idErrors" direction="out"/>
          </method>
          <signal name="ItemsPropertiesUpdated">
            <arg type="a(ia{sv})" name="updatedProps"/><arg type="a(ias)" name="removedProps"/>
          </signal>
          <signal name="LayoutUpdated"><arg type="u" name="revision"/><arg type="i" name="parent"/></signal>
          <signal name="ItemActivationRequested"><arg type="i" name="id"/><arg type="u" name="timestamp"/></signal>
        </interface></node>
        """;

    readonly IntPtr _bus;
    readonly string _name;
    readonly Func<IEnumerable<TrayItem>> _build;
    readonly Action _activate;
    readonly UiThread _ui;
    TrayMenu _menu;
    uint _revision = 1;
    string _tooltip = "Umbra";
    bool _attention;
    bool _dirty;

    /// <summary>True while a watcher has our item (an icon is shown somewhere).</summary>
    public bool Shown { get; private set; }

    LinuxTray(IntPtr bus, UiThread ui, Func<IEnumerable<TrayItem>> build, Action activate)
    {
        _bus = bus;
        _ui = ui;
        _build = build;
        _activate = activate;
        _menu = new TrayMenu(build(), _revision);
        _name = $"org.kde.StatusNotifierItem-{Environment.ProcessId}-1";
    }

    /// <summary>Exports the item and its menu and registers with the watcher whenever one is (or later comes) on the bus. Null without a session bus.</summary>
    public static LinuxTray? TryStart(UiThread ui, Func<IEnumerable<TrayItem>> build, Action activate)
    {
        var bus = DBus.Session;
        if (bus == IntPtr.Zero) { Log.Write("No session bus: no tray icon"); return null; }
        var tray = new LinuxTray(bus, ui, build, activate);
        try
        {
            DBus.Register(bus, ItemPath, ItemXml, ItemIface, new ItemObject(tray));
            DBus.Register(bus, MenuPath, MenuXml, MenuIface, new MenuObject(tray));
            DBus.OwnName(bus, tray._name);
        }
        catch (Exception ex) when (ex is DBusException or FormatException)
        {
            Log.Write($"Tray icon unavailable: {ex.Message}");
            return null;
        }
        var announced = false;
        DBus.Watch(bus, Watcher, present =>
        {
            if (!present)
            {
                tray.Shown = false;
                if (!announced) { announced = true; Log.Write("No StatusNotifierWatcher on this desktop (on stock GNOME: the AppIndicator extension): no tray icon. Launch Umbra again to open Settings."); }
                return;
            }
            _ = tray.RegisterAsync();
        });
        return tray;
    }

    async Task RegisterAsync()
    {
        try
        {
            using var _ = await DBus.CallAsync(_bus, Watcher, "/StatusNotifierWatcher", Watcher, "RegisterStatusNotifierItem", $"({V.Str(_name)},)");
            Shown = true;
            Log.Write("Tray icon registered");
        }
        catch (DBusException ex) { Log.Write($"Tray icon not registered: {ex.Message}"); }
    }

    /// <summary>The app state changed: rebuild the menu and tooltip once, on the next idle moment.</summary>
    public void Invalidate()
    {
        if (_dirty) return;
        _dirty = true;
        _ui.Post(_ => Rebuild(), null);
    }

    void Rebuild()
    {
        _dirty = false;
        _menu = new TrayMenu(_build(), ++_revision);
        DBus.Emit(_bus, MenuPath, MenuIface, "LayoutUpdated", $"({V.UInt(_revision)}, 0)");
    }

    /// <summary>Sets the hover text and whether the icon asks for attention (an Umbra update is available).</summary>
    public void SetStatus(string tooltip, bool attention)
    {
        if (tooltip != _tooltip)
        {
            _tooltip = tooltip;
            DBus.Emit(_bus, ItemPath, ItemIface, "NewToolTip");
        }
        if (attention != _attention)
        {
            _attention = attention;
            DBus.Emit(_bus, ItemPath, ItemIface, "NewStatus", $"({V.Str(attention ? "NeedsAttention" : "Active")},)");
            DBus.Emit(_bus, ItemPath, ItemIface, "NewIcon");
        }
    }

    /// <summary>Folder with our hicolor theme (next to the binary; the packages install it system-wide too).</summary>
    static string IconThemePath => Path.Combine(AppPaths.AppDir, "icons");

    sealed class ItemObject(LinuxTray t) : DBus.IObject
    {
        public string? Call(string method, DBus.Value args, string sender)
        {
            switch (method)
            {
                case "Activate":
                case "SecondaryActivate":
                    t._ui.Post(_ => t._activate(), null);
                    break;
            }
            return null;
        }

        public string? Get(string property) => property switch
        {
            "Category" => V.Str("ApplicationStatus"),
            "Id" => V.Str(DesktopEntry.AppId),
            "Title" => V.Str("Umbra"),
            "Status" => V.Str(t._attention ? "NeedsAttention" : "Active"),
            "WindowId" => V.Int(0),
            "IconThemePath" => V.Str(IconThemePath),
            // GNOME's AppIndicator shows the attention icon only for NeedsAttention; the badge is drawn into the name we give.
            "IconName" => V.Str(t._attention ? DesktopEntry.AppId + "-attention" : DesktopEntry.AppId),
            "AttentionIconName" => V.Str(DesktopEntry.AppId + "-attention"),
            "OverlayIconName" or "AttentionMovieName" => V.Str(""),
            "IconPixmap" or "OverlayIconPixmap" or "AttentionIconPixmap" => "@a(iiay) []",
            "ToolTip" => $"({V.Str("")}, @a(iiay) [], {V.Str("Umbra")}, {V.Str(t._tooltip)})",
            "ItemIsMenu" => V.Bool(false),
            "Menu" => V.Path(MenuPath),
            _ => null,
        };
    }

    sealed class MenuObject(LinuxTray t) : DBus.IObject
    {
        public string? Call(string method, DBus.Value args, string sender)
        {
            switch (method)
            {
                case "GetLayout":
                {
                    using var parent = args[0];
                    using var depth = args[1];
                    return t._menu.GetLayoutReply(parent.Int, depth.Int);
                }
                case "GetGroupProperties":
                {
                    using var ids = args[0];
                    var list = new List<int>();
                    for (var i = 0; i < ids.Count; i++) { using var id = ids[i]; list.Add(id.Int); }
                    return t._menu.GetGroupPropertiesReply(list);
                }
                case "GetProperty":
                {
                    using var id = args[0];
                    using var name = args[1];
                    var item = t._menu.Find(id.Int) ?? throw new DBusException("Unknown item");
                    return name.Str switch
                    {
                        "label" => $"(<{V.Str(TrayMenu.MenuLabel(item.Label))}>,)",
                        "enabled" => $"(<{V.Bool(item.Enabled)}>,)",
                        _ => throw new DBusException("Unknown property"),
                    };
                }
                case "Event":
                {
                    using var id = args[0];
                    using var ev = args[1];
                    if (ev.Str == "clicked") Click(id.Int);
                    return null;
                }
                case "EventGroup":
                {
                    using var events = args[0];
                    for (var i = 0; i < events.Count; i++)
                    {
                        using var e = events[i];
                        using var id = e[0];
                        using var ev = e[1];
                        if (ev.Str == "clicked") Click(id.Int);
                    }
                    return "(@ai [],)";
                }
                case "AboutToShow":
                    return "(false,)";
                case "AboutToShowGroup":
                    return "(@ai [], @ai [])";
            }
            throw new DBusException($"Unknown method {method}");
        }

        void Click(int id)
        {
            if (t._menu.Find(id) is { Click: { } action, Enabled: true }) t._ui.Post(_ => action(), null);
        }

        public string? Get(string property) => property switch
        {
            "Version" => V.UInt(3),
            "TextDirection" => V.Str("ltr"),
            "Status" => V.Str("normal"),
            "IconThemePath" => V.Strings([IconThemePath]),
            _ => null,
        };
    }
}
