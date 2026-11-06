using System.Diagnostics;

namespace Umbra.Tests;

/// <summary>Settings > Add script > Browse on a shortcut: the host reads where it points (#322).</summary>
public class ShellLinkTests
{
    /// <summary>Makes a real .lnk with the Windows Script Host (what Explorer's "Create shortcut" writes too).</summary>
    static string MakeShortcut(string dir, string target, string args, string startIn)
    {
        Directory.CreateDirectory(dir);
        var lnk = Path.Combine(dir, "test.lnk");
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var script = $"$s = (New-Object -ComObject WScript.Shell).CreateShortcut({Q(lnk)}); $s.TargetPath = {Q(target)}; $s.Arguments = {Q(args)}; $s.WorkingDirectory = {Q(startIn)}; $s.Save()";
        using var p = Process.Start(new ProcessStartInfo("powershell", ["-NoProfile", "-NonInteractive", "-Command", script]) { UseShellExecute = false, CreateNoWindow = true })!;
        p.WaitForExit(30000);
        Assert.True(File.Exists(lnk), "the test shortcut was not created");
        return lnk;
    }

    static string NewDir() => Path.Combine(Path.GetTempPath(), "umbra-lnk-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_shortcut_to_a_ps1_becomes_a_powershell_script_with_its_folder()
    {
        var dir = NewDir();
        var ps1 = Path.Combine(dir, "tunnel.ps1");
        Directory.CreateDirectory(dir);
        File.WriteAllText(ps1, "\"hi\"");
        var link = ShellLink.Read(MakeShortcut(dir, ps1, "-Port 2222", dir));
        Assert.NotNull(link);
        Assert.Equal(ps1, link.Target, ignoreCase: true);
        Assert.Equal(dir, link.WorkingDirectory, ignoreCase: true);
        var (kind, path, args) = ShellLink.AsScript(link);
        Assert.Equal("powershell", kind);
        Assert.Equal(ps1, path, ignoreCase: true);
        Assert.Equal("-Port 2222", args);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Powershell_dash_file_is_unwrapped_and_other_programs_stay_programs()
    {
        var ps = new ShellLink.Link("C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe", "-ExecutionPolicy Bypass -File \"C:\\My Scripts\\up.ps1\" -Name \"a b\"", "");
        Assert.Equal(("powershell", "C:\\My Scripts\\up.ps1", "-Name \"a b\""), ShellLink.AsScript(ps));
        var exe = new ShellLink.Link("C:\\Tools\\ssh.exe", "-N host", "C:\\Tools");
        Assert.Equal(("program", "C:\\Tools\\ssh.exe", "-N host"), ShellLink.AsScript(exe));
        Assert.Equal(("python", "C:\\s\\a.py", ""), ShellLink.AsScript(new ShellLink.Link("C:\\s\\a.py", "", "")));
    }

    [Fact]
    public void A_file_that_is_not_a_shortcut_reads_as_null()
    {
        var dir = NewDir();
        Directory.CreateDirectory(dir);
        var fake = Path.Combine(dir, "fake.lnk");
        File.WriteAllText(fake, "not a shortcut");
        Assert.Null(ShellLink.Read(fake));
        Directory.Delete(dir, true);
    }
}
