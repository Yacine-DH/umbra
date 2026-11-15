namespace Umbra.Tests;

public class OcclusionTests
{
    static readonly MonitorInfo Main = new(@"\\.\DISPLAY1", new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1040), true);
    static readonly MonitorInfo Side = new(@"\\.\DISPLAY2", new Rectangle(1920, 0, 1280, 1024), new Rectangle(1920, 0, 1280, 1024), false);
    static readonly MonitorInfo[] Both = { Main, Side };

    static WindowSnapshot Win(Rectangle r, bool visible = true, bool minimized = false, bool cloaked = false, bool clickThrough = false, bool ignored = false) =>
        new(r, visible, minimized, cloaked, clickThrough, ignored);

    // Windows reports a maximized window's rectangle with its resize border hanging off the work area.
    static readonly Rectangle MaximizedOnMain = new(-8, -8, 1936, 1056);

    [Fact]
    public void A_maximized_window_covers_only_its_own_monitor() =>
        Assert.Equal(new[] { Main.Name }, Occlusion.CoveredMonitors(Both, new[] { Win(MaximizedOnMain) }));

    [Fact]
    public void A_fullscreen_window_covers_its_monitor() =>
        Assert.Equal(new[] { Side.Name }, Occlusion.CoveredMonitors(Both, new[] { Win(Side.Bounds) }));

    [Fact]
    public void A_window_that_leaves_part_of_the_work_area_free_covers_nothing()
    {
        Assert.Empty(Occlusion.CoveredMonitors(Both, new[] { Win(new Rectangle(0, 0, 1900, 1040)) }));
        Assert.Empty(Occlusion.CoveredMonitors(Both, new[] { Win(new Rectangle(960, 0, 1920, 1040)) })); // spans both, fills neither
    }

    [Fact]
    public void A_window_spanning_every_monitor_covers_them_all() =>
        Assert.Equal(2, Occlusion.CoveredMonitors(Both, new[] { Win(new Rectangle(0, 0, 3200, 1080)) }).Count);

    [Theory]
    [InlineData(false, false, false, false)] // hidden
    [InlineData(true, true, false, false)]   // minimized (Windows parks it off screen, but don't rely on that)
    [InlineData(true, false, true, false)]   // on another virtual desktop
    [InlineData(true, false, false, true)]   // click-through overlay
    public void Windows_that_do_not_hide_the_desktop_are_skipped(bool visible, bool minimized, bool cloaked, bool clickThrough) =>
        Assert.Empty(Occlusion.CoveredMonitors(Both, new[] { Win(MaximizedOnMain, visible, minimized, cloaked, clickThrough) }));

    [Fact]
    public void Umbras_own_and_the_shells_desktop_windows_never_cover() =>
        Assert.Empty(Occlusion.CoveredMonitors(Both, new[] { Win(Main.Bounds, ignored: true), Win(Side.Bounds, ignored: true) }));

    [Fact]
    public void Without_a_work_area_the_whole_monitor_must_be_covered()
    {
        var bare = Main with { WorkArea = Rectangle.Empty };
        Assert.False(Occlusion.Covers(new Rectangle(0, 0, 1920, 1040), bare));
        Assert.True(Occlusion.Covers(Main.Bounds, bare));
    }

    [Fact]
    public void Covered_widgets_stop_drawing_except_while_arranging_peeking_or_capturing()
    {
        Assert.True(Occlusion.ShouldDraw(covered: false, editMode: false, peeking: false, capturing: false));
        Assert.False(Occlusion.ShouldDraw(covered: true, editMode: false, peeking: false, capturing: false));
        Assert.True(Occlusion.ShouldDraw(covered: true, editMode: true, peeking: false, capturing: false));
        Assert.True(Occlusion.ShouldDraw(covered: true, editMode: false, peeking: true, capturing: false));
        Assert.True(Occlusion.ShouldDraw(covered: true, editMode: false, peeking: false, capturing: true));
    }
}
