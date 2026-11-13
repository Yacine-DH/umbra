namespace Umbra.Linux.Tests;

/// <summary>The pure rules of the session watcher (#193): the portal accent and clock-jump detection.</summary>
public sealed class SessionTests
{
    [Fact]
    public void Portal_accent_becomes_a_readable_pair()
    {
        var blue = LinuxSession.AccentFromPortal(0.2078, 0.5176, 0.8941)!.Value;
        Assert.Equal("#3584e4", blue.Accent);
        Assert.NotEqual(blue.Accent, blue.Strong);
        var dark = LinuxSession.AccentFromPortal(0.1, 0.1, 0.2)!.Value;
        Assert.NotEqual("#1a1a33", dark.Accent); // lightened for true black
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(0, 2, 0)]
    [InlineData(double.NaN, 0, 0)]
    public void Unset_or_bad_portal_values_give_no_accent(double r, double g, double b) =>
        Assert.Null(LinuxSession.AccentFromPortal(r, g, b));

    [Fact]
    public void Only_a_real_jump_counts_as_a_clock_change()
    {
        Assert.False(LinuxSession.ClockJumped(TimeSpan.FromSeconds(5.3), TimeSpan.FromSeconds(5)));
        Assert.True(LinuxSession.ClockJumped(TimeSpan.FromSeconds(3605), TimeSpan.FromSeconds(5)));
        Assert.True(LinuxSession.ClockJumped(TimeSpan.FromSeconds(-60), TimeSpan.FromSeconds(5)));
    }
}
