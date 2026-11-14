namespace Umbra.Tests;

/// <summary>Points AppPaths at a throwaway folder so tests never touch the real %APPDATA%\Umbra.</summary>
static class TestEnv
{
    public static readonly string DataDir = Path.Combine(Path.GetTempPath(), $"umbra-tests-{Guid.NewGuid():N}");
    static TestEnv() => Environment.SetEnvironmentVariable("UMBRA_DATA_DIR", DataDir);
    public static void Init() { _ = DataDir; }
}

/// <summary>A test of Windows-only behaviour (Windows paths, the Windows updater): skipped on other systems.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Windows only"; }
}

/// <summary>A theory of Windows-only behaviour: skipped on other systems.</summary>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Windows only"; }
}

/// <summary>A test that needs a real Linux system (live <c>/proc</c>, XDG tools): skipped elsewhere.</summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute() { if (!OperatingSystem.IsLinux()) Skip = "Linux only"; }
}
