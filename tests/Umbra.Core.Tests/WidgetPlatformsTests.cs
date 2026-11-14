using System.Text;
using System.Text.Json.Nodes;

namespace Umbra.Tests;

/// <summary>The optional <c>platforms</c> field: manifest parsing, the support policy with an injected OS, catalog filtering and the install gates.</summary>
public class WidgetPlatformsTests
{
    const string W = WidgetPlatforms.Windows, L = WidgetPlatforms.Linux;

    public WidgetPlatformsTests() => TestEnv.Init();

    static string Manifest(string platformsJson) =>
        $$"""{ "id": "demo", "name": "Demo", "description": "d", "author": "a", "version": "1.0.0"{{(platformsJson.Length == 0 ? "" : $", \"platforms\": {platformsJson}")}} }""";

    // ---------- manifest ----------

    [Fact]
    public void A_manifest_without_platforms_runs_everywhere()
    {
        var m = WidgetManifest.Parse(Manifest(""));
        Assert.Null(m.Platforms);
        Assert.True(m.SupportsPlatform(W));
        Assert.True(m.SupportsPlatform(L));
        Assert.True(m.SupportsPlatform(WidgetPlatforms.Other));
    }

    [Theory]
    [InlineData("""["windows"]""", true, false)]
    [InlineData("""["linux"]""", false, true)]
    [InlineData("""["windows", "linux"]""", true, true)]
    [InlineData("""["linux", "windows"]""", true, true)]
    public void Declared_platforms_decide_where_a_widget_runs(string json, bool onWindows, bool onLinux)
    {
        var m = WidgetManifest.Parse(Manifest(json));
        Assert.Equal(onWindows, m.SupportsPlatform(W));
        Assert.Equal(onLinux, m.SupportsPlatform(L));
        Assert.False(m.SupportsPlatform(WidgetPlatforms.Other)); // a restricted widget never runs on an unknown OS
    }

