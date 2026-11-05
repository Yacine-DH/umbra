namespace Umbra.Tests;

public class PlacementTests
{
    static readonly MonitorInfo Main = new(@"\\.\DISPLAY1", new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1040), true);
    static readonly MonitorInfo Side = new(@"\\.\DISPLAY2", new Rectangle(1920, 0, 1280, 1024), new Rectangle(1920, 0, 1280, 1024), false);
    static readonly Size Size = new(300, 200);

    [Fact]
    public void Resolve_puts_the_widget_at_its_offset_on_the_saved_monitor() =>
        Assert.Equal(new Point(1920 + 100, 50), Placement.Resolve(new[] { Main, Side }, Side.Name, 100, 50, Size));

    [Fact]
    public void Resolve_falls_back_to_primary_when_the_monitor_is_unplugged() =>
        Assert.Equal(new Point(100, 50), Placement.Resolve(new[] { Main }, Side.Name, 100, 50, Size));

    [Fact]
    public void Resolve_pulls_the_widget_inside_a_smaller_monitor() =>
        Assert.Equal(new Point(1920 + 1280 - 300, 1024 - 200), Placement.Resolve(new[] { Main, Side }, Side.Name, 2000, 2000, Size));

    [Fact]
    public void Find_picks_the_monitor_under_the_window_centre()
    {
        Assert.Equal(Side.Name, Placement.Find(new[] { Main, Side }, new Rectangle(2000, 100, 300, 200))!.Value.Name);
        Assert.Equal(Main.Name, Placement.Find(new[] { Main, Side }, new Rectangle(10, 10, 300, 200))!.Value.Name);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(7, false)]
    public void Only_fullscreen_notification_states_hide_widgets(int state, bool expected) =>
        Assert.Equal(expected, Placement.IsFullscreenState(state));

    [Fact]
    public void Peek_is_a_hotkey_action_and_a_control_command()
    {
        Assert.True(Hotkey.IsAction("peek"));
        Assert.Contains(Protocol.Commands, c => c.Cmd == "peek");
    }

    [Fact]
    public void New_settings_default_to_off_and_five_seconds()
    {
        var g = new GeneralSettings();
        Assert.False(g.HideOnFullscreen);
        Assert.Equal(5, g.PeekSeconds);
    }

    // Settings window placement (#111)
    static readonly Size Min = new(760, 520);

    [Fact]
    public void Capture_then_resolve_gives_the_same_rectangle_on_the_same_monitor()
    {
        var screens = new[] { Main, Side };
        var rect = new Rectangle(1920 + 80, 60, 1000, 700);
        var saved = Placement.Capture(screens, rect, 1f, false, "themes");
        Assert.Equal(Side.Name, saved.Monitor);
        Assert.Equal("themes", saved.Page);
        Assert.Equal(rect, Placement.ResolveWindow(screens, saved, _ => 1f, Min));
    }

    [Fact]
    public void Capture_stores_logical_pixels_and_resolve_rescales_them_to_the_monitor_dpi()
    {
        var screens = new[] { Main };
        var saved = Placement.Capture(screens, new Rectangle(150, 90, 1500, 900), 1.5f, true, null);
        Assert.True(saved.Maximized);
        Assert.Equal((100, 60, 1000, 600), (saved.X, saved.Y, saved.Width, saved.Height));
        Assert.Equal(new Rectangle(125, 75, 1250, 750), Placement.ResolveWindow(screens, saved, _ => 1.25f, Min));
    }

    [Fact]
    public void Resolve_returns_null_when_the_monitor_is_unplugged_or_nothing_was_saved()
    {
        var saved = Placement.Capture(new[] { Main, Side }, new Rectangle(1920 + 80, 60, 1000, 700), 1f, false, null);
        Assert.Null(Placement.ResolveWindow(new[] { Main }, saved, _ => 1f, Min));
        Assert.Null(Placement.ResolveWindow(new[] { Main, Side }, new SettingsPlacement(), _ => 1f, Min));
    }

    [Fact]
    public void Resolve_keeps_the_window_visible_on_a_smaller_work_area_and_above_the_minimum_size()
    {
        var saved = new SettingsPlacement { Monitor = Side.Name, X = 1200, Y = 1000, Width = 3000, Height = 3000 };
        Assert.Equal(new Rectangle(1920, 0, 1280, 1024), Placement.ResolveWindow(new[] { Main, Side }, saved, _ => 1f, Min));

        var tiny = new SettingsPlacement { Monitor = Main.Name, X = -500, Y = -500, Width = 100, Height = 100 };
        Assert.Equal(new Rectangle(0, 0, 760, 520), Placement.ResolveWindow(new[] { Main }, tiny, _ => 1f, Min));
    }

    [Fact]
    public void Placement_survives_a_settings_roundtrip_and_old_files_default_to_unsaved()
    {
        var s = new Settings();
        s.SettingsPanel = new SettingsPlacement { Monitor = Main.Name, X = 5, Y = 6, Width = 1000, Height = 700, Maximized = true, Page = "hotkeys" };
        var back = Settings.FromJson(s.ToJson()).SettingsPanel;
        Assert.Equal((Main.Name, 5, 6, 1000, 700, true, "hotkeys"), (back.Monitor, back.X, back.Y, back.Width, back.Height, back.Maximized, back.Page));

        var old = Settings.FromJson(System.Text.Json.Nodes.JsonNode.Parse("""{ "widgets": {} }""")!);
        Assert.Null(old.SettingsPanel.Monitor);
        Assert.Null(Placement.ResolveWindow(new[] { Main }, old.SettingsPanel, _ => 1f, Min));
    }

    [Fact]
    public void Settings_zoom_steps_5_percent_like_a_browser_within_50_to_200()
    {
        Assert.Equal(1.05, SettingsPlacement.StepZoom(1.0, 1));
        Assert.Equal(0.95, SettingsPlacement.StepZoom(1.0, -1));
        Assert.Equal(1.0, SettingsPlacement.StepZoom(1.35, 0));
        Assert.Equal(2.0, SettingsPlacement.StepZoom(2.0, 1));
        Assert.Equal(0.5, SettingsPlacement.StepZoom(0.5, -1));
        // Twenty steps up and back land exactly on 100 % (no float drift).
        var z = 1.0;
        for (var i = 0; i < 20; i++) z = SettingsPlacement.StepZoom(z, 1);
        Assert.Equal(2.0, z);
        for (var i = 0; i < 20; i++) z = SettingsPlacement.StepZoom(z, -1);
        Assert.Equal(1.0, z);
    }

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(-2.0, 1.0)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(0.1, 0.5)]
    [InlineData(9.0, 2.0)]
    [InlineData(1.12, 1.1)]
    [InlineData(1.13, 1.15)]
    public void Settings_zoom_from_a_file_is_clamped_and_snapped(double stored, double expected)
    {
        Assert.Equal(expected, new SettingsPlacement { Zoom = stored }.Zoom);
    }

    [Fact]
    public void Settings_zoom_survives_capture_and_a_settings_roundtrip_and_defaults_to_100()
    {
        var s = new Settings { SettingsPanel = Placement.Capture(new[] { Main }, new Rectangle(10, 10, 1000, 700), 1f, false, null, 1.25) };
        Assert.Equal(1.25, Settings.FromJson(s.ToJson()).SettingsPanel.Zoom);
        Assert.Equal(1.0, Settings.FromJson(System.Text.Json.Nodes.JsonNode.Parse("""{ "settingsPanel": { "page": "home" } }""")!).SettingsPanel.Zoom);
    }

    [Fact]
    public void A_null_settingsPanel_in_the_file_means_defaults()
    {
        var s = Settings.FromJson(System.Text.Json.Nodes.JsonNode.Parse("""{ "settingsPanel": null }""")!);
        Assert.NotNull(s.SettingsPanel);
        Assert.Null(s.SettingsPanel.Monitor);
    }
}
