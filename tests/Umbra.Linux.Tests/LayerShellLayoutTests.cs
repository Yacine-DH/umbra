using System.Drawing;

namespace Umbra.Linux.Tests;

/// <summary>Native Wayland pinning (#186): backend choice, layer-surface margins from a placement, pointer positions in a moving surface.</summary>
public sealed class LayerShellLayoutTests
{
    static readonly Rectangle Main = new(0, 0, 1920, 1080);
    static readonly Rectangle Side = new(1920, 0, 1280, 1024);
    static readonly Size Size = new(260, 128);

    [Theory]
    // gdkBackend, override, wayland, x11, library, compositor -> backend
    [InlineData(null, null, true, true, true, true, "LayerShell")]     // KWin Wayland with XWayland
    [InlineData(null, null, true, false, true, true, "LayerShell")]    // Sway without XWayland
    [InlineData(null, null, true, true, true, false, "X11")]           // GNOME: no layer-shell, XWayland
    [InlineData(null, null, true, true, false, true, "X11")]           // library not installed
    [InlineData(null, null, true, false, false, true, "None")]         // nothing to pin with
    [InlineData(null, null, false, true, false, false, "X11")]         // plain X session
    [InlineData(null, "x11", true, true, true, true, "X11")]           // UMBRA_PIN_BACKEND=x11
    [InlineData(null, " X11 ", true, true, true, true, "X11")]
    [InlineData("x11", null, true, true, true, true, "X11")]           // explicit GDK_BACKEND wins
    [InlineData("wayland", null, true, true, true, true, "LayerShell")]
    [InlineData("wayland,x11", null, true, true, true, false, "None")] // forced Wayland on GNOME: no pin
    [InlineData("x11", null, true, false, true, true, "None")]
    public void The_backend_follows_the_session(string? gdk, string? pinOverride, bool wayland, bool x11, bool lib, bool offered, string expected) =>
        Assert.Equal(expected, LayerShellLayout.Choose(gdk, pinOverride, wayland, x11, lib, offered).ToString());

    [Fact]
    public void Margins_are_the_offset_from_the_monitor_corner()
    {
        Assert.Equal((0, 150, 200), LayerShellLayout.Place(new Point(150, 200), Size, [Main, Side]));
        Assert.Equal((1, 100, 40), LayerShellLayout.Place(new Point(2020, 40), Size, [Main, Side]));
    }

    [Fact]
    public void A_window_across_two_monitors_goes_to_the_one_holding_most_of_it()
    {
        // 60 px on the main monitor, 200 on the side one: bound to the side monitor, clamped to its left edge.
        Assert.Equal((1, 0, 10), LayerShellLayout.Place(new Point(1860, 10), Size, [Main, Side]));
        Assert.Equal((0, 1720, 10), LayerShellLayout.Place(new Point(1720, 10), Size, [Main, Side]));
    }

    [Fact]
    public void Monitors_left_of_or_above_the_origin_give_positive_margins()
    {
        Rectangle left = new(-1280, -200, 1280, 1024);
        Assert.Equal((1, 280, 300), LayerShellLayout.Place(new Point(-1000, 100), Size, [Main, left]));
    }

    [Fact]
    public void A_spot_off_every_monitor_goes_to_the_nearest_and_is_clamped()
    {
        Assert.Equal((1, 1380, 0), LayerShellLayout.Place(new Point(3300, -50), new Size(1, 1), [Main, Side]));
        Assert.Equal((0, 0, 0), LayerShellLayout.Place(new Point(-500, -500), Size, [Main, Side]));
        Assert.Equal((-1, 5, 0), LayerShellLayout.Place(new Point(5, -3), Size, []));
    }

    [Fact]
    public void A_stale_pointer_sample_does_not_run_away_while_the_surface_moves()
    {
        var latch = new PointerLatch();
        var s = (IntPtr)1;
        var origin = new Point(100, 100);
        var start = latch.Resolve(s, 10, 10, origin);           // the press
        Assert.Equal(new Point(110, 110), start);
        var p = latch.Resolve(s, 15, 10, origin);                // pointer moved 5 px
        origin = new Point(origin.X + p.X - start.X, origin.Y);  // the drag moves the surface by 5
        // No new motion yet: the same relative sample must not count again against the new origin.
        for (var i = 0; i < 10; i++) Assert.Equal(new Point(115, 110), latch.Resolve(s, 15, 10, origin));
        // The compositor moved the surface; the next motion is relative to it.
        Assert.Equal(new Point(117, 110), latch.Resolve(s, 12, 10, origin));
    }

    [Fact]
    public void A_new_surface_is_a_new_sample()
    {
        var latch = new PointerLatch();
        Assert.Equal(new Point(15, 15), latch.Resolve(1, 5, 5, new Point(10, 10)));
        Assert.Equal(new Point(505, 505), latch.Resolve(2, 5, 5, new Point(500, 500)));
        Assert.Equal(new Point(9, 10), latch.Resolve(2, 5.9, 5.2, new Point(4, 5)));
    }

    [Fact]
    public void During_a_drag_the_surface_keeps_its_output()
    {
        // Dragged onto the side monitor: still bound to the main one, margins past its edge, nothing clamped.
        Assert.Equal((0, 2020, 40), LayerShellLayout.Place(new Point(2020, 40), Size, [Main, Side], keep: 0));
        Assert.Equal((1, -100, -5), LayerShellLayout.Place(new Point(1820, -5), Size, [Main, Side], keep: 1));
        // The drag's end puts it on the monitor that holds it.
        Assert.Equal((1, 100, 40), LayerShellLayout.Place(new Point(2020, 40), Size, [Main, Side]));
        // A kept monitor that is gone (unplugged) falls back to the normal choice.
        Assert.Equal((0, 150, 200), LayerShellLayout.Place(new Point(150, 200), Size, [Main], keep: 1));
    }

    [Fact]
    public void A_lent_focus_is_taken_back_unless_the_surface_became_active()
    {
        var lease = new FocusLease();
        var t = lease.Lend();
        Assert.True(lease.ShouldRevoke(t, active: false));
        Assert.False(lease.ShouldRevoke(t, active: true));
    }

    [Fact]
    public void A_newer_press_or_an_ended_lease_supersedes_the_old_timer()
    {
        var lease = new FocusLease();
        var first = lease.Lend();
        var second = lease.Lend();
        Assert.False(lease.ShouldRevoke(first, active: false));
        Assert.True(lease.ShouldRevoke(second, active: false));
        lease.End();
        Assert.False(lease.ShouldRevoke(second, active: false));
    }
}
