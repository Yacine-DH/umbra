namespace Umbra.Linux.Tests;

/// <summary>The pure sandbox rules (#180): the page policy header, the extension's policy text, the navigation lock.</summary>
public sealed class SandboxTests
{
    static WidgetManifest Widget(string folder, WidgetSource source, params string[] hosts)
    {
        var m = new WidgetManifest { Id = folder, Folder = folder, Source = source };
        m.Permissions.Hosts.AddRange(hosts);
        return m;
    }

    [Fact]
    public void Hosts_are_lower_cased_deduplicated_and_must_be_plain_names()
    {
        var clean = Sandbox.CleanHosts(["API.Example.com", "api.example.com", " cdn.example.com ", "", "evil.com; script-src *",
            "a b.com", "https://x.com", "x.com/path", "*.example.com", "exa_mple.com", "-bad.com", "bad-.com", "xn--bcher-kva.example", "localhost"]);
        Assert.Equal(["api.example.com", "cdn.example.com", "xn--bcher-kva.example", "localhost"], clean);
    }

    [Fact]
    public void Policy_text_lists_only_the_declared_hosts()
    {
        Assert.Equal("open", Sandbox.PolicyText(null));
        Assert.Equal("sandbox", Sandbox.PolicyText(Widget("clock", WidgetSource.Community)));
        Assert.Equal("sandbox\napi.example.com\ncdn.example.com",
            Sandbox.PolicyText(Widget("w", WidgetSource.Community, "API.example.com", "cdn.example.com", "bad host\nopen")));
    }

    [Fact]
    public void Csp_allows_the_umbra_origins_and_declared_hosts_only()
    {
        var csp = Sandbox.Csp(["api.example.com"]);
        Assert.Contains("default-src umbra: data: blob: https://umbra.local https://community.umbra.local https://api.example.com 'unsafe-inline' 'unsafe-eval';", csp);
        Assert.Contains("connect-src umbra: data: blob: https://umbra.local https://community.umbra.local https://api.example.com;", csp);
        Assert.Contains("frame-src 'none'", csp);
        Assert.Contains("object-src 'none'", csp);
        Assert.Contains("form-action 'none'", csp);
        Assert.DoesNotContain("*", csp);
        Assert.DoesNotContain("http:", csp);
        Assert.DoesNotContain("ws", csp); // no WebSocket source at all
    }

    [Fact]
    public void A_manifest_value_cannot_add_a_directive()
    {
        var csp = Sandbox.Csp(["evil.com; script-src *", "ok.example.com\nframe-src *", "x.com 'unsafe-hashes'"]);
        Assert.DoesNotContain("evil.com", csp);
        Assert.DoesNotContain("script-src", csp);
        Assert.DoesNotContain("unsafe-hashes", csp);
        Assert.Contains("frame-src 'none'", csp);
    }

    [Theory]
    [InlineData("umbra://community.umbra.local/clock/index.html", WidgetSource.Community, "clock")]
    [InlineData("https://community.umbra.local/clock/js/app.js", WidgetSource.Community, "clock")]
    [InlineData("umbra://umbra.local/widgets/sp500/index.html", WidgetSource.BuiltIn, "sp500")]
    [InlineData("UMBRA://Community.Umbra.Local/clock/index.html", WidgetSource.Community, "clock")]
    public void Finds_the_widget_a_url_belongs_to(string url, WidgetSource source, string folder) =>
        Assert.Equal((source, folder), Sandbox.WidgetOf(url));

    [Theory]
    [InlineData("umbra://umbra.local/shared/umbra.js")]
    [InlineData("umbra://umbra.local/settings/index.html")]
    [InlineData("umbra://community.umbra.local/index.html")]
    [InlineData("umbra://community.umbra.local/")]
    [InlineData("umbra://community.umbra.local/../x/index.html")]
    [InlineData("umbra://community.umbra.local/%2e%2e/index.html")]
    [InlineData("https://example.com/clock/index.html")]
    public void Other_urls_belong_to_no_widget(string url) => Assert.Null(Sandbox.WidgetOf(url));

