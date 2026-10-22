using Umbra.Linux;

namespace Umbra.Tests;

public class LinuxMetricsTests
{
    const string Stat1 = """
        cpu  1000 50 500 8000 200 10 20 30 0 0
        cpu0 500 25 250 4000 100 5 10 15 0 0
        intr 12345
        ctxt 999
        """;

    // +100 busy-ish ticks and +300 idle ticks since Stat1: total grows by 400, idle by 300 -> 25% busy.
    const string Stat2 = """
        cpu  1050 50 530 8290 210 10 20 50 0 0
        cpu0 500 25 250 4000 100 5 10 15 0 0
        """;

    const string MemInfo = """
        MemTotal:       16384000 kB
        MemFree:         1000000 kB
        MemAvailable:    8192000 kB
        Buffers:          200000 kB
        Cached:          4000000 kB
        SwapTotal:       2097148 kB
        """;

    const string NetDev = """
        Inter-|   Receive                                                |  Transmit
         face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
            lo: 9999999   100    0    0    0     0          0         0  9999999   100    0    0    0     0       0          0
        enp3s0: 1000000  2000    0    0    0     0          0         0   500000  1000    0    0    0     0       0          0
        wlan0:   200000   300    0    0    0     0          0         0    50000   100    0    0    0     0       0          0
        docker0: 77777777  10    0    0    0     0          0         0  88888888   10    0    0    0     0       0          0
        veth1a2b: 5555555  10    0    0    0     0          0         0   6666666   10    0    0    0     0       0          0
        """;

    const string Bat0 = """
        POWER_SUPPLY_NAME=BAT0
        POWER_SUPPLY_TYPE=Battery
        POWER_SUPPLY_STATUS=Charging
        POWER_SUPPLY_CAPACITY=80
        POWER_SUPPLY_ENERGY_NOW=40000000
        POWER_SUPPLY_ENERGY_FULL=50000000
        """;

    sealed class FakeSource(Dictionary<string, string> files) : IProcSource
    {
        public Dictionary<string, string> Files { get; } = files;
        public string? ReadText(string path) => Files.GetValueOrDefault(path);
        public IReadOnlyList<string> ListDirectory(string path) =>
            Files.Keys.Where(k => k.StartsWith(path + "/", StringComparison.Ordinal)).Select(k => k[(path.Length + 1)..].Split('/')[0]).Distinct().ToArray();
    }

    [Fact]
    public void CpuStat_sums_all_states_and_counts_idle_plus_iowait()
    {
        var t = ProcParsers.ParseCpuStat(Stat1)!.Value;
        Assert.Equal(8200UL, t.Idle);
        Assert.Equal(1000UL + 50 + 500 + 8000 + 200 + 10 + 20 + 30, t.Total);
    }

    [Theory]
    [InlineData("")]
    [InlineData("cpu0 1 2 3 4 5")]
    [InlineData("cpu  a b c d e")]
    [InlineData("cpu 1 2 3")]
    public void CpuStat_garbage_is_null(string text) => Assert.Null(ProcParsers.ParseCpuStat(text));

    [Fact]
    public void CpuStat_old_kernel_with_four_fields_still_parses()
    {
        var t = ProcParsers.ParseCpuStat("cpu  10 0 20 70")!.Value;
        Assert.Equal(70UL, t.Idle);
        Assert.Equal(100UL, t.Total);
    }

    [Fact]
    public void MemInfo_uses_MemAvailable()
    {
        var m = ProcParsers.ParseMemInfo(MemInfo)!.Value;
        Assert.Equal(16384000L * 1024, m.TotalBytes);
        Assert.Equal(8192000L * 1024, m.UsedBytes);
        Assert.Equal(50, m.Percent);
    }

