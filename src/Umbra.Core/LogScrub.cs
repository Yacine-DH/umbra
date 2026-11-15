using System.Text.RegularExpressions;

namespace Umbra;

/// <summary>
/// Redacts a log tail for a pasted bug report (Settings > Copy diagnostics, <c>umbractl diagnostics</c>), on Windows and Linux:
/// known folders become tokens, any other user home (<c>X:\Users\name</c>, <c>/home/name</c>) too, the query and fragment of every
/// http(s) URL go (they can carry keys and tokens), the user and machine names are replaced, and each line is capped.
/// </summary>
public static class LogScrub
{
    public const int MaxLineLength = 300;

    static readonly Regex WindowsUserDir = new(@"[A-Za-z]:[\\/]+Users[\\/]+[^\\/\s""']+", RegexOptions.IgnoreCase);
    // Only a path that starts a token: not "/home/..." inside a URL such as https://example.com/home/page.
    static readonly Regex UnixUserDir = new(@"(?<![\w.:/~-])/home/[^/\s""']+", RegexOptions.None);

    /// <summary>The query and fragment of an http(s) URL (up to whitespace, a quote or a bracket), which can carry API keys.</summary>
    static readonly Regex UrlSecrets = new(@"(?<=https?://[^\s?#""'<>()\[\]]*)[?#][^\s""'<>()\[\]]*", RegexOptions.IgnoreCase);

    public static List<string> Scrub(IEnumerable<string> lines, IEnumerable<(string Path, string Token)> roots, IEnumerable<(string Name, string Token)>? names = null)
    {
        var ordered = roots.Where(r => r.Path.Trim('\\', '/').Length > 0)
            .Select(r => (Path: r.Path.TrimEnd('\\', '/'), r.Token)).OrderByDescending(r => r.Path.Length).ToList();
        var nameRx = (names ?? Array.Empty<(string, string)>())
            .Where(n => n.Name is { Length: >= 3 })
            .Select(n => (Rx: new Regex(@"(?<![A-Za-z0-9])" + Regex.Escape(n.Name) + @"(?![A-Za-z0-9])", RegexOptions.IgnoreCase), n.Token)).ToList();
        return lines.Select(line =>
        {
            foreach (var (path, token) in ordered)
            {
                line = line.Replace(path, token, StringComparison.OrdinalIgnoreCase);
                line = line.Replace(path.Replace('\\', '/'), token, StringComparison.OrdinalIgnoreCase);
            }
            line = WindowsUserDir.Replace(line, "%USERPROFILE%");
            line = UnixUserDir.Replace(line, "~");
            line = UrlSecrets.Replace(line, "");
            foreach (var (rx, token) in nameRx) line = rx.Replace(line, token);
            return line.Length > MaxLineLength ? line[..MaxLineLength] + "..." : line;
        }).ToList();
    }
}
