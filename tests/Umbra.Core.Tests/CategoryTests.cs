namespace Umbra.Tests;

/// <summary>widget.json "category": the fixed list Browse and the website filter by.</summary>
public class CategoryTests
{
    static WidgetLint.Result Lint(string? category)
    {
        var dir = Path.Combine(TestEnv.DataDir, "lint-" + Guid.NewGuid().ToString("N"), "catme"); // the folder name must equal the id
        Directory.CreateDirectory(dir);
        var cat = category is null ? "" : $"\"category\": \"{category}\",";
        File.WriteAllText(Path.Combine(dir, "widget.json"), $$"""{ "id": "catme", "name": "C", "description": "d", "author": "a", "version": "1.0.0", {{cat}} "permissions": { "hosts": [] } }""");
        File.WriteAllText(Path.Combine(dir, "index.html"), "<html></html>");
        return WidgetLint.Run(dir);
    }

    [Fact]
    public void A_known_category_is_accepted()
    {
        var r = Lint("games");
        Assert.DoesNotContain(r.Errors, e => e.Contains("category"));
        Assert.DoesNotContain(r.Warnings, e => e.Contains("category"));
    }

    [Fact]
    public void An_unknown_category_is_an_error() => Assert.Contains(Lint("toys").Errors, e => e.Contains("unknown category \"toys\""));

    [Fact]
    public void A_missing_category_is_only_a_warning()
    {
        var r = Lint(null);
        Assert.DoesNotContain(r.Errors, e => e.Contains("category"));
        Assert.Contains(r.Warnings, e => e.Contains("\"category\""));
    }

    [Fact]
    public void The_manifest_reads_it_and_the_list_matches_the_catalog_script()
    {
        Assert.Equal("time", System.Text.Json.JsonSerializer.Deserialize<WidgetManifest>("""{ "id": "x", "category": "time" }""", new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!.Category);
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "catalog.mjs"));
        Assert.Contains("const CATEGORIES = [" + string.Join(", ", WidgetManifest.Categories.Select(c => $"\"{c}\"")) + "];", script);
    }

    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Umbra.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("repo root");
    }
}
