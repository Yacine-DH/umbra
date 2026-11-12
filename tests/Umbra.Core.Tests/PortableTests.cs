namespace Umbra.Tests;

public class PortableTests
{
    static string TempApp()
    {
        var d = Path.Combine(Path.GetTempPath(), $"umbra-portable-{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Marker_switches_data_dir_to_app_data_folder()
    {
        var app = TempApp();
        try
        {
            Assert.False(PortableMode.IsPortable(app));
            Assert.EndsWith("Umbra", PortableMode.DataDir(app, null), StringComparison.OrdinalIgnoreCase); // Linux: $XDG_DATA_HOME/umbra
            File.WriteAllText(Path.Combine(app, PortableMode.Marker), "");
            Assert.True(PortableMode.IsPortable(app));
            Assert.Equal(Path.Combine(app, "data"), PortableMode.DataDir(app, null));
            Assert.Equal(Path.Combine(app, "data"), PortableMode.DataDir(app, ""));
        }
        finally { Directory.Delete(app, true); }
    }

    [Fact]
    public void Env_override_beats_the_marker()
    {
        var app = TempApp();
        try
        {
            File.WriteAllText(Path.Combine(app, PortableMode.Marker), "");
            Assert.Equal(@"C:\custom", PortableMode.DataDir(app, @"C:\custom"));
        }
        finally { Directory.Delete(app, true); }
    }

    [Fact]
    public void Hash_is_stable_case_insensitive_and_differs_per_folder()
    {
        Assert.Equal(PortableMode.Hash(@"C:\Tools\Umbra\data"), PortableMode.Hash(@"c:\tools\umbra\data\"));
        Assert.NotEqual(PortableMode.Hash(@"C:\A\data"), PortableMode.Hash(@"C:\B\data"));
        Assert.Equal(8, PortableMode.Hash(@"C:\A").Length);
    }

    [Fact]
    public void Installed_copy_keeps_the_plain_pipe_name_and_no_portable_state()
    {
        // The test host has no portable.txt next to it.
        Assert.False(AppPaths.IsPortable);
        Assert.Equal($"Umbra-{Environment.UserName}", Protocol.PipeName);
    }
}
