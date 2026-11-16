using System.Globalization;
using Umbra.Linux;

namespace Umbra.Tests;

public class DesktopLocaleTests
{
    static Func<string, string?> Env(params (string K, string V)[] vars)
    {
        var d = vars.ToDictionary(v => v.K, v => v.V);
        return k => d.GetValueOrDefault(k);
    }

    static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

    [Theory]
    [InlineData("en_US.UTF-8", "en-US")]
    [InlineData("de_DE.UTF-8@euro", "de-DE")]
    [InlineData("sr_RS@latin", "sr-RS")]
    [InlineData("fr", "fr")]
    [InlineData("es_419.UTF-8", "es-419")]
    [InlineData(" en_GB ", "en-GB")]
    public void Posix_names_become_bcp47_tags(string posix, string tag) => Assert.Equal(tag, DesktopLocale.TagFromPosix(posix));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("C")]
    [InlineData("C.UTF-8")]
    [InlineData("POSIX")]
    [InlineData("en_us")]
    [InlineData("EN_US")]
    [InlineData("en_US_x")]
    [InlineData("e")]
    [InlineData("<script>")]
    public void Odd_or_neutral_names_give_no_tag(string? posix) => Assert.Null(DesktopLocale.TagFromPosix(posix));

    [Fact]
    public void Gnome_clock_format_maps_to_hour12()
    {
        Assert.True(DesktopLocale.Hour12FromClockFormat("12h"));
        Assert.False(DesktopLocale.Hour12FromClockFormat("24h"));
        Assert.Null(DesktopLocale.Hour12FromClockFormat(null));
        Assert.Null(DesktopLocale.Hour12FromClockFormat("weird"));
    }

    [Fact]
    public void Gnome_24h_wins_over_an_en_US_LANG()
    {
        // The bug in #359: LANG=en_US.UTF-8 (12-hour culture) while GNOME is set to 24h.
        var (locale, hour12) = DesktopLocale.Resolve(Env(("LANG", "en_US.UTF-8")), null, "24h", EnUs);
        Assert.Equal("en-US", locale);
        Assert.False(hour12);
    }

    [Fact]
    public void Gnome_12h_wins_over_a_24_hour_locale()
    {
        var (locale, hour12) = DesktopLocale.Resolve(Env(("LANG", "de_DE.UTF-8")), null, "12h", EnUs);
        Assert.Equal("de-DE", locale);
        Assert.True(hour12);
    }

    [Fact]
    public void Without_gnome_the_lc_time_locale_decides()
    {
        // KDE: Region & Language sets LC_TIME; en_GB is a 24-hour locale although LANG is en_US.
        var (locale, hour12) = DesktopLocale.Resolve(Env(("LANG", "en_US.UTF-8"), ("LC_TIME", "en_GB.UTF-8")), null, null, EnUs);
        Assert.Equal("en-GB", locale);
        Assert.False(hour12);

        var us = DesktopLocale.Resolve(Env(("LANG", "de_DE.UTF-8"), ("LC_TIME", "en_US.UTF-8")), null, null, EnUs);
        Assert.Equal("en-US", us.Locale);
        Assert.True(us.Hour12);
    }

    [Fact]
    public void Lc_all_beats_lc_time_and_lang()
    {
        Assert.Equal("fr-FR", DesktopLocale.TimeLocale(Env(("LC_ALL", "fr_FR.UTF-8"), ("LC_TIME", "en_US.UTF-8"), ("LANG", "de_DE")), null));
        Assert.Equal("en-US", DesktopLocale.TimeLocale(Env(("LC_TIME", "en_US.UTF-8"), ("LANG", "de_DE")), null));
        Assert.Equal("de-DE", DesktopLocale.TimeLocale(Env(("LANG", "de_DE")), null));
        Assert.Null(DesktopLocale.TimeLocale(Env(), null));
    }

    [Fact]
    public void A_set_C_locale_stops_the_search()
    {
        // As in libc: LC_ALL=C means no locale, LANG is not consulted.
        Assert.Null(DesktopLocale.TimeLocale(Env(("LC_ALL", "C"), ("LANG", "de_DE.UTF-8")), null));
        var (locale, _) = DesktopLocale.Resolve(Env(("LC_ALL", "C"), ("LANG", "de_DE.UTF-8")), null, null, CultureInfo.InvariantCulture);
        Assert.Equal("en-US", locale);
    }

    [Fact]
    public void Plasma_localerc_is_used_when_the_session_did_not_export_lc_time()
    {
        const string rc = "[Formats]\nLANG=en_US.UTF-8\nLC_TIME=en_GB.UTF-8\n\n[Translations]\nLANGUAGE=en_US\n";
        Assert.Equal("en-GB", DesktopLocale.TimeLocale(Env(("LANG", "en_US.UTF-8")), rc));
        // An exported LC_TIME is what every app of the session uses: it wins over the file.
        Assert.Equal("de-DE", DesktopLocale.TimeLocale(Env(("LC_TIME", "de_DE.UTF-8")), rc));
        var (_, hour12) = DesktopLocale.Resolve(Env(("LANG", "en_US.UTF-8")), rc, null, EnUs);
        Assert.False(hour12);
    }

    [Fact]
    public void Ini_values_are_read_per_section()
    {
        const string rc = "[Other]\nLC_TIME=xx\n[Formats]\n LC_TIME = en_GB.UTF-8 \r\n";
        Assert.Equal("en_GB.UTF-8", DesktopLocale.IniValue(rc, "Formats", "LC_TIME"));
        Assert.Null(DesktopLocale.IniValue(rc, "Formats", "LANG"));
        Assert.Null(DesktopLocale.IniValue(null, "Formats", "LC_TIME"));
    }

    [Fact]
    public void Without_any_desktop_setting_the_process_culture_is_kept()
    {
        var (locale, hour12) = DesktopLocale.Resolve(Env(), null, null, CultureInfo.GetCultureInfo("de-DE"));
        Assert.Equal("de-DE", locale);
        Assert.False(hour12);
    }
}
