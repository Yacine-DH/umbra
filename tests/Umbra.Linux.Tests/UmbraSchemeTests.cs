namespace Umbra.Linux.Tests;

/// <summary>The umbra:// path rules: only files under the two roots are served; everything else is a 404 (null).</summary>
public sealed class UmbraSchemeTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), $"umbra-scheme-{Guid.NewGuid():N}");
    readonly string _app, _community;

    public UmbraSchemeTests()
    {
        _app = Path.Combine(_root, "app");
        _community = Path.Combine(_root, "community");
        Directory.CreateDirectory(Path.Combine(_app, "shared"));
        Directory.CreateDirectory(Path.Combine(_community, "clock", "img"));
        File.WriteAllText(Path.Combine(_app, "shared", "umbra.js"), "//");
        File.WriteAllText(Path.Combine(_community, "clock", "index.html"), "<html>");
        File.WriteAllText(Path.Combine(_community, "clock", "img", "a b.png"), "x");
        File.WriteAllText(Path.Combine(_root, "secret.txt"), "secret");
    }

    public void Dispose() => Directory.Delete(_root, true);

    string? Resolve(string uri) => UmbraScheme.Resolve(uri, _app, _community);

    [Fact]
    public void Serves_files_under_each_root()
    {
        Assert.Equal(Path.Combine(_app, "shared", "umbra.js"), Resolve("umbra://umbra.local/shared/umbra.js"));
        Assert.Equal(Path.Combine(_community, "clock", "index.html"), Resolve("umbra://community.umbra.local/clock/index.html"));
        Assert.Equal(Path.Combine(_app, "shared", "umbra.js"), Resolve("UMBRA://Umbra.Local/shared/umbra.js"));
    }

    [Fact]
    public void Query_and_fragment_are_ignored_and_escapes_decoded()
    {
        Assert.Equal(Path.Combine(_app, "shared", "umbra.js"), Resolve("umbra://umbra.local/shared/umbra.js?v=3#top"));
        Assert.Equal(Path.Combine(_community, "clock", "img", "a b.png"), Resolve("umbra://community.umbra.local/clock/img/a%20b.png"));
    }

    [Theory]
    [InlineData("umbra://umbra.local/../secret.txt")]
    [InlineData("umbra://umbra.local/shared/../../secret.txt")]
    [InlineData("umbra://umbra.local/shared/%2e%2e/%2e%2e/secret.txt")]
    [InlineData("umbra://umbra.local/shared/..%2f..%2fsecret.txt")]
    [InlineData("umbra://umbra.local/shared/..\\..\\secret.txt")]
    [InlineData("umbra://umbra.local/shared/%5c..%5csecret.txt")]
    [InlineData("umbra://umbra.local//etc/passwd")]
    [InlineData("umbra://umbra.local/%2fetc/passwd")]
    [InlineData("umbra://umbra.local/shared/./umbra.js")]
    [InlineData("umbra://umbra.local/shared/umbra.js%00.png")]
    [InlineData("umbra://community.umbra.local/../app/shared/umbra.js")]
    public void Traversal_is_refused(string uri) => Assert.Null(Resolve(uri));

    [Theory]
    [InlineData("umbra://evil.example/shared/umbra.js")]
    [InlineData("umbra://umbra.local.evil.example/shared/umbra.js")]
    [InlineData("umbra:///shared/umbra.js")]
    [InlineData("umbra://umbra.local")]
    [InlineData("umbra://umbra.local/")]
    [InlineData("umbra://umbra.local/shared")]
    [InlineData("umbra://umbra.local/shared/")]
    [InlineData("umbra://umbra.local/shared/missing.js")]
    [InlineData("https://umbra.local/shared/umbra.js")]
    [InlineData("file:///etc/passwd")]
    public void Other_hosts_folders_and_missing_files_are_refused(string uri) => Assert.Null(Resolve(uri));

    [Fact]
    public void Links_out_of_the_root_are_refused()
    {
        if (OperatingSystem.IsWindows()) return; // creating links needs a privilege there
        File.CreateSymbolicLink(Path.Combine(_community, "clock", "leak.txt"), Path.Combine(_root, "secret.txt"));
        Directory.CreateSymbolicLink(Path.Combine(_community, "out"), _root);
        Assert.Null(Resolve("umbra://community.umbra.local/clock/leak.txt"));
        Assert.Null(Resolve("umbra://community.umbra.local/out/secret.txt"));
    }

    [Theory]
    [InlineData("index.html", "text/html")]
    [InlineData("umbra.JS", "text/javascript")]
    [InlineData("umbra.css", "text/css")]
    [InlineData("icon.png", "image/png")]
    [InlineData("data.json", "application/json")]
    [InlineData("font.woff2", "font/woff2")]
    [InlineData("unknown.bin", "application/octet-stream")]
    [InlineData("noextension", "application/octet-stream")]
    public void Content_types(string file, string expected) => Assert.Equal(expected, UmbraScheme.ContentType(file));

    [Fact]
    public void Page_urls_are_rewritten_to_the_scheme()
    {
        Assert.Equal("umbra://umbra.local/widgets/clock/index.html", UmbraScheme.FromHttps("https://umbra.local/widgets/clock/index.html"));
        Assert.Equal("umbra://community.umbra.local/clock/index.html", UmbraScheme.FromHttps("https://community.umbra.local/clock/index.html"));
        Assert.Equal("https://example.com/", UmbraScheme.FromHttps("https://example.com/"));
        Assert.True(UmbraScheme.IsLocal("umbra://umbra.local/shared/umbra.js"));
        Assert.True(UmbraScheme.IsLocal("https://community.umbra.local/x/index.html"));
        Assert.False(UmbraScheme.IsLocal("https://umbra.local.evil.example/x"));
        Assert.False(UmbraScheme.IsLocal("umbra://evil.example/x"));
    }
}
