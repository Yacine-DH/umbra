using System.Text.Json;
using System.Text.Json.Nodes;

namespace Umbra;

/// <summary>
/// The community catalog. Widgets are contributed by pull request to the repo's widgets/ folder;
/// a GitHub Action validates them and regenerates widgets/index.json on merge. Umbra reads that
/// index and installs widgets file by file into %APPDATA%\Umbra\widgets\&lt;id&gt;.
/// </summary>
static class Community
{
    public const string Repo = "AmineChr54/umbra";
    public const string Branch = "main";

    static string RawBase =>
        Environment.GetEnvironmentVariable("UMBRA_CATALOG_BASE")?.TrimEnd('/')
        ?? $"https://raw.githubusercontent.com/{Repo}/{Branch}/widgets";

    static readonly HttpClient Http = new(Prefs.CreateHandler()) { Timeout = TimeSpan.FromSeconds(20) };

    static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".html", ".css", ".js", ".mjs", ".svg", ".png", ".jpg", ".jpeg", ".webp", ".gif",
        ".woff", ".woff2", ".ttf", ".otf", ".txt", ".md",
    };

    const long MaxWidgetBytes = 5 * 1024 * 1024;

    /// <summary>Themes live in a themes/ folder next to widgets/ (same override: UMBRA_CATALOG_BASE points at widgets).</summary>
    static string ThemesBase => RawBase.EndsWith("/widgets", StringComparison.OrdinalIgnoreCase) ? RawBase[..^"/widgets".Length] + "/themes" : RawBase + "/themes";

    static Task<byte[]> GetBytes(string relative) => GetBytes(RawBase, relative);

    static async Task<byte[]> GetBytes(string baseUrl, string relative)
    {
        var url = $"{baseUrl}/{relative}";
        // A local folder (file:///...) can stand in for GitHub when testing a catalog before pushing.
        if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return await File.ReadAllBytesAsync(new Uri(url.Split('?')[0]).LocalPath);
        return await Http.GetByteArrayAsync(url);
    }

    /// <summary>Downloads widgets/index.json. Each entry: id, name, description, author, version, hosts, preview, files[].</summary>
    public static async Task<JsonArray> FetchCatalogAsync()
    {
        var bytes = await GetBytes($"index.json?t={DateTime.UtcNow.Ticks}");
        var root = JsonNode.Parse(bytes);
        return root?["widgets"] as JsonArray ?? new JsonArray();
    }

    /// <summary>Downloads themes/index.json. Each entry: id, name, description, author, version, tokens, preview, files[].</summary>
    public static async Task<JsonArray> FetchThemeCatalogAsync()
    {
        var bytes = await GetBytes(ThemesBase, $"index.json?t={DateTime.UtcNow.Ticks}");
        return JsonNode.Parse(bytes)?["themes"] as JsonArray ?? new JsonArray();
    }

    const long MaxThemeBytes = 1024 * 1024;
    static readonly string[] ThemeFileTypes = { ".json", ".png", ".jpg", ".jpeg", ".webp" };

    /// <summary>Installs or updates a catalog theme. Only theme.json and its preview image are fetched; all validated before anything is written.</summary>
    public static async Task InstallThemeAsync(JsonNode entry)
    {
        var id = entry["id"]?.GetValue<string>() ?? "";
        if (!WidgetManifest.IdPattern().IsMatch(id) || id == Theme.DefaultId) throw new InvalidOperationException($"Invalid theme id '{id}'");
        var files = (entry["files"] as JsonArray)?.Select(f => f?.GetValue<string>() ?? "").ToList() ?? new();
        if (!files.Contains("theme.json")) throw new InvalidOperationException("Catalog entry has no theme.json");

        foreach (var rel in files)
            if (!IsSafeRelativePath(rel) || rel.Contains('/') || !ThemeFileTypes.Contains(Path.GetExtension(rel), StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Unsafe or disallowed theme file '{rel}'");

        var staged = new Dictionary<string, byte[]>();
        long total = 0;
        foreach (var rel in files)
        {
            var data = await GetBytes(ThemesBase, $"{id}/{rel}");
            total += data.Length;
            if (total > MaxThemeBytes) throw new InvalidOperationException("Theme is larger than 1 MB");
            staged[rel] = data;
        }
        CommitTheme(id, staged);
        Log.Write($"Installed community theme {id} {entry["version"]}");
    }

    /// <summary>
    /// Validates theme.json (id = folder, allow-listed tokens, safe values) and replaces the theme folder.
    /// Nothing is written until everything passed.
    /// </summary>
    internal static void CommitTheme(string id, IReadOnlyDictionary<string, byte[]> files)
    {
        if (!files.TryGetValue("theme.json", out var json)) throw new InvalidOperationException("Missing theme.json");
        var theme = Theme.Parse(System.Text.Encoding.UTF8.GetString(json));
        if (theme.Id != id) throw new InvalidOperationException($"theme.json id is '{theme.Id}', expected '{id}'");
        if (theme.Preview is not null && !files.ContainsKey(theme.Preview)) throw new InvalidOperationException($"Preview '{theme.Preview}' is missing");

        var target = Path.Combine(Theme.Dir, id);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var tmp = $"{target}.{tag}.tmp";
        var old = $"{target}.{tag}.old";
        Directory.CreateDirectory(tmp);
        var movedAside = false;
        try
        {
            foreach (var (rel, data) in files) File.WriteAllBytes(Path.Combine(tmp, rel), data);
            if (Directory.Exists(target)) { Directory.Move(target, old); movedAside = true; }
            Directory.Move(tmp, target);
            if (movedAside) try { Directory.Delete(old, recursive: true); } catch { } // a leftover .old is cleaned at startup
        }
        catch
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
            // Put the previous version back if the new one never made it in.
            if (movedAside && !Directory.Exists(target)) try { Directory.Move(old, target); } catch { }
            throw;
        }
    }

    public static void RemoveTheme(string id)
    {
        if (!WidgetManifest.IdPattern().IsMatch(id)) return;
        var target = Path.Combine(Theme.Dir, id);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Log.Write($"Removed community theme {id}");
    }

    /// <summary>Preview image as a data: URL, so the Settings page never hot-links remote files.</summary>
    public static async Task<string?> PreviewDataUrlAsync(string id, string file)
    {
        try
        {
            var bytes = await GetBytes($"{id}/{file}");
            var mime = Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp",
                ".gif" => "image/gif", ".svg" => "image/svg+xml", _ => null,
            };
            return mime is null || bytes.Length > 1_000_000 ? null : $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }
        catch { return null; }
    }

    /// <summary>
    /// Installs or updates one widget. Everything is downloaded to a temp folder and validated first. An update that
    /// adds permissions (hosts, system, media, notifications, camera, commands) throws <see cref="PermissionsChangedException"/>
    /// unless <paramref name="acceptPermissions"/> is true, which only a user action may set.
    /// </summary>
    public static async Task InstallAsync(JsonNode entry, bool acceptPermissions = false)
    {
        using var pending = await PrepareAsync(entry);
        pending.Require(acceptPermissions);
        pending.Commit();
    }

    /// <summary>
    /// The install used by "update all": downloads and validates first, then refuses (throws, nothing written, the running
    /// widget untouched) an update that adds hosts, system, media, notifications, camera or commands, naming the new access and how
    /// to accept it. <paramref name="beforeCommit"/> gets the widget id just before the folder is replaced (to close its window).
    /// </summary>
    public static async Task UpdateWithoutNewAccessAsync(JsonNode entry, Action<string>? beforeCommit = null, Func<string, Task<byte[]>>? fetch = null)
    {
        using var pending = await PrepareAsync(entry, fetch);
        try { pending.Require(accepted: false); }
        catch (PermissionsChangedException ex)
        {
            throw new InvalidOperationException($"Skipped, needs your approval: {string.Join("; ", ex.Diff.Describe())}. Update it from Settings > Browse or with umbractl install {pending.Manifest.Id} --accept-permissions.");
        }
        beforeCommit?.Invoke(pending.Manifest.Id);
        pending.Commit();
    }

    /// <summary>
    /// Downloads and fully validates a catalog widget without writing it. <see cref="PendingInstall.Added"/> is what
    /// it would add to the installed copy; call <see cref="PendingInstall.Require"/> then <see cref="PendingInstall.Commit"/>.
    /// <paramref name="fetch"/> replaces the download (tests).
    /// </summary>
    public static async Task<PendingInstall> PrepareAsync(JsonNode entry, Func<string, Task<byte[]>>? fetch = null)
    {
        fetch ??= GetBytes;
        var id = entry["id"]?.GetValue<string>() ?? "";
        if (!WidgetManifest.IdPattern().IsMatch(id)) throw new InvalidOperationException($"Invalid widget id '{id}'");
        var files = (entry["files"] as JsonArray)?.Select(f => f?.GetValue<string>() ?? "").ToList() ?? new();
        if (!files.Contains("widget.json")) throw new InvalidOperationException("Catalog entry has no widget.json");

        var staging = Path.Combine(Path.GetTempPath(), $"umbra-{id}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            long total = 0;
            foreach (var rel in files)
            {
                if (!IsSafeRelativePath(rel)) throw new InvalidOperationException($"Unsafe file path '{rel}'");
                var data = await fetch($"{id}/{rel}");
                total += data.Length;
                if (total > MaxWidgetBytes) throw new InvalidOperationException("Widget is larger than 5 MB");
                var dest = Path.Combine(staging, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                await File.WriteAllBytesAsync(dest, data);
            }

            // Full parse, so a malformed permissions.commands block is refused before anything is committed.
            var manifest = WidgetManifest.Parse(await File.ReadAllTextAsync(Path.Combine(staging, "widget.json")));
            if (manifest.Id != id)
                throw new InvalidOperationException("widget.json id does not match the catalog");
            manifest.RequireCompatible();

            var installed = InstalledManifest(id);
            if (installed is { Private: true })
                throw new InvalidOperationException($"'{id}' is a private widget installed from a file; the catalog widget with the same id can't replace it. Remove it first to install the catalog one.");
            return new PendingInstall(staging, manifest, PermissionDiff.Compute(installed, manifest), isUpdate: installed is not null);
        }
        catch
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
            throw;
        }
    }

    /// <summary>
    /// The standard widget package: a zip with widget.json at its root (no wrapper folder), the same file rules as a
    /// catalog widget (allowed types, safe relative paths, 5 MB, 1000 files). <see cref="WritePackage"/> makes one,
    /// <see cref="PrepareLocal"/> reads one.
    /// </summary>
    public const string PackageExtension = ".umbrawidget";

    /// <summary>File types "Install from file" accepts besides a folder. A plain .zip may also hold one wrapper folder named like the id.</summary>
    public static readonly string[] PackageExtensions = { ".zip", PackageExtension };

    /// <summary>Fixed entry time in every package (the zip format's earliest date), so packing the same files twice gives the same bytes.</summary>
    static readonly DateTimeOffset PackageTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Packs a widget folder into a <see cref="PackageExtension"/> file. The folder is checked exactly like an install
    /// (<see cref="PrepareLocal"/>: links, paths, types, size, id = folder, entry file, manifest, compatibility) before
    /// anything is written. Entries are sorted by path with a fixed time, so the output is deterministic and
    /// pack, install, pack gives the same file. Returns the manifest.
    /// </summary>
    public static WidgetManifest WritePackage(string folder, string outPath)
    {
        if (!Directory.Exists(folder)) throw new InvalidOperationException($"'{folder}' is not a folder");
        using var pending = PrepareLocal(folder);
        var tmp = outPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
            using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create))
                foreach (var rel in pending.Files())
                {
                    var e = zip.CreateEntry(rel, System.IO.Compression.CompressionLevel.Optimal);
                    e.LastWriteTime = PackageTime;
                    using var o = e.Open();
                    using var i = File.OpenRead(pending.PathOf(rel));
                    i.CopyTo(o);
                }
            File.Move(tmp, outPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
        return pending.Manifest;
    }

    const int MaxWidgetFiles = 1000;

    /// <summary>
    /// Reads a widget folder or a .zip / .umbrawidget into a temp staging folder and validates it with the catalog rules
    /// (safe relative paths, allowed types, 5 MB, widget.json id = folder, entry file present). Zip entries that would land
    /// outside the widget (zip-slip: "..", absolute paths, drive letters, symlinks) are refused, and so are links in a folder.
    /// Nothing is written to the widgets folder: call <see cref="PendingInstall.Require"/> then <see cref="PendingInstall.Commit"/>.
    /// Any access the widget declares needs the user's accept, even on a fresh install.
    /// </summary>
    public static PendingInstall PrepareLocal(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Pick a widget folder or a .zip file");
        var staging = Path.Combine(Path.GetTempPath(), $"umbra-local-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            string? expectedId;
            if (Directory.Exists(path)) expectedId = StageFolder(path, staging);
            else if (File.Exists(path) && PackageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                expectedId = StageZip(path, staging, allowWrapper: !string.Equals(Path.GetExtension(path), PackageExtension, StringComparison.OrdinalIgnoreCase));
            else throw new InvalidOperationException($"'{Path.GetFileName(path)}' is not a widget folder or a .zip / .umbrawidget file");

            var manifestPath = Path.Combine(staging, "widget.json");
            if (!File.Exists(manifestPath)) throw new InvalidOperationException("No widget.json found at the top of the widget");
            WidgetManifest manifest;
            try { manifest = WidgetManifest.Parse(File.ReadAllText(manifestPath)); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException) { throw new InvalidOperationException($"widget.json is invalid: {ex.Message}"); }
            if (!WidgetManifest.IdPattern().IsMatch(manifest.Id)) throw new InvalidOperationException($"Invalid widget id '{manifest.Id}': use 2-40 lowercase letters, digits and dashes");
            if (expectedId is not null && manifest.Id != expectedId)
                throw new InvalidOperationException($"widget.json id is '{manifest.Id}' but the folder is '{expectedId}'; they must match");
            if (!File.Exists(Path.Combine(staging, manifest.Entry.Replace('/', Path.DirectorySeparatorChar))))
                throw new InvalidOperationException($"Entry file '{manifest.Entry}' is missing");
            manifest.RequireCompatible();

            var installed = InstalledManifest(manifest.Id);
            return new PendingInstall(staging, manifest, PermissionDiff.Compute(installed, manifest), isUpdate: installed is not null, local: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            // A corrupt zip, a locked file or an odd path: one clear refusal type for the callers.
            try { Directory.Delete(staging, recursive: true); } catch { }
            throw new InvalidOperationException($"Could not read the widget: {ex.Message}", ex);
        }
        catch
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
            throw;
        }
    }

    const int MaxRedirects = 5;

    static readonly string[] BlockedHostSuffixes = { ".local", ".localhost", ".lan", ".internal", ".intranet", ".home", ".corp", ".localdomain", ".home.arpa" };

    /// <summary>
    /// Why <paramref name="url"/> can't be downloaded as a widget package, or null: HTTPS only, a public-looking host name (no IP
    /// address, localhost, single-label or local-network name, with or without a trailing dot), no user name or password, the
    /// standard port, at most 2048 characters. Names that resolve to a private address are refused at connect time
    /// (<see cref="CreateDownloadHandler"/>).
    /// </summary>
    public static string? CheckDownloadUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048 || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return "That is not a valid web address";
        if (u.Scheme != Uri.UriSchemeHttps) return "Only https:// addresses are allowed";
        if (u.UserInfo.Length > 0) return "Addresses with a user name or password are not allowed";
        if (!u.IsDefaultPort) return "Only the standard https port is allowed";
        var host = u.IdnHost.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0 || System.Net.IPAddress.TryParse(host, out _) || host == "localhost" || !host.Contains('.')
            || BlockedHostSuffixes.Any(s => host.EndsWith(s, StringComparison.Ordinal)))
            return "Only public web sites are allowed (no IP addresses, localhost or local network names)";
        return null;
    }

    /// <summary>True for loopback, private, link-local, unique-local, carrier-grade NAT, multicast and other non-public addresses (IPv4-mapped IPv6 included).</summary>
    internal static bool IsBlockedAddress(System.Net.IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (System.Net.IPAddress.IsLoopback(a) || a.Equals(System.Net.IPAddress.Any) || a.Equals(System.Net.IPAddress.IPv6Any) || a.Equals(System.Net.IPAddress.None)) return true;
        var b = a.GetAddressBytes();
        if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC /* fc00::/7 */ || a.IsIPv6Teredo;
        return b[0] == 0 || b[0] == 10 || b[0] >= 224
            || (b[0] == 100 && b[1] is >= 64 and <= 127)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 192 && b[1] == 0 && b[2] == 0)
            || (b[0] == 198 && b[1] is 18 or 19);
    }

    /// <summary>
    /// The handler for package downloads: no automatic redirects (every hop is checked by the caller), no cookies, no
    /// decompression (the 5 MB cap counts the bytes on the wire), and a connect step that resolves the name itself and refuses
    /// any non-public address, so it also covers redirects and DNS rebinding. When the request goes through a proxy
    /// (<paramref name="viaProxy"/>) the proxy makes the connection and only the name checks apply.
    /// <paramref name="resolve"/> and <paramref name="useProxy"/> exist for tests.
    /// </summary>
    internal static SocketsHttpHandler CreateDownloadHandler(Func<bool>? viaProxy = null, Func<string, CancellationToken, Task<System.Net.IPAddress[]>>? resolve = null, bool useProxy = true)
    {
        var handler = useProxy && Prefs.CreateHandler() is SocketsHttpHandler s ? s : new SocketsHttpHandler { UseProxy = false };
        handler.AllowAutoRedirect = false;
        handler.UseCookies = false;
        handler.AutomaticDecompression = System.Net.DecompressionMethods.None;
        resolve ??= async (host, ct) => await System.Net.Dns.GetHostAddressesAsync(host, ct);
        handler.ConnectCallback = async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = System.Net.IPAddress.TryParse(host, out var literal) ? new[] { literal } : await resolve(host, ct);
            if (viaProxy?.Invoke() != true && addresses.Any(IsBlockedAddress))
                throw new HttpRequestException($"'{host}' points to a private or local address, which widget downloads never use");
            Exception? last = null;
            foreach (var address in addresses)
            {
                var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new System.Net.IPEndPoint(address, context.DnsEndPoint.Port), ct);
                    return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception ex) when (ex is System.Net.Sockets.SocketException) { socket.Dispose(); last = ex; }
            }
            throw new HttpRequestException($"Could not connect to '{host}'", last);
        };
        return handler;
    }

    /// <summary>
    /// Downloads a package to a new temp file: every hop (the URL and each redirect, at most 5) must pass
    /// <see cref="CheckDownloadUrl"/> and connect to a public address, no credentials or cookies are sent, at most 5 MB are read
    /// and the whole thing has 60 seconds. The body must start like a zip. Returns the temp file (named .umbrawidget when the
    /// final URL says so, else .zip); the caller deletes it. <paramref name="handler"/> replaces the network (tests).
    /// </summary>
    public static async Task<string> DownloadPackageAsync(string url, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        var current = new Uri(url.Trim());
        var proxied = false;
        var own = handler is null ? CreateDownloadHandler(() => proxied) : null;
        using var client = new HttpClient(handler ?? own!, disposeHandler: handler is null) { Timeout = TimeSpan.FromSeconds(60) };
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (CheckDownloadUrl(current.AbsoluteUri) is { } bad) throw new InvalidOperationException(hop == 0 ? bad : $"A redirect was refused: {bad}");
            proxied = own is { UseProxy: true, Proxy: { } px } && !px.IsBypassed(current);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var next = response.Headers.Location ?? throw new InvalidOperationException("The server redirected without saying where");
                current = next.IsAbsoluteUri ? next : new Uri(current, next);
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"The download failed: HTTP {(int)response.StatusCode}");
            if (response.Content.Headers.ContentLength > MaxWidgetBytes) throw new InvalidOperationException("The file is larger than 5 MB");

            var ext = current.AbsolutePath.EndsWith(PackageExtension, StringComparison.OrdinalIgnoreCase) || new Uri(url.Trim()).AbsolutePath.EndsWith(PackageExtension, StringComparison.OrdinalIgnoreCase) ? PackageExtension : ".zip";
            var path = Path.Combine(Path.GetTempPath(), $"umbra-dl-{Guid.NewGuid():N}{ext}");
            try
            {
                await using var input = await response.Content.ReadAsStreamAsync(cts.Token);
                await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int n;
                    while ((n = await input.ReadAsync(buffer, cts.Token)) > 0)
                    {
                        total += n;
                        if (total > MaxWidgetBytes) throw new InvalidOperationException("The file is larger than 5 MB");
                        await output.WriteAsync(buffer.AsMemory(0, n), cts.Token);
                    }
                }
                var head = new byte[4];
                await using (var check = File.OpenRead(path)) _ = await check.ReadAsync(head, cts.Token);
                if (!(head[0] == 'P' && head[1] == 'K' && head[2] == 3 && head[3] == 4))
                    throw new InvalidOperationException($"{current.Host} did not send a {PackageExtension} or .zip file (the address may be a web page, not the file itself)");
                return path;
            }
            catch
            {
                try { File.Delete(path); } catch { }
                throw;
            }
        }
        throw new InvalidOperationException($"Too many redirects (more than {MaxRedirects})");
    }

    /// <summary>
    /// "Install from URL": downloads (<see cref="DownloadPackageAsync"/>), then runs the exact checks of an install from a file
    /// (<see cref="PrepareLocal"/>). Like any file install it is local: any access it declares needs the user's accept.
    /// </summary>
    public static async Task<PendingInstall> PrepareUrlAsync(string url, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        string file;
        try { file = await DownloadPackageAsync(url, handler, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new InvalidOperationException($"Could not download the widget: {(ex.InnerException is { } inner && ex.Message.StartsWith("An error occurred", StringComparison.Ordinal) ? inner.Message : ex.Message)}", ex);
        }
        try { return await Task.Run(() => PrepareLocal(file), ct); }
        finally { try { File.Delete(file); } catch { } }
    }

    /// <summary>
    /// For install-url over the control pipe: null when <paramref name="id"/> is free (or <paramref name="overwrite"/> is set), else a sentence
    /// naming what would be replaced. An installed copy and a catalog widget with that id both count.
    /// </summary>
    public static string? ReplacementWarning(string id, WidgetManifest? installed, bool installedFromFile, bool inCatalog, bool overwrite)
    {
        if (overwrite) return null;
        if (installed is not null)
            return $"'{id}' is already installed ({installed.Name} v{installed.Version}, {(installedFromFile ? "from a file" : "from the catalog")}); this download would replace it. Repeat with --overwrite to replace it";
        if (inCatalog)
            return $"'{id}' is the id of a widget in the community catalog; this download would take its place. Repeat with --overwrite if that is what you want";
        return null;
    }

    static readonly object LocalInstallsLock = new();
    static string LocalInstallsFile => Path.Combine(AppPaths.DataDir, "local-installs.json");

    /// <summary>
    /// True when the installed copy came from a file or folder (Install from file, umbractl install-local, create-widget)
    /// rather than the catalog. Such a copy is never offered a catalog update and update-all skips it.
    /// </summary>
    public static bool IsLocalInstall(string id) => LocalInstalls().Contains(id);

    static HashSet<string> LocalInstalls()
    {
        lock (LocalInstallsLock)
        {
            try
            {
                return File.Exists(LocalInstallsFile)
                    ? new HashSet<string>(JsonSerializer.Deserialize<string[]>(File.ReadAllText(LocalInstallsFile)) ?? Array.Empty<string>(), StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new HashSet<string>(StringComparer.Ordinal); }
        }
    }

    /// <summary>Records where the installed copy of <paramref name="id"/> came from (false = catalog or removed).</summary>
    internal static void MarkLocalInstall(string id, bool local)
    {
        lock (LocalInstallsLock)
        {
            var set = LocalInstalls();
            if (!(local ? set.Add(id) : set.Remove(id))) return;
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                File.WriteAllText(LocalInstallsFile, JsonSerializer.Serialize(set.Order(StringComparer.Ordinal).ToArray()));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Write($"Could not save local-installs.json: {ex.Message}"); }
        }
    }

    /// <summary>Copies a widget folder into staging; returns the folder name, which widget.json's id must equal. Links are refused.</summary>
    static string StageFolder(string folder, string staging)
    {
        var root = Path.GetFullPath(folder).TrimEnd('\\', '/');
        if (new DirectoryInfo(root).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException("The widget folder is a link; pick the real folder");
        long total = 0;
        var count = 0;
        var dirs = new Stack<string>();
        dirs.Push(root);
        while (dirs.Count > 0)
        {
            var dir = dirs.Pop();
            foreach (var sub in Directory.GetDirectories(dir))
            {
                if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException($"Links are not allowed: '{Path.GetRelativePath(root, sub)}'");
                dirs.Push(sub);
            }
            foreach (var file in Directory.GetFiles(dir))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (new FileInfo(file).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException($"Links are not allowed: '{rel}'");
                if (++count > MaxWidgetFiles) throw new InvalidOperationException($"Too many files (more than {MaxWidgetFiles})");
                using var s = File.OpenRead(file);
                StageFile(staging, rel, s, ref total);
            }
        }
        return Path.GetFileName(root);
    }

    /// <summary>
    /// Extracts a zip into staging, entry by entry, never trusting the entry names. A zip whose files all sit in one top folder
    /// (what "Send to > Compressed folder" makes) has that folder stripped, and widget.json's id must equal it. Returns that
    /// folder name, or null when widget.json is at the top of the zip. A <see cref="PackageExtension"/> file
    /// (<paramref name="allowWrapper"/> false) must have widget.json at the top.
    /// </summary>
    static string? StageZip(string zipPath, string staging, bool allowWrapper)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
        var entries = new List<(string Name, System.IO.Compression.ZipArchiveEntry Entry)>();
        foreach (var e in zip.Entries)
        {
            // Some Windows tools write backslashes; normalize first, so "..\x" is caught by the same checks as "../x".
            var name = e.FullName.Replace('\\', '/');
            const int S_IFMT = 0xF000, S_IFLNK = 0xA000;
            if (((e.ExternalAttributes >> 16) & S_IFMT) == S_IFLNK) throw new InvalidOperationException($"Links are not allowed in a widget zip: '{name}'");
            if (name.EndsWith('/') && e.Length == 0) continue; // a folder entry
            if (entries.Count >= MaxWidgetFiles) throw new InvalidOperationException($"Too many files (more than {MaxWidgetFiles})");
            entries.Add((name, e));
        }

        string? prefix = null;
        if (!entries.Any(x => x.Name == "widget.json"))
        {
            if (!allowWrapper) throw new InvalidOperationException($"No widget.json found at the top of the {PackageExtension} file (a package has no wrapper folder)");
            var top = entries.Select(x => x.Name.Split('/')[0]).Distinct(StringComparer.Ordinal).ToList();
            if (top.Count == 1 && WidgetManifest.IdPattern().IsMatch(top[0]) && entries.Any(x => x.Name == top[0] + "/widget.json"))
                prefix = top[0];
        }

        long total = 0;
        foreach (var (name, e) in entries)
        {
            // A file named exactly like the top folder (or "folder/" with data) has no path inside the widget.
            if (prefix is not null && (name.Length <= prefix.Length + 1 || !name.StartsWith(prefix + "/", StringComparison.Ordinal)))
                throw new InvalidOperationException($"Unsafe or disallowed file '{name}' (relative paths and widget file types only)");
            var rel = prefix is null ? name : name[(prefix.Length + 1)..];
            using var s = e.Open();
            StageFile(staging, rel, s, ref total);
        }
        return prefix;
    }

    /// <summary>Writes one file into staging after the path, type, duplicate and size checks. Reads at most what the 5 MB budget allows.</summary>
    static void StageFile(string staging, string rel, Stream source, ref long total)
    {
        if (!IsSafeRelativePath(rel)) throw new InvalidOperationException($"Unsafe or disallowed file '{rel}' (relative paths and widget file types only)");
        var root = Path.GetFullPath(staging).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var dest = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Unsafe file path '{rel}'");
        if (File.Exists(dest)) throw new InvalidOperationException($"'{rel}' is in the widget twice");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

        // Counts real bytes (a zip's declared sizes can lie), and stops at the first chunk past the limit.
        var buffer = new byte[81920];
        using var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write);
        int n;
        while ((n = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += n;
            if (total > MaxWidgetBytes) throw new InvalidOperationException("Widget is larger than 5 MB");
            output.Write(buffer, 0, n);
        }
    }

    /// <summary>The community copy on disk, or null when the widget is not installed (or its widget.json does not load, so it holds no access).</summary>
    static WidgetManifest? InstalledManifest(string id)
    {
        try
        {
            var file = Path.Combine(AppPaths.CommunityDir, id, "widget.json");
            return File.Exists(file) ? WidgetManifest.Parse(File.ReadAllText(file)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return null; }
    }

    /// <summary>A downloaded, validated widget that is not installed yet. Dispose removes the staging folder.</summary>
    public sealed class PendingInstall : IDisposable
    {
        readonly string _staging;
        public WidgetManifest Manifest { get; }
        /// <summary>Permissions the installed copy lacks (everything, for a fresh install).</summary>
        public PermissionDiff Added { get; }

        readonly bool _isUpdate;

        /// <summary>From a file or folder the user picked (not the reviewed catalog): any declared access needs an accept.</summary>
        public bool IsLocal { get; }

        /// <summary>True when an installed copy already exists (the commit replaces it).</summary>
        public bool IsUpdate => _isUpdate;

        /// <summary>Every staged file as a forward-slash relative path, sorted ordinally.</summary>
        internal IEnumerable<string> Files() =>
            Directory.GetFiles(_staging, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(_staging, f).Replace('\\', '/'))
                .Order(StringComparer.Ordinal);

        /// <summary>Full path of a staged file (a relative path from <see cref="Files"/>).</summary>
        internal string PathOf(string rel) => Path.Combine(_staging, rel.Replace('/', Path.DirectorySeparatorChar));

        internal PendingInstall(string staging, WidgetManifest manifest, PermissionDiff added, bool isUpdate, bool local = false)
        {
            _staging = staging; Manifest = manifest; Added = added; _isUpdate = isUpdate; IsLocal = local;
        }

        /// <summary>True when the widget would gain access (an update, or any local install): the user must accept it first.</summary>
        public bool NeedsApproval => (_isUpdate || IsLocal) && !Added.IsEmpty;

        /// <summary>Throws <see cref="PermissionsChangedException"/> when approval is needed and <paramref name="accepted"/> is false.</summary>
        public void Require(bool accepted)
        {
            if (NeedsApproval && !accepted) throw new PermissionsChangedException(Manifest.Name, Manifest.Version, Added);
        }

        /// <summary>Replaces the installed folder. Call <see cref="Require"/> first.</summary>
        public void Commit()
        {
            var fresh = !Directory.Exists(Path.Combine(AppPaths.CommunityDir, Manifest.Id));
            Community.Commit(_staging, Manifest.Id);
            MarkLocalInstall(Manifest.Id, IsLocal);
            Log.Write($"Installed {(IsLocal ? "local" : "community")} widget {Manifest.Id} {Manifest.Version}");
            InstallStats.Installed(Manifest.Id, fresh, IsLocal);
        }

        public void Dispose()
        {
            try { Directory.Delete(_staging, recursive: true); } catch { }
        }
    }

    static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".woff", ".woff2", ".ttf", ".otf",
    };

    /// <summary>
    /// Writes a widget authored locally (umbractl create-widget, MCP create_widget) into the community folder.
    /// Same rules as a catalog install: safe relative paths, allowed file types, 5 MB, widget.json id = folder.
    /// Text files are passed as text, binary files (images, fonts) as base64.
    /// </summary>
    public static string WriteLocalWidget(string id, IReadOnlyDictionary<string, string> files, bool overwrite)
    {
        if (!WidgetManifest.IdPattern().IsMatch(id)) throw new InvalidOperationException($"Invalid widget id '{id}': use 2-40 lowercase letters, digits and dashes");
        if (!files.ContainsKey("widget.json")) throw new InvalidOperationException("files must include widget.json");
        var target = Path.Combine(AppPaths.CommunityDir, id);
        if (Directory.Exists(target) && !overwrite) throw new InvalidOperationException($"Widget '{id}' already exists; pass overwrite=true to replace it");

        var manifest = WidgetManifest.Parse(files["widget.json"]);
        if (manifest.Id != id) throw new InvalidOperationException($"widget.json id is '{manifest.Id}', expected '{id}'");
        if (!files.ContainsKey(manifest.Entry)) throw new InvalidOperationException($"Entry file '{manifest.Entry}' is missing from files");
        manifest.RequireCompatible();

        var staging = Path.Combine(Path.GetTempPath(), $"umbra-{id}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            long total = 0;
            foreach (var (rel, content) in files)
            {
                if (!IsSafeRelativePath(rel)) throw new InvalidOperationException($"Unsafe or disallowed file path '{rel}'");
                byte[] data;
                try { data = BinaryExtensions.Contains(Path.GetExtension(rel)) ? Convert.FromBase64String(content) : System.Text.Encoding.UTF8.GetBytes(content); }
                catch (FormatException) { throw new InvalidOperationException($"'{rel}' is a binary file and must be base64"); }
                total += data.Length;
                if (total > MaxWidgetBytes) throw new InvalidOperationException("Widget is larger than 5 MB");
                var dest = Path.Combine(staging, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.WriteAllBytes(dest, data);
            }
            Commit(staging, id);
            MarkLocalInstall(id, true);
            Log.Write($"Wrote local widget {id} ({files.Count} files)");
            return target;
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    /// <summary>Replaces the widget's community folder with a fully validated staging folder.</summary>
    static void Commit(string staging, string id)
    {
        var target = Path.Combine(AppPaths.CommunityDir, id);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        CopyDirectory(staging, target);
    }

    /// <summary>
    /// Same checks a catalog install does, for a widget folder that is already on disk (a trash entry about to be restored):
    /// widget.json parses and its id equals <paramref name="id"/>, the entry file exists, every file has a safe relative path
    /// and an allowed type, and the total size is within 5 MB. Throws <see cref="InvalidOperationException"/> otherwise.
    /// </summary>
    internal static void ValidateFolder(string folder, string id)
    {
        var manifestPath = Path.Combine(folder, "widget.json");
        if (!File.Exists(manifestPath)) throw new InvalidOperationException("The trash entry has no widget.json");
        WidgetManifest manifest;
        try { manifest = WidgetManifest.Parse(File.ReadAllText(manifestPath)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { throw new InvalidOperationException($"The trashed widget.json is invalid: {ex.Message}"); }
        if (manifest.Id != id) throw new InvalidOperationException($"The trashed widget.json id is '{manifest.Id}', expected '{id}'");
        long total = 0;
        foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(folder, file).Replace('\\', '/');
            if (!IsSafeRelativePath(rel)) throw new InvalidOperationException($"Unsafe or disallowed file in the trash entry: '{rel}'");
            total += new FileInfo(file).Length;
            if (total > MaxWidgetBytes) throw new InvalidOperationException("The trashed widget is larger than 5 MB");
        }
        if (!File.Exists(Path.Combine(folder, manifest.Entry.Replace('/', Path.DirectorySeparatorChar)))) throw new InvalidOperationException($"Entry file '{manifest.Entry}' is missing from the trash entry");
    }

    public static void Remove(string id)
    {
        if (!WidgetManifest.IdPattern().IsMatch(id)) return;
        var target = Path.Combine(AppPaths.CommunityDir, id);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        // A legacy id may also exist as a stale built-in folder from an old install (per-user, so writable).
        if (Restore.LegacyIds.Contains(id))
        {
            var old = Path.Combine(AppPaths.WidgetsDir, id);
            try { if (Directory.Exists(old)) Directory.Delete(old, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Write($"Could not delete old built-in copy of {id}: {ex.Message}"); }
        }
        WidgetStore.DeleteAll(id);
        CommandApprovals.Shared.Revoke(id);
        // The local-install mark stays, so Undo from the trash keeps it; every install path rewrites it anyway.
        // Working folders of every copy ("sp500" and "sp500__2", ...).
        var runRoot = Path.Combine(AppPaths.DataDir, "data", "run");
        if (Directory.Exists(runRoot))
            foreach (var d in Directory.GetDirectories(runRoot).Where(d => { var n = Path.GetFileName(d); return n == id || n.StartsWith(id + "__", StringComparison.Ordinal); }))
                try { Directory.Delete(d, recursive: true); } catch (IOException) { }
        Log.Write($"Removed community widget {id}");
    }

    /// <summary>Relative, forward-slash, no "..", no drive letters, allowed file types only.</summary>
    internal static bool IsSafeRelativePath(string rel)
    {
        if (string.IsNullOrWhiteSpace(rel) || rel.Contains('\\') || rel.Contains(':') || rel.StartsWith('/')) return false;
        if (rel.Split('/').Any(p => p is "" or "." or "..")) return false;
        return AllowedExtensions.Contains(Path.GetExtension(rel));
    }

    static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    /// <summary>
    /// True when the catalog entry is newer than the installed community copy. Built-in widgets, private widgets and copies
    /// installed from a file (<see cref="IsLocalInstall"/>) never update from the catalog, so update-all skips them too.
    /// </summary>
    public static bool IsUpdate(JsonNode? entry, WidgetManifest? installed) =>
        installed is { Source: WidgetSource.Community, Private: false }
        && !IsLocalInstall(installed.Id)
        && CompareVersions(entry?["version"]?.GetValue<string>() ?? "0", installed.Version) > 0
        && CatalogIncompatibility(entry) is null;

    /// <summary>
    /// Why this Umbra can't install a catalog entry (its manifestVersion or minUmbraVersion is too new), or null.
    /// Browse greys such an entry out with this text, and it is never offered as an update.
    /// </summary>
    public static string? CatalogIncompatibility(JsonNode? entry)
    {
        // Missing means 1; a present but malformed value is invalid, never silently 1.
        var mv = 1;
        if (entry is JsonObject o && o.TryGetPropertyValue("manifestVersion", out var node))
        {
            if (node is JsonValue v && v.TryGetValue<int>(out var n) && n >= 1) mv = n;
            else return "its manifestVersion is invalid";
        }
        var min = entry?["minUmbraVersion"] is JsonValue s && s.TryGetValue<string>(out var t) ? t : null;
        return WidgetManifest.Incompatibility(mv, min);
    }

    /// <summary>The catalog entries that would update an installed widget, in catalog order.</summary>
    public static List<JsonNode> Outdated(IEnumerable<JsonNode?> catalog, IEnumerable<WidgetManifest> installed)
    {
        var byId = installed.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First());
        return catalog
            .Where(e => e?["id"]?.GetValue<string>() is { } id && IsUpdate(e, byId.GetValueOrDefault(id)))
            .Select(e => e!).ToList();
    }

    /// <summary>
    /// Installs the given updates one after another. A failure is recorded and the rest still run, so the caller
    /// can report every failure together. <paramref name="progress"/> gets (index from 1, total, widget id) before each one.
    /// </summary>
    public static async Task<UpdateAllResult> UpdateAllAsync(IReadOnlyList<JsonNode> entries, Func<JsonNode, Task> install, Action<int, int, string>? progress = null)
    {
        var result = new UpdateAllResult();
        for (var i = 0; i < entries.Count; i++)
        {
            var id = entries[i]["id"]?.GetValue<string>() ?? "?";
            progress?.Invoke(i + 1, entries.Count, id);
            try
            {
                await install(entries[i]);
                result.Updated.Add((id, entries[i]["version"]?.GetValue<string>() ?? ""));
            }
            catch (Exception ex)
            {
                Log.Write($"Update of {id} failed: {ex.Message}");
                result.Failed.Add((id, ex.Message));
            }
        }
        return result;
    }

    /// <summary>Semantic-ish version compare ("1.2.0" &gt; "1.10.0" is handled numerically).</summary>
    public static int CompareVersions(string a, string b)
    {
        static int[] Parts(string v) => v.Split('.', '-').Take(3).Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        var pa = Parts(a); var pb = Parts(b);
        for (int i = 0; i < 3; i++)
        {
            var x = i < pa.Length ? pa[i] : 0; var y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }
}

/// <summary>Outcome of "update all": what was updated and what failed (with the reason).</summary>
sealed class UpdateAllResult
{
    public List<(string Id, string Version)> Updated { get; } = new();
    public List<(string Id, string Error)> Failed { get; } = new();

    public JsonObject ToJson() => new()
    {
        ["updated"] = new JsonArray(Updated.Select(u => (JsonNode)new JsonObject { ["id"] = u.Id, ["version"] = u.Version }).ToArray()),
        ["failed"] = new JsonArray(Failed.Select(f => (JsonNode)new JsonObject { ["id"] = f.Id, ["error"] = f.Error }).ToArray()),
    };
}
