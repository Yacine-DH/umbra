using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;

namespace Umbra.Tests;

/// <summary>The anonymous install counter: only a fresh catalog install, only while allowed, only the widget id.</summary>
public class InstallStatsTests
{
    static JsonObject Entry(string id) => new() { ["id"] = id, ["files"] = new JsonArray("widget.json", "index.html") };

    static Func<string, Task<byte[]>> Catalog(string id, string version) => rel => Task.FromResult(Encoding.UTF8.GetBytes(
        rel == $"{id}/widget.json" ? $$"""{ "id": "{{id}}", "name": "Count", "version": "{{version}}", "permissions": { "hosts": [] } }""" : "<html></html>"));

    /// <summary>Runs <paramref name="body"/> with counting allowed or not, returning the URLs it would have requested for <paramref name="id"/>.</summary>
    static async Task<List<string>> Capture(string id, bool allowed, Func<Task> body)
    {
        var sent = new ConcurrentQueue<string>();
        InstallStats.Enabled = () => allowed;
        InstallStats.Send = url => { sent.Enqueue(url); return Task.CompletedTask; };
        try
        {
            await body();
            await Task.Delay(300); // the report runs in the background
        }
        finally { InstallStats.Enabled = null; InstallStats.Send = null; }
        return sent.Where(u => u.EndsWith("=" + id, StringComparison.Ordinal)).ToList();
    }

    static async Task Install(string id, string version)
    {
        using var p = await Community.PrepareAsync(Entry(id), Catalog(id, version));
        p.Require(true);
        p.Commit();
    }

    [Fact]
    public async Task A_fresh_catalog_install_sends_only_the_widget_id_and_an_update_sends_nothing()
    {
        var id = $"cnt-{Guid.NewGuid():N}"[..12];
        try
        {
            var first = await Capture(id, true, () => Install(id, "1.0.0"));
            Assert.Equal(new[] { $"{InstallStats.Base}/stats/install?w={id}" }, first);
            Assert.Empty(await Capture(id, true, () => Install(id, "1.1.0")));
        }
        finally { Community.Remove(id); }
    }

    [Fact]
    public async Task Nothing_is_sent_when_the_user_turned_it_off_or_the_host_never_enabled_it()
    {
        var id = $"cnt-{Guid.NewGuid():N}"[..12];
        try { Assert.Empty(await Capture(id, false, () => Install(id, "1.0.0"))); }
        finally { Community.Remove(id); }

        Assert.Null(InstallStats.Enabled); // the default: tests and tools that use Core never report
        InstallStats.Installed("clock", fresh: true, local: false); // no hook: a no-op, no network
    }

    [Fact]
    public async Task Local_installs_and_invalid_ids_are_never_counted()
    {
        Assert.Empty(await Capture("clock", true, () => { InstallStats.Installed("clock", fresh: true, local: true); return Task.CompletedTask; }));
        Assert.Empty(await Capture("..", true, () => { InstallStats.Installed("..", fresh: true, local: false); return Task.CompletedTask; }));
    }

    [Fact]
    public void Counts_keep_only_valid_ids_and_whole_non_negative_numbers()
    {
        var map = InstallStats.Parse(JsonNode.Parse("""{ "widgets": { "clock": 12, "Bad Id": 3, "todo": -1, "x": 4, "weather": "9", "sp500": 1.5, "habits": 0 }, "since": "2026-11-12" }"""));
        Assert.Equal(new Dictionary<string, long> { ["clock"] = 12, ["habits"] = 0 }, map);
        Assert.Empty(InstallStats.Parse(JsonNode.Parse("""{ "error": "server error" }""")));
        Assert.Empty(InstallStats.Parse(null));
    }

    [Fact]
    public void Counting_defaults_on_and_survives_a_save()
    {
        var s = new Settings();
        Assert.True(s.General.CountInstalls);
        s.General.CountInstalls = false;
        Assert.False(Settings.FromJson(s.ToJson()).General.CountInstalls);
    }
}
