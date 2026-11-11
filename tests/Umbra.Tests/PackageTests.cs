extern alias cli;

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommandLine = cli::Umbra.Cli.CommandLine;
using McpServer = cli::Umbra.Cli.McpServer;
using DevLoop = cli::Umbra.Cli.DevLoop;

namespace Umbra.Tests;

/// <summary>The .umbrawidget package (Community.WritePackage / PrepareLocal) and the manifest contract (manifestVersion, minUmbraVersion).</summary>
public class PackageTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), $"umbra-pkg-tests-{Guid.NewGuid():N}");

    public PackageTests() { TestEnv.Init(); Directory.CreateDirectory(_dir); }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    static string Manifest(string id, string extra = "") =>
        $$"""{ "id": "{{id}}", "name": "Pkg {{id}}", "version": "1.0.0"{{extra}} }""";

    string Folder(string id, string manifest, params (string Name, string Content)[] files)
    {
        var dir = Path.Combine(_dir, Guid.NewGuid().ToString("N"), id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "widget.json"), manifest);
        File.WriteAllText(Path.Combine(dir, "index.html"), "<!doctype html><p>hi</p>");
        foreach (var (name, content) in files)
        {
            var p = Path.Combine(dir, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, content);
        }
        return dir;
    }

    static string Installed(string id) => Path.Combine(AppPaths.CommunityDir, id);

    [Fact]
    public void Missing_manifestVersion_means_1_and_is_compatible()
    {
        var m = WidgetManifest.Parse(Manifest("mv-missing"));
        Assert.Equal(1, m.ManifestVersion);
        Assert.Null(m.MinUmbraVersion);
        Assert.Null(m.Incompatibility());
    }

    [Theory]
    [InlineData(""", "manifestVersion": 0""")]
    [InlineData(", \"minUmbraVersion\": \"2.0\"")]
    [InlineData(", \"minUmbraVersion\": \"v2.0.0\"")]
    public void Bad_contract_fields_are_rejected(string extra) =>
        Assert.Throws<JsonException>(() => WidgetManifest.Parse(Manifest("mv-bad", extra)));

    [Fact]
    public void Newer_manifestVersion_or_minUmbraVersion_asks_to_update_Umbra()
    {
        var newer = WidgetManifest.Incompatibility(WidgetManifest.SupportedManifestVersion + 1, null, "1.8.0");
        Assert.Contains("Update Umbra", newer);
        var min = WidgetManifest.Incompatibility(1, "1.9.0", "1.8.0");
        Assert.Contains("1.9.0", min);
        Assert.Contains("Update Umbra", min);
        Assert.Null(WidgetManifest.Incompatibility(1, "1.8.0", "1.8.0"));
        Assert.Null(WidgetManifest.Incompatibility(1, "1.7.9", "1.8.0"));
    }

    [Fact]
    public void Install_of_a_widget_for_a_newer_Umbra_is_refused_and_writes_nothing()
    {
        foreach (var (id, extra) in new[] { ("mv-two", """, "manifestVersion": 2"""), ("mv-min", """, "minUmbraVersion": "999.0.0" """) })
        {
            var ex = Assert.Throws<InvalidOperationException>(() => Community.PrepareLocal(Folder(id, Manifest(id, extra))));
            Assert.Contains("Update Umbra", ex.Message);
            Assert.False(Directory.Exists(Installed(id)));
        }
    }

    [Fact]
    public void Discover_skips_a_widget_for_a_newer_Umbra()
    {
        var root = Path.Combine(_dir, "discover");
        foreach (var (id, extra) in new[] { ("mv-ok", ""), ("mv-future", """, "manifestVersion": 9""") })
        {
            Directory.CreateDirectory(Path.Combine(root, id));
            File.WriteAllText(Path.Combine(root, id, "widget.json"), Manifest(id, extra));
        }
        var found = WidgetManifest.Discover(root, WidgetSource.Community).Select(m => m.Id).ToList();
        Assert.Equal(new[] { "mv-ok" }, found);
    }

    [Fact]
    public void Catalog_entries_for_a_newer_Umbra_are_flagged_and_never_offered_as_updates()
    {
        Assert.Null(Community.CatalogIncompatibility(JsonNode.Parse("""{ "id": "x", "version": "2.0.0" }""")));
        var future = JsonNode.Parse("""{ "id": "x", "version": "2.0.0", "minUmbraVersion": "999.0.0" }""");
        Assert.Contains("Update Umbra", Community.CatalogIncompatibility(future));
        var installed = new WidgetManifest { Id = "x", Version = "1.0.0", Source = WidgetSource.Community };
        Assert.False(Community.IsUpdate(future, installed));
        Assert.True(Community.IsUpdate(JsonNode.Parse("""{ "id": "x", "version": "2.0.0", "manifestVersion": 1 }"""), installed));
    }

    [Fact]
    public void Every_catalog_widget_declares_a_supported_manifestVersion()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "Umbra.slnx"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        foreach (var file in new[] { "widgets", "private-widgets" }.SelectMany(d => Directory.GetFiles(Path.Combine(root!, d), "widget.json", SearchOption.AllDirectories)))
        {
            var m = WidgetManifest.Parse(File.ReadAllText(file));
            Assert.Equal(1, m.ManifestVersion);
            Assert.True(JsonNode.Parse(File.ReadAllText(file))!["manifestVersion"] is not null, $"{file} has no manifestVersion");
            Assert.Null(m.Incompatibility());
        }
    }

    [Fact]
    public void Pack_is_deterministic_and_round_trips_through_install()
    {
        var id = "pkg-round";
        var folder = Folder(id, Manifest(id), ("b/z.css", "z{}"), ("a.js", "1"), ("img/p.svg", "<svg/>"));
        var first = Path.Combine(_dir, "first" + Community.PackageExtension);
        var again = Path.Combine(_dir, "again" + Community.PackageExtension);
        Community.WritePackage(folder, first);
        // A different mtime on every file must not change the bytes.
        foreach (var f in Directory.GetFiles(folder, "*", SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-400));
        Thread.Sleep(20);
        Community.WritePackage(folder, again);
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(again));

        using (var zip = ZipFile.OpenRead(first))
        {
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Equal(names.Order(StringComparer.Ordinal), names);
            Assert.Contains("widget.json", names); // at the root, no wrapper folder
        }

        using (var p = Community.PrepareLocal(first)) { p.Require(accepted: false); p.Commit(); }
        var repacked = Path.Combine(_dir, "repacked" + Community.PackageExtension);
        Community.WritePackage(Installed(id), repacked);
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(repacked));
        Community.Remove(id);
    }

    [Fact]
    public void Pack_refuses_a_bad_folder_and_writes_no_file()
    {
        var id = "pkg-bad";
        var folder = Folder(id, Manifest(id), ("run.exe", "MZ"));
        var outPath = Path.Combine(_dir, "bad" + Community.PackageExtension);
        Assert.Throws<InvalidOperationException>(() => Community.WritePackage(folder, outPath));
        Assert.Empty(Directory.GetFiles(_dir, "bad*"));

        var wrongId = Folder("pkg-other", Manifest("pkg-mismatch"));
        Assert.Throws<InvalidOperationException>(() => Community.WritePackage(wrongId, outPath));
        Assert.False(File.Exists(outPath));
    }

    [Fact]
    public void A_umbrawidget_package_must_have_widget_json_at_the_root()
    {
        var id = "pkg-wrapped";
        var path = Path.Combine(_dir, "wrapped" + Community.PackageExtension);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (var (name, content) in new[] { ($"{id}/widget.json", Manifest(id)), ($"{id}/index.html", "") })
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(Encoding.UTF8.GetBytes(content));
            }
        var ex = Assert.Throws<InvalidOperationException>(() => Community.PrepareLocal(path));
        Assert.Contains("wrapper", ex.Message);
        Assert.False(Directory.Exists(Installed(id)));

        // The same layout as a plain .zip is still accepted (Send to > Compressed folder).
        var asZip = Path.ChangeExtension(path, ".zip");
        File.Copy(path, asZip);
        using var ok = Community.PrepareLocal(asZip);
        Assert.Equal(id, ok.Manifest.Id);
    }

    string Package(string name, params (string Name, byte[] Data, int UnixMode)[] entries)
    {
        var path = Path.Combine(_dir, name + Community.PackageExtension);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (var (n, data, mode) in entries)
            {
                var e = zip.CreateEntry(n);
                if (mode != 0) e.ExternalAttributes = mode << 16;
                using var s = e.Open();
                s.Write(data);
            }
        return path;
    }

    static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    public static TheoryData<string> Hostile => new() { "zipslip", "backslash", "absolute", "drive", "symlink", "duplicate", "bomb", "badext", "toomany" };

    [Theory]
    [MemberData(nameof(Hostile))]
    public void Hostile_umbrawidget_packages_are_refused_and_write_nothing(string kind)
    {
        var id = "pkg-evil";
        var good = new[] { ("widget.json", B(Manifest(id)), 0), ("index.html", B("<p/>"), 0) };
        var entries = kind switch
        {
            "zipslip" => good.Append(("../evil.html", B("x"), 0)),
            "backslash" => good.Append((@"..\evil.html", B("x"), 0)),
            "absolute" => good.Append(("/evil.html", B("x"), 0)),
            "drive" => good.Append(("C:/evil.html", B("x"), 0)),
            "symlink" => good.Append(("link.html", B("../../secret"), 0xA000 | 0x1FF)),
            "duplicate" => good.Append(("index.html", B("again"), 0)),
            "bomb" => good.Append(("big.txt", new byte[6 * 1024 * 1024], 0)), // compresses to a few KB, expands past 5 MB
            "badext" => good.Append(("run.exe", B("MZ"), 0)),
            _ => good.Concat(Enumerable.Range(0, 1001).Select(i => ($"f{i}.txt", B("x"), 0))),
        };
        var path = Package("evil-" + kind, entries.ToArray());
        Assert.True(new FileInfo(path).Length < 5 * 1024 * 1024 || kind != "bomb", "the bomb must be small on disk");
        Assert.Throws<InvalidOperationException>(() => Community.PrepareLocal(path));
        Assert.False(Directory.Exists(Installed(id)));
        Assert.False(File.Exists(Path.Combine(_dir, "evil.html")));
    }

    [Fact]
    public void Validate_reports_errors_and_warnings_without_installing()
    {
        var id = "lint-me";
        var folder = Folder(id, Manifest(id, ", \"bogus\": 1, \"options\": { \"a\": { \"type\": \"slider\" } }, \"permissions\": { \"hosts\": [\"https://x.com\"], \"sudo\": true }"),
            ("style.css", ".x { font-size: 9px }"), ("main.js", "window.chrome.webview.postMessage(1)"));
        var r = WidgetLint.Run(folder);
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Contains("bogus"));
        Assert.Contains(r.Errors, e => e.Contains("slider"));
        Assert.Contains(r.Errors, e => e.Contains("https://x.com"));
        Assert.Contains(r.Errors, e => e.Contains("sudo"));
        Assert.Contains(r.Warnings, w => w.Contains("9px"));
        Assert.Contains(r.Warnings, w => w.Contains("chrome.webview"));
        Assert.Contains(r.Warnings, w => w.Contains("preview"));
        Assert.False(Directory.Exists(Installed(id)));

        var bad = WidgetLint.Run(Folder("lint-newer", Manifest("lint-newer", ", \"manifestVersion\": 2")));
        Assert.Contains(bad.Errors, e => e.Contains("Update Umbra"));
        var noEntry = WidgetLint.Run(Folder("lint-entry", Manifest("lint-entry", ", \"entry\": \"nope.html\"")));
        Assert.Contains(noEntry.Errors, e => e.Contains("nope.html"));
    }

    [Fact]
    public void Validate_passes_a_clean_widget_and_a_packed_file()
    {
        var id = "lint-clean";
        var folder = Folder(id, Manifest(id, ", \"description\": \"d\", \"author\": \"a\", \"preview\": \"preview.svg\""), ("preview.svg", "<svg/>"));
        var r = WidgetLint.Run(folder);
        Assert.True(r.Ok, string.Join("; ", r.Errors));
        var pkg = Path.Combine(_dir, "clean" + Community.PackageExtension);
        Community.WritePackage(folder, pkg);
        Assert.True(WidgetLint.Run(pkg).Ok);
    }

    [Fact]
    public void Skipped_widgets_are_listed_and_not_pruned()
    {
        var root = Path.Combine(_dir, "skipped");
        Directory.CreateDirectory(Path.Combine(root, "future-x"));
        File.WriteAllText(Path.Combine(root, "future-x", "widget.json"), Manifest("future-x", ", \"minUmbraVersion\": \"999.0.0\""));
        var skipped = new List<(string Id, string Name, string Why)>();
        Assert.Empty(WidgetManifest.Discover(root, WidgetSource.Community, skipped));
        var s = Assert.Single(skipped);
        Assert.Equal("future-x", s.Id);
        Assert.Contains("999.0.0", s.Why);
        // App.ForgetOrphans counts these ids as known, so extra copies and their data are kept.
        Assert.Empty(Restore.Orphans(new[] { "future-x#2" }, new[] { "future-x" }, new HashSet<string>()));
    }

    [Fact]
    public void Catalog_flags_a_malformed_manifestVersion_as_invalid_not_1()
    {
        Assert.Contains("invalid", Community.CatalogIncompatibility(JsonNode.Parse("""{ "id": "x", "manifestVersion": "two" }""")));
        Assert.Contains("invalid", Community.CatalogIncompatibility(JsonNode.Parse("""{ "id": "x", "manifestVersion": 0 }""")));
        Assert.Contains("invalid", Community.CatalogIncompatibility(JsonNode.Parse("""{ "id": "x", "manifestVersion": null }""")));
        Assert.Throws<JsonException>(() => WidgetManifest.Parse(Manifest("mv-str", ", \"manifestVersion\": \"two\"")));
    }

    [Fact]
    public void AppVersion_is_never_the_zero_fallback() =>
        Assert.NotEqual("0.0.0", WidgetManifest.AppVersion);

    [Fact]
    public async Task Validate_pack_and_dev_map_to_full_paths_and_stay_available_over_mcp_safely()
    {
        var (cmd, a) = await CommandLine.TranslateAsync(new[] { "validate", "w" }, (_, _) => Task.FromResult(new JsonObject()));
        Assert.Equal("validate-widget", cmd);
        Assert.Equal(Path.GetFullPath("w"), a!["path"]!.GetValue<string>());
        var (c2, b) = await CommandLine.TranslateAsync(new[] { "pack", "-o", "o.umbrawidget", "w" }, (_, _) => Task.FromResult(new JsonObject()));
        Assert.Equal("pack-widget", c2);
        Assert.Equal(Path.GetFullPath("w"), b!["path"]!.GetValue<string>());
        Assert.Equal(Path.GetFullPath("o.umbrawidget"), b["out"]!.GetValue<string>());
        var (_, d) = await CommandLine.TranslateAsync(new[] { "pack" }, (_, _) => Task.FromResult(new JsonObject()));
        Assert.Equal(Path.GetFullPath("."), d!["path"]!.GetValue<string>());
        Assert.Null(d["out"]);
        Assert.Contains("validate-widget", McpServer.ToolCommands);
        Assert.Contains("pack-widget", McpServer.ToolCommands);
    }

    [Fact]
    public void Dev_loop_shows_only_the_widgets_blocked_requests_and_errors_and_only_new_lines()
    {
        Assert.True(DevLoop.IsRelevant("12:00 [clock] sandbox blocked https://evil.com/", "clock"));
        Assert.True(DevLoop.IsRelevant("12:00 [clock#2] blocked fetch: https://x.com", "clock"));
        Assert.False(DevLoop.IsRelevant("12:00 [clock-two] sandbox blocked https://evil.com/", "clock"));
        Assert.False(DevLoop.IsRelevant("12:00 [clock] notify: hi", "clock"));
        // The first read replays no history; later reads give only lines past the overlap with the previous window (also after rotation).
        Assert.Empty(DevLoop.Fresh(null, new[] { "a", "b" }));
        Assert.Equal(new[] { "c" }, DevLoop.Fresh(new[] { "a", "b" }, new[] { "a", "b", "c" }));
        Assert.Equal(new[] { "d" }, DevLoop.Fresh(new[] { "a", "b", "c" }, new[] { "b", "c", "d" }));
        Assert.Empty(DevLoop.Fresh(new[] { "a", "b" }, new[] { "a", "b" }));
        Assert.Equal(new[] { "x", "y" }, DevLoop.Fresh(new[] { "a", "b" }, new[] { "x", "y" }));
    }

    [Fact]
    public async Task Dev_loop_survives_an_unreachable_umbra()
    {
        var calls = 0;
        var output = new StringWriter();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(2500));
        var folder = Folder("dev-retry", Manifest("dev-retry"));
        await DevLoop.RunAsync(folder, false, (c, a) =>
        {
            calls++;
            if (calls <= 2) throw new TimeoutException("pipe gone");
            return Task.FromResult(new JsonObject { ["ok"] = true, ["result"] = new JsonArray() });
        }, output, cts.Token);
        var text = output.ToString();
        Assert.Contains("not reachable", text);
        Assert.Contains("Umbra is back", text);
        Assert.Equal(1, text.Split("not reachable").Length - 1); // reported once
    }

    [Fact]
    public void Camera_widgets_lint_clean_and_system_media_icon_values_are_checked()
    {
        var cam = WidgetLint.Run(Folder("cam-w", Manifest("cam-w", ", \"description\": \"d\", \"author\": \"a\", \"permissions\": { \"camera\": true }")));
        Assert.DoesNotContain(cam.Errors, e => e.Contains("unknown permission"));
        var bad = WidgetLint.Run(Folder("enum-w", Manifest("enum-w", ", \"icon\": \"../x.png\", \"permissions\": { \"system\": [\"processes\"], \"media\": [\"record\"] }")));
        Assert.False(bad.Ok);
        Assert.Contains(bad.Errors, e => e.Contains("processes")); // gpu and disk are valid kinds now; process lists never will be
        var bad2 = WidgetLint.Run(Folder("enum-x", Manifest("enum-x", ", \"permissions\": { \"media\": [\"record\"] }")));
        Assert.Contains(bad2.Errors, e => e.Contains("record"));
        var icon = WidgetLint.Run(Folder("icon-w", Manifest("icon-w", ", \"icon\": \"a b.png\"")));
        Assert.Contains(icon.Errors, e => e.Contains("icon"));
    }

    [Fact]
    public void Pack_widget_refuses_bad_requests_and_never_replaces_without_overwrite()
    {
        var id = "pack-ctl";
        var folder = Folder(id, Manifest(id));
        // relative paths and wrong extensions
        Assert.Contains("full path", Assert.Throws<InvalidOperationException>(() => WidgetLint.Pack("pack-ctl", null, false)).Message);
        Assert.Contains(".umbrawidget", Assert.Throws<InvalidOperationException>(() => WidgetLint.Pack(folder, Path.Combine(_dir, "x.zip"), false)).Message);
        Assert.Contains(".umbrawidget", Assert.Throws<InvalidOperationException>(() => WidgetLint.Pack(folder, "rel.umbrawidget", false)).Message);
        // output inside the folder
        Assert.Contains("outside", Assert.Throws<InvalidOperationException>(() => WidgetLint.Pack(folder, Path.Combine(folder, "self.umbrawidget"), false)).Message);
        Assert.False(File.Exists(Path.Combine(folder, "self.umbrawidget")));
        // existing file: kept unless overwrite
        var outPath = Path.Combine(_dir, "keep.umbrawidget");
        File.WriteAllText(outPath, "precious");
        Assert.Contains("already exists", Assert.Throws<InvalidOperationException>(() => WidgetLint.Pack(folder, outPath, false)).Message);
        Assert.Equal("precious", File.ReadAllText(outPath));
        var ok = WidgetLint.Pack(folder, outPath, true);
        Assert.Equal(id, ok["id"]!.GetValue<string>());
        Assert.True(WidgetLint.Run(outPath).Ok);
        // default output sits next to the folder
        var def = WidgetLint.Pack(folder, null, false);
        Assert.Equal(folder + ".umbrawidget", def["path"]!.GetValue<string>());
        // failing lint writes nothing
        var broken = Folder("pack-broken", Manifest("pack-broken", ", \"bogus\": 1"));
        var brokenOut = Path.Combine(_dir, "broken.umbrawidget");
        Assert.Contains("fix these first", Assert.Throws<InvalidOperationException>(() => WidgetLint.Pack(broken, brokenOut, false)).Message);
        Assert.False(File.Exists(brokenOut));
    }
}
