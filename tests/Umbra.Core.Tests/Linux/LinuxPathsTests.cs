using Umbra.Linux;

namespace Umbra.Tests;

public class LinuxPathsTests
{
    static Func<string, string?> Env(params (string K, string V)[] vars)
    {
        var d = vars.ToDictionary(v => v.K, v => v.V);
        return k => d.GetValueOrDefault(k);
    }

    static readonly Func<string, bool> NoFiles = _ => false;

    [Fact]
    public void Xdg_unset_falls_back_to_home_local_share()
    {
        var p = LinuxAppPaths.Resolve(Env(("HOME", "/home/ada")), "/opt/umbra", NoFiles);
        Assert.Equal("/home/ada/.local/share/umbra", p.DataDir);
        Assert.Equal("/home/ada/.cache/umbra", p.CacheDir);
        Assert.False(p.IsPortable);
    }

    [Fact]
    public void Xdg_set_is_used_for_data_and_cache()
    {
        var p = LinuxAppPaths.Resolve(Env(("HOME", "/home/ada"), ("XDG_DATA_HOME", "/data/xdg"), ("XDG_CACHE_HOME", "/cache/xdg")), "/opt/umbra", NoFiles);
        Assert.Equal("/data/xdg/umbra", p.DataDir);
        Assert.Equal("/cache/xdg/umbra", p.CacheDir);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/dir")]
    [InlineData("  ")]
    public void Xdg_empty_or_relative_is_ignored(string bad)
    {
        var p = LinuxAppPaths.Resolve(Env(("HOME", "/home/ada"), ("XDG_DATA_HOME", bad), ("XDG_CACHE_HOME", bad)), "/opt/umbra", NoFiles);
        Assert.Equal("/home/ada/.local/share/umbra", p.DataDir);
        Assert.Equal("/home/ada/.cache/umbra", p.CacheDir);
    }

    [Fact]
    public void Home_unset_uses_the_fallback()
    {
        var p = LinuxAppPaths.Resolve(Env(), "/opt/umbra", NoFiles, "/home/fallback");
        Assert.Equal("/home/fallback/.local/share/umbra", p.DataDir);
    }

    [Fact]
    public void Layout_matches_the_windows_data_folder()
    {
        var p = LinuxAppPaths.Resolve(Env(("HOME", "/h")), "/opt/umbra", NoFiles);
        Assert.Equal("/h/.local/share/umbra/settings.json", p.SettingsFile);
        Assert.Equal("/h/.local/share/umbra/widgets", p.WidgetsDir);
        Assert.Equal("/h/.local/share/umbra/data", p.StoreDir);
        Assert.Equal("/h/.local/share/umbra/themes", p.ThemesDir);
        Assert.Equal("/h/.local/share/umbra/trash", p.TrashDir);
        Assert.Equal("/h/.local/share/umbra/umbra.log", p.LogFile);
    }

    [Fact]
    public void Umbra_data_dir_wins_over_portable_and_xdg()
    {
        var p = LinuxAppPaths.Resolve(Env(("HOME", "/h"), ("XDG_DATA_HOME", "/x"), ("UMBRA_DATA_DIR", "/tmp/profile")), "/opt/umbra", _ => true);
        Assert.Equal("/tmp/profile", p.DataDir);
        Assert.Equal("/tmp/profile/cache", p.CacheDir); // a throwaway profile never touches ~/.cache
        Assert.Equal("/tmp/profile/settings.json", p.SettingsFile);
    }

    [Fact]
    public void Portable_marker_puts_data_next_to_the_binary()
    {
        var seen = new List<string>();
        var p = LinuxAppPaths.Resolve(Env(("HOME", "/h"), ("XDG_DATA_HOME", "/x")), "/opt/umbra/", f => { seen.Add(f); return true; });
        Assert.Equal(["/opt/umbra/portable.txt"], seen);
        Assert.True(p.IsPortable);
        Assert.Equal("/opt/umbra/data", p.DataDir);
        Assert.Equal("/opt/umbra/data/cache", p.CacheDir);
    }

    [Fact]
    public void Empty_umbra_data_dir_is_ignored()
    {
        var p = LinuxAppPaths.Resolve(Env(("HOME", "/h"), ("UMBRA_DATA_DIR", "")), "/opt/umbra", NoFiles);
        Assert.Equal("/h/.local/share/umbra", p.DataDir);
    }

    [Fact]
    public void Config_home_follows_xdg()
    {
        Assert.Equal("/h/.config", LinuxAppPaths.ConfigHome(Env(("HOME", "/h"))));
        Assert.Equal("/c", LinuxAppPaths.ConfigHome(Env(("HOME", "/h"), ("XDG_CONFIG_HOME", "/c"))));
    }

    [Fact]
    public void Flatpak_is_detected_by_the_info_file()
    {
        Assert.True(LinuxAppPaths.IsFlatpak(f => f == "/.flatpak-info"));
        Assert.False(LinuxAppPaths.IsFlatpak(NoFiles));
    }
}