    [Fact]
    public void Each_response_gets_its_own_widgets_policy()
    {
        var manifests = new[]
        {
            Widget("weather", WidgetSource.Community, "api.open-meteo.com"),
            Widget("clock", WidgetSource.Community),
            Widget("clock", WidgetSource.BuiltIn, "builtin.example.com"),
        };
        Assert.Contains("https://api.open-meteo.com", Sandbox.CspFor("umbra://community.umbra.local/weather/index.html", manifests));
        Assert.DoesNotContain("open-meteo", Sandbox.CspFor("umbra://community.umbra.local/clock/index.html", manifests));
        Assert.Contains("https://builtin.example.com", Sandbox.CspFor("umbra://umbra.local/widgets/clock/index.html", manifests));
        // An unknown folder and shared files get the policy of a widget without hosts; the host-owned Settings pages get none.
        Assert.Equal(Sandbox.Csp([]), Sandbox.CspFor("umbra://community.umbra.local/unknown/index.html", manifests));
        Assert.Equal(Sandbox.Csp([]), Sandbox.CspFor("umbra://umbra.local/shared/crashed.html", manifests));
        Assert.Null(Sandbox.CspFor("umbra://umbra.local/settings/index.html", manifests));
    }

    [Theory]
    [InlineData("umbra://umbra.local/settings/index.html", false, "Allow")]
    [InlineData("umbra://community.umbra.local/clock/index.html", true, "Allow")]
    [InlineData("https://example.com/page", true, "External")]
    [InlineData("http://example.com/page", true, "External")]
    [InlineData("https://example.com/page", false, "Block")] // a script alone never opens the browser
    [InlineData("https://umbra.local/settings/index.html", true, "Block")] // only the umbra:// spelling is a page
    [InlineData("umbra://evil.example/x", true, "Block")]
    [InlineData("file:///etc/passwd", true, "Block")]
    [InlineData("javascript:alert(1)", true, "Block")]
    [InlineData("data:text/html,<p>x", true, "Block")]
    [InlineData("about:blank", true, "Block")]
    [InlineData("", true, "Block")]
    [InlineData(null, true, "Block")]
    public void Navigation_lock(string? url, bool realPress, string expected) =>
        Assert.Equal(Enum.Parse<NavigationVerdict>(expected), Sandbox.Navigation(url, realPress));

    [Fact]
    public void The_dead_end_proxy_is_used_unless_the_user_has_a_proxy()
    {
        string? None(string _) => null;
        Assert.Equal(Sandbox.DeadEnd, Sandbox.DeadEndProxy("system", None));
        Assert.Equal(Sandbox.DeadEnd, Sandbox.DeadEndProxy("none", None));
        Assert.Equal(Sandbox.DeadEnd, Sandbox.DeadEndProxy("none", n => n == "https_proxy" ? "http://corp:3128" : null)); // "none" ignores the environment
        Assert.Null(Sandbox.DeadEndProxy("system", n => n == "https_proxy" ? "http://corp:3128" : null));
        Assert.Null(Sandbox.DeadEndProxy("system", n => n == "ALL_PROXY" ? "socks5://corp:1080" : null));
        Assert.Null(Sandbox.DeadEndProxy("https://proxy.example.com:8443", None));
    }
}

/// <summary>How the Linux host reads WebKit's "web process terminated" reasons (#185); the retry budget itself is Core's <c>CrashRecovery</c>.</summary>
public sealed class CrashTests
{
    [Fact]
    public void A_crash_or_a_memory_kill_reloads_and_our_own_termination_does_not()
    {
        Assert.Equal(CrashReaction.Reload, WidgetWindow.Classify(WebKit.WebProcessTerminationReason.Crashed));
        Assert.Equal(CrashReaction.Reload, WidgetWindow.Classify(WebKit.WebProcessTerminationReason.ExceededMemoryLimit));
        Assert.Equal(CrashReaction.Ignore, WidgetWindow.Classify(WebKit.WebProcessTerminationReason.TerminatedByApi));
    }

    [Fact]
    public void The_fourth_crash_inside_ten_minutes_gives_up()
    {
        var recovery = new CrashRecovery();
        Assert.True(recovery.TryAcquire(0));
        Assert.True(recovery.TryAcquire(1_000));
        Assert.True(recovery.TryAcquire(2_000));
        Assert.False(recovery.TryAcquire(3_000));
        Assert.True(recovery.TryAcquire(601_000)); // the first one aged out
        recovery.Reset();
        Assert.Equal(0, recovery.Used);
    }
}
