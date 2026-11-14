namespace Umbra.Core.Tests;

/// <summary>The diagnostics log tail redaction, shared by the Windows and Linux hosts.</summary>
public sealed class LogScrubTests
{
    [Fact]
    public void Linux_homes_folders_urls_and_names_are_hidden()
    {
        var lines = new[]
        {
            "Installed from /home/alice/Downloads/w.umbrawidget",
            "data at /home/alice/.local/share/umbra/data/x.json",
            "fetch https://api.example.com/v1?key=SECRET#frag failed",
            "page https://example.com/home/page loaded",
            "host alice-laptop said hi to alice",
        };
        var roots = new[] { ("/home/alice/.local/share/umbra", "<data>"), ("/home/alice", "~") };
        var names = new[] { ("alice", "<user>"), ("alice-laptop", "<machine>") };
        var o = LogScrub.Scrub(lines, roots, names);
        Assert.Equal("Installed from ~/Downloads/w.umbrawidget", o[0]);
        Assert.Equal("data at <data>/data/x.json", o[1]);
        Assert.Equal("fetch https://api.example.com/v1 failed", o[2]);
        Assert.Equal("page https://example.com/home/page loaded", o[3]); // a URL path is not a home folder
        Assert.DoesNotContain("alice", o[4]);
    }

    [Fact]
    public void Another_users_home_is_hidden_and_long_lines_are_capped()
    {
        var o = LogScrub.Scrub(["open /home/bob/x and C:\\Users\\Bob\\y", new string('x', 1000)], []);
        Assert.Equal("open ~/x and %USERPROFILE%\\y", o[0]);
        Assert.Equal(LogScrub.MaxLineLength + 3, o[1].Length);
    }
}
