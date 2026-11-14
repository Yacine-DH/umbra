using System.Text.RegularExpressions;

namespace Umbra;

/// <summary>What the host does with a navigation a page asked for.</summary>
enum NavigationVerdict
{
    /// <summary>One of Umbra's own pages: let it load.</summary>
    Allow,
    /// <summary>An http(s) link: cancel it and open it in the user's browser.</summary>
    External,
    /// <summary>Anything else: cancel it.</summary>
    Block,
}

/// <summary>
/// The pure rules of the Linux widget sandbox (#180): the Content-Security-Policy sent with every <c>umbra://</c> response, the
/// policy text the web-process extension is given, which widget a URL belongs to, and the navigation lock. Same invariant as
/// Windows (<c>WebHost.InitAsync</c>): a widget page reaches only the two Umbra origins, <c>data:</c>, <c>blob:</c> and the HTTPS
/// hosts in its <c>permissions.hosts</c>.
/// </summary>
static partial class Sandbox
{
    /// <summary>A DNS host name and nothing else, so a manifest value can never add a CSP directive or a policy line.</summary>
    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)*$")]
    private static partial Regex HostPattern();

    /// <summary>The declared hosts in the one spelling both layers compare: lower case, valid names only, no duplicates.</summary>
    public static List<string> CleanHosts(IEnumerable<string> hosts) =>
        hosts.Select(h => (h ?? "").Trim().ToLowerInvariant())
            .Where(h => h.Length is > 0 and <= 253 && HostPattern().IsMatch(h))
            .Distinct().ToList();

    /// <summary>
    /// The policy the web-process extension enforces for a page: <c>sandbox</c> plus one allowed host per line for a widget,
    /// <c>open</c> for a host-owned page (Settings), which has no host list on Windows either.
    /// </summary>
    public static string PolicyText(WidgetManifest? widget) =>
        widget is null ? "open" : string.Join('\n', new[] { "sandbox" }.Concat(CleanHosts(widget.Permissions.Hosts)));

    /// <summary>
    /// The Content-Security-Policy for a widget's pages and workers. It is the second layer: it also covers what never reaches
    /// <c>send-request</c> (WebSocket, EventSource, beacons, worker fetches). Inline script and style stay allowed, as on Windows,
    /// where widgets have no CSP. <c>https://umbra.local</c> is listed because CSP is checked before the extension rewrites it.
    /// </summary>
    public static string Csp(IEnumerable<string> declaredHosts)
    {
        var own = $"{UmbraScheme.Scheme}: data: blob: https://{VirtualHosts.Host} https://{VirtualHosts.CommunityHost}";
        var hosts = string.Concat(CleanHosts(declaredHosts).Select(h => $" https://{h}"));
        return $"default-src {own}{hosts} 'unsafe-inline' 'unsafe-eval'; "
            + $"connect-src {own}{hosts}; "
            + "frame-src 'none'; object-src 'none'; form-action 'none'; base-uri 'self'";
    }

    /// <summary>
    /// The widget folder a URL on one of the Umbra origins belongs to: <c>community.umbra.local/&lt;folder&gt;/...</c> or
    /// <c>umbra.local/widgets/&lt;folder&gt;/...</c>. Null for shared/, settings/ and anything else.
    /// </summary>
    public static (WidgetSource Source, string Folder)? WidgetOf(string url)
    {
        url = UmbraScheme.FromHttps(url);
        var community = $"{UmbraScheme.Scheme}://{VirtualHosts.CommunityHost}/";
        var builtIn = $"{UmbraScheme.Scheme}://{VirtualHosts.Host}/widgets/";
        string rest;
        WidgetSource source;
        if (url.StartsWith(community, StringComparison.OrdinalIgnoreCase)) { rest = url[community.Length..]; source = WidgetSource.Community; }
        else if (url.StartsWith(builtIn, StringComparison.OrdinalIgnoreCase)) { rest = url[builtIn.Length..]; source = WidgetSource.BuiltIn; }
        else return null;
        var slash = rest.IndexOf('/');
        if (slash <= 0) return null;
        string folder;
        try { folder = Uri.UnescapeDataString(rest[..slash]); }
        catch (UriFormatException) { return null; }
        return folder is "." or ".." || folder.Contains('/') || folder.Contains('\\') ? null : (source, folder);
    }

    /// <summary>The manifest whose folder a URL is served from, or null.</summary>
    public static WidgetManifest? ManifestFor(string url, IEnumerable<WidgetManifest> manifests) =>
        WidgetOf(url) is { } w ? manifests.FirstOrDefault(m => m.Source == w.Source && m.Folder == w.Folder) : null;

    /// <summary>
    /// The CSP header for one <c>umbra://</c> response, or null for none. Widget files get their widget's policy; other app files
    /// (shared/) get the policy of a widget without hosts, which only matters if one is ever opened as a page; the host-owned
    /// Settings pages get none, as on Windows.
    /// </summary>
    public static string? CspFor(string url, IEnumerable<WidgetManifest> manifests)
    {
        if (ManifestFor(url, manifests) is { } m) return Csp(m.Permissions.Hosts);
        var settings = $"{UmbraScheme.Scheme}://{VirtualHosts.Host}/settings/";
        return UmbraScheme.FromHttps(url).StartsWith(settings, StringComparison.OrdinalIgnoreCase) ? null : Csp([]);
    }

    /// <summary>A loopback address where nothing listens: a connection sent there is refused at once and never leaves the machine.</summary>
    public const string DeadEnd = "http://127.0.0.1:1";

    /// <summary>
    /// The proxy address that swallows everything but a widget's declared hosts, or null when the user has a proxy of their own
    /// (General &gt; proxy, or the usual environment variables): WebKit cannot route "declared hosts through the user's proxy,
    /// nothing else anywhere", so with a proxy this network layer is off and the two page layers remain.
    /// </summary>
    public static string? DeadEndProxy(string proxySetting, Func<string, string?> getEnv)
    {
        if (proxySetting is not ("system" or "none")) return null;
        if (proxySetting == "system")
            foreach (var name in new[] { "https_proxy", "HTTPS_PROXY", "http_proxy", "HTTP_PROXY", "all_proxy", "ALL_PROXY" })
                if (!string.IsNullOrWhiteSpace(getEnv(name))) return null;
        return DeadEnd;
    }

    /// <summary>
    /// The navigation lock: a page may only navigate to Umbra's own <c>umbra://</c> pages. An http(s) link is handed to the
    /// user's browser, but only when <paramref name="userGesture"/> says a real mouse press on the widget caused it: a script
    /// cannot open browser tabs on its own (stricter than Windows, which opens any cancelled navigation).
    /// </summary>
    public static NavigationVerdict Navigation(string? url, bool userGesture)
    {
        if (string.IsNullOrEmpty(url)) return NavigationVerdict.Block;
        if (url.StartsWith($"{UmbraScheme.Scheme}://{VirtualHosts.Host}/", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith($"{UmbraScheme.Scheme}://{VirtualHosts.CommunityHost}/", StringComparison.OrdinalIgnoreCase))
            return NavigationVerdict.Allow;
        if (userGesture && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "https" or "http" && !UmbraScheme.IsLocal(url))
            return NavigationVerdict.External;
        return NavigationVerdict.Block;
    }
}
