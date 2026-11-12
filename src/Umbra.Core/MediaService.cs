using System.Text.Json.Nodes;

namespace Umbra;

/// <summary>
/// Backs <c>umbra.media</c>: media session rules shared by every host (Windows reads System Media Transport Controls in <c>WindowsMedia</c>). Reading needs
/// <c>permissions.media</c> to contain "read", transport commands need "control" and a physical click on the widget.
/// </summary>
public static class MediaService
{
    public static readonly string[] Kinds = ["read", "control"];
    public static readonly string[] Commands = ["play", "pause", "toggle", "next", "previous"];
    /// <summary>Album art the host sends is at most this big (the data URL is ~4/3 of it); larger art is scaled down first.</summary>
    public const int MaxArtBytes = 512 * 1024;
    /// <summary>Art bigger than this at the source is not even read (players hand out art of a few hundred KB, browsers up to ~1 MB PNGs).</summary>
    public const int MaxArtSourceBytes = 16 * 1024 * 1024;
    /// <summary>The longest side, in pixels, of the art the host sends: enough for a widget a few hundred pixels wide at 2x.</summary>
    public const uint ArtEdge = 512;

    /// <summary>The size to scale art of <paramref name="width"/> x <paramref name="height"/> to: unchanged when it fits <see cref="ArtEdge"/>, else the long side becomes ArtEdge.</summary>
    public static (uint Width, uint Height) ArtSize(uint width, uint height)
    {
        if (width == 0 || height == 0) return (0, 0);
        var longest = Math.Max(width, height);
        if (longest <= ArtEdge) return (width, height);
        var scale = (double)ArtEdge / longest;
        return (Math.Max(1, (uint)Math.Round(width * scale)), Math.Max(1, (uint)Math.Round(height * scale)));
    }

    /// <summary>
    /// The <c>app</c> a widget sees: the platform's display name for the media session's app when it has one ("Firefox"), else a readable
    /// part of the app id ("Spotify.exe" or "...\VLC\vlc.exe" give "Spotify" and "vlc", a packaged "Pub.Name_hash!App" gives "App"), and null for an id that is only a hash
    /// (Firefox registers "308046B0AF4A39CB", which no widget should show).
    /// </summary>
    public static string? AppLabel(string? appId, string? displayName)
    {
        if (!string.IsNullOrWhiteSpace(displayName)) return displayName.Trim();
        var id = appId?.Trim();
        if (string.IsNullOrEmpty(id)) return null;
        var bang = id.LastIndexOf('!');
        if (bang >= 0) id = id[(bang + 1)..];
        var slash = id.LastIndexOfAny(['\\', '/']);
        if (slash >= 0) id = id[(slash + 1)..];
        if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) id = id[..^4];
        var dot = id.LastIndexOf('.');
        if (bang >= 0 && dot >= 0) id = id[(dot + 1)..];
        id = id.Trim();
        if (id.Length == 0 || LooksLikeHash(id)) return null;
        return id;
    }

    static bool LooksLikeHash(string s) =>
        (s.Length >= 8 && s.All(Uri.IsHexDigit) && s.Any(char.IsDigit)) || Guid.TryParse(s.Trim('{', '}'), out _);

    public static void Validate(IEnumerable<string> kinds)
    {
        foreach (var k in kinds)
            if (!Kinds.Contains(k)) throw new InvalidOperationException($"permissions.media: unknown entry '{k}' (allowed: {string.Join(", ", Kinds)})");
    }

    /// <summary>"image/png" or "image/jpeg" from the leading bytes, else null (anything else is not sent to the page).</summary>
    public static string? SniffImage(ReadOnlySpan<byte> b) =>
        b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 ? "image/png"
        : b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ? "image/jpeg" : null;

    public static bool IsCommand(string? c) => c is not null && Commands.Contains(c);
}
