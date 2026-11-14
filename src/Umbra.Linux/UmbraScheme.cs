namespace Umbra;

/// <summary>
/// The <c>umbra://</c> scheme: the Linux counterpart of the WebView2 virtual-host folder mapping (<c>WebHost.cs</c> on Windows).
/// <c>umbra://umbra.local/&lt;path&gt;</c> serves the app folder (shared/, settings/, built-in widgets/) and
/// <c>umbra://community.umbra.local/&lt;path&gt;</c> the installed community widgets. Pure, so the path rules are unit tested.
/// </summary>
static class UmbraScheme
{
    public const string Scheme = "umbra";

    /// <summary>The <c>umbra://</c> form of one of Umbra's own page URLs (<c>https://umbra.local/...</c>); other URLs come back unchanged.</summary>
    public static string FromHttps(string url)
    {
        foreach (var host in new[] { VirtualHosts.Host, VirtualHosts.CommunityHost })
        {
            var prefix = $"https://{host}/";
            if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return $"{Scheme}://{host}/{url[prefix.Length..]}";
        }
        return url;
    }

    /// <summary>True for a URL on one of the two Umbra origins, in either the <c>umbra://</c> or the <c>https://</c> spelling.</summary>
    public static bool IsLocal(string url)
    {
        foreach (var host in new[] { VirtualHosts.Host, VirtualHosts.CommunityHost })
            if (url.StartsWith($"{Scheme}://{host}/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith($"https://{host}/", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// The file a request names, or null (answered 404): unknown host, a path that leaves its root (<c>..</c>, encoded or not,
    /// backslashes, an absolute path), a folder, a missing file or a symbolic link.
    /// </summary>
    /// <param name="uri">The full request URI, e.g. <c>umbra://umbra.local/shared/umbra.js?v=2</c>.</param>
    /// <param name="appDir">Root of <c>umbra.local</c>.</param>
    /// <param name="communityDir">Root of <c>community.umbra.local</c>.</param>
    public static string? Resolve(string uri, string appDir, string communityDir)
    {
        const string prefix = Scheme + "://";
        if (!uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = uri[prefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0) return null;
        var host = rest[..slash];
        var path = rest[(slash + 1)..];
        var cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0) path = path[..cut];

        string root;
        if (host.Equals(VirtualHosts.Host, StringComparison.OrdinalIgnoreCase)) root = appDir;
        else if (host.Equals(VirtualHosts.CommunityHost, StringComparison.OrdinalIgnoreCase)) root = communityDir;
        else return null;

        try { path = Uri.UnescapeDataString(path); }
        catch (UriFormatException) { return null; }
        if (path.Length == 0 || path.Contains('\\') || path.Contains('\0') || path.StartsWith('/')) return null;
        foreach (var part in path.Split('/'))
            if (part is ".." or "." or "") return null;

        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(rootFull, path));
        if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
        var info = new FileInfo(full);
        if (!info.Exists || info.LinkTarget is not null) return null;
        // No folder on the way down may be a link out of the root either.
        for (var dir = info.Directory; dir is not null && dir.FullName.Length > rootFull.Length; dir = dir.Parent)
            if (dir.LinkTarget is not null) return null;
        return full;
    }

    static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html", [".htm"] = "text/html", [".js"] = "text/javascript", [".mjs"] = "text/javascript",
        [".css"] = "text/css", [".json"] = "application/json", [".txt"] = "text/plain", [".md"] = "text/markdown",
        [".svg"] = "image/svg+xml", [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif", [".webp"] = "image/webp", [".ico"] = "image/x-icon", [".avif"] = "image/avif",
        [".woff"] = "font/woff", [".woff2"] = "font/woff2", [".ttf"] = "font/ttf", [".otf"] = "font/otf",
        [".mp3"] = "audio/mpeg", [".wav"] = "audio/wav", [".ogg"] = "audio/ogg", [".mp4"] = "video/mp4", [".webm"] = "video/webm",
        [".wasm"] = "application/wasm",
    };

    /// <summary>The Content-Type for a file name; unknown extensions are served as bytes, never sniffed as a page.</summary>
    public static string ContentType(string path) =>
        Types.TryGetValue(Path.GetExtension(path), out var t) ? t : "application/octet-stream";
}
