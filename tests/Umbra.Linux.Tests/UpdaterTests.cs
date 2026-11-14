namespace Umbra.Linux.Tests;

/// <summary>The notify-only update check on Linux (#197), against a fake release feed: no network.</summary>
public sealed class UpdaterTests
{
    const string Feed = "https://feed.test/latest";
    static void Quiet(string line) { } // the real Log is also redirected (TestEnv), but these lines go nowhere at all

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
            new(() => Enabled, () => Changed++, flatpak, (url, _) => { Asked.Add(url); return Task.FromResult(Json); }, Feed, "1.15.0", arch, Quiet);
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
    public async Task A_manual_check_during_the_automatic_one_waits_for_its_result()
    {
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var u = new LinuxUpdater(() => true, () => { }, false, (_, _) => { calls++; return gate.Task; }, Feed, "1.15.0", "x64", Quiet);
        var auto = u.CheckAsync(manual: false);
        var manual = u.CheckAsync(manual: true);
        Assert.False(manual.IsCompleted);
        gate.SetResult(Release("2.0.0"));
        await manual;
        await auto;
        Assert.Equal("2.0.0", u.Available?.Version);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_feed_over_one_megabyte_is_refused()
    {
        var huge = Release("2.0.0").Replace("Fixed things", new string('x', LinuxUpdater.MaxFeedBytes));
        var u = new LinuxUpdater(() => true, () => { }, false, (_, _) => Task.FromResult(huge), Feed, "1.15.0", "x64", Quiet);
        await u.CheckAsync(manual: true);
        Assert.Null(u.Available);
        Assert.NotNull(u.Error);
    }

    [Fact]
    public void The_daily_check_is_due_by_the_wall_clock()
    {
        var t = new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc);
        Assert.False(LinuxUpdater.IsDue(null, false, t));                       // the start check comes first
        Assert.False(LinuxUpdater.IsDue(t, false, t.AddHours(23)));
        Assert.True(LinuxUpdater.IsDue(t, false, t.AddHours(24)));
        Assert.True(LinuxUpdater.IsDue(t, false, t.AddDays(3)));                 // after a long sleep
        Assert.False(LinuxUpdater.IsDue(t, true, t.AddMinutes(59)));
        Assert.True(LinuxUpdater.IsDue(t, true, t.AddHours(1)));                 // a failed check retries hourly
    }

    [Fact]
    public async Task A_failed_check_reports_the_error_and_keeps_nothing()
    {
        var u = new LinuxUpdater(() => true, () => { }, false, (_, _) => throw new HttpRequestException("offline"), Feed, "1.15.0", "x64", Quiet);
        await u.CheckAsync(manual: true);
        Assert.Null(u.Available);
        Assert.Equal("offline", (string?)u.ToJson()["error"]);
    }
}
