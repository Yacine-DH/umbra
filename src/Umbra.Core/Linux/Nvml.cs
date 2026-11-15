using System.Runtime.InteropServices;

namespace Umbra.Linux;

/// <summary>
/// NVIDIA GPU usage through NVML (<c>libnvidia-ml.so.1</c>, shipped with the proprietary driver), loaded at run time only when
/// present: no package, no build dependency. Busiest device's utilization and the sum of used memory. Null when the library or
/// a device is missing, or NVML answers an error (nouveau and Flatpak without the driver extension read as no NVIDIA GPU).
/// </summary>
public static class Nvml
{
    [StructLayout(LayoutKind.Sequential)] struct Utilization { public uint Gpu, Memory; }
    [StructLayout(LayoutKind.Sequential)] struct MemoryInfo { public ulong Total, Free, Used; }

    static readonly object Gate = new();
    static bool _tried;
    static unsafe delegate* unmanaged<uint*, int> _count;
    static unsafe delegate* unmanaged<uint, nint*, int> _handle;
    static unsafe delegate* unmanaged<nint, Utilization*, int> _utilization;
    static unsafe delegate* unmanaged<nint, MemoryInfo*, int> _memory;

    /// <summary>The current reading, or null without a working NVML.</summary>
    public static unsafe GpuSample? Read()
    {
        lock (Gate)
        {
            if (!_tried)
            {
                _tried = true;
                Load();
            }
            if (_count == null) return null;
            uint n = 0;
            if (_count(&n) != 0 || n == 0) return null;
            double? busiest = null;
            long? used = null;
            for (uint i = 0; i < Math.Min(n, 16u); i++)
            {
                nint h = 0;
                if (_handle(i, &h) != 0) continue;
                Utilization u;
                if (_utilization(h, &u) == 0) busiest = Math.Max(busiest ?? 0, Math.Min(u.Gpu, 100u));
                MemoryInfo m;
                if (_memory(h, &m) == 0) used = (used ?? 0) + (long)Math.Min(m.Used, (ulong)long.MaxValue / 2);
            }
            return busiest == null && used == null ? null : new GpuSample(busiest, used);
        }
    }

    static unsafe void Load()
    {
        if (!OperatingSystem.IsLinux() || !NativeLibrary.TryLoad("libnvidia-ml.so.1", out var lib)) return;
        if (!NativeLibrary.TryGetExport(lib, "nvmlInit_v2", out var init)
            || !NativeLibrary.TryGetExport(lib, "nvmlDeviceGetCount_v2", out var count)
            || !NativeLibrary.TryGetExport(lib, "nvmlDeviceGetHandleByIndex_v2", out var handle)
            || !NativeLibrary.TryGetExport(lib, "nvmlDeviceGetUtilizationRates", out var utilization)
            || !NativeLibrary.TryGetExport(lib, "nvmlDeviceGetMemoryInfo", out var memory))
            return;
        try
        {
            if (((delegate* unmanaged<int>)init)() != 0) return;
        }
        catch (Exception ex) { Log.Write($"system metrics: NVML init failed: {ex.Message}"); return; }
        _handle = (delegate* unmanaged<uint, nint*, int>)handle;
        _utilization = (delegate* unmanaged<nint, Utilization*, int>)utilization;
        _memory = (delegate* unmanaged<nint, MemoryInfo*, int>)memory;
        _count = (delegate* unmanaged<uint*, int>)count;
        Log.Write("system metrics: NVML loaded, NVIDIA gpu readings on");
    }
}
