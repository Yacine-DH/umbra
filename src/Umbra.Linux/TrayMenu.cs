namespace Umbra.Linux;

/// <summary>One entry of the tray menu: a label with an action, a checkmark toggle, a submenu or a separator.</summary>
sealed class TrayItem
{
    public string Label { get; init; } = "";
    public Action? Click { get; init; }
    /// <summary>Null: a plain item. True/false: a checkmark item in that state.</summary>
    public bool? Checked { get; init; }
    public bool Enabled { get; init; } = true;
    public bool Separator { get; init; }
    public List<TrayItem> Children { get; init; } = new();
    /// <summary>Assigned by <see cref="TrayMenu"/>: the dbusmenu item id (the root is 0).</summary>
    public int Id { get; internal set; }

    public static TrayItem Line() => new() { Separator = true };
}

/// <summary>
/// The tray menu as com.canonical.dbusmenu serves it: numbered items and their properties in GVariant text. Pure (no D-Bus),
/// so the layout and the escaping are unit tested; <see cref="LinuxTray"/> does the bus side.
/// </summary>
sealed class TrayMenu
{
    readonly Dictionary<int, TrayItem> _byId = new();
    public TrayItem Root { get; }
    public uint Revision { get; }

    public TrayMenu(IEnumerable<TrayItem> items, uint revision)
    {
        Revision = revision;
        Root = new TrayItem { Children = items.ToList() };
        var next = 0;
        void Number(TrayItem item)
        {
            item.Id = next++;
            _byId[item.Id] = item;
            foreach (var c in item.Children) Number(c);
        }
        Number(Root);
    }

    public TrayItem? Find(int id) => _byId.GetValueOrDefault(id);

    public IEnumerable<int> Ids => _byId.Keys;

    /// <summary>dbusmenu labels use "_" for mnemonics: a literal underscore is doubled.</summary>
    public static string MenuLabel(string s) => Tray.MenuText(s).Replace("_", "__");

    /// <summary>The a{sv} of one item's properties (the root only says it has children).</summary>
    public static string Props(TrayItem item)
    {
        var p = new List<(string, string)>();
        if (item.Id == 0) p.Add(("children-display", V.Str("submenu")));
        else if (item.Separator) p.Add(("type", V.Str("separator")));
        else
        {
            p.Add(("label", V.Str(MenuLabel(item.Label))));
            if (!item.Enabled) p.Add(("enabled", V.Bool(false)));
            if (item.Checked is { } on) { p.Add(("toggle-type", V.Str("checkmark"))); p.Add(("toggle-state", V.Int(on ? 1 : 0))); }
            if (item.Children.Count > 0) p.Add(("children-display", V.Str("submenu")));
        }
        return V.Dict(p);
    }

    /// <summary>The (ia{sv}av) layout of an item and its children down to <paramref name="depth"/> levels (-1: all).</summary>
    public static string Layout(TrayItem item, int depth)
    {
        var children = depth == 0 || item.Children.Count == 0 ? "@av []"
            : "[" + string.Join(", ", item.Children.Select(c => "<" + Layout(c, depth < 0 ? -1 : depth - 1) + ">")) + "]";
        return $"({V.Int(item.Id)}, {Props(item)}, {children})";
    }

    /// <summary>The reply of GetLayout: (u (ia{sv}av)). An unknown parent gives an empty root.</summary>
    public string GetLayoutReply(int parent, int depth) =>
        $"({V.UInt(Revision)}, {Layout(Find(parent) ?? Root, depth)})";

    /// <summary>The reply of GetGroupProperties: (a(ia{sv})).</summary>
    public string GetGroupPropertiesReply(IEnumerable<int> ids)
    {
        var wanted = ids.ToList();
        var items = (wanted.Count == 0 ? _byId.Values : wanted.Select(Find).OfType<TrayItem>())
            .Select(i => $"({V.Int(i.Id)}, {Props(i)})").ToList();
        return items.Count == 0 ? "(@a(ia{sv}) [],)" : "([" + string.Join(", ", items) + "],)";
    }
}

/// <summary>The tray tooltip and menu texts (the Linux counterpart of the pure parts of <c>Tray.cs</c>).</summary>
static class Tray
{
    public const int MaxTooltip = 127;

    /// <summary>"3 widgets · arranging · safe mode · update ready", cut to <see cref="MaxTooltip"/>.</summary>
    public static string Tooltip(int widgets, bool editMode, bool safeMode, bool updateReady, int scripts = 0)
    {
        var parts = new List<string> { widgets == 1 ? "1 widget" : $"{widgets} widgets" };
        if (editMode) parts.Add("arranging");
        if (safeMode) parts.Add("safe mode");
        if (scripts > 0) parts.Add(scripts == 1 ? "1 script running" : $"{scripts} scripts running");
        if (updateReady) parts.Add("update available");
        var s = string.Join(" · ", parts);
        return s.Length <= MaxTooltip ? s : s[..MaxTooltip];
    }

    /// <summary>Widget-controlled menu text: control characters removed, at most 60 characters.</summary>
    public static string MenuText(string s)
    {
        var t = new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return t.Length > 60 ? t[..60] + "..." : t;
    }
}
