using System.Text.Json.Nodes;

namespace Umbra.Linux;

/// <summary>Reads text files under <c>/proc</c> and <c>/sys</c>; the seam that lets <see cref="LinuxMetrics"/> run on fixtures.</summary>
public interface IProcSource
{
    /// <summary>The file's text, or null when it is missing or unreadable.</summary>
    string? ReadText(string path);

    /// <summary>The names (not full paths) of the entries in a directory; empty when it does not exist.</summary>
    IReadOnlyList<string> ListDirectory(string path);
}

/// <summary>
/// The real file-system source. Thin wrapper over <see cref="File"/> and <see cref="Directory"/>; NOT RUN ON LINUX YET
/// (nothing here is covered by tests, the parsing it feeds is).
/// </summary>
public sealed class FileProcSource : IProcSource
{
    public string? ReadText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public IReadOnlyList<string> ListDirectory(string path)
    {
        try { return Directory.Exists(path) ? Directory.GetFileSystemEntries(path).Select(p => Path.GetFileName(p)!).ToArray() : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}

/// <summary>
/// <c>umbra.system()</c> on Linux: CPU from <c>/proc/stat</c> deltas, memory from <c>/proc/meminfo</c> (<c>MemAvailable</c>), network
/// from <c>/proc/net/dev</c> deltas, in the same JSON shape as <c>WindowsMetrics</c>. Also battery (<c>/sys/class/power_supply/BAT*</c>)
/// and uptime for later use. Samples at most twice a second, shared by all widgets.
/// </summary>
public sealed class LinuxMetrics(IProcSource source, Func<long> tickMs)
{
    const long MinSampleMs = 500;
    readonly object _gate = new();
    CpuTimes? _lastCpu;
    long _cpuAt, _netAt, _rx, _tx;
    double _cpu, _down, _up;

    /// <summary>Same shape as the Windows reader: only the kinds in <paramref name="allowed"/> appear.</summary>
    public JsonObject Read(IReadOnlyCollection<string> allowed)
    {
        var o = new JsonObject();
        lock (_gate)
        {
            if (allowed.Contains("cpu")) o["cpu"] = new JsonObject { ["percent"] = Math.Round(Cpu(), 1) };
            if (allowed.Contains("memory") && Memory() is { } m)
                o["memory"] = new JsonObject { ["totalBytes"] = m.TotalBytes, ["usedBytes"] = m.UsedBytes, ["percent"] = m.Percent };
            if (allowed.Contains("network"))
            {
                Net();
                o["network"] = new JsonObject { ["downBytesPerSec"] = (long)_down, ["upBytesPerSec"] = (long)_up };
            }
        }
        return o;
    }

    /// <summary>Current memory, or null when <c>/proc/meminfo</c> cannot be read.</summary>
    public MemoryInfo? Memory() => source.ReadText("/proc/meminfo") is { } t ? ProcParsers.ParseMemInfo(t) : null;

    /// <summary>Seconds since boot, or null when <c>/proc/uptime</c> cannot be read.</summary>
    public double? UptimeSeconds() => source.ReadText("/proc/uptime") is { } t ? ProcParsers.ParseUptime(t) : null;

    /// <summary>Every <c>BAT*</c> battery that reports a level (empty on a desktop PC).</summary>
    public IReadOnlyList<BatteryInfo> Batteries() =>
        source.ListDirectory("/sys/class/power_supply")
            .Where(n => n.StartsWith("BAT", StringComparison.Ordinal)).Order(StringComparer.Ordinal)
            .Select(n => source.ReadText($"/sys/class/power_supply/{n}/uevent"))
            .Select(t => t == null ? null : ProcParsers.ParseBattery(t))
            .OfType<BatteryInfo>().ToArray();

    double Cpu()
    {
        var now = tickMs();
        if (_cpuAt != 0 && now - _cpuAt < MinSampleMs) return _cpu;
        if (source.ReadText("/proc/stat") is not { } text || ProcParsers.ParseCpuStat(text) is not { } cur) return _cpu;
        if (_lastCpu is { } prev && cur.Total > prev.Total)
            _cpu = Math.Clamp(100.0 * (1.0 - (double)(cur.Idle - prev.Idle) / (cur.Total - prev.Total)), 0, 100);
        _lastCpu = cur; _cpuAt = now;
        return _cpu;
    }

    void Net()
    {
        var now = tickMs();
        if (_netAt != 0 && now - _netAt < MinSampleMs) return;
        if (source.ReadText("/proc/net/dev") is not { } text) return;
        var (rx, tx) = ProcParsers.ParseNetDev(text);
        if (_netAt != 0 && now > _netAt && rx >= _rx && tx >= _tx)
        {
            var secs = (now - _netAt) / 1000.0;
            _down = (rx - _rx) / secs; _up = (tx - _tx) / secs;
        }
        _rx = rx; _tx = tx; _netAt = now;
    }
}
