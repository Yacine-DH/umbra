namespace Umbra.Linux.Tests;

/// <summary>The host-drawn widget menu (#360): widget-controlled names as menu labels.</summary>
public sealed class WidgetMenuTests
{
    [Theory]
    [InlineData("Clock", "Clock")]
    [InlineData("my_widget", "my__widget")]          // "_" is a mnemonic in a GtkPopoverMenu label
    [InlineData("a\nb\tc", "abc")]                   // no control characters
    public void MenuText_escapes_mnemonics_and_strips_control_characters(string name, string label) =>
        Assert.Equal(label, WidgetWindow.MenuText(name));

    [Fact]
    public void MenuText_caps_long_names() => Assert.Equal(new string('x', 60) + "...", WidgetWindow.MenuText(new string('x', 500)));
}
