namespace Umbra.Linux.Tests;

/// <summary>The tray menu as dbusmenu serves it (#191) and the GVariant text quoting every D-Bus value goes through.</summary>
public sealed class TrayMenuTests
{
    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("it's", @"'it\'s'")]
    [InlineData(@"back\slash", @"'back\\slash'")]
    [InlineData("new\nline", @"'new\u000aline'")]
    [InlineData("caf\u00e9", "'caf\\u00e9'")]
    [InlineData("'>, 'type': <'separator", @"'\'>, \'type\': <\'separator'")]
    public void Strings_are_quoted_so_text_never_becomes_structure(string input, string expected) =>
        Assert.Equal(expected, V.Str(input));

    [Fact]
    public void Empty_collections_carry_their_type() =>
        Assert.Equal(("@a{sv} {}", "@as []"), (V.Dict([]), V.Strings([])));

    static TrayMenu Sample(uint revision = 7) => new(
    [
        new TrayItem { Label = "Settings...", Click = () => { } },
        new TrayItem { Label = "Arrange", Checked = true },
        TrayItem.Line(),
        new TrayItem { Label = "Layouts", Children = [new TrayItem { Label = "Work_desk" }] },
        new TrayItem { Label = "Off", Enabled = false },
    ], revision);

    [Fact]
    public void Items_are_numbered_depth_first_from_the_root()
    {
        var m = Sample();
        Assert.Equal("Settings...", m.Find(1)!.Label);
        Assert.Equal("Work_desk", m.Find(5)!.Label);
        Assert.Equal(7, m.Ids.Count());
    }

    [Fact]
    public void Layout_has_labels_checkmarks_separators_and_submenus()
    {
        var reply = Sample().GetLayoutReply(0, -1);
        Assert.StartsWith("(uint32 7, (0, {'children-display': <'submenu'>}, [", reply);
        Assert.Contains("(1, {'label': <'Settings...'>}, @av [])", reply);
        Assert.Contains("(2, {'label': <'Arrange'>, 'toggle-type': <'checkmark'>, 'toggle-state': <1>}, @av [])", reply);
        Assert.Contains("(3, {'type': <'separator'>}, @av [])", reply);
        Assert.Contains("(4, {'label': <'Layouts'>, 'children-display': <'submenu'>}, [<(5, {'label': <'Work__desk'>}, @av [])>])", reply);
        Assert.Contains("(6, {'label': <'Off'>, 'enabled': <false>}, @av [])", reply);
    }

    [Fact]
    public void Depth_limits_the_children_and_an_unknown_parent_gives_the_root()
    {
        var m = Sample();
        Assert.Equal("(uint32 7, (4, {'label': <'Layouts'>, 'children-display': <'submenu'>}, @av []))", m.GetLayoutReply(4, 0));
        Assert.StartsWith("(uint32 7, (0,", m.GetLayoutReply(99, 1));
    }

    [Fact]
    public void Group_properties_for_some_or_all_items()
    {
        var m = Sample();
        Assert.Equal("([(1, {'label': <'Settings...'>})],)", m.GetGroupPropertiesReply([1]));
        Assert.Equal("(@a(ia{sv}) [],)", m.GetGroupPropertiesReply([42]));
        Assert.Contains("(6, {'label': <'Off'>, 'enabled': <false>})", m.GetGroupPropertiesReply([]));
    }

    [Fact]
    public void Widget_text_in_the_menu_is_cleaned_and_cut()
    {
        Assert.Equal("ab", Tray.MenuText("a\u0007b"));
        Assert.Equal(new string('x', 60) + "...", Tray.MenuText(new string('x', 80)));
    }

    [Fact]
    public void Tooltip_lists_the_state()
    {
        Assert.Equal("1 widget", Tray.Tooltip(1, false, false, false));
        Assert.Equal("3 widgets · arranging · safe mode · 2 scripts running · update available", Tray.Tooltip(3, true, true, true, 2));
    }
}
