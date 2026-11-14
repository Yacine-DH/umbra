using System.Drawing;

namespace Umbra.Linux.Tests;

/// <summary>GNOME Wayland pinning through the Shell extension (#187): window tokens, call arguments, replies, coordinates, the Settings hint.</summary>
public sealed class GnomeShellTests
{
    [Fact]
    public void Tokens_are_128_random_bits_in_lowercase_hex()
    {
        var a = GnomeShell.NewToken();
        var b = GnomeShell.NewToken();
        Assert.True(GnomeShell.IsToken(a));
        Assert.Matches("^[0-9a-f]{32}$", a);
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0123456789abcdef0123456789abcde")]   // 31
    [InlineData("0123456789abcdef0123456789abcdef0")] // 33
    [InlineData("0123456789ABCDEF0123456789abcdef")]  // upper case
    [InlineData("0123456789abcdef0123456789abcdeg")]
    [InlineData("0123456789abcdef 123456789abcdef")]
    public void Anything_else_is_not_a_token(string? s) => Assert.False(GnomeShell.IsToken(s));

    [Fact]
    public void The_title_carries_the_token_and_nothing_else()
    {
        var t = GnomeShell.NewToken();
        Assert.Equal("umbra:" + t, GnomeShell.Title(t));
        Assert.Equal(t, GnomeShell.TokenFromTitle("umbra:" + t));
        Assert.Null(GnomeShell.TokenFromTitle("Umbra - Clock"));
        Assert.Null(GnomeShell.TokenFromTitle("umbra:" + t + " "));
        Assert.Null(GnomeShell.TokenFromTitle("xumbra:" + t));
        Assert.Null(GnomeShell.TokenFromTitle("UMBRA:" + t));
        Assert.Null(GnomeShell.TokenFromTitle(null));
        Assert.Throws<ArgumentException>(() => GnomeShell.Title("not-a-token"));
    }

    [Fact]
    public void Call_arguments_are_well_formed_gvariant_text()
    {
        var t = new string('a', 32);
        Assert.Equal($"('{t}', 'widget')", GnomeShell.AddArgs(t, overlay: false));
        Assert.Equal($"('{t}', 'overlay')", GnomeShell.AddArgs(t, overlay: true));
        Assert.Equal($"('{t}',)", GnomeShell.TokenArgs(t));
        Assert.Equal($"('{t}', -20, 300, 0, 0)", GnomeShell.MoveArgs(t, new Point(-20, 300)));
        Assert.Equal($"('{t}', true)", GnomeShell.FocusArgs(t, true));
        Assert.Throws<ArgumentException>(() => GnomeShell.MoveArgs("x', 1, 2, 3, 4) --", Point.Empty));
    }

    [Fact]
    public void Only_this_protocol_version_is_used()
    {
        Assert.True(GnomeShell.Compatible(1));
        Assert.False(GnomeShell.Compatible(2));
        Assert.False(GnomeShell.Compatible(0));
        Assert.False(GnomeShell.Compatible(null));
    }

    [Fact]
    public void The_pointer_is_unknown_unless_the_extension_says_so()
    {
        Assert.Null(GnomeShell.Pointer(false, new Point(5, 5), 0x100));
        Assert.Equal(new PointerState(new Point(5, 6), true, false, true), GnomeShell.Pointer(true, new Point(5, 6), 0x100 | 4));
        Assert.Equal(new PointerState(new Point(0, 0), false, true, false), GnomeShell.Pointer(true, Point.Empty, 1));
        Assert.True(GnomeShell.Pointer(true, Point.Empty, 1 << 10)!.Value.ButtonDown); // button 3
    }

    [Fact]
    public void The_primary_work_area_comes_from_the_primary_monitor()
    {
        var monitors = new[]
        {
            new GnomeShell.Monitor(new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 32, 1920, 1048)),
            new GnomeShell.Monitor(new Rectangle(1920, 0, 1280, 1024), new Rectangle(1920, 0, 1280, 1024)),
        };
        Assert.Equal(new Rectangle(0, 32, 1920, 1048), GnomeShell.PrimaryWorkArea(0, monitors));
        Assert.Equal(new Rectangle(1920, 0, 1280, 1024), GnomeShell.PrimaryWorkArea(1, monitors));
        Assert.Null(GnomeShell.PrimaryWorkArea(-1, monitors));
        Assert.Null(GnomeShell.PrimaryWorkArea(2, monitors));
        Assert.Null(GnomeShell.PrimaryWorkArea(0, [new(new Rectangle(0, 0, 100, 100), new Rectangle(500, 500, 10, 10))]));
    }

    [Fact]
    public void Points_map_between_logical_monitors_and_the_stage()
    {
        // A 2x monitor laid out in physical pixels: GDK says 1280x720 logical, the stage 2560x1440.
        Rectangle[] gdk = [new(0, 0, 1280, 720), new(1280, 0, 1920, 1080)];
        Rectangle[] stage = [new(0, 0, 2560, 1440), new(2560, 0, 1920, 1080)];
        Assert.Equal(new Point(200, 100), GnomeShell.Map(new Point(100, 50), gdk, stage));
        Assert.Equal(new Point(100, 50), GnomeShell.Map(new Point(200, 100), stage, gdk));
        Assert.Equal(new Point(2660, 40), GnomeShell.Map(new Point(1380, 40), gdk, stage));
        // Same layout, or monitors that do not line up: unchanged.
        Assert.Equal(new Point(7, 9), GnomeShell.Map(new Point(7, 9), gdk, gdk));
        Assert.Equal(new Point(7, 9), GnomeShell.Map(new Point(7, 9), gdk, [stage[0]]));
        Assert.Equal(new Point(7, 9), GnomeShell.Map(new Point(7, 9), [], []));
    }

    [Theory]
    // desktop, session, backend, installed, dismissed -> offer
    [InlineData("ubuntu:GNOME", "wayland", "X11", false, false, "Install")]
    [InlineData("GNOME", "wayland", "X11", true, false, "Relogin")]
    [InlineData("GNOME", "wayland", "X11", false, true, "None")]           // shown once already, or "Not now"
    [InlineData("GNOME", "wayland", "GnomeExtension", false, false, "None")] // the extension already pins
    [InlineData("GNOME", "x11", "X11", false, false, "None")]              // GNOME on Xorg needs nothing
    [InlineData("KDE", "wayland", "X11", false, false, "None")]
    [InlineData(null, null, "X11", false, false, "None")]
    public void Settings_offers_the_extension_only_where_it_helps(string? desktop, string? session, string backend, bool installed, bool dismissed, string expected) =>
        Assert.Equal(expected, GnomeShell.OfferFor(desktop, session, Enum.Parse<PinBackend>(backend), installed, dismissed).ToString());
}
