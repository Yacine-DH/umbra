using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Umbra;

/// <summary>A published Umbra release that can be installed.</summary>
public sealed record ReleaseInfo(string Version, string PageUrl, string AssetName, string AssetUrl, string Notes, string? Digest = null);

/// <summary>
/// Pure update logic: version comparison, GitHub release parsing and download validation.
/// No I/O here so it is unit tested; <see cref="UpdateService"/> does the network and process work.
/// </summary>
public static partial class Updates
{
    public const string ReleasesApi = "https://api.github.com/repos/AmineChr54/umbra/releases/latest";
    const string DownloadPrefix = "/AmineChr54/umbra/releases/download/";

    /// <summary>
    /// What the purple sidebar button shows: "none" (nothing to offer), "downloading" (staging, or the click is waiting for it),
    /// "ready" (installer verified, a click restarts into it) or "available" (a click starts the download; portable copies open the release page).
    /// </summary>
    public static string ButtonState(bool available, bool ready, bool installing, int? progress, bool portable)
    {
        if (!available) return "none";
        if (portable) return "available";
        if (ready) return "ready";
        return installing || progress is not null ? "downloading" : "available";
    }

    [GeneratedRegex(@"^v?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex SemVer();

    [GeneratedRegex(@"^UmbraSetup-(\d+\.\d+\.\d+)\.exe$")]
    private static partial Regex InstallerName();

    /// <summary>Compares semantic versions (a leading "v" and +build are ignored, a pre-release sorts below its release). Null when either is not a version.</summary>
    public static int? Compare(string a, string b)
    {
        var ma = SemVer().Match(a.Trim());
        var mb = SemVer().Match(b.Trim());
        if (!ma.Success || !mb.Success) return null;
        for (int i = 1; i <= 3; i++)
        {
            var c = long.Parse(ma.Groups[i].Value).CompareTo(long.Parse(mb.Groups[i].Value));
            if (c != 0) return Math.Sign(c);
        }
        var pa = ma.Groups[4].Value;
        var pb = mb.Groups[4].Value;
        if (pa == pb) return 0;
        if (pa.Length == 0) return 1;
        if (pb.Length == 0) return -1;
        var xa = pa.Split('.');
        var xb = pb.Split('.');
        for (int i = 0; i < Math.Min(xa.Length, xb.Length); i++)
        {
            var na = long.TryParse(xa[i], out var la);
            var nb = long.TryParse(xb[i], out var lb);
            int c = na && nb ? la.CompareTo(lb) : na ? -1 : nb ? 1 : string.CompareOrdinal(xa[i], xb[i]);
            if (c != 0) return Math.Sign(c);
        }
        return Math.Sign(xa.Length.CompareTo(xb.Length));
    }

    public static bool IsNewer(string current, string candidate) => Compare(candidate, current) is > 0;

    /// <summary>Notes of one release by tag. Only used with a version that passed <see cref="Compare"/>.</summary>
    public static string ReleaseByTagApi(string version) => $"https://api.github.com/repos/AmineChr54/umbra/releases/tags/v{version}";

    public static string ReleasePageUrl(string version) => $"https://github.com/AmineChr54/umbra/releases/tag/v{version}";

    /// <summary>True when Umbra was updated since it last ran. A first install (nothing recorded) or an unreadable value never announces.</summary>
    public static bool ShouldAnnounce(string? lastSeen, string current) =>
        !string.IsNullOrEmpty(lastSeen) && IsNewer(lastSeen, current);

    /// <summary>The version to record after this start: the running one when first seen or newer, never a downgrade (so going back and forth does not announce twice).</summary>
    public static string NextLastSeen(string? lastSeen, string current)
    {
        if (Compare(current, "0.0.0") is null) return lastSeen ?? "";
        return string.IsNullOrEmpty(lastSeen) || Compare(lastSeen, current) is null || IsNewer(lastSeen, current) ? current : lastSeen;
    }

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>
    /// Reads GitHub's "latest release" JSON. Returns null for drafts, pre-releases, bad versions or a release without
    /// a valid installer asset, so the app never offers something it would refuse to install.
    /// </summary>
    public static ReleaseInfo? ParseRelease(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); } catch (JsonException) { return null; }
        if (root is not JsonObject o) return null;
        if (o["draft"] is JsonValue d && d.TryGetValue<bool>(out var isDraft) && isDraft) return null;
        if (o["prerelease"] is JsonValue p && p.TryGetValue<bool>(out var isPre) && isPre) return null;
        var tag = Str(o["tag_name"]);
        var m = tag is null ? null : SemVer().Match(tag);
        if (m is not { Success: true } || m.Groups[4].Success) return null;
        var version = $"{m.Groups[1].Value}.{m.Groups[2].Value}.{m.Groups[3].Value}";

        foreach (var asset in o["assets"] as JsonArray ?? new JsonArray())
        {
            var name = Str(asset?["name"]);
            var url = Str(asset?["browser_download_url"]);
            if (name is null || url is null || !IsValidInstaller(url, name, version)) continue;
            var page = Str(o["html_url"]);
            if (page is null || !page.StartsWith("https://github.com/", StringComparison.Ordinal))
                page = $"https://github.com/AmineChr54/umbra/releases/tag/v{version}";
            var body = Str(o["body"]) ?? "";
            return new ReleaseInfo(version, page, name, url, body.Length > 2000 ? body[..2000] : body, Str(asset?["digest"]));
        }
        return null;
    }

