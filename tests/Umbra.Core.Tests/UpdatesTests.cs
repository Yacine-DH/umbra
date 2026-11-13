namespace Umbra.Tests;

public class UpdatesTests
{
    [Theory]
    [InlineData(false, false, false, null, false, "none")]
    [InlineData(false, true, true, 50, false, "none")]
    [InlineData(true, false, false, null, false, "available")]
    [InlineData(true, false, false, 40, false, "downloading")]
    [InlineData(true, false, true, null, false, "downloading")]
    [InlineData(true, true, false, null, false, "ready")]
    [InlineData(true, true, true, null, false, "ready")]
    [InlineData(true, false, false, null, true, "available")]
    [InlineData(true, true, false, null, true, "available")]
    public void ButtonState_follows_the_update_lifecycle(bool available, bool ready, bool installing, int? progress, bool portable, string expected) =>
        Assert.Equal(expected, Updates.ButtonState(available, ready, installing, progress, portable));

    [Theory]
    [InlineData("1.2.1", "1.2.0", 1)]
    [InlineData("1.10.0", "1.9.9", 1)]
    [InlineData("v2.0.0", "1.99.99", 1)]
    [InlineData("1.2.0", "1.2.0", 0)]
    [InlineData("1.2.0", "v1.2.0+abc", 0)]
    [InlineData("1.1.9", "1.2.0", -1)]
    [InlineData("1.2.0-beta.1", "1.2.0", -1)]
    [InlineData("1.2.0-beta.2", "1.2.0-beta.10", -1)]
    public void Compare_orders_semantic_versions(string a, string b, int expected) =>
        Assert.Equal(expected, Updates.Compare(a, b));

    [Fact]
    public void Compare_rejects_garbage()
    {
        Assert.Null(Updates.Compare("latest", "1.0.0"));
        Assert.False(Updates.IsNewer("1.0.0", "nonsense"));
        Assert.True(Updates.IsNewer("1.2.0", "1.3.0"));
        Assert.False(Updates.IsNewer("1.3.0", "1.3.0"));
    }

    static string Release(string tag = "v1.3.0", string name = "UmbraSetup-1.3.0.exe", bool draft = false, bool pre = false)
    {
        var url = $"https://github.com/AmineChr54/umbra/releases/download/{tag}/{name}";
        return $$"""
            { "tag_name": "{{tag}}", "draft": {{draft.ToString().ToLower()}}, "prerelease": {{pre.ToString().ToLower()}},
              "html_url": "https://github.com/AmineChr54/umbra/releases/tag/{{tag}}", "body": "notes",
              "assets": [ { "name": "other.zip", "browser_download_url": "https://github.com/AmineChr54/umbra/releases/download/{{tag}}/other.zip" },
                          { "name": "{{name}}", "browser_download_url": "{{url}}" } ] }
            """;
    }

    [Fact]
    public void ParseRelease_finds_the_installer()
    {
        var r = Updates.ParseRelease(Release())!;
        Assert.Equal("1.3.0", r.Version);
        Assert.Equal("UmbraSetup-1.3.0.exe", r.AssetName);
        Assert.Equal("notes", r.Notes);
    }

    [Fact]
    public void ParseRelease_ignores_drafts_prereleases_and_bad_input()
    {
        Assert.Null(Updates.ParseRelease(Release(draft: true)));
        Assert.Null(Updates.ParseRelease(Release(pre: true)));
        Assert.Null(Updates.ParseRelease(Release(tag: "v1.3.0-rc1")));
        Assert.Null(Updates.ParseRelease(Release(name: "UmbraSetup-1.4.0.exe")));
        Assert.Null(Updates.ParseRelease("not json"));
        Assert.Null(Updates.ParseRelease("[]"));
        Assert.Null(Updates.ParseRelease("""{ "tag_name": "v1.3.0", "assets": [] }"""));
    }

    [Theory]
    [InlineData("http://github.com/AmineChr54/umbra/releases/download/v1.3.0/UmbraSetup-1.3.0.exe")]
    [InlineData("https://evil.example/AmineChr54/umbra/releases/download/v1.3.0/UmbraSetup-1.3.0.exe")]
    [InlineData("https://github.com.evil.example/AmineChr54/umbra/releases/download/v1.3.0/UmbraSetup-1.3.0.exe")]
    [InlineData("https://github.com/Other/repo/releases/download/v1.3.0/UmbraSetup-1.3.0.exe")]
    [InlineData("https://github.com:8443/AmineChr54/umbra/releases/download/v1.3.0/UmbraSetup-1.3.0.exe")]
    [InlineData("https://user@github.com/AmineChr54/umbra/releases/download/v1.3.0/UmbraSetup-1.3.0.exe")]
    [InlineData("https://github.com/AmineChr54/umbra/releases/download/v1.3.0/Other.exe")]
    public void Installer_urls_must_be_https_github_release_downloads(string url) =>
        Assert.False(Updates.IsValidInstaller(url, "UmbraSetup-1.3.0.exe", "1.3.0"));

