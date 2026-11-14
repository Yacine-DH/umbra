using System.Drawing;

namespace Umbra.Linux.Tests;

/// <summary>Where a widget window goes (#182): saved monitor and offset, saved spot, manifest default, corner; never off screen.</summary>
public sealed class WidgetPlacementTests
{
    static readonly MonitorInfo Main = new("DP-1", new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 32, 1920, 1048), true);
    static readonly MonitorInfo Side = new("HDMI-1", new Rectangle(1920, 0, 1280, 1024), new Rectangle(1920, 0, 1280, 1024), false);
    static readonly Size Size = new(260, 128);

    [Fact]
    public void A_new_widget_goes_to_the_top_right_of_the_primary_work_area()
    {
        var at = WidgetPlacement.Locate(new WidgetSettings(), null, Size, [Main, Side]);
        Assert.Equal(new Point(1920 - 260 - 24, 32 + 24), at);
    }

    [Fact]
    public void The_manifest_default_is_relative_to_the_work_area()
    {
        var at = WidgetPlacement.Locate(new WidgetSettings(), new WidgetManifest.PointSpec { X = 24, Y = 240 }, Size, [Main]);
        Assert.Equal(new Point(24, 32 + 240), at);
    }

    [Fact]
    public void A_saved_spot_wins_over_the_default()
    {
        var ws = new WidgetSettings { X = 150, Y = 200 };
        Assert.Equal(new Point(150, 200), WidgetPlacement.Locate(ws, new WidgetManifest.PointSpec { X = 24, Y = 240 }, Size, [Main]));
    }

    [Fact]
    public void A_saved_monitor_and_offset_follow_the_monitor()
    {
        var ws = new WidgetSettings { X = 2000, Y = 100, Monitor = "HDMI-1", RelX = 80, RelY = 100 };
        Assert.Equal(new Point(1920 + 80, 100), WidgetPlacement.Locate(ws, null, Size, [Main, Side]));
        // The side monitor moved to the left of the main one: the widget goes with it.
        var moved = Side with { Bounds = new Rectangle(-1280, 0, 1280, 1024), WorkArea = new Rectangle(-1280, 0, 1280, 1024) };
        Assert.Equal(new Point(-1280 + 80, 100), WidgetPlacement.Locate(ws, null, Size, [Main, moved]));
    }

    [Fact]
    public void A_widget_on_an_unplugged_monitor_comes_back_on_screen()
    {
        var ws = new WidgetSettings { X = 2500, Y = 300 };
        var at = WidgetPlacement.Locate(ws, null, Size, [Main]);
        Assert.True(Main.WorkArea.IntersectsWith(new Rectangle(at, Size)), $"{at} is off screen");

        var pinned = new WidgetSettings { X = 2500, Y = 300, Monitor = "HDMI-1", RelX = 580, RelY = 300 };
        at = WidgetPlacement.Locate(pinned, null, Size, [Main]);
        Assert.True(Main.WorkArea.IntersectsWith(new Rectangle(at, Size)), $"{at} is off screen");
    }

    [Fact]
    public void Without_monitors_the_saved_spot_is_used_as_is()
    {
        Assert.Equal(new Point(10, 20), WidgetPlacement.Locate(new WidgetSettings { X = 10, Y = 20 }, null, Size, []));
        Assert.Equal(new Point(0, 0), WidgetPlacement.Locate(new WidgetSettings(), null, Size, []));
    }

    [Fact]
    public void Remember_records_the_monitor_and_offset()
    {
        var ws = new WidgetSettings();
        WidgetPlacement.Remember(ws, new Rectangle(2000, 100, 260, 128), [Main, Side]);
        Assert.Equal((2000, 100, "HDMI-1", 80, 100), (ws.X, ws.Y, ws.Monitor, ws.RelX, ws.RelY));
    }

    [Fact]
    public void Move_to_monitor_keeps_the_offset_from_the_corner()
    {
        var ws = new WidgetSettings { X = 100, Y = 200 };
        WidgetPlacement.MoveToMonitor(ws, "HDMI-1", new Rectangle(100, 200, 260, 128), [Main, Side]);
        Assert.Equal(("HDMI-1", 100, 200), (ws.Monitor, ws.RelX, ws.RelY));
        Assert.Equal(new Point(1920 + 100, 200), WidgetPlacement.Locate(ws, null, Size, [Main, Side]));
    }

    // ---------- display changes: a monitor vanished, the scale changed (the suspend/undock report) ----------

    static readonly MonitorInfo Panel = new("eDP-1", new Rectangle(0, 0, 1440, 900), new Rectangle(0, 0, 1440, 900), true);
    static readonly MonitorInfo External = new("DP-1", new Rectangle(1440, 0, 3440, 1440), new Rectangle(1440, 0, 3440, 1440), false);
    static readonly MonitorInfo ExternalAlone = External with { Bounds = new Rectangle(0, 0, 3440, 1440), WorkArea = new Rectangle(0, 0, 3440, 1440), Primary = true };

    [Fact]
    public void When_the_saved_monitor_vanishes_the_widget_goes_to_a_connected_one_at_the_same_size_and_the_setting_stays()
    {
        var ws = new WidgetSettings { Monitor = "eDP-1", RelX = 100, RelY = 100, X = 100, Y = 100 };
        var before = WidgetPlacement.Layout(ws, null, Size, [Panel, External]);
        var after = WidgetPlacement.Layout(ws, null, Size, [ExternalAlone]);
        Assert.Equal(new Rectangle(100, 100, 260, 128), before);
        Assert.Equal(new Rectangle(100, 100, 260, 128), after);
        Assert.True(ExternalAlone.WorkArea.Contains(after));
        Assert.Equal(("eDP-1", 100, 100), (ws.Monitor, ws.RelX, ws.RelY)); // not rewritten: it comes back with the panel
        Assert.Equal(before, WidgetPlacement.Layout(ws, null, Size, [Panel, External]));
    }

    [Fact]
    public void A_widget_far_into_a_vanished_monitor_is_pulled_inside_the_one_left()
    {
        var ws = new WidgetSettings { Monitor = "DP-1", RelX = 3200, RelY = 1300 };
        var r = WidgetPlacement.Layout(ws, null, Size, [Panel]);
        Assert.True(Panel.WorkArea.Contains(r), $"{r} is not inside {Panel.WorkArea}");
    }

    [Fact]
    public void Scale_2_to_1_keeps_the_logical_bounds_and_halves_the_device_pixels()
    {
        var logical = new Rectangle(100, 100, 211, 104); // 260x128 x widget scale 0.81
        Assert.Equal(new Rectangle(200, 200, 422, 208), WidgetPlacement.ToDevice(logical, 2));
        Assert.Equal(new Rectangle(100, 100, 211, 104), WidgetPlacement.ToDevice(logical, 1));
    }

    [Theory]
    [InlineData(1.25, 125, 125, 264, 130)]
    [InlineData(1.5, 150, 150, 317, 156)]
    public void Fractional_scales_round_to_whole_device_pixels(double scale, int x, int y, int w, int h)
    {
        Assert.Equal(new Rectangle(x, y, w, h), WidgetPlacement.ToDevice(new Rectangle(100, 100, 211, 104), scale));
    }

    [Fact]
    public void A_scale_below_1_or_a_tiny_window_never_gives_an_empty_or_shrunken_rectangle()
    {
        Assert.Equal(new Rectangle(10, 10, 1, 1), WidgetPlacement.ToDevice(new Rectangle(10, 10, 0, 0), 0));
    }

    [Fact]
    public void The_layout_key_changes_with_monitors_and_scales_only()
    {
        var docked = WidgetPlacement.LayoutKey([Panel, External], [2, 1]);
        Assert.Equal(docked, WidgetPlacement.LayoutKey([Panel, External], [2, 1]));
        Assert.NotEqual(docked, WidgetPlacement.LayoutKey([ExternalAlone], [1]));          // a monitor vanished
        Assert.NotEqual(docked, WidgetPlacement.LayoutKey([Panel, External], [1, 1]));     // same monitors, scale 2 -> 1
        Assert.NotEqual(WidgetPlacement.LayoutKey([Panel], [1.25]), WidgetPlacement.LayoutKey([Panel], [1.5])); // fractional
    }
}
