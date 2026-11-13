namespace Umbra.Linux.Tests;

/// <summary>The notify-only update check on Linux (#197), against a fake release feed: no network.</summary>
public sealed class UpdaterTests
{
    const string Feed = "https://feed.test/latest";

    static string Release(string version, string arch = "x64") => $$"""
        { "tag_name": "v{{version}}", "draft": false, "prerelease": false, "body": "Fixed things",
          "assets": [ { "name": "umbra-{{version}}-linux-{{arch}}.tar.gz",
                        "browser_download_url": "https://github.com/AmineChr54/umbra/releases/download/v{{version}}/umbra-{{version}}-linux-{{arch}}.tar.gz" } ] }
        """;

    sealed class Fake
    {
        public string Json = Release("2.0.0");
        public List<string> Asked = new();
        public int Changed;
        public bool Enabled = true;
        public LinuxUpdater Make(bool flatpak = false, string arch = "x64") =>
            new(() => Enabled, () => Changed++, flatpak, (url, _) => { Asked.Add(url); return Task.FromResult(Json); }, Feed, "1.15.0", arch);
    }

    [Fact]
    public async Task A_newer_release_with_a_linux_tarball_is_announced()
    {
        var f = new Fake();
        var u = f.Make();
        await u.CheckAsync(manual: false);
        Assert.Equal([Feed], f.Asked);
        Assert.Equal("2.0.0", u.Available?.Version);
        Assert.Equal("https://github.com/AmineChr54/umbra/releases/tag/v2.0.0", u.Available?.PageUrl);
        Assert.Equal(1, f.Changed);
        var j = u.ToJson();
        Assert.Equal("available", (string?)j["button"]); // notify-only: never "downloading" or "ready"
        Assert.Equal("2.0.0", (string?)j["available"]?["version"]);
        Assert.False((bool)j["ready"]!);
        Assert.True((bool)j["supported"]!);
    }

    [Fact]
    public async Task Same_or_older_versions_and_releases_without_this_arch_are_not()
    {
        var f = new Fake { Json = Release("1.15.0") };
        var u = f.Make();
        await u.CheckAsync(manual: true);
        Assert.Null(u.Available);
        f.Json = Release("2.0.0", arch: "arm64");
        await u.CheckAsync(manual: true);
        Assert.Null(u.Available);
        Assert.Equal("none", (string?)u.ToJson()["button"]);
        Assert.NotNull(u.LastChecked);
    }

    [Fact]
    public async Task Automatic_checks_respect_the_switch_manual_ones_do_not()
    {
        var f = new Fake { Enabled = false };
        var u = f.Make();
        await u.CheckAsync(manual: false);
        Assert.Empty(f.Asked);
        await u.CheckAsync(manual: true);
        Assert.Single(f.Asked);
    }

    [Fact]
    public async Task Flatpak_never_checks()
    {
        var f = new Fake();
        var u = f.Make(flatpak: true);
        await u.CheckAsync(manual: true, download: true);
        Assert.Empty(f.Asked);
        Assert.Null(u.Available);
        var j = u.ToJson();
        Assert.False((bool)j["supported"]!);
        Assert.True((bool)j["flatpak"]!);
    }

    [Fact]
    public async Task A_failed_check_reports_the_error_and_keeps_nothing()
    {
        var u = new LinuxUpdater(() => true, () => { }, false, (_, _) => throw new HttpRequestException("offline"), Feed, "1.15.0", "x64");
        await u.CheckAsync(manual: true);
        Assert.Null(u.Available);
        Assert.Equal("offline", (string?)u.ToJson()["error"]);
    }
}