    [Theory]
    [InlineData("[]")]                          // would run nowhere
    [InlineData("""["macos"]""")]
    [InlineData("""["Windows"]""")]              // lowercase only
    [InlineData("""["windows", "windows"]""")]
    [InlineData("""["windows", ""]""")]
    [InlineData("""["windows", null]""")]
    [InlineData(""""windows"""")]               // not an array
    [InlineData("7")]
    public void Invalid_platforms_stop_the_widget_from_loading(string json) =>
        Assert.ThrowsAny<Exception>(() => WidgetManifest.Parse(Manifest(json)));

    [Fact]
    public void Discover_skips_widgets_for_another_platform()
    {
        var root = Path.Combine(TestEnv.DataDir, "plat-discover-" + Guid.NewGuid().ToString("N"));
        void Put(string id, string platforms)
        {
            Directory.CreateDirectory(Path.Combine(root, id));
            File.WriteAllText(Path.Combine(root, id, "widget.json"),
                $$"""{ "id": "{{id}}", "name": "N"{{(platforms.Length == 0 ? "" : $", \"platforms\": {platforms}")}} }""");
        }
        Put("both-ok", "");
        Put("win-only", """["windows"]""");
        Put("lin-only", """["linux"]""");

        Assert.Equal(new[] { "both-ok", "win-only" }, WidgetManifest.Discover(root, WidgetSource.Community, W).Select(m => m.Id).Order().ToArray());
        Assert.Equal(new[] { "both-ok", "lin-only" }, WidgetManifest.Discover(root, WidgetSource.Community, L).Select(m => m.Id).Order().ToArray());
        Assert.Equal(new[] { "both-ok" }, WidgetManifest.Discover(root, WidgetSource.Community, WidgetPlatforms.Other).Select(m => m.Id).ToArray());
    }

    // ---------- pure policy ----------

    [Fact]
    public void Supports_follows_the_declared_list()
    {
        Assert.True(WidgetPlatforms.Supports((IEnumerable<string>?)null, L));
        Assert.True(WidgetPlatforms.Supports(new[] { W }, W));
        Assert.False(WidgetPlatforms.Supports(new[] { W }, L));
        Assert.False(WidgetPlatforms.Supports(Array.Empty<string>(), W));
    }

    [Fact]
    public void Current_is_one_of_the_known_values_on_this_host()
    {
        Assert.Contains(WidgetPlatforms.Current, new[] { W, L, WidgetPlatforms.Other });
        Assert.Equal(OperatingSystem.IsWindows(), WidgetPlatforms.Current == W);
    }

    [Theory]
    [InlineData(new[] { "windows" }, "Windows only")]
    [InlineData(new[] { "linux" }, "Linux only")]
    [InlineData(new[] { "windows", "linux" }, null)]
    public void Label_flags_only_single_platform_widgets(string[] declared, string? expected) =>
        Assert.Equal(expected, WidgetPlatforms.Label(declared));

    [Fact]
    public void Label_is_null_when_nothing_is_declared() => Assert.Null(WidgetPlatforms.Label(null));

    [Fact]
    public void Require_names_the_widget_and_both_systems_in_the_error()
    {
        WidgetPlatforms.Require("ok", new[] { W }, W);
        WidgetPlatforms.Require("any", null, L);
        var ex = Assert.Throws<InvalidOperationException>(() => WidgetPlatforms.Require("script-runner", new[] { W }, L));
        Assert.Equal("'script-runner' is only for Windows, so it can't be installed on Linux.", ex.Message);
    }

    // ---------- catalog entries (remote data, read defensively) ----------

    static JsonObject Entry(JsonNode? platforms, string id = "demo")
    {
        var o = new JsonObject { ["id"] = id };
        if (platforms is not null) o["platforms"] = platforms;
        return o;
    }

    [Fact]
    public void A_catalog_entry_without_platforms_is_offered_everywhere()
    {
        Assert.True(WidgetPlatforms.Supports(Entry(null), W));
        Assert.True(WidgetPlatforms.Supports(Entry(null), L));
        Assert.True(WidgetPlatforms.Supports((JsonNode?)null, L));
    }

    [Fact]
    public void Catalog_filtering_hides_install_for_the_other_platform()
    {
        var windowsOnly = Entry(new JsonArray("windows"));
        var linuxOnly = Entry(new JsonArray("linux"));
        var both = Entry(new JsonArray("windows", "linux"));
        var catalog = new[] { windowsOnly, linuxOnly, both };

        Assert.Equal(2, catalog.Count(e => WidgetPlatforms.Supports(e, L)));
        Assert.DoesNotContain(windowsOnly, catalog.Where(e => WidgetPlatforms.Supports(e, L)));
        Assert.DoesNotContain(linuxOnly, catalog.Where(e => WidgetPlatforms.Supports(e, W)));
    }

    [Fact]
    public void A_malformed_catalog_platforms_value_never_throws()
    {
        Assert.True(WidgetPlatforms.Supports(Entry(JsonValue.Create("windows")), L));      // not an array: ignored, the install re-checks widget.json
        Assert.False(WidgetPlatforms.Supports(Entry(new JsonArray()), W));                 // an empty list supports nothing
        Assert.False(WidgetPlatforms.Supports(Entry(new JsonArray(1, null, "macos")), W)); // only string items count
        Assert.True(WidgetPlatforms.Supports(Entry(new JsonArray(1, null, "linux")), L));
    }

    // ---------- install gates ----------

    static Func<string, Task<byte[]>> Catalog(string id, string widgetJson, List<string>? fetched = null) => rel =>
    {
        fetched?.Add(rel);
        return Task.FromResult(Encoding.UTF8.GetBytes(rel == $"{id}/widget.json" ? widgetJson : "<html></html>"));
    };

    static JsonObject InstallEntry(string id, JsonNode? platforms = null)
    {
        var e = Entry(platforms, id);
        e["files"] = new JsonArray("widget.json", "index.html");
        return e;
    }

    static string UniqueId() => $"plat-{Guid.NewGuid():N}"[..12];

    [Fact]
    public async Task Prepare_refuses_by_catalog_entry_before_downloading_anything()
    {
        var id = UniqueId();
        var fetched = new List<string>();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Community.PrepareAsync(InstallEntry(id, new JsonArray("windows")), Catalog(id, Manifest(""), fetched), platform: L));
        Assert.Contains("only for Windows", ex.Message);
        Assert.Empty(fetched);
    }

    [Fact]
    public async Task Prepare_also_checks_the_downloaded_widget_json_whatever_the_index_says()
    {
        var id = UniqueId();
        // The index claims "both" (or nothing), the real manifest says Windows only.
        var json = $$"""{ "id": "{{id}}", "name": "N", "platforms": ["windows"] }""";
        await Assert.ThrowsAsync<InvalidOperationException>(() => Community.PrepareAsync(InstallEntry(id), Catalog(id, json), platform: L));
        Assert.False(Directory.Exists(Path.Combine(AppPaths.CommunityDir, id)));
        using var ok = await Community.PrepareAsync(InstallEntry(id), Catalog(id, json), platform: W);
        Assert.Equal(new[] { "windows" }, ok.Manifest.Platforms);
    }

    [Fact]
    public async Task An_unrestricted_widget_installs_on_either_platform()
    {
        var id = UniqueId();
        var json = $$"""{ "id": "{{id}}", "name": "N" }""";
        using var a = await Community.PrepareAsync(InstallEntry(id), Catalog(id, json), platform: L);
        using var b = await Community.PrepareAsync(InstallEntry(id), Catalog(id, json), platform: W);
        Assert.Null(a.Manifest.Platforms);
    }

    [Fact]
    public void A_local_install_of_an_incompatible_widget_fails_with_a_clear_error()
    {
        var id = UniqueId();
        var files = new Dictionary<string, string>
        {
            ["widget.json"] = $$"""{ "id": "{{id}}", "name": "N", "platforms": ["linux"] }""",
            ["index.html"] = "<html></html>",
        };
        var ex = Assert.Throws<InvalidOperationException>(() => Community.WriteLocalWidget(id, files, overwrite: false, platform: W));
        Assert.Equal($"'{id}' is only for Linux, so it can't be installed on Windows.", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(AppPaths.CommunityDir, id)));

        var dir = Community.WriteLocalWidget(id, files, overwrite: false, platform: L);
        Assert.True(File.Exists(Path.Combine(dir, "widget.json")));
        Community.Remove(id);
    }
}
