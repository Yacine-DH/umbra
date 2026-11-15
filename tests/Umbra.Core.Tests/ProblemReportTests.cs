using System.Text.Json.Nodes;

namespace Umbra.Tests;

/// <summary>Settings > Report a problem: only what the user typed plus version/OS/widget, scrubbed, rate-limited, never from automation.</summary>
[Collection("ProblemReport")]
public class ProblemReportTests
{
    static readonly ReportContext About = new("about");

    [Theory]
    [InlineData("", false)]
    [InlineData("   not too short   ", true)]
    [InlineData("short", false)]
    [InlineData("It broke.", false)]
    [InlineData("It broke!!", true)]
    public void Text_must_be_long_enough(string text, bool ok) => Assert.Equal(ok, ProblemReport.Validate(text) is null);

    [Fact]
    public void Text_length_counts_code_points_like_the_relay()
    {
        Assert.Null(ProblemReport.Validate(string.Concat(Enumerable.Repeat("😀", ProblemReport.MaxText))));
        Assert.NotNull(ProblemReport.Validate(string.Concat(Enumerable.Repeat("😀", ProblemReport.MaxText + 1))));
    }

    [Fact]
    public void The_payload_carries_only_text_version_os_place_and_widget()
    {
        var ctx = new ReportContext("crash", "clock", "1.2.0", "Renderer exited");
        var o = ProblemReport.Payload("  The clock froze.  ", ctx, "1.9.0", "Windows 11 (26200)", s => s);
        Assert.Equal(
            """{"text":"The clock froze.","version":"1.9.0","os":"Windows 11 (26200)","where":"crash","widget":"clock","widgetVersion":"1.2.0","error":"Renderer exited"}""",
            o.ToJsonString());
        var plain = ProblemReport.Payload("Something odd happened", About, "1.9.0", "Linux, KDE, wayland", s => s);
        Assert.Equal(["text", "version", "os", "where"], plain.Select(p => p.Key));
    }

    [Fact]
    public void The_error_text_loses_folders_names_and_url_queries()
    {
        var s = ProblemReport.Scrub(
            @"Can't read C:\Users\Jane Doe\AppData\Roaming\Umbra\x.json; D:\Users\other\a; /home/jane/.local/x; https://api.example.com/v1?key=SECRET#f by jdoe on JDOE-PC",
            @"C:\Users\Jane Doe", "jdoe", "JDOE-PC");
        Assert.DoesNotContain("Jane", s);
        Assert.DoesNotContain("other", s);
        Assert.DoesNotContain("/home/jane", s);
        Assert.DoesNotContain("SECRET", s);
        Assert.Contains("https://api.example.com/v1", s);
        Assert.Contains("by <user> on <machine>", s);
        Assert.Contains(@"~\AppData\Roaming\Umbra\x.json", s);
    }

    [Fact]
    public void Contexts_from_the_page_are_cleaned()
    {
        var c = ReportContext.From("somewhere", "../etc", "1.0.0", "\u0007  boom  ");
        Assert.Equal("about", c.Where);
        Assert.Null(c.Widget);
        Assert.Null(c.WidgetVersion);
        Assert.Equal("boom", c.Error);
        var w = ReportContext.From("crash", "clock", "v2", new string('e', 500));
        Assert.Equal(("crash", "clock", (string?)null), (w.Where, w.Widget, w.WidgetVersion));
        Assert.Equal(ProblemReport.MaxError, w.Error!.EnumerateRunes().Count());
        Assert.EndsWith("…", w.Error);
    }

    [Theory]
    [InlineData(true, 26200, null, null, "Windows 11 (26200)")]
    [InlineData(true, 19045, null, null, "Windows 10 (19045)")]
    [InlineData(false, 0, "ubuntu:GNOME", "wayland", "Linux, ubuntu/GNOME, wayland")]
    [InlineData(false, 0, "KDE", null, "Linux, KDE")]
    [InlineData(false, 0, "<b>", "", "Linux, b")]
    public void Os_text(bool windows, int build, string? desktop, string? session, string expected) =>
        Assert.Equal(expected, ProblemReport.OsText(windows, build, desktop, session));

    [Fact]
    public void Summary_names_the_widget()
    {
        Assert.Equal("Umbra 1.9.0 · Windows 11 (26200)", ProblemReport.Summary(About, "1.9.0", "Windows 11 (26200)", null));
        Assert.Equal("Umbra 1.9.0 · Linux · Clock 1.2.0", ProblemReport.Summary(new("crash", "clock", "1.2.0"), "1.9.0", "Linux", "Clock"));
    }

    [Fact]
    public async Task Sending_posts_json_to_the_relay_and_is_rate_limited()
    {
        var sent = new List<(string Url, string Body)>();
        var now = DateTimeOffset.Parse("2026-11-15T12:00:00Z");
        ProblemReport.ResetLimit();
        ProblemReport.Now = () => now;
        ProblemReport.Send = (url, body) => { sent.Add((url, body)); return Task.FromResult("""{"ok":true}"""); };
        try
        {
            Assert.Null(await ProblemReport.SendAsync("First report about it", About, "Linux"));
            Assert.NotNull(await ProblemReport.SendAsync("Second report right away", About, "Linux"));
            Assert.Single(sent);
            Assert.EndsWith("/stats/report", sent[0].Url);
            Assert.Equal("First report about it", JsonNode.Parse(sent[0].Body)!["text"]!.GetValue<string>());

            for (int i = 1; i < ProblemReport.PerDay; i++)
            {
                now += ProblemReport.Gap;
                Assert.Null(await ProblemReport.SendAsync("Another report " + i, About, "Linux"));
            }
            now += ProblemReport.Gap;
            Assert.NotNull(await ProblemReport.SendAsync("One too many today", About, "Linux"));
            now += TimeSpan.FromDays(1);
            Assert.Null(await ProblemReport.SendAsync("A new day, a new report", About, "Linux"));
            Assert.Equal(ProblemReport.PerDay + 1, sent.Count);

            Assert.NotNull(await ProblemReport.SendAsync("short", About, "Linux"));
            Assert.Equal(ProblemReport.PerDay + 1, sent.Count);
        }
        finally { ProblemReport.Send = null; ProblemReport.Now = () => DateTimeOffset.UtcNow; ProblemReport.ResetLimit(); }
    }

    [Fact]
    public async Task A_failed_send_does_not_use_up_the_limit()
    {
        ProblemReport.ResetLimit();
        var calls = 0;
        ProblemReport.Send = (_, _) => ++calls == 1 ? throw new HttpRequestException("offline") : Task.FromResult("""{"error":"bad os"}""");
        try
        {
            Assert.Contains("Couldn't reach", await ProblemReport.SendAsync("Offline the first time", About, "Linux"));
            Assert.Contains("bad os", await ProblemReport.SendAsync("Refused the second time", About, "Linux"));
            Assert.Equal(2, calls);
        }
        finally { ProblemReport.Send = null; ProblemReport.ResetLimit(); }
    }

    [Fact]
    public void Automation_cannot_send_a_report()
    {
        Assert.DoesNotContain(Protocol.Commands, c => c.Cmd.Contains("report", StringComparison.OrdinalIgnoreCase)
            || c.Cmd.Contains("feedback", StringComparison.OrdinalIgnoreCase));
    }
}
