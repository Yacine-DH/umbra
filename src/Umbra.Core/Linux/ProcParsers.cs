using System.Globalization;

namespace Umbra.Linux;

/// <summary>Aggregate CPU jiffies from the first line of <c>/proc/stat</c>.</summary>
public readonly record struct CpuTimes(ulong Idle, ulong Total);

/// <summary>Memory from <c>/proc/meminfo</c>, in bytes.</summary>
public readonly record struct MemoryInfo(long TotalBytes, long AvailableBytes)
{
    /// <summary>Total minus available.</summary>
    public long UsedBytes => Math.Max(0, TotalBytes - AvailableBytes);

    /// <summary>Used share of total, rounded, 0-100 (as <c>MemoryLoad</c> on Windows).</summary>
    public int Percent => TotalBytes <= 0 ? 0 : (int)Math.Clamp(Math.Round(100.0 * UsedBytes / TotalBytes), 0, 100);
}

/// <summary>One battery from <c>/sys/class/power_supply/BAT*/uevent</c>.</summary>
public sealed record BatteryInfo(string Name, int Percent, string Status, long? EnergyNow, long? EnergyFull)
{
    /// <summary>True while the battery reports <c>Charging</c>.</summary>
    public bool Charging => Status == "Charging";
}

/// <summary>Parsers for the <c>/proc</c> and <c>/sys</c> text files behind <c>umbra.system()</c>. Pure: they take the file text.</summary>
public static class ProcParsers
{
    /// <summary>
    /// The aggregate <c>cpu</c> line of <c>/proc/stat</c>: Total = user+nice+system+idle+iowait+irq+softirq+steal (guest time is
    /// already inside user/nice), Idle = idle+iowait. Null when the line is missing or malformed.
    /// </summary>
    public static CpuTimes? ParseCpuStat(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var f = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 5 || f[0] != "cpu") continue;
            var v = new ulong[Math.Min(f.Length - 1, 8)];
            for (var i = 0; i < v.Length; i++)
                if (!ulong.TryParse(f[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out v[i])) return null;
            ulong total = 0;
            foreach (var x in v) total += x;
            var idle = v[3] + (v.Length > 4 ? v[4] : 0);
            return new CpuTimes(idle, total);
        }
        return null;
    }

    /// <summary>
    /// <c>MemTotal</c> and <c>MemAvailable</c> (kB -> bytes). Kernels without <c>MemAvailable</c> (before 3.14) fall back to
    /// MemFree+Buffers+Cached. Null when <c>MemTotal</c> is missing.
    /// </summary>
    public static MemoryInfo? ParseMemInfo(string text)
    {
        var kb = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var colon = raw.IndexOf(':');
            if (colon <= 0) continue;
            var parts = raw[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                kb[raw[..colon]] = n;
        }
        if (!kb.TryGetValue("MemTotal", out var total) || total <= 0) return null;
        long avail;
        if (kb.TryGetValue("MemAvailable", out var a)) avail = a;
        else avail = kb.GetValueOrDefault("MemFree") + kb.GetValueOrDefault("Buffers") + kb.GetValueOrDefault("Cached");
        avail = Math.Clamp(avail, 0, total);
        return new MemoryInfo(total * 1024, avail * 1024);
    }

    /// <summary>Seconds since boot: the first number of <c>/proc/uptime</c>. Null when unreadable.</summary>
    public static double? ParseUptime(string text)
    {
        var first = text.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first != null && double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s >= 0 ? s : null;
    }

    /// <summary>
    /// Total received and sent bytes over real interfaces from <c>/proc/net/dev</c>. Loopback, container/VM bridges and
    /// tunnels (<c>lo</c>, <c>veth*</c>, <c>docker*</c>, <c>br-*</c>, <c>virbr*</c>, <c>tun*</c>, <c>tap*</c>) are skipped, like the
    /// Windows reader skips loopback and tunnels.
    /// </summary>
    public static (long Rx, long Tx) ParseNetDev(string text)
    {
        long rx = 0, tx = 0;
        foreach (var raw in text.Split('\n'))
        {
            var colon = raw.IndexOf(':');
            if (colon <= 0) continue;
            var name = raw[..colon].Trim();
            if (IsVirtualInterface(name)) continue;
            var f = raw[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 9) continue;
            if (long.TryParse(f[0], NumberStyles.None, CultureInfo.InvariantCulture, out var r) && long.TryParse(f[8], NumberStyles.None, CultureInfo.InvariantCulture, out var t))
            {
                rx += r; tx += t;
            }
        }
        return (rx, tx);
    }

    static bool IsVirtualInterface(string n) =>
        n == "lo" || n.StartsWith("veth", StringComparison.Ordinal) || n.StartsWith("docker", StringComparison.Ordinal)
        || n.StartsWith("br-", StringComparison.Ordinal) || n.StartsWith("virbr", StringComparison.Ordinal)
        || n.StartsWith("tun", StringComparison.Ordinal) || n.StartsWith("tap", StringComparison.Ordinal);

    /// <summary>
    /// A power-supply <c>uevent</c> file (<c>POWER_SUPPLY_KEY=value</c> lines). Null unless <c>POWER_SUPPLY_TYPE=Battery</c> and a
    /// percentage is known (<c>POWER_SUPPLY_CAPACITY</c>, else derived from energy or charge now/full).
    /// </summary>
    public static BatteryInfo? ParseBattery(string uevent)
    {
        var k = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in uevent.Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=');
            if (eq > 0 && line.StartsWith("POWER_SUPPLY_", StringComparison.Ordinal))
                k[line["POWER_SUPPLY_".Length..eq]] = line[(eq + 1)..];
        }
        if (!k.TryGetValue("TYPE", out var type) || !type.Equals("Battery", StringComparison.OrdinalIgnoreCase)) return null;
        long? now = Num(k, "ENERGY_NOW") ?? Num(k, "CHARGE_NOW");
        long? full = Num(k, "ENERGY_FULL") ?? Num(k, "CHARGE_FULL");
        int percent;
        if (Num(k, "CAPACITY") is { } cap) percent = (int)Math.Clamp(cap, 0, 100);
        else if (now is { } n && full is > 0) percent = (int)Math.Clamp(Math.Round(100.0 * n / full.Value), 0, 100);
        else return null;
        return new BatteryInfo(k.GetValueOrDefault("NAME", "BAT"), percent, k.GetValueOrDefault("STATUS", "Unknown"), now, full);
    }

    static long? Num(Dictionary<string, string> k, string key) =>
        k.TryGetValue(key, out var s) && long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>
    /// One percentage for all batteries: weighted by full capacity when every battery reports it, else the plain average.
    /// Null when there is no battery (a desktop PC).
    /// </summary>
    public static int? CombinedBatteryPercent(IReadOnlyCollection<BatteryInfo> batteries)
    {
        if (batteries.Count == 0) return null;
        if (batteries.All(b => b.EnergyNow != null && b.EnergyFull is > 0))
        {
            double now = batteries.Sum(b => (double)b.EnergyNow!.Value), full = batteries.Sum(b => (double)b.EnergyFull!.Value);
            return (int)Math.Clamp(Math.Round(100.0 * now / full), 0, 100);
        }
        return (int)Math.Round(batteries.Average(b => (double)b.Percent));
    }
}
