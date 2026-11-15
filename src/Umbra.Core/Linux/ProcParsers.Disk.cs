using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Umbra.Linux;

/// <summary>One whole disk's cumulative counters from <c>/proc/diskstats</c> (sectors are always 512 bytes there).</summary>
public readonly record struct DiskCounters(string Name, ulong SectorsRead, ulong SectorsWritten, ulong IoTicksMs);

/// <summary>One line of <c>/proc/mounts</c>, octal escapes (<c>\040</c> for a space) decoded.</summary>
public readonly record struct MountEntry(string Device, string MountPoint, string FsType);

/// <summary>A GPU reading: busiest engine in percent and memory in use, each null when the driver does not say.</summary>
public readonly record struct GpuSample(double? Percent, long? MemoryUsedBytes);

public static partial class ProcParsers
{
    /// <summary>Whole physical disks only: SCSI/SATA/virtio/Xen/IDE (<c>sda</c>), NVMe namespaces (<c>nvme0n1</c>), SD/eMMC (<c>mmcblk0</c>).
    /// Partitions, loop, ram, zram, device-mapper and md devices never match, so nothing is counted twice.</summary>
    [GeneratedRegex(@"^(?:(?:sd|vd|xvd|hd)[a-z]+|nvme\d+n\d+|mmcblk\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex WholeDiskName();

    /// <summary>True for a whole physical disk's name in <c>/proc/diskstats</c> (see <see cref="WholeDiskName"/>).</summary>
    public static bool IsWholeDisk(string name) => WholeDiskName().IsMatch(name);

    /// <summary>
    /// The whole physical disks of <c>/proc/diskstats</c> (<c>major minor name reads merged sectorsRead msRead writes merged
    /// sectorsWritten msWritten inFlight ioTicks ...</c>). Malformed lines are skipped.
    /// </summary>
    public static IReadOnlyList<DiskCounters> ParseDiskStats(string text)
    {
        var list = new List<DiskCounters>();
        foreach (var raw in text.Split('\n'))
        {
            var f = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 13 || !IsWholeDisk(f[2])) continue;
            if (U(f[5]) is { } read && U(f[9]) is { } written && U(f[12]) is { } ticks)
                list.Add(new DiskCounters(f[2], read, written, ticks));
        }
        return list;

        static ulong? U(string s) => ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>Every line of <c>/proc/mounts</c> (or <c>/proc/self/mounts</c>) with its escapes decoded.</summary>
    public static IReadOnlyList<MountEntry> ParseMounts(string text)
    {
        var list = new List<MountEntry>();
        foreach (var raw in text.Split('\n'))
        {
            var f = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 3) continue;
            list.Add(new MountEntry(Unescape(f[0]), Unescape(f[1]), f[2]));
        }
        return list;
    }

    /// <summary>The kernel's <c>\ooo</c> octal escapes in mount fields (space, tab, newline, backslash).</summary>
    static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var src = Encoding.UTF8.GetBytes(s);
        var bytes = new List<byte>(src.Length);
        for (var i = 0; i < src.Length; i++)
        {
            if (src[i] == '\\' && i + 3 < src.Length && src[i + 1] is >= (byte)'0' and <= (byte)'3'
                && src[i + 2] is >= (byte)'0' and <= (byte)'7' && src[i + 3] is >= (byte)'0' and <= (byte)'7')
            {
                bytes.Add((byte)((src[i + 1] - '0') * 64 + (src[i + 2] - '0') * 8 + (src[i + 3] - '0')));
                i += 3;
            }
            else bytes.Add(src[i]);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>File systems that never hold the user's files, even with a <c>/dev/</c> source.</summary>
    static readonly HashSet<string> SkippedFsTypes = new(StringComparer.Ordinal)
    {
        "squashfs", "iso9660", "udf", "tmpfs", "devtmpfs", "ramfs", "overlay", "proc", "sysfs", "devpts", "cgroup", "cgroup2",
        "securityfs", "debugfs", "tracefs", "configfs", "pstore", "bpf", "efivarfs", "autofs", "mqueue", "hugetlbfs", "fusectl",
        "binfmt_misc", "nsfs", "rpc_pipefs", "selinuxfs",
    };

    /// <summary>
    /// The mounts worth showing as drives, in <c>/proc/mounts</c> order: a block device (<c>/dev/...</c>, not loop, ram, zram or
    /// an optical drive) or a ZFS dataset, not a pseudo or read-only image file system (snap squashfs), not <c>/boot</c> or the
    /// EFI partition. Each device (ZFS: each pool) appears once, at its first mount point, so bind mounts and btrfs subvolumes
    /// of one disk don't repeat it.
    /// </summary>
    public static IReadOnlyList<MountEntry> DriveMounts(IEnumerable<MountEntry> mounts)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<MountEntry>();
        foreach (var m in mounts)
        {
            if (SkippedFsTypes.Contains(m.FsType) || !m.MountPoint.StartsWith('/')) continue;
            string key;
            if (m.FsType == "zfs") key = "zfs:" + m.Device.Split('/')[0];
            else if (m.Device.StartsWith("/dev/", StringComparison.Ordinal) && !IsVirtualBlock(m.Device["/dev/".Length..])) key = m.Device;
            else continue;
            if (m.MountPoint is "/boot" or "/efi" || m.MountPoint.StartsWith("/boot/", StringComparison.Ordinal)) continue;
            if (seen.Add(key)) list.Add(m);
        }
        return list;
    }

    static bool IsVirtualBlock(string dev) =>
        dev.StartsWith("loop", StringComparison.Ordinal) || dev.StartsWith("ram", StringComparison.Ordinal)
        || dev.StartsWith("zram", StringComparison.Ordinal) || dev.StartsWith("sr", StringComparison.Ordinal);

    /// <summary>A sysfs file holding one non-negative integer (<c>gpu_busy_percent</c>, <c>mem_info_vram_used</c>); null otherwise.</summary>
    public static long? ParseSysfsNumber(string? text) =>
        text != null && long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
}