    /// <summary>The Linux release asset name for one version and architecture ("x64" or "arm64"), as <c>linux/package.sh</c> writes it.</summary>
    public static string LinuxTarballName(string version, string arch) => $"umbra-{version}-linux-{arch}.tar.gz";

    /// <summary>"x64" or "arm64" for the Linux tarball of this machine, null for an architecture Umbra doesn't publish.</summary>
    public static string? LinuxArch(System.Runtime.InteropServices.Architecture a) => a switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "x64",
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        _ => null,
    };

    /// <summary>
    /// Reads GitHub's "latest release" JSON for the Linux update notice (notify only, nothing is downloaded): a stable release that
    /// has this architecture's tarball, so Linux is never told about a release without a Linux build. The page URL is always
    /// built from the version (never taken from the feed), because it is what the notice opens in the browser.
    /// </summary>
    public static ReleaseInfo? ParseLinuxNotice(string json, string arch)
    {
        if (arch is not ("x64" or "arm64")) return null;
        JsonNode? root;
        try { root = JsonNode.Parse(json); } catch (JsonException) { return null; }
        if (root is not JsonObject o) return null;
        if (o["draft"] is JsonValue d && d.TryGetValue<bool>(out var isDraft) && isDraft) return null;
        if (o["prerelease"] is JsonValue p && p.TryGetValue<bool>(out var isPre) && isPre) return null;
        var tag = Str(o["tag_name"]);
        var m = tag is null ? null : SemVer().Match(tag);
        if (m is not { Success: true } || m.Groups[4].Success) return null;
        var version = $"{m.Groups[1].Value}.{m.Groups[2].Value}.{m.Groups[3].Value}";
        var want = LinuxTarballName(version, arch);
        foreach (var asset in o["assets"] as JsonArray ?? new JsonArray())
        {
            var name = Str(asset?["name"]);
            var url = Str(asset?["browser_download_url"]);
            if (name != want || url is null || !IsReleaseDownload(url, name)) continue;
            var body = Str(o["body"]) ?? "";
            return new ReleaseInfo(version, ReleasePageUrl(version), name, url, body.Length > 2000 ? body[..2000] : body, Str(asset?["digest"]));
        }
        return null;
    }

    public const long MaxDownloadBytes = 200L * 1024 * 1024;

    /// <summary>Hosts the download may end up on after GitHub's redirects.</summary>
    public static bool IsAllowedDownloadHost(string host) =>
        host == "github.com" || host == "githubusercontent.com" || host.EndsWith(".githubusercontent.com", StringComparison.Ordinal);

    /// <summary>True when the API's "sha256:&lt;hex&gt;" digest equals the file's hash. A missing or other-algorithm digest never matches.</summary>
    public static bool DigestMatches(string? digest, string sha256Hex) =>
        digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && digest.Length == 71
        && string.Equals(digest[7..], sha256Hex, StringComparison.OrdinalIgnoreCase);

    /// <summary>WinVerifyTrust results Umbra installs: 0 (valid signature) or TRUST_E_NOSIGNATURE (unsigned release; the SHA-256 digest still protects it). Anything else, e.g. a bad or tampered signature, is refused.</summary>
    public static bool SignatureAcceptable(int winVerifyTrustResult) => winVerifyTrustResult is 0 or unchecked((int)0x800B0100);

    /// <summary>Installer switches for a self-update: no window, no message box, no restart prompt, no cancel; /UPDATE=1 tells Umbra.iss to start the app again afterwards.</summary>
    public static string InstallerArguments(string logPath) => UpdaterArgs.InstallerArguments(logPath);

    /// <summary>What a start finds after Umbra handed over to the installer: running the expected version means it worked, anything else means the installer failed.</summary>
    public enum UpdateOutcome { None, Applied, Failed }

    /// <summary>Reads the marker <see cref="UpdateService"/> leaves before it runs the installer and compares it with the running version.</summary>
    public static (UpdateOutcome Outcome, string? To, string? Log) ReadPending(string? json, string currentVersion)
    {
        if (string.IsNullOrWhiteSpace(json)) return (UpdateOutcome.None, null, null);
        JsonNode? root;
        try { root = JsonNode.Parse(json); } catch (JsonException) { return (UpdateOutcome.None, null, null); }
        var to = Str(root?["to"]);
        if (to is null || Compare(to, "0.0.0") is null) return (UpdateOutcome.None, null, null);
        return (Compare(currentVersion, to) is >= 0 ? UpdateOutcome.Applied : UpdateOutcome.Failed, to, Str(root?["log"]));
    }

    /// <summary>The download must be HTTPS from github.com's release downloads for this repo and named UmbraSetup-&lt;version&gt;.exe.</summary>
    public static bool IsValidInstaller(string url, string name, string version)
    {
        var nm = InstallerName().Match(name);
        if (!nm.Success || nm.Groups[1].Value != version) return false;
        return IsReleaseDownload(url, name);
    }

    /// <summary>HTTPS from github.com's release downloads for this repo, ending in <paramref name="name"/>.</summary>
    static bool IsReleaseDownload(string url, string name)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == Uri.UriSchemeHttps && uri.Host == "github.com" && uri.IsDefaultPort && uri.UserInfo.Length == 0
            && uri.AbsolutePath.StartsWith(DownloadPrefix, StringComparison.Ordinal)
            && !uri.AbsolutePath.Contains("..")
            && Uri.UnescapeDataString(uri.AbsolutePath).EndsWith("/" + name, StringComparison.Ordinal);
    }
}
