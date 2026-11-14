using System.Text;

namespace Umbra.Linux;

/// <summary>
/// Builds and reads freedesktop <c>.desktop</c> files for the autostart entry
/// (<c>~/.config/autostart/io.github.aminechr54.Umbra.desktop</c>). Pure text, no file access.
/// </summary>
public static class DesktopEntry
{
    /// <summary>The Linux app id, also the autostart file name without <c>.desktop</c>.</summary>
    public const string AppId = "io.github.aminechr54.Umbra";

    /// <summary>The autostart file path under the given XDG config home.</summary>
    public static string AutostartPath(string configHome) => LinuxAppPaths.Join(LinuxAppPaths.Join(configHome, "autostart"), AppId + ".desktop");

    /// <summary>Characters that force quoting an Exec argument (Desktop Entry spec, "The Exec key").</summary>
    static readonly char[] ExecReserved = " \t\n\"'\\><~|&;$*?#()`".ToCharArray();

    /// <summary>
    /// The autostart file: Exec is the launcher path plus <paramref name="args"/>. Throws for an empty path or any line break,
    /// since a newline would let a path inject extra keys.
    /// </summary>
    public static string Autostart(string launcherPath, IEnumerable<string>? args = null)
    {
        if (string.IsNullOrWhiteSpace(launcherPath)) throw new ArgumentException("launcher path is empty", nameof(launcherPath));
        var parts = new List<string> { launcherPath };
        if (args != null) parts.AddRange(args);
        var sb = new StringBuilder();
        sb.Append("[Desktop Entry]\n");
        sb.Append("Type=Application\n");
        sb.Append("Version=1.0\n");
        sb.Append("Name=Umbra\n");
        sb.Append("Comment=Desktop widgets\n");
        sb.Append("Exec=").Append(EscapeValue(FormatExec(parts))).Append('\n');
        sb.Append("Icon=").Append(AppId).Append('\n');
        sb.Append("Terminal=false\n");
        sb.Append("Categories=Utility;\n");
        sb.Append("StartupNotify=false\n");
        sb.Append("X-GNOME-Autostart-enabled=true\n");
        return sb.ToString();
    }

    /// <summary>Joins arguments into one Exec line, double-quoting and escaping those that need it; <c>%</c> becomes <c>%%</c>.</summary>
    public static string FormatExec(IEnumerable<string> args) => string.Join(' ', args.Select(QuoteArg));

    static string QuoteArg(string a)
    {
        if (a.IndexOfAny(['\n', '\r', '\0']) >= 0) throw new ArgumentException("Exec arguments cannot contain line breaks");
        a = a.Replace("%", "%%");
        if (a.Length > 0 && a.IndexOfAny(ExecReserved) < 0) return a;
        var sb = new StringBuilder("\"");
        foreach (var c in a)
        {
            if (c is '"' or '`' or '$' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    /// <summary>The string-value escaping of the file format: a backslash doubles, so a quoted-Exec backslash survives both layers.</summary>
    static string EscapeValue(string v) => v.Replace("\\", "\\\\");

    static string UnescapeValue(string v)
    {
        if (!v.Contains('\\')) return v;
        var sb = new StringBuilder();
        for (var i = 0; i < v.Length; i++)
        {
            if (v[i] == '\\' && i + 1 < v.Length)
            {
                i++;
                sb.Append(v[i] switch { 's' => ' ', 'n' => '\n', 't' => '\t', 'r' => '\r', _ => v[i] });
            }
            else sb.Append(v[i]);
        }
        return sb.ToString();
    }

    /// <summary>Keys of the <c>[Desktop Entry]</c> group (comments, other groups and localized keys such as <c>Name[de]</c> are skipped).</summary>
    public static Dictionary<string, string> Parse(string text)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        var inEntry = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '[')
            {
                inEntry = line == "[Desktop Entry]";
                continue;
            }
            if (!inEntry) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            if (key.Contains('[')) continue;
            keys.TryAdd(key, UnescapeValue(line[(eq + 1)..].TrimStart()));
        }
        return keys;
    }

    /// <summary>Splits an (already unescaped) Exec value into arguments, undoing <see cref="FormatExec"/>; <c>%%</c> becomes <c>%</c>.</summary>
    public static List<string> ParseExec(string exec)
    {
        var args = new List<string>();
        var cur = new StringBuilder();
        var has = false;
        var quoted = false;
        for (var i = 0; i < exec.Length; i++)
        {
            var c = exec[i];
            if (quoted)
            {
                if (c == '\\' && i + 1 < exec.Length) cur.Append(exec[++i]);
                else if (c == '"') quoted = false;
                else cur.Append(c);
            }
            else if (c == '"') { quoted = true; has = true; }
            else if (c is ' ' or '\t')
            {
                if (has) { args.Add(cur.ToString()); cur.Clear(); has = false; }
            }
            else if (c == '%' && i + 1 < exec.Length && exec[i + 1] == '%') { cur.Append('%'); i++; has = true; }
            else { cur.Append(c); has = true; }
        }
        if (has) args.Add(cur.ToString());
        return args;
    }

    /// <summary>
    /// True when the entry would start at login: an Application with an Exec, not <c>Hidden=true</c> and not
    /// <c>X-GNOME-Autostart-enabled=false</c>.
    /// </summary>
    public static bool IsAutostartEnabled(string text)
    {
        var k = Parse(text);
        if (!k.TryGetValue("Type", out var type) || type != "Application") return false;
        if (!k.TryGetValue("Exec", out var exec) || exec.Length == 0) return false;
        if (k.TryGetValue("Hidden", out var hidden) && hidden == "true") return false;
        if (k.TryGetValue("X-GNOME-Autostart-enabled", out var en) && en == "false") return false;
        return true;
    }
}
