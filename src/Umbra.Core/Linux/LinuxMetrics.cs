using System.Text.Json.Nodes;

namespace Umbra.Linux;

/// <summary>Reads text files under <c>/proc</c> and <c>/sys</c>; the seam that lets <see cref="LinuxMetrics"/> run on fixtures.</summary>
public interface IProcSource
{
    /// <summary>The file's text, or null when it is missing or unreadable.</summary>
    string? ReadText(string path);

    /// <summary>The names (not full paths) of the entries in a directory; empty when it does not exist.</summary>
    IReadOnlyList<string> ListDirectory(string path);

    /// <summary>Size and space available to the user of the file system mounted at <paramref name="mountPoint"/> (statvfs), or null.</summary>
    (long TotalBytes, long FreeBytes)? FileSystemSpace(string mountPoint);

    /// <summary>NVIDIA GPU usage through NVML, or null without the proprietary driver.</summary>
    GpuSample? NvidiaGpu();
}

/// <summary>
/// The real file-system source: a thin wrapper over <see cref="File"/> and <see cref="Directory"/>
/// (<c>LinuxLiveTests</c> runs it against the live <c>/proc</c> on Linux).
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

    public (long TotalBytes, long FreeBytes)? FileSystemSpace(string mountPoint)
    {
        try
        {
            var d = new DriveInfo(mountPoint); // statvfs on Unix; AvailableFreeSpace is f_bavail, what df shows
            return d.TotalSize > 0 ? (d.TotalSize, d.AvailableFreeSpace) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    public GpuSample? NvidiaGpu() => Nvml.Read();
}

/// <summary>
/// <c>umbra.system()</c> on Linux: CPU from <c>/proc/stat</c> deltas, memory from <c>/proc/meminfo</c> (<c>MemAvailable</c>), network
/// from <c>/proc/net/dev</c> deltas, disk from <c>/proc/diskstats</c> deltas (whole disks) and drives from <c>/proc/mounts</c> + statvfs,
/// gpu from amdgpu's sysfs files or NVML (null on Intel and nouveau), in the same JSON shape as <c>WindowsMetrics</c>. Also battery (<c>/sys/class/power_supply/BAT*</c>)
/// and uptime for later use. Samples at most twice a second, shared by all widgets.
/// </summary>
public sealed class LinuxMetrics(IProcSource source, Func<long> tickMs)
{
    const long MinSampleMs = 500;
    readonly object _gate = new();
    CpuTimes? _lastCpu;
    long _cpuAt, _netAt, _rx, _tx, _diskAt, _drivesAt, _gpuAt;
    double _cpu, _down, _up;
    double? _diskActive, _diskRead, _diskWrite;
    Dictionary<string, DiskCounters> _lastDisks = [];
    JsonArray _drives = [];
    GpuSample? _gpu;

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
            if (allowed.Contains("gpu"))
            {
                var g = Gpu();
                o["gpu"] = new JsonObject
                {
                    ["percent"] = g?.Percent is { } p ? Math.Round(p, 1) : null,
                    ["memoryUsedBytes"] = g?.MemoryUsedBytes,
                };
            }
            if (allowed.Contains("disk"))
            {
                Disk();
                o["disk"] = new JsonObject
                {
                    ["activePercent"] = _diskActive is { } a ? Math.Round(a, 1) : null,
                    ["readBytesPerSec"] = _diskRead is { } r ? (long)r : null,
                    ["writeBytesPerSec"] = _diskWrite is { } w ? (long)w : null,
                    ["drives"] = Drives().DeepClone(),
                };
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

    /// <summary>
    /// The busiest GPU: <c>gpu_busy_percent</c> of every <c>/sys/class/drm/cardN</c> that has one (amdgpu), memory as VRAM + GTT
    /// used (dedicated + shared, like Windows); NVML for NVIDIA; null when nothing reports (Intel i915/xe, nouveau, no GPU).
    /// </summary>
    GpuSample? Gpu()
    {
        var now = tickMs();
        if (_gpuAt != 0 && now - _gpuAt < MinSampleMs) return _gpu;
        double? busiest = null;
        long? used = null;
        foreach (var card in source.ListDirectory("/sys/class/drm").Where(IsCard).Order(StringComparer.Ordinal))
        {
            var dev = $"/sys/class/drm/{card}/device";
            if (ProcParsers.ParseSysfsNumber(source.ReadText($"{dev}/gpu_busy_percent")) is { } busy)
                busiest = Math.Max(busiest ?? 0, Math.Clamp(busy, 0, 100));
            var vram = ProcParsers.ParseSysfsNumber(source.ReadText($"{dev}/mem_info_vram_used"));
            var gtt = ProcParsers.ParseSysfsNumber(source.ReadText($"{dev}/mem_info_gtt_used"));
            if (vram != null || gtt != null) used = (used ?? 0) + (vram ?? 0) + (gtt ?? 0);
        }
        if (source.NvidiaGpu() is { } nv)
        {
            if (nv.Percent is { } p) busiest = Math.Max(busiest ?? 0, p);
            if (nv.MemoryUsedBytes is { } m) used = (used ?? 0) + m;
        }
        _gpu = busiest == null && used == null ? null : new GpuSample(busiest, used);
        _gpuAt = now;
        return _gpu;
    }

    static bool IsCard(string name) => name.Length > 4 && name.StartsWith("card", StringComparison.Ordinal) && name[4..].All(char.IsAsciiDigit);

    /// <summary>Read and write bytes per second summed over whole disks; active time of the busiest one (io_ticks over elapsed time).</summary>
    void Disk()
    {
        var now = tickMs();
        if (_diskAt != 0 && now - _diskAt < MinSampleMs) return;
        var cur = source.ReadText("/proc/diskstats") is { } text
            ? ProcParsers.ParseDiskStats(text).ToDictionary(d => d.Name, StringComparer.Ordinal) : [];
        if (cur.Count == 0) _diskActive = _diskRead = _diskWrite = null;
        else if (_diskAt != 0 && now > _diskAt)
        {
            var ms = (double)(now - _diskAt);
            double read = 0, write = 0, active = 0;
            var any = false;
            foreach (var (name, d) in cur)
            {
                if (!_lastDisks.TryGetValue(name, out var p) || d.SectorsRead < p.SectorsRead || d.SectorsWritten < p.SectorsWritten
                    || d.IoTicksMs < p.IoTicksMs) continue; // a new disk, or counters reset (hot-plug): next sample
                any = true;
                read += (d.SectorsRead - p.SectorsRead) * 512.0;
                write += (d.SectorsWritten - p.SectorsWritten) * 512.0;
                active = Math.Max(active, Math.Clamp(100.0 * (d.IoTicksMs - p.IoTicksMs) / ms, 0, 100));
            }
            if (any) { _diskRead = read * 1000 / ms; _diskWrite = write * 1000 / ms; _diskActive = active; }
        }
        _lastDisks = cur; _diskAt = now;
    }

    /// <summary>The mounted drives (<see cref="ProcParsers.DriveMounts"/>) by mount point, refreshed every 5 s, as on Windows.</summary>
    JsonArray Drives()
    {
        var now = tickMs();
        if (_drivesAt != 0 && now - _drivesAt < 5000) return _drives;
        var list = new JsonArray();
        if ((source.ReadText("/proc/self/mounts") ?? source.ReadText("/proc/mounts")) is { } mounts)
            foreach (var m in ProcParsers.DriveMounts(ProcParsers.ParseMounts(mounts)))
            {
                if (source.FileSystemSpace(m.MountPoint) is not { } space || space.TotalBytes <= 0) continue;
                var free = Math.Clamp(space.FreeBytes, 0, space.TotalBytes);
                list.Add(new JsonObject
                {
                    ["name"] = m.MountPoint,
                    ["totalBytes"] = space.TotalBytes,
                    ["freeBytes"] = free,
                    ["usedPercent"] = Math.Round(100.0 * (space.TotalBytes - free) / space.TotalBytes, 1),
                });
            }
        _drives = list; _drivesAt = now;
        return _drives;
    }
}