    [Theory]
    [InlineData("UmbraSetup-1.3.1.exe")]
    [InlineData("umbrasetup-1.3.0.exe")]
    [InlineData("UmbraSetup-1.3.0.msi")]
    [InlineData("UmbraSetup-1.3.exe")]
    [InlineData("x/UmbraSetup-1.3.0.exe")]
    public void Installer_names_must_match_the_release_version(string name) =>
        Assert.False(Updates.IsValidInstaller($"https://github.com/AmineChr54/umbra/releases/download/v1.3.0/{name}", name, "1.3.0"));

    [Fact]
    public void Valid_installer_and_protocol_entries()
    {
        Assert.True(Updates.IsValidInstaller("https://github.com/AmineChr54/umbra/releases/download/v1.3.0/UmbraSetup-1.3.0.exe", "UmbraSetup-1.3.0.exe", "1.3.0"));
        Assert.Contains(Protocol.Commands, c => c.Cmd == "check-update");
        Assert.True(new GeneralSettings().CheckForUpdates);
    }

    [Theory]
    [InlineData("github.com", true)]
    [InlineData("objects.githubusercontent.com", true)]
    [InlineData("release-assets.githubusercontent.com", true)]
    [InlineData("evilgithubusercontent.com", false)]
    [InlineData("githubusercontent.com.evil.example", false)]
    [InlineData("example.com", false)]
    public void Download_hosts_are_allow_listed(string host, bool ok) => Assert.Equal(ok, Updates.IsAllowedDownloadHost(host));

    [Fact]
    public void Digest_must_match_sha256()
    {
        var hex = new string('a', 64);
        Assert.True(Updates.DigestMatches("sha256:" + hex.ToUpper(), hex));
        Assert.False(Updates.DigestMatches("sha256:" + new string('b', 64), hex));
        Assert.False(Updates.DigestMatches(null, hex));
        Assert.False(Updates.DigestMatches("sha1:" + hex, hex));
    }

    [Theory]
    [InlineData(0, true)]                        // valid signature
    [InlineData(unchecked((int)0x800B0100), true)] // TRUST_E_NOSIGNATURE: unsigned release, the digest protects it
    [InlineData(unchecked((int)0x80096010), false)] // TRUST_E_BAD_DIGEST: tampered after signing
    [InlineData(unchecked((int)0x800B0109), false)] // CERT_E_UNTRUSTEDROOT
    [InlineData(unchecked((int)0x800B0101), false)] // CERT_E_EXPIRED
    [InlineData(unchecked((int)0x800B010C), false)] // CERT_E_REVOKED
    [InlineData(unchecked((int)0x800B0004), false)] // TRUST_E_SUBJECT_NOT_TRUSTED
    [InlineData(1, false)]
    public void Only_valid_or_unsigned_installers_pass_the_signature_check(int status, bool ok) =>
        Assert.Equal(ok, Updates.SignatureAcceptable(status));

