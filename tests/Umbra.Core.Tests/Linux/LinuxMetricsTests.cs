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
        public Dictionary<string, (long, long)> Space { get; } = [];
        public GpuSample? Nvidia { get; set; }
        public List<string> SpaceAsked { get; } = [];
        public (long TotalBytes, long FreeBytes)? FileSystemSpace(string mountPoint)
        {
            SpaceAsked.Add(mountPoint);
            return Space.TryGetValue(mountPoint, out var s) ? s : null;
        }
        public GpuSample? NvidiaGpu() => Nvidia;
    }

    // A laptop with an NVMe disk (two partitions), a SATA disk, snaps, a zram swap and a LUKS/LVM volume on top.
    const string DiskStats1 = """
           7       0 loop0 13268 1 1185446 7035 0 0 0 0 0 5315 7035 0 0 0 0 0 0
           1       0 ram0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0
         259       0 nvme0n1 100 0 10000 50 200 0 20000 80 0 1000 130 0 0 0 0 10 5
         259       1 nvme0n1p1 50 0 5000 25 100 0 10000 40 0 500 65 0 0 0 0 0 0
         259       2 nvme0n1p2 50 0 5000 25 100 0 10000 40 0 500 65 0 0 0 0 0 0
           8       0 sda 10 0 800 5 20 0 1600 10 0 100 15 0 0 0 0 0 0
           8       1 sda1 10 0 800 5 20 0 1600 10 0 100 15 0 0 0 0 0 0
         252       0 zram0 99 0 99999 1 99 0 99999 1 0 9999 2 0 0 0 0 0 0
         253       0 dm-0 50 0 5000 25 100 0 10000 40 0 500 65 0 0 0 0 0 0
         179       0 mmcblk0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0
         179       1 mmcblk0p1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0
         179       8 mmcblk0boot0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0
        """;

    // 2 s later: nvme read 2000 sectors (1 MB) and wrote 4000 (2 MB), busy 1000 ms; sda read 4000 sectors (2 MB), busy 400 ms.
    // The partitions, zram and dm-0 grow too, and must not be counted again.
    const string DiskStats2 = """
           7       0 loop0 99999 1 9999999 7035 0 0 0 0 0 9999 7035 0 0 0 0 0 0
         259       0 nvme0n1 120 0 12000 60 240 0 24000 90 0 2000 150 0 0 0 0 10 5
         259       1 nvme0n1p1 70 0 7000 35 140 0 14000 50 0 1500 85 0 0 0 0 0 0
           8       0 sda 30 0 4800 9 20 0 1600 10 0 500 19 0 0 0 0 0 0
           8       1 sda1 30 0 4800 9 20 0 1600 10 0 500 19 0 0 0 0 0 0
         252       0 zram0 999 0 999999 1 999 0 999999 1 0 99999 2 0 0 0 0 0 0
         253       0 dm-0 70 0 7000 35 140 0 14000 50 0 1500 85 0 0 0 0 0 0
         179       0 mmcblk0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0
        """;

    // An Ubuntu desktop: pseudo file systems, snaps, a bind mount, btrfs subvolumes, a USB stick with a space, a ZFS pool, the ESP.
    const string Mounts = """
        sysfs /sys sysfs rw,nosuid,nodev,noexec,relatime 0 0
        proc /proc proc rw,nosuid,nodev,noexec,relatime 0 0
        udev /dev devtmpfs rw,nosuid,relatime,size=8062804k,nr_inodes=2015701,mode=755,inode64 0 0
        tmpfs /run tmpfs rw,nosuid,nodev,noexec,relatime,size=1620212k,mode=755,inode64 0 0
        /dev/nvme0n1p2 / ext4 rw,relatime,errors=remount-ro 0 0
        tmpfs /dev/shm tmpfs rw,nosuid,nodev,inode64 0 0
        cgroup2 /sys/fs/cgroup cgroup2 rw,nosuid,nodev,noexec,relatime,nsdelegate,memory_recursiveprot 0 0
        efivarfs /sys/firmware/efi/efivars efivarfs rw,nosuid,nodev,noexec,relatime 0 0
        /dev/loop0 /snap/core22/1380 squashfs ro,nodev,relatime,errors=continue,threads=single 0 0
        /dev/loop1 /snap/firefox/4173 squashfs ro,nodev,relatime,errors=continue,threads=single 0 0
        /dev/nvme0n1p1 /boot/efi vfat rw,relatime,fmask=0077,dmask=0077,codepage=437 0 0
        /dev/sda1 /home btrfs rw,relatime,ssd,space_cache=v2,subvolid=256,subvol=/@home 0 0
        /dev/sda1 /var/log btrfs rw,relatime,ssd,space_cache=v2,subvolid=258,subvol=/@log 0 0
        /dev/nvme0n1p2 /var/snap/lxd/common/shmounts ext4 rw,relatime 0 0
        /dev/zram0 /var/cache zram rw 0 0
        /dev/sr0 /media/user/CDROM iso9660 ro,nosuid,nodev,relatime 0 0
        /dev/sdb1 /media/user/My\040Stick vfat rw,nosuid,nodev,relatime,uid=1000 0 0
        rpool/ROOT/ubuntu /tank zfs rw,relatime,xattr,posixacl 0 0
        rpool/USERDATA/home /tank/home zfs rw,relatime,xattr,posixacl 0 0
        gvfsd-fuse /run/user/1000/gvfs fuse.gvfsd-fuse rw,nosuid,nodev,relatime,user_id=1000,group_id=1000 0 0
        overlay /var/lib/docker/overlay2/abc/merged overlay rw,relatime,lowerdir=/x 0 0
        nsfs /run/snapd/ns/firefox.mnt nsfs rw 0 0
        """;

    [Fact]
    public void DiskStats_keeps_whole_disks_only()
    {
        var disks = ProcParsers.ParseDiskStats(DiskStats1);
        Assert.Equal(["nvme0n1", "sda", "mmcblk0"], disks.Select(d => d.Name).ToArray());
        Assert.Equal(new DiskCounters("nvme0n1", 10000, 20000, 1000), disks[0]);
        Assert.Empty(ProcParsers.ParseDiskStats("garbage\n 8 0 sda 1 2\n 8 0 sda x x x x x x x x x x x\n"));
    }

    [Theory]
    [InlineData("sda", true)] [InlineData("sdab", true)] [InlineData("vda", true)] [InlineData("xvda", true)]
    [InlineData("nvme0n1", true)] [InlineData("nvme12n3", true)] [InlineData("mmcblk0", true)]
    [InlineData("sda1", false)] [InlineData("nvme0n1p2", false)] [InlineData("mmcblk0p1", false)] [InlineData("mmcblk0boot0", false)]
    [InlineData("loop0", false)] [InlineData("ram0", false)] [InlineData("zram0", false)] [InlineData("dm-0", false)]
    [InlineData("md127", false)] [InlineData("sr0", false)] [InlineData("nbd0", false)]
    public void Whole_disk_names(string name, bool whole) => Assert.Equal(whole, ProcParsers.IsWholeDisk(name));

    [Fact]
    public void Mounts_decode_escapes()
    {
        var m = ProcParsers.ParseMounts(Mounts).Single(e => e.Device == "/dev/sdb1");
        Assert.Equal("/media/user/My Stick", m.MountPoint);
        Assert.Equal("vfat", m.FsType);
        Assert.Equal(@"/a\b c", ProcParsers.ParseMounts(@"/dev/sdc1 /a\134b\040c ext4 rw 0 0").Single().MountPoint);
        Assert.Equal("/é", ProcParsers.ParseMounts("/dev/sdc1 /é ext4 rw 0 0").Single().MountPoint);
        Assert.Equal(@"/x\9", ProcParsers.ParseMounts(@"/dev/sdc1 /x\9 ext4 rw 0 0").Single().MountPoint);
    }

    [Fact]
    public void Drive_mounts_skip_pseudo_snap_boot_and_repeats()
    {
        var drives = ProcParsers.DriveMounts(ProcParsers.ParseMounts(Mounts));
        Assert.Equal(["/", "/home", "/media/user/My Stick", "/tank"], drives.Select(d => d.MountPoint).ToArray());
    }

    [Fact]
    public void Sysfs_numbers()
    {
        Assert.Equal(42, ProcParsers.ParseSysfsNumber("42\n"));
        Assert.Equal(8589934592, ProcParsers.ParseSysfsNumber("8589934592\n"));
        Assert.Null(ProcParsers.ParseSysfsNumber(null));
        Assert.Null(ProcParsers.ParseSysfsNumber(""));
        Assert.Null(ProcParsers.ParseSysfsNumber("-1"));
        Assert.Null(ProcParsers.ParseSysfsNumber("n/a"));
    }

    [Fact]
    public void Disk_reads_rates_and_the_busiest_disk_from_deltas()
    {
        var src = new FakeSource(new() { ["/proc/diskstats"] = DiskStats1, ["/proc/self/mounts"] = Mounts });
        src.Space["/"] = (500_000_000_000, 125_000_000_000);
        src.Space["/home"] = (1_000_000_000_000, 900_000_000_000);
        src.Space["/tank"] = (2_000_000_000_000, 2_000_000_000_000);
        long now = 10_000;
        var m = new LinuxMetrics(src, () => now);

        var first = m.Read(["disk"])["disk"]!;
        Assert.Null(first["activePercent"]); // one sample: no rate yet, like the Windows counters
        Assert.Null(first["readBytesPerSec"]);
        Assert.Null(first["writeBytesPerSec"]);
        var drives = first["drives"]!.AsArray();
        Assert.Equal(["/", "/home", "/tank"], drives.Select(d => d!["name"]!.GetValue<string>()).ToArray()); // the stick reports no size
        Assert.Equal(125_000_000_000, drives[0]!["freeBytes"]!.GetValue<long>());
        Assert.Equal(500_000_000_000, drives[0]!["totalBytes"]!.GetValue<long>());
        Assert.Equal(75, drives[0]!["usedPercent"]!.GetValue<double>());
        Assert.Equal(0, drives[2]!["usedPercent"]!.GetValue<double>());

        src.Files["/proc/diskstats"] = DiskStats2;
        now += 2000;
        var second = m.Read(["disk"])["disk"]!;
        Assert.Equal(50, second["activePercent"]!.GetValue<double>());                     // nvme busy 1000 of 2000 ms
        Assert.Equal((1_024_000 + 2_048_000) / 2, second["readBytesPerSec"]!.GetValue<long>()); // 1 MB + 2 MB in 2 s
        Assert.Equal(2_048_000 / 2, second["writeBytesPerSec"]!.GetValue<long>());
    }

    [Fact]
    public void Drives_are_refreshed_every_five_seconds()
    {
        var src = new FakeSource(new() { ["/proc/mounts"] = Mounts });
        src.Space["/"] = (100, 50);
        long now = 1000;
        var m = new LinuxMetrics(src, () => now);
        m.Read(["disk"]);
        var asked = src.SpaceAsked.Count;
        now += 4000;
        m.Read(["disk"]);
        Assert.Equal(asked, src.SpaceAsked.Count);
        now += 1500;
        src.Space["/"] = (100, 10);
        Assert.Equal(90, m.Read(["disk"])["disk"]!["drives"]![0]!["usedPercent"]!.GetValue<double>()); // /proc/mounts is the fallback
    }

    [Fact]
    public void Gpu_reads_amdgpu_sysfs_and_adds_nvml()
    {
        var src = new FakeSource(new()
        {
            ["/sys/class/drm/card0/device/gpu_busy_percent"] = "37\n",
            ["/sys/class/drm/card0/device/mem_info_vram_used"] = "1073741824\n",
            ["/sys/class/drm/card0/device/mem_info_gtt_used"] = "268435456\n",
            ["/sys/class/drm/card0-DP-1/device/gpu_busy_percent"] = "99\n", // a connector, not a card
            ["/sys/class/drm/card1/device/vendor"] = "0x8086\n",             // Intel: no busy file
            ["/sys/class/drm/renderD128/device/gpu_busy_percent"] = "99\n",
        });
        long now = 1000;
        var m = new LinuxMetrics(src, () => now);
        var gpu = m.Read(["gpu"])["gpu"]!;
        Assert.Equal(37, gpu["percent"]!.GetValue<double>());
        Assert.Equal(1073741824L + 268435456, gpu["memoryUsedBytes"]!.GetValue<long>());

        src.Nvidia = new GpuSample(64, 2_000_000_000);
        now += 600;
        gpu = m.Read(["gpu"])["gpu"]!;
        Assert.Equal(64, gpu["percent"]!.GetValue<double>()); // the busiest of all
        Assert.Equal(1073741824L + 268435456 + 2_000_000_000, gpu["memoryUsedBytes"]!.GetValue<long>());
    }

    [Fact]
    public void Gpu_and_disk_without_sources_are_null_in_the_windows_shape()
    {
        var o = new LinuxMetrics(new FakeSource(new()
        {
            ["/sys/class/drm/card1/device/vendor"] = "0x8086\n",
            ["/proc/diskstats"] = "   7       0 loop0 1 0 1 0 0 0 0 0 0 1 0 0 0 0 0 0 0\n",
        }), () => 1).Read(["gpu", "disk"]);
        Assert.True(o["gpu"]!.AsObject().ContainsKey("percent"));
        Assert.Null(o["gpu"]!["percent"]);
        Assert.Null(o["gpu"]!["memoryUsedBytes"]);
        Assert.Null(o["disk"]!["activePercent"]);
        Assert.Null(o["disk"]!["readBytesPerSec"]);
        Assert.Empty(o["disk"]!["drives"]!.AsArray());
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
