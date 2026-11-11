namespace Umbra.Tests;

public class SystemMetricsTests
{
    [Fact]
    public void Manifest_accepts_known_kinds_and_rejects_others()
    {
        var ok = WidgetManifest.Parse("""{ "id": "mon", "name": "M", "permissions": { "system": ["cpu", "memory", "network", "gpu", "disk"] } }""");
        Assert.Equal(5, ok.Permissions.System.Count);
        Assert.Throws<InvalidOperationException>(() =>
            WidgetManifest.Parse("""{ "id": "mon", "name": "M", "permissions": { "system": ["processes"] } }"""));
    }

    [Fact]
    public void Read_returns_only_declared_kinds_as_numbers()
    {
        var o = WindowsMetrics.Read(["cpu", "memory"]);
        Assert.Null(o["network"]);
        Assert.InRange(o["cpu"]!["percent"]!.GetValue<double>(), 0, 100);
        var mem = o["memory"]!;
        Assert.True(mem["totalBytes"]!.GetValue<long>() >= mem["usedBytes"]!.GetValue<long>());
        Assert.Empty(WindowsMetrics.Read([]));
    }

    [Fact]
    public async Task Gpu_and_disk_are_numbers_and_drive_letters_only()
    {
        WindowsMetrics.Read(["gpu", "disk"]);
        await Task.Delay(700); // rates and percentages need a second sample
        var o = WindowsMetrics.Read(["gpu", "disk"]);
        Assert.Null(o["cpu"]);

        // GPU counters are missing on some PCs (VMs, old drivers): then the percent is null, never a guess.
        var gpu = o["gpu"]!.AsObject();
        Assert.Equal(["memoryUsedBytes", "percent"], gpu.Select(p => p.Key).Order());
        if (gpu["percent"] is { } p) Assert.InRange(p.GetValue<double>(), 0, 100);

        var disk = o["disk"]!.AsObject();
        Assert.Equal(["activePercent", "drives", "readBytesPerSec", "writeBytesPerSec"], disk.Select(p => p.Key).Order());
        if (disk["activePercent"] is { } a) Assert.InRange(a.GetValue<double>(), 0, 100);
        var drives = disk["drives"]!.AsArray();
        Assert.NotEmpty(drives); // the system drive at least
        foreach (var d in drives)
        {
            Assert.Matches("^[A-Z]:$", d!["name"]!.GetValue<string>()); // a drive letter, never a volume label
            Assert.True(d["totalBytes"]!.GetValue<long>() >= d["freeBytes"]!.GetValue<long>());
            Assert.InRange(d["usedPercent"]!.GetValue<double>(), 0, 100);
        }
    }
}
