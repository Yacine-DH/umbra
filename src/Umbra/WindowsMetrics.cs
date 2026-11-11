using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Umbra;

/// <summary>Reads the <see cref="SystemMetrics"/> kinds on Windows (kernel32 times and memory, NIC counters, <see cref="PerfCounters"/> for gpu and disk).</summary>
static class WindowsMetrics
{
    [StructLayout(LayoutKind.Sequential)] struct FileTime { public uint Lo, Hi; public readonly ulong Value => ((ulong)Hi << 32) | Lo; }
    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemoryStatus s);

    static readonly object Gate = new();
    static ulong _idle, _total;
    static long _cpuAt, _netAt, _bytesIn, _bytesOut;
    static double _cpu, _down, _up;
    static PerfCounters? _perf;
    static bool _perfTried;
    static long _perfAt, _drivesAt;
    static JsonArray _drives = new();

    /// <summary>Samples at most twice a second, shared by all widgets, so rates stay meaningful whoever asks.</summary>
    public static JsonObject Read(IReadOnlyCollection<string> allowed)
    {
        var o = new JsonObject();
        lock (Gate)
        {
            if (allowed.Contains("cpu")) o["cpu"] = new JsonObject { ["percent"] = Math.Round(Cpu(), 1) };
            if (allowed.Contains("memory"))
            {
                var s = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
                if (GlobalMemoryStatusEx(ref s))
                    o["memory"] = new JsonObject
                    {
                        ["totalBytes"] = (long)s.TotalPhys,
                        ["usedBytes"] = (long)(s.TotalPhys - s.AvailPhys),
                        ["percent"] = (int)s.MemoryLoad,
                    };
            }
            if (allowed.Contains("network"))
            {
                Net();
                o["network"] = new JsonObject { ["downBytesPerSec"] = (long)_down, ["upBytesPerSec"] = (long)_up };
            }
            if (allowed.Contains("gpu") || allowed.Contains("disk"))
            {
                var perf = Perf();
                if (allowed.Contains("gpu"))
                    o["gpu"] = new JsonObject
                    {
                        ["percent"] = Round(perf?.GpuPercent()),
                        ["memoryUsedBytes"] = perf?.GpuMemoryUsed() is { } mem ? (long)mem : null,
                    };
                if (allowed.Contains("disk"))
                    o["disk"] = new JsonObject
                    {
                        ["activePercent"] = Round(perf?.DiskActivePercent()),
                        ["readBytesPerSec"] = perf?.DiskReadBytesPerSec() is { } r ? (long)r : null,
                        ["writeBytesPerSec"] = perf?.DiskWriteBytesPerSec() is { } w ? (long)w : null,
                        ["drives"] = Drives().DeepClone(),
                    };
            }
        }
        return o;
    }

    static double? Round(double? v) => v is { } d ? Math.Round(d, 1) : null;

    /// <summary>The performance counters, opened on the first gpu or disk read and sampled like the rest (at most twice a second).</summary>
    static PerfCounters? Perf()
    {
        if (!_perfTried)
        {
            _perfTried = true;
            _perf = PerfCounters.Open();
            if (_perf is null) Log.Write("system metrics: performance counters unavailable, gpu and disk read as empty");
        }
        var now = Environment.TickCount64;
        if (_perf is not null && (_perfAt == 0 || now - _perfAt >= 500))
        {
            _perf.Collect();
            _perfAt = now;
        }
        return _perf;
    }

    /// <summary>Fixed drives that are ready, by drive letter only (no volume labels), refreshed every 5 s.</summary>
    static JsonArray Drives()
    {
        var now = Environment.TickCount64;
        if (_drivesAt != 0 && now - _drivesAt < 5000) return _drives;
        var list = new JsonArray();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                long total = d.TotalSize, free = d.TotalFreeSpace;
                if (total <= 0) continue;
                list.Add(new JsonObject
                {
                    ["name"] = d.Name.TrimEnd('\\'),
                    ["totalBytes"] = total,
                    ["freeBytes"] = free,
                    ["usedPercent"] = Math.Round(100.0 * (total - free) / total, 1),
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Write($"system metrics: drive read failed: {ex.Message}"); }
        _drives = list;
        _drivesAt = now;
        return _drives;
    }

    static double Cpu()
    {
        var now = Environment.TickCount64;
        if (now - _cpuAt < 500 && _cpuAt != 0) return _cpu;
        if (!GetSystemTimes(out var i, out var k, out var u)) return _cpu;
        var idle = i.Value; var total = k.Value + u.Value; // kernel time includes idle
        if (_cpuAt != 0 && total > _total)
            _cpu = Math.Clamp(100.0 * (1.0 - (double)(idle - _idle) / (total - _total)), 0, 100);
        _idle = idle; _total = total; _cpuAt = now;
        return _cpu;
    }

    static void Net()
    {
        var now = Environment.TickCount64;
        if (now - _netAt < 500 && _netAt != 0) return;
        long rx = 0, tx = 0;
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var st = n.GetIPStatistics();
                rx += st.BytesReceived; tx += st.BytesSent;
            }
        }
        catch (Exception ex) { Log.Write($"system metrics: network read failed: {ex.Message}"); }
        if (_netAt != 0 && now > _netAt && rx >= _bytesIn && tx >= _bytesOut)
        {
            var secs = (now - _netAt) / 1000.0;
            _down = (rx - _bytesIn) / secs; _up = (tx - _bytesOut) / secs;
        }
        _bytesIn = rx; _bytesOut = tx; _netAt = now;
    }
}
