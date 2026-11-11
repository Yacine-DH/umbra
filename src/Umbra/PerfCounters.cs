using System.Runtime.InteropServices;

namespace Umbra;

/// <summary>
/// The Windows performance counters behind the gpu and disk kinds of <c>umbra.system()</c>, read the way Task Manager
/// does: GPU load is the busiest engine of any adapter, disk activity the busiest physical disk. English counter names
/// (<c>PdhAddEnglishCounter</c>), so it works on every display language. Only totals leave this class; the per-process and
/// per-adapter instance names PDH reports never do. Not thread-safe: <see cref="WindowsMetrics"/> calls it under its lock.
/// </summary>
sealed class PerfCounters : IDisposable
{
    const uint FmtDouble = 0x200, FmtNoCap100 = 0x8000, MoreData = 0x800007D2;

    [StructLayout(LayoutKind.Explicit)] struct FmtValue { [FieldOffset(0)] public uint CStatus; [FieldOffset(8)] public double Double; }
    [StructLayout(LayoutKind.Sequential)] struct FmtItem { public IntPtr Name; public FmtValue Value; }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQueryW(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out FmtValue value);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, out uint count, IntPtr items);
    [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);

    readonly IntPtr _query;
    readonly IntPtr _gpuEngine, _gpuDedicated, _gpuShared, _diskIdle, _diskRead, _diskWrite;

    PerfCounters(IntPtr query)
    {
        _query = query;
        _gpuEngine = Add(@"\GPU Engine(*)\Utilization Percentage");
        _gpuDedicated = Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
        _gpuShared = Add(@"\GPU Adapter Memory(*)\Shared Usage");
        _diskIdle = Add(@"\PhysicalDisk(*)\% Idle Time");
        _diskRead = Add(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
        _diskWrite = Add(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
    }

    /// <summary>Opens the query, or null when PDH isn't usable here. A counter Windows doesn't have (no GPU counters in some VMs) just reads as null.</summary>
    public static PerfCounters? Open()
    {
        try { return PdhOpenQueryW(null, IntPtr.Zero, out var q) == 0 ? new PerfCounters(q) : null; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    IntPtr Add(string path) => PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out var c) == 0 ? c : IntPtr.Zero;

    /// <summary>Takes a new sample. Rates and percentages need two samples, so the first read after <see cref="Open"/> gives nulls.</summary>
    public void Collect() => PdhCollectQueryData(_query);

    /// <summary>GPU load in percent: per engine (3D, copy, video...) the sum over processes, then the busiest engine of any adapter.</summary>
    public double? GpuPercent()
    {
        var engines = new Dictionary<string, double>(StringComparer.Ordinal);
        if (!Array(_gpuEngine, (name, v) =>
            {
                // "pid_1234_luid_0x0_0xD1F2_phys_0_eng_3_engtype_3D": the engine is everything from "luid_" to "_engtype".
                var at = name.IndexOf("luid_", StringComparison.Ordinal);
                var end = name.IndexOf("_engtype", StringComparison.Ordinal);
                var key = at >= 0 && end > at ? name[at..end] : name;
                engines[key] = engines.GetValueOrDefault(key) + v;
            })) return null;
        return Math.Clamp(engines.Count == 0 ? 0 : engines.Values.Max(), 0, 100);
    }

    /// <summary>GPU memory in use, in bytes: dedicated plus shared (Task Manager's "GPU memory"), summed over adapters.</summary>
    public double? GpuMemoryUsed()
    {
        double sum = 0;
        var dedicated = Array(_gpuDedicated, (_, v) => sum += v);
        var shared = Array(_gpuShared, (_, v) => sum += v);
        return dedicated || shared ? sum : null;
    }

    /// <summary>Disk activity in percent (Task Manager's "Active time"): the busiest physical disk.</summary>
    public double? DiskActivePercent()
    {
        double? busiest = null;
        if (!Array(_diskIdle, (name, v) => { if (name != "_Total") busiest = Math.Max(busiest ?? 0, 100 - Math.Clamp(v, 0, 100)); })) return null;
        return busiest ?? 0;
    }

    public double? DiskReadBytesPerSec() => Single(_diskRead);
    public double? DiskWriteBytesPerSec() => Single(_diskWrite);

    static double? Single(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return null;
        return PdhGetFormattedCounterValue(counter, FmtDouble | FmtNoCap100, out _, out var v) == 0 && v.CStatus <= 1 ? Math.Max(0, v.Double) : null;
    }

    /// <summary>Calls <paramref name="each"/> for every valid instance of a wildcard counter. False when there is no data yet.</summary>
    static bool Array(IntPtr counter, Action<string, double> each)
    {
        if (counter == IntPtr.Zero) return false;
        uint size = 0;
        var rc = PdhGetFormattedCounterArrayW(counter, FmtDouble | FmtNoCap100, ref size, out _, IntPtr.Zero);
        if (rc == 0) return true; // no instances (no GPU work at all, say): a valid empty answer
        if (rc != MoreData || size == 0) return false;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, FmtDouble | FmtNoCap100, ref size, out var count, buf) != 0) return false;
            var stride = Marshal.SizeOf<FmtItem>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<FmtItem>(buf + i * stride);
                if (item.Value.CStatus > 1) continue; // PDH_CSTATUS_VALID_DATA / NEW_DATA only
                each(Marshal.PtrToStringUni(item.Name) ?? "", Math.Max(0, item.Value.Double));
            }
            return true;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public void Dispose() => PdhCloseQuery(_query);
}
