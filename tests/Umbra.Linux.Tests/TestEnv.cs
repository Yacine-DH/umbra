using System.Runtime.CompilerServices;

namespace Umbra.Linux.Tests;

/// <summary>
/// Points AppPaths (and so <c>Log</c>, settings, stores) at a throwaway folder before any test in this assembly runs, so no
/// test ever writes to the real profile (~/.local/share/umbra).
/// </summary>
static class TestEnv
{
    public static readonly string DataDir = Path.Combine(Path.GetTempPath(), $"umbra-linux-tests-{Guid.NewGuid():N}");

    [ModuleInitializer]
    internal static void Init() => Environment.SetEnvironmentVariable("UMBRA_DATA_DIR", DataDir);
}

/// <summary>A fact that runs only on Linux (Umbra.slnx also runs this assembly on Windows CI, where Unix paths and libc don't exist).</summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute() { if (!OperatingSystem.IsLinux()) Skip = "Linux only"; }
}

/// <summary>A theory that runs only on Linux.</summary>
public sealed class LinuxTheoryAttribute : TheoryAttribute
{
    public LinuxTheoryAttribute() { if (!OperatingSystem.IsLinux()) Skip = "Linux only"; }
}
