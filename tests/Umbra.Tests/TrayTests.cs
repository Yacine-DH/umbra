namespace Umbra.Tests;

public class TrayTests
{
    [Fact]
    public void Tooltip_counts_widgets()
    {
        Assert.Equal("Umbra · 6 widgets", Tray.Tooltip(6, false, false, false));
        Assert.Equal("Umbra · 1 widget", Tray.Tooltip(1, false, false, false));
        Assert.Equal("Umbra · 0 widgets", Tray.Tooltip(0, false, false, false));
    }

    [Fact]
    public void Tooltip_counts_running_scripts()
    {
        Assert.Equal("Umbra · 2 widgets · 1 script running", Tray.Tooltip(2, false, false, false, scripts: 1));
        Assert.Equal("Umbra · 2 widgets · 3 scripts running · Edit mode", Tray.Tooltip(2, true, false, false, scripts: 3));
    }

    [Fact]
    public void Menu_text_is_one_short_line_without_access_keys()
    {
        Assert.Equal("Tom && Jerry", Tray.MenuText("Tom & Jerry"));
        Assert.Equal("a b", Tray.MenuText("a\nb"));
        Assert.Equal(60, Tray.MenuText(new string('x', 100)).Length);
    }

    [Fact]
    public void Tooltip_lists_modes_and_update_in_order()
    {
        Assert.Equal("Umbra · 3 widgets · Edit mode", Tray.Tooltip(3, true, false, false));
        Assert.Equal("Umbra · 3 widgets · Safe mode · Update ready", Tray.Tooltip(3, false, true, true));
        Assert.Equal("Umbra · 3 widgets · Edit mode · Safe mode · Update ready", Tray.Tooltip(3, true, true, true));
    }

    [Fact]
    public void Tooltip_never_exceeds_the_notifyicon_limit()
    {
        Assert.True(Tray.Tooltip(int.MaxValue, true, true, true).Length <= Tray.MaxTooltip);
        Assert.True(Tray.MaxTooltip <= 127);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void Badge_icon_has_the_requested_size_and_a_visible_dot(int px)
    {
        using var icon = Tray.WithBadge(App.AppIcon, new Size(px, px));
        Assert.Equal(px, icon.Width);
        Assert.Equal(px, icon.Height);
        using var bmp = icon.ToBitmap();
        var c = bmp.GetPixel(px - px / 4, px - px / 4); // inside the dot, bottom-right
        Assert.Equal(255, c.A);
        Assert.True(c.R > 200 && c.G > 100 && c.B < 100, $"expected the orange dot, got {c}");
    }
}
