using Umbra.Linux;

namespace Umbra.Tests;

public class LinuxAutostartTests
{
    static string Temp()
    {
        var d = Path.Combine(Path.GetTempPath(), $"umbra-autostart-{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Desktop_file_has_the_required_keys()
    {
        var text = DesktopEntry.Autostart("/opt/umbra/umbra.sh", ["--background"]);
        Assert.StartsWith("[Desktop Entry]\n", text);
        Assert.DoesNotContain('\r', text);
        var k = DesktopEntry.Parse(text);
        Assert.Equal("Application", k["Type"]);
        Assert.Equal("Umbra", k["Name"]);
        Assert.Equal("/opt/umbra/umbra.sh --background", k["Exec"]);
        Assert.Equal("true", k["X-GNOME-Autostart-enabled"]);
        Assert.Equal("io.github.aminechr54.Umbra", k["Icon"]);
        Assert.Equal("false", k["Terminal"]);
        Assert.True(DesktopEntry.IsAutostartEnabled(text));
    }

    [Fact]
    public void Autostart_path_is_under_config_autostart()
    {
        Assert.Equal("/h/.config/autostart/io.github.aminechr54.Umbra.desktop", DesktopEntry.AutostartPath("/h/.config"));
    }

    [Theory]
    [InlineData("/opt/Umbra App/umbra.sh")]
    [InlineData("/opt/umbra \"quoted\"/umbra.sh")]
    [InlineData("/opt/$HOME/`x`/umbra.sh")]
    [InlineData("/opt/back\\slash/umbra.sh")]
    [InlineData("/opt/100%/umbra.sh")]
    public void Exec_round_trips_awkward_paths(string path)
    {
        var text = DesktopEntry.Autostart(path, ["--background"]);
        var exec = DesktopEntry.Parse(text)["Exec"];
        Assert.Equal([path, "--background"], DesktopEntry.ParseExec(exec));
    }

    [Fact]
    public void Exec_quotes_a_path_with_spaces()
    {
        var exec = DesktopEntry.Parse(DesktopEntry.Autostart("/opt/Umbra App/umbra.sh"))["Exec"];
        Assert.Equal("\"/opt/Umbra App/umbra.sh\"", exec);
    }

    [Theory]
    [InlineData("/opt/umbra\nExec=evil")]
    [InlineData("")]
    public void Bad_launcher_paths_are_rejected(string path)
    {
        Assert.Throws<ArgumentException>(() => DesktopEntry.Autostart(path));
    }

    [Fact]
    public void Parse_ignores_comments_other_groups_and_localized_keys()
    {
        const string text = "# c\n[Desktop Entry]\nName=Umbra\nName[de]=Schatten\nExec=/bin/umbra\n\n[Desktop Action x]\nExec=/bin/other\n";
        var k = DesktopEntry.Parse(text);
        Assert.Equal("Umbra", k["Name"]);
        Assert.Equal("/bin/umbra", k["Exec"]);
        Assert.False(k.ContainsKey("Name[de]"));
    }

    [Theory]
    [InlineData("[Desktop Entry]\nType=Application\nExec=/bin/umbra\nHidden=true\n", false)]
    [InlineData("[Desktop Entry]\nType=Application\nExec=/bin/umbra\nX-GNOME-Autostart-enabled=false\n", false)]
    [InlineData("[Desktop Entry]\nType=Application\nExec=/bin/umbra\n", true)]
    [InlineData("[Desktop Entry]\nType=Application\n", false)]
    [InlineData("[Desktop Entry]\nType=Link\nExec=/bin/umbra\n", false)]
    [InlineData("", false)]
    public void IsAutostartEnabled_reads_hidden_and_gnome_flags(string text, bool expected)
    {
        Assert.Equal(expected, DesktopEntry.IsAutostartEnabled(text));
    }

    [Fact]
    public void File_backend_writes_and_removes_the_entry()
    {
        var dir = Temp();
        try
        {
            var path = Path.Combine(dir, "config", "autostart", "x.desktop");
            var a = new DesktopFileAutostart(path, "/opt/umbra/umbra.sh", ["--background"]);
            Assert.False(a.IsEnabled());
            a.Set(true);
            Assert.True(a.IsEnabled());
            Assert.Equal("/opt/umbra/umbra.sh --background", DesktopEntry.Parse(File.ReadAllText(path))["Exec"]);
            Assert.False(File.Exists(path + ".tmp"));
            a.Set(true); // idempotent
            a.Set(false);
            Assert.False(a.IsEnabled());
            Assert.False(File.Exists(path));
            a.Set(false); // removing twice is fine
        }
        finally { Directory.Delete(dir, true); }
    }

    sealed class FakePortal(bool answer = true) : IBackgroundPortal
    {
        public List<BackgroundRequest> Calls { get; } = [];
        public bool RequestBackground(BackgroundRequest request) { Calls.Add(request); return answer; }
    }

    [Fact]
    public void Flatpak_uses_the_background_portal()
    {
        var portal = new FakePortal();
        var a = LinuxAutostart.Create(_ => null, f => f == "/.flatpak-info", "umbra", portable: false, portal);
        Assert.IsType<PortalAutostart>(a);
        a.Set(true);
        Assert.True(a.IsEnabled());
        var call = Assert.Single(portal.Calls);
        Assert.True(call.Autostart);
        Assert.Equal(["umbra", "--background"], call.CommandLine);
        a.Set(false);
        Assert.False(a.IsEnabled());
        Assert.False(portal.Calls[1].Autostart);
    }

    [Fact]
    public void Portal_refusal_keeps_the_state()
    {
        var a = new PortalAutostart(new FakePortal(answer: false), ["umbra"]);
        a.Set(true);
        Assert.False(a.IsEnabled());
    }

    [Fact]
    public void Outside_flatpak_the_desktop_file_backend_is_used_under_xdg_config()
    {
        var a = LinuxAutostart.Create(k => k == "HOME" ? "/h" : null, _ => false, "/opt/umbra.sh", portable: false, new FakePortal());
        Assert.IsType<DesktopFileAutostart>(a);
    }

    [Fact]
    public void Portable_never_registers_autostart()
    {
        var portal = new FakePortal();
        var a = LinuxAutostart.Create(_ => null, _ => true, "/opt/umbra.sh", portable: true, portal);
        a.Set(true);
        Assert.False(a.IsEnabled());
        Assert.Empty(portal.Calls);
    }
}