    [Fact]
    public void MemInfo_without_MemAvailable_falls_back_to_free_buffers_cached()
    {
        var m = ProcParsers.ParseMemInfo("MemTotal: 1000 kB\nMemFree: 100 kB\nBuffers: 100 kB\nCached: 300 kB\n")!.Value;
        Assert.Equal(500L * 1024, m.UsedBytes);
        Assert.Equal(50, m.Percent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("MemFree: 5 kB")]
    [InlineData("MemTotal: 0 kB")]
    public void MemInfo_without_total_is_null(string text) => Assert.Null(ProcParsers.ParseMemInfo(text));

    [Fact]
    public void Uptime_reads_the_first_number()
    {
        Assert.Equal(12345.67, ProcParsers.ParseUptime("12345.67 98765.43\n"));
        Assert.Null(ProcParsers.ParseUptime("nope"));
        Assert.Null(ProcParsers.ParseUptime(""));
    }

    [Fact]
    public void NetDev_sums_real_interfaces_only()
    {
        var (rx, tx) = ProcParsers.ParseNetDev(NetDev);
        Assert.Equal(1_200_000, rx);
        Assert.Equal(550_000, tx);
    }

    [Fact]
    public void Battery_uevent_is_parsed()
    {
        var b = ProcParsers.ParseBattery(Bat0)!;
        Assert.Equal("BAT0", b.Name);
        Assert.Equal(80, b.Percent);
        Assert.Equal("Charging", b.Status);
        Assert.True(b.Charging);
    }

    [Fact]
    public void Battery_without_capacity_derives_it_from_charge_or_energy()
    {
        var b = ProcParsers.ParseBattery("POWER_SUPPLY_TYPE=Battery\nPOWER_SUPPLY_STATUS=Discharging\nPOWER_SUPPLY_CHARGE_NOW=2500\nPOWER_SUPPLY_CHARGE_FULL=5000\n")!;
        Assert.Equal(50, b.Percent);
        Assert.False(b.Charging);
        Assert.Equal("BAT", b.Name);
    }

    [Theory]
    [InlineData("POWER_SUPPLY_NAME=AC\nPOWER_SUPPLY_TYPE=Mains\nPOWER_SUPPLY_ONLINE=1\n")]
    [InlineData("POWER_SUPPLY_TYPE=Battery\nPOWER_SUPPLY_STATUS=Unknown\n")]
    [InlineData("")]
    public void Non_batteries_and_unknown_levels_are_null(string text) => Assert.Null(ProcParsers.ParseBattery(text));

    [Fact]
    public void Combined_battery_is_weighted_by_capacity()
    {
        var a = new BatteryInfo("BAT0", 100, "Full", 30, 30);
        var b = new BatteryInfo("BAT1", 0, "Discharging", 0, 10);
        Assert.Equal(75, ProcParsers.CombinedBatteryPercent([a, b]));
        Assert.Equal(50, ProcParsers.CombinedBatteryPercent([new("A", 100, "x", null, null), new("B", 0, "x", null, null)]));
        Assert.Null(ProcParsers.CombinedBatteryPercent([]));
    }

    [Fact]
    public void Read_returns_the_windows_shape_and_cpu_from_deltas()
    {
        var src = new FakeSource(new() { ["/proc/stat"] = Stat1, ["/proc/meminfo"] = MemInfo, ["/proc/net/dev"] = NetDev });
        long now = 10_000;
        var m = new LinuxMetrics(src, () => now);
        string[] all = ["cpu", "memory", "network"];

        var first = m.Read(all);
        Assert.Equal(0, first["cpu"]!["percent"]!.GetValue<double>());
        Assert.Equal(16384000L * 1024, first["memory"]!["totalBytes"]!.GetValue<long>());
        Assert.Equal(8192000L * 1024, first["memory"]!["usedBytes"]!.GetValue<long>());
        Assert.Equal(50, first["memory"]!["percent"]!.GetValue<int>());
        Assert.Equal(0, first["network"]!["downBytesPerSec"]!.GetValue<long>());

        src.Files["/proc/stat"] = Stat2;
        src.Files["/proc/net/dev"] = NetDev.Replace("1000000  2000", "3000000  2000").Replace("500000  1000", "1500000  1000");
        now += 2000;
        var second = m.Read(all);
        Assert.Equal(25, second["cpu"]!["percent"]!.GetValue<double>());
        Assert.Equal(1_000_000, second["network"]!["downBytesPerSec"]!.GetValue<long>()); // +2 MB in 2 s
        Assert.Equal(500_000, second["network"]!["upBytesPerSec"]!.GetValue<long>());     // +1 MB in 2 s
    }

    [Fact]
    public void Read_only_returns_the_allowed_kinds()
    {
        var src = new FakeSource(new() { ["/proc/stat"] = Stat1, ["/proc/meminfo"] = MemInfo });
        var o = new LinuxMetrics(src, () => 1).Read(["memory"]);
        Assert.Equal(["memory"], o.Select(p => p.Key).ToArray());
    }

    [Fact]
    public void Samples_inside_half_a_second_are_shared()
    {
        var src = new FakeSource(new() { ["/proc/stat"] = Stat1 });
        long now = 5000;
        var m = new LinuxMetrics(src, () => now);
        m.Read(["cpu"]);
        src.Files["/proc/stat"] = Stat2;
        now += 100; // too soon: the first sample stays the baseline
        Assert.Equal(0, m.Read(["cpu"])["cpu"]!["percent"]!.GetValue<double>());
        now += 500;
        Assert.Equal(25, m.Read(["cpu"])["cpu"]!["percent"]!.GetValue<double>());
    }

    [Fact]
    public void Missing_files_do_not_throw()
    {
        var o = new LinuxMetrics(new FakeSource(new()), () => 1).Read(["cpu", "memory", "network"]);
        Assert.Equal(0, o["cpu"]!["percent"]!.GetValue<double>());
        Assert.False(o.ContainsKey("memory"));
        Assert.Equal(0, o["network"]!["downBytesPerSec"]!.GetValue<long>());
    }

    [Fact]
    public void Batteries_are_found_under_power_supply()
    {
        var src = new FakeSource(new()
        {
            ["/sys/class/power_supply/AC/uevent"] = "POWER_SUPPLY_NAME=AC\nPOWER_SUPPLY_TYPE=Mains\n",
            ["/sys/class/power_supply/BAT1/uevent"] = Bat0.Replace("BAT0", "BAT1"),
            ["/sys/class/power_supply/BAT0/uevent"] = Bat0,
            ["/proc/uptime"] = "100.5 400.0\n",
        });
        var m = new LinuxMetrics(src, () => 1);
        Assert.Equal(["BAT0", "BAT1"], m.Batteries().Select(b => b.Name).ToArray());
        Assert.Equal(100.5, m.UptimeSeconds());
        Assert.Empty(new LinuxMetrics(new FakeSource(new()), () => 1).Batteries());
    }
}