    [Fact]
    public void Self_update_runs_the_installer_invisibly_with_the_update_flag()
    {
        var args = Updates.InstallerArguments(@"C:\Temp\UmbraUpdate.log");
        foreach (var flag in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/NOCANCEL", "/SP-", "/UPDATE=1" })
            Assert.Contains(flag, args.Split(' '));
        Assert.Contains("/LOG=\"C:\\Temp\\UmbraUpdate.log\"", args);
        Assert.DoesNotContain("/SILENT", args.Split(' ')); // /SILENT still shows the setup window
    }

    [WindowsFact] // the updater and its paths are Windows-only
    public void The_updater_command_line_round_trips()
    {
        var req = new UpdaterArgs.Request(@"C:\Temp\umbra-update-1\UmbraSetup-1.6.0.exe", "1.6.0", @"C:\Temp\UmbraUpdate.log", 4242, @"C:\Users\me\AppData\Local\Programs\Umbra\");
        var parsed = UpdaterArgs.Parse(CommandLineToArgs(UpdaterArgs.Build(req)));
        Assert.Equal(req with { InstallDir = @"C:\Users\me\AppData\Local\Programs\Umbra" }, parsed);
    }

    [WindowsTheory]
    [InlineData("UmbraSetup-1.6.0.exe", "1.6.0", true)]
    [InlineData("UmbraSetup-1.6.0.exe", "1.7.0", false)]  // wrong version
    [InlineData("calc.exe", "1.6.0", false)]
    [InlineData("UmbraSetup-1.6.0.exe.bat", "1.6.0", false)]
    public void The_updater_only_runs_a_correctly_named_installer(string name, string version, bool ok)
    {
        Assert.Equal(ok, UpdaterArgs.IsInstallerFor(@"C:\Temp\" + name, version));
        var args = new[] { "--installer", @"C:\Temp\" + name, "--version", version, "--log", "l.log", "--pid", "1", "--dir", @"C:\x" };
        Assert.Equal(ok, UpdaterArgs.Parse(args) is not null);
    }

    [Fact]
    public void The_updater_refuses_an_incomplete_command_line() =>
        Assert.Null(UpdaterArgs.Parse(new[] { "--installer", "UmbraSetup-1.6.0.exe", "--version", "1.6.0" }));

    [Fact]
    public void Install_progress_follows_the_installer_log_and_stops_short_of_done()
    {
        var entry = "-- File entry --\nInstalling the file.\n";
        Assert.Equal(0.5, UpdaterArgs.InstallProgress(string.Concat(Enumerable.Repeat(entry, 5)), 10));
        Assert.Equal(0.95, UpdaterArgs.InstallProgress(string.Concat(Enumerable.Repeat(entry, 50)), 10)); // more entries than expected
        Assert.Equal(0, UpdaterArgs.InstallProgress("Setup started", 10));
        Assert.Null(UpdaterArgs.InstallProgress(entry, 0)); // unknown total: indeterminate
    }

    /// <summary>Splits like Windows does for a process command line (quotes group, no escapes needed here).</summary>
    static string[] CommandLineToArgs(string line) =>
        System.Text.RegularExpressions.Regex.Matches(line, "\"([^\"]*)\"|(\\S+)").Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToArray();

    [Theory]
    [InlineData("""{ "from": "1.4.0", "to": "1.5.0", "log": "C:\\Temp\\UmbraUpdate.log" }""", "1.5.0", Updates.UpdateOutcome.Applied)]
    [InlineData("""{ "from": "1.4.0", "to": "1.5.0" }""", "1.4.0", Updates.UpdateOutcome.Failed)]
    [InlineData("""{ "from": "1.4.0", "to": "1.5.0" }""", "1.6.0", Updates.UpdateOutcome.Applied)]
    [InlineData("""{ "to": "latest" }""", "1.4.0", Updates.UpdateOutcome.None)]
    [InlineData("not json", "1.4.0", Updates.UpdateOutcome.None)]
    [InlineData("", "1.4.0", Updates.UpdateOutcome.None)]
    [InlineData(null, "1.4.0", Updates.UpdateOutcome.None)]
    public void The_update_marker_tells_a_finished_update_from_a_failed_one(string? json, string current, Updates.UpdateOutcome expected) =>
        Assert.Equal(expected, Updates.ReadPending(json, current).Outcome);

    [Fact]
    public void ParseRelease_keeps_the_asset_digest()
    {
        var json = Release().Replace("\"name\": \"UmbraSetup-1.3.0.exe\"", "\"name\": \"UmbraSetup-1.3.0.exe\", \"digest\": \"sha256:" + new string('c', 64) + "\"");
        Assert.Equal("sha256:" + new string('c', 64), Updates.ParseRelease(json)!.Digest);
        Assert.Null(Updates.ParseRelease(Release())!.Digest);
    }

    [Theory]
    [InlineData("1.4.0", "1.5.0", true)]
    [InlineData("1.9.9", "1.10.0", true)]
    [InlineData("1.5.0", "1.5.0", false)]
    [InlineData("1.5.0", "1.4.0", false)]
    [InlineData("", "1.5.0", false)]   // first install: nothing to announce
    [InlineData(null, "1.5.0", false)]
    [InlineData("garbage", "1.5.0", false)]
    public void ShouldAnnounce_only_after_a_real_update(string? lastSeen, string current, bool expected) =>
        Assert.Equal(expected, Updates.ShouldAnnounce(lastSeen, current));

    [Theory]
    [InlineData("", "1.5.0", "1.5.0")]      // first install records the running version
    [InlineData("1.4.0", "1.5.0", "1.5.0")] // update
    [InlineData("1.5.0", "1.5.0", "1.5.0")]
    [InlineData("1.5.0", "1.4.0", "1.5.0")] // a downgrade must not make the next upgrade announce again
    [InlineData("garbage", "1.5.0", "1.5.0")]
    public void NextLastSeen_never_goes_backwards(string lastSeen, string current, string expected) =>
        Assert.Equal(expected, Updates.NextLastSeen(lastSeen, current));

    [Fact]
    public void Announcing_happens_once_per_version()
    {
        var seen = "1.4.0";
        var shown = 0;
        foreach (var run in new[] { "1.5.0", "1.5.0", "1.5.0", "1.6.0", "1.6.0" })
        {
            if (Updates.ShouldAnnounce(seen, run)) shown++;
            seen = Updates.NextLastSeen(seen, run);
        }
        Assert.Equal(2, shown);
    }

    [Fact]
    public void Release_notes_urls_are_fixed_github_addresses()
    {
        Assert.Equal("https://api.github.com/repos/AmineChr54/umbra/releases/tags/v1.5.0", Updates.ReleaseByTagApi("1.5.0"));
        Assert.Equal("https://github.com/AmineChr54/umbra/releases/tag/v1.5.0", Updates.ReleasePageUrl("1.5.0"));
    }

    [Fact]
    public void Settings_keep_the_last_seen_version_in_the_file()
    {
        var s = new Settings();
        Assert.Equal("", s.General.LastSeenVersion);
        s.General.LastSeenVersion = "1.5.0";
        Assert.Equal("1.5.0", Settings.FromJson(s.ToJson()).General.LastSeenVersion);
    }

    static string LinuxRelease(string tag = "v1.3.0", string name = "umbra-1.3.0-linux-x64.tar.gz", string? url = null,
        string page = "https://github.com/AmineChr54/umbra/releases/tag/v1.3.0", bool draft = false, bool pre = false) =>
        $$"""
        { "tag_name": "{{tag}}", "draft": {{draft.ToString().ToLower()}}, "prerelease": {{pre.ToString().ToLower()}},
          "html_url": "{{page}}", "body": "notes",
          "assets": [ { "name": "UmbraSetup-1.3.0.exe", "browser_download_url": "https://github.com/AmineChr54/umbra/releases/download/{{tag}}/UmbraSetup-1.3.0.exe" },
                      { "name": "{{name}}", "browser_download_url": "{{url ?? $"https://github.com/AmineChr54/umbra/releases/download/{tag}/{name}"}}" } ] }
        """;

    [Fact]
    public void ParseLinuxNotice_needs_this_architectures_tarball()
    {
        var r = Updates.ParseLinuxNotice(LinuxRelease(), "x64")!;
        Assert.Equal("1.3.0", r.Version);
        Assert.Equal("umbra-1.3.0-linux-x64.tar.gz", r.AssetName);
        Assert.Equal("https://github.com/AmineChr54/umbra/releases/tag/v1.3.0", r.PageUrl);
        Assert.Null(Updates.ParseLinuxNotice(LinuxRelease(), "arm64")); // only the x64 tarball is published
        Assert.Null(Updates.ParseLinuxNotice(LinuxRelease(), "riscv64"));
        Assert.Null(Updates.ParseLinuxNotice(LinuxRelease(name: "umbra-1.3.0-linux-x64.zip"), "x64")); // Windows-only release
        Assert.NotNull(Updates.ParseRelease(LinuxRelease())); // the Windows updater still sees its installer
    }

    [Fact]
    public void ParseLinuxNotice_ignores_drafts_prereleases_and_foreign_downloads()
    {
        Assert.Null(Updates.ParseLinuxNotice(LinuxRelease(draft: true), "x64"));
        Assert.Null(Updates.ParseLinuxNotice(LinuxRelease(pre: true), "x64"));
        Assert.Null(Updates.ParseLinuxNotice(LinuxRelease(tag: "v1.3.0-rc1"), "x64"));
        Assert.Null(Updates.ParseLinuxNotice(LinuxRelease(name: "umbra-1.4.0-linux-x64.tar.gz"), "x64")); // other version
        Assert.Null(Updates.ParseLinuxNotice(LinuxRelease(url: "https://evil.example/AmineChr54/umbra/releases/download/v1.3.0/umbra-1.3.0-linux-x64.tar.gz"), "x64"));
        Assert.Null(Updates.ParseLinuxNotice(LinuxRelease(url: "http://github.com/AmineChr54/umbra/releases/download/v1.3.0/umbra-1.3.0-linux-x64.tar.gz"), "x64"));
        Assert.Null(Updates.ParseLinuxNotice("not json", "x64"));
        Assert.Null(Updates.ParseLinuxNotice("""{ "tag_name": "v1.3.0", "assets": [] }""", "x64"));
    }

    [Fact]
    public void ParseLinuxNotice_never_takes_the_page_from_the_feed()
    {
        var r = Updates.ParseLinuxNotice(LinuxRelease(page: "https://evil.example/phish"), "x64")!;
        Assert.Equal(Updates.ReleasePageUrl("1.3.0"), r.PageUrl);
    }

    [Theory]
    [InlineData(System.Runtime.InteropServices.Architecture.X64, "x64")]
    [InlineData(System.Runtime.InteropServices.Architecture.Arm64, "arm64")]
    [InlineData(System.Runtime.InteropServices.Architecture.X86, null)]
    public void LinuxArch_names_the_published_tarballs(System.Runtime.InteropServices.Architecture a, string? expected) =>
        Assert.Equal(expected, Updates.LinuxArch(a));
}
