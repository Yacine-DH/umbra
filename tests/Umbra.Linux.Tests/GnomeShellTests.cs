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
    public void Points_map_between_logical_monitors_and_the_stage_paired_by_connector()
    {
        // A 2x monitor laid out in physical pixels: GDK says 1280x720 logical, the stage 2560x1440. Mutter lists the
        // monitors in another order than GDK: the connector pairs them.
        GnomeShell.Output[] gdk = [new(new(0, 0, 1280, 720), "eDP-1"), new(new(1280, 0, 1920, 1080), "DP-2")];
        GnomeShell.Output[] stage = [new(new(2560, 0, 1920, 1080), "DP-2"), new(new(0, 0, 2560, 1440), "eDP-1")];
        Assert.Equal(new Point(200, 100), GnomeShell.Map(new Point(100, 50), gdk, stage));
        Assert.Equal(new Point(100, 50), GnomeShell.Map(new Point(200, 100), stage, gdk));
        Assert.Equal(new Point(2660, 40), GnomeShell.Map(new Point(1380, 40), gdk, stage));
        Assert.Equal(new Point(1380, 40), GnomeShell.Map(new Point(2660, 40), stage, gdk));
    }

    [Fact]
    public void Without_a_connector_to_pair_with_the_point_is_left_as_it_is()
    {
        GnomeShell.Output[] gdk = [new(new(0, 0, 1280, 720), "eDP-1")];
        Assert.Equal(new Point(7, 9), GnomeShell.Map(new Point(7, 9), gdk, [new(new(0, 0, 2560, 1440), "HDMI-1")]));
        Assert.Equal(new Point(7, 9), GnomeShell.Map(new Point(7, 9), gdk, [new(new(0, 0, 2560, 1440), "")]));
        Assert.Equal(new Point(7, 9), GnomeShell.Map(new Point(7, 9), [new(new(0, 0, 1280, 720), null)], [new(new(0, 0, 2560, 1440), null)]));
        Assert.Equal(new Point(5000, 9), GnomeShell.Map(new Point(5000, 9), gdk, [new(new(0, 0, 2560, 1440), "eDP-1")])); // off every monitor
        Assert.Equal(new Point(7, 9), GnomeShell.Map(new Point(7, 9), [], []));
    }

    [Theory]
    // desktop, session, backend, installed, dismissed, flatpak -> offer
    [InlineData("ubuntu:GNOME", "wayland", "X11", false, false, false, "Install")]
    [InlineData("GNOME", "wayland", "X11", true, false, false, "TurnOn")]          // disabled, just installed, or another protocol
    [InlineData("GNOME", "wayland", "X11", false, true, false, "None")]            // shown once already, or "Not now"
    [InlineData("GNOME", "wayland", "X11", false, false, true, "None")]            // Flatpak: the pid check cannot pass (#196)
    [InlineData("GNOME", "wayland", "GnomeExtension", false, false, false, "None")] // the extension already pins
    [InlineData("GNOME", "x11", "X11", false, false, false, "None")]               // GNOME on Xorg needs nothing
    [InlineData("KDE", "wayland", "X11", false, false, false, "None")]
    [InlineData(null, null, "X11", false, false, false, "None")]
    public void Settings_offers_the_extension_only_where_it_helps(string? desktop, string? session, string backend, bool installed, bool dismissed, bool flatpak, string expected) =>
        Assert.Equal(expected, GnomeShell.OfferFor(desktop, session, Enum.Parse<PinBackend>(backend), installed, dismissed, flatpak).ToString());

    [Theory]
    [InlineData("@as []\n", "['umbra@aminechr54.github.io']")]
    [InlineData("['ding@rastersoft.com', 'ubuntu-dock@ubuntu.com']\n", "['ding@rastersoft.com', 'ubuntu-dock@ubuntu.com', 'umbra@aminechr54.github.io']")]
    [InlineData("['a@b', \"it's@c\"]", "['a@b', 'it\\'s@c', 'umbra@aminechr54.github.io']")]
    [InlineData("['umbra@aminechr54.github.io']", null)] // already there: nothing written
    [InlineData("not a list", null)]
    [InlineData("['unterminated]", null)]
    public void Enabling_appends_to_the_users_list_and_keeps_the_rest(string current, string? expected) =>
        Assert.Equal(expected, GnomeShell.EnabledWith(current));

    [Fact]
    public void Enabling_takes_it_off_the_disabled_list()
    {
        Assert.Equal("['x@y']", GnomeShell.DisabledWithout("['umbra@aminechr54.github.io', 'x@y']"));
        Assert.Equal("@as []", GnomeShell.DisabledWithout("['umbra@aminechr54.github.io']"));
        Assert.Null(GnomeShell.DisabledWithout("['x@y']"));
        Assert.Null(GnomeShell.DisabledWithout("@as []"));
    }

    [Theory]
    [InlineData("GDBus.Error:org.freedesktop.DBus.Error.AccessDenied: token in use", true)]
    [InlineData("GDBus.Error:org.freedesktop.DBus.Error.LimitsExceeded: too many windows", false)]
    [InlineData(null, false)]
    public void A_token_held_by_someone_else_is_recognised(string? error, bool taken) => Assert.Equal(taken, GnomeShell.TokenTaken(error));
}
