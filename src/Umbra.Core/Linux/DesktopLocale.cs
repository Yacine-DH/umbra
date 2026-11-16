using System.Globalization;

namespace Umbra.Linux;

/// <summary>
/// The time format widgets get on Linux (<c>settings.locale</c> and <c>settings.hour12</c>). On Windows both follow Region
/// settings through <see cref="CultureInfo.CurrentCulture"/>; on Linux .NET takes its culture from <c>LANG</c>, while the user's
/// choice lives in the desktop: GNOME's 12/24-hour switch (<c>org.gnome.desktop.interface clock-format</c>, read through the
/// settings portal) and the region format for times (<c>LC_TIME</c>, which KDE's Region &amp; Language page sets; also kept in
/// <c>~/.config/plasma-localerc</c>). Pure: the environment and both settings come in as parameters, so it is tested on any OS.
/// </summary>
public static class DesktopLocale
{
    /// <summary>
    /// A POSIX locale name as a BCP-47 tag: "de_DE.UTF-8@euro" → "de-DE", "sr_RS@latin" → "sr-RS". Null for "C", "POSIX",
    /// an empty value or anything that isn't a plain language[_REGION] name.
    /// </summary>
    public static string? TagFromPosix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var name = value.Trim();
        var cut = name.IndexOfAny(['.', '@']);
        if (cut >= 0) name = name[..cut];
        if (name is "C" or "POSIX") return null;
        var parts = name.Split('_');
        if (parts.Length > 2 || parts[0].Length is < 2 or > 3 || !parts[0].All(char.IsAsciiLetterLower)) return null;
        if (parts.Length == 1) return parts[0];
        var region = parts[1];
        if (!(region.Length == 2 && region.All(char.IsAsciiLetterUpper)) && !(region.Length == 3 && region.All(char.IsAsciiDigit))) return null;
        return $"{parts[0]}-{region}";
    }

    /// <summary>The value of <paramref name="key"/> in <c>[<paramref name="section"/>]</c> of an INI-style text (KDE config files); null when absent.</summary>
    public static string? IniValue(string? text, string section, string key)
    {
        if (text is null) return null;
        var inSection = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { inSection = line == $"[{section}]"; continue; }
            if (!inSection) continue;
            var eq = line.IndexOf('=');
            if (eq > 0 && line[..eq].Trim() == key) return line[(eq + 1)..].Trim();
        }
        return null;
    }

    /// <summary>
    /// The locale times are formatted in, as POSIX libc picks it for LC_TIME: <c>LC_ALL</c>, then <c>LC_TIME</c>, then KDE's
    /// saved <c>[Formats] LC_TIME</c> (for a session that didn't export it), then <c>LANG</c>. Null when none is a usable name.
    /// </summary>
    public static string? TimeLocale(Func<string, string?> env, string? plasmaLocalerc)
    {
        foreach (var value in new[] { env("LC_ALL"), env("LC_TIME"), IniValue(plasmaLocalerc, "Formats", "LC_TIME"), env("LANG") })
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            // The first variable that is set wins, as in libc, even when it is "C": that means "no locale", so en-US.
            return TagFromPosix(value);
        }
        return null;
    }

    /// <summary>GNOME's <c>clock-format</c>: "12h" → true, "24h" → false, anything else (unset, no GNOME) → null.</summary>
    public static bool? Hour12FromClockFormat(string? clockFormat) => clockFormat switch
    {
        "12h" => true,
        "24h" => false,
        _ => null,
    };

    /// <summary>
    /// What widgets get: the time locale (else the process culture) as an <c>Intl</c> tag, and a 12-hour clock when GNOME says so,
    /// else when that locale's short time pattern uses one (<see cref="Prefs.Hour12Of"/>, the same rule as Windows).
    /// </summary>
    public static (string Locale, bool Hour12) Resolve(Func<string, string?> env, string? plasmaLocalerc, string? gnomeClockFormat, CultureInfo fallback)
    {
        var culture = fallback;
        if (TimeLocale(env, plasmaLocalerc) is { } tag)
        {
            try { culture = CultureInfo.GetCultureInfo(tag); }
            catch (CultureNotFoundException) { }
        }
        return (Prefs.LocaleOf(culture), Hour12FromClockFormat(gnomeClockFormat) ?? Prefs.Hour12Of(culture));
    }
}
