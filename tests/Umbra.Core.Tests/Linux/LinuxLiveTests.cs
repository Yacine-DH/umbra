using System.Diagnostics;
using Umbra.Linux;

namespace Umbra.Tests;

/// <summary>The Linux platform code against the real system (live /proc, desktop-file-validate); skipped on Windows.</summary>
public class LinuxLiveTests
{
    [LinuxFact]
    public void Metrics_read_the_live_proc_files()
    {
        var clock = Stopwatch.StartNew();
        var metrics = new LinuxMetrics(new FileProcSource(), () => clock.ElapsedMilliseconds + 1);
        metrics.Read(["cpu", "memory", "network"]);
        Thread.Sleep(600); // past the 500 ms sample gate, so the second read computes deltas
        var o = metrics.Read(["cpu", "memory", "network"]);

        Assert.InRange(o["cpu"]!["percent"]!.GetValue<double>(), 0, 100);
        var total = o["memory"]!["totalBytes"]!.GetValue<long>();
        var used = o["memory"]!["usedBytes"]!.GetValue<long>();
        Assert.True(total > 64L * 1024 * 1024, $"total {total}");
        Assert.InRange(used, 1, total);
        Assert.True(o["network"]!["downBytesPerSec"]!.GetValue<long>() >= 0);
        Assert.True(o["network"]!["upBytesPerSec"]!.GetValue<long>() >= 0);
        Assert.True(metrics.UptimeSeconds() > 0);
        Assert.All(metrics.Batteries(), b => Assert.InRange(b.Percent, 0, 100));
    }

    [LinuxFact]
    public void Gpu_and_disk_read_the_live_proc_and_sys_files()
    {
        var clock = Stopwatch.StartNew();
        var metrics = new LinuxMetrics(new FileProcSource(), () => clock.ElapsedMilliseconds + 1);
        metrics.Read(["gpu", "disk"]);
        Thread.Sleep(600);
        var o = metrics.Read(["gpu", "disk"]);

        if (ProcParsers.ParseDiskStats(File.ReadAllText("/proc/diskstats")).Count > 0)
        {
            Assert.InRange(o["disk"]!["activePercent"]!.GetValue<double>(), 0, 100);
            Assert.True(o["disk"]!["readBytesPerSec"]!.GetValue<long>() >= 0);
            Assert.True(o["disk"]!["writeBytesPerSec"]!.GetValue<long>() >= 0);
        }
        var drives = o["disk"]!["drives"]!.AsArray();
        // The root file system is a drive wherever it is a real disk; in a container (the local CI's Docker job) it is an
        // overlay, which DriveMounts skips on purpose.
        var mounts = ProcParsers.DriveMounts(ProcParsers.ParseMounts(File.ReadAllText("/proc/self/mounts")));
        if (mounts.Any(m => m.MountPoint == "/")) Assert.Contains(drives, d => d!["name"]!.GetValue<string>() == "/");
        Assert.DoesNotContain(drives, d => d!["name"]!.GetValue<string>().StartsWith("/snap/", StringComparison.Ordinal));
        Assert.All(drives, d =>
        {
            Assert.InRange(d!["freeBytes"]!.GetValue<long>(), 0, d["totalBytes"]!.GetValue<long>());
            Assert.InRange(d["usedPercent"]!.GetValue<double>(), 0, 100);
        });
        Assert.True(o["gpu"]!.AsObject().ContainsKey("percent"));
        if (o["gpu"]!["percent"] is { } p) Assert.InRange(p.GetValue<double>(), 0, 100);
        Assert.Null(new FileProcSource().FileSystemSpace("/umbra-does-not-exist"));
    }

    [LinuxFact]
    public void Missing_files_read_as_nothing()
    {
        var source = new FileProcSource();
        Assert.Null(source.ReadText("/proc/umbra-does-not-exist"));
        Assert.Empty(source.ListDirectory("/proc/umbra-does-not-exist"));
        Assert.Contains("stat", source.ListDirectory("/proc"));
    }

    [LinuxFact]
    public void Default_data_dir_follows_xdg_and_keeps_the_override()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expected = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { } x && x.StartsWith('/')
            ? LinuxAppPaths.Join(x, "umbra") : LinuxAppPaths.Join(home, ".local/share/umbra");
        Assert.Equal(expected, PortableMode.DataDir("/nonexistent-umbra-app", null));
        Assert.Equal("/tmp/custom", PortableMode.DataDir("/nonexistent-umbra-app", "/tmp/custom"));
        Assert.StartsWith(TestEnv.DataDir, AppPaths.WebViewDataDir); // UMBRA_DATA_DIR keeps the cache inside the profile
    }

    [LinuxFact]
    public void The_autostart_file_passes_desktop_file_validate()
    {
        const string tool = "/usr/bin/desktop-file-validate";
        if (!File.Exists(tool)) return; // not installed: nothing to check against
        var dir = Path.Combine(Path.GetTempPath(), $"umbra-desktop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, DesktopEntry.AppId + ".desktop");
            var autostart = new DesktopFileAutostart(path, "/opt/My Apps/umbra $x/umbra.sh", ["--background"]);
            autostart.Set(true);
            Assert.True(autostart.IsEnabled());

            var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add(path);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, output);

            autostart.Set(false);
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(dir, true); }
    }
}
