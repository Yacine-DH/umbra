using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Umbra.Cli;

/// <summary>Sends control commands to the running Umbra, starting it in the background if needed.</summary>
static class UmbraClient
{
    public static async Task<JsonObject> SendAsync(string cmd, JsonObject? args = null, bool autoStart = true)
    {
        var request = Protocol.Request(cmd, args);
        try
        {
            return await Protocol.SendAsync(request);
        }
        catch (TimeoutException) when (autoStart && cmd != "quit")
        {
            var exe = FindUmbraExe() ?? throw new InvalidOperationException(
                $"Umbra is not running and {AppFileName} was not found. Install it from https://github.com/AmineChr54/umbra/releases or set UMBRA_EXE.");
            Process.Start(new ProcessStartInfo(exe, "--background") { UseShellExecute = false });
            var deadline = DateTime.UtcNow.AddSeconds(25);
            while (true)
            {
                await Task.Delay(500);
                try { return await Protocol.SendAsync(request, connectTimeoutMs: 1000); }
                catch (TimeoutException) when (DateTime.UtcNow < deadline) { }
            }
        }
    }

    /// <summary>The app's file name: Umbra.exe on Windows, umbra on Linux.</summary>
    static string AppFileName => OperatingSystem.IsWindows() ? "Umbra.exe" : "umbra";

    static string? FindUmbraExe() =>
        Candidates(OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable("UMBRA_EXE"), AppContext.BaseDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
            .FirstOrDefault(File.Exists);

    /// <summary>
    /// Where the app may be, in order: UMBRA_EXE, next to umbractl, then the per-user install. Windows: the installer's
    /// %LOCALAPPDATA%\Programs\Umbra. Linux: the tarball layout (umbractl/ and umbra/ side by side, so ../umbra/umbra from
    /// umbractl's real folder; ~/.local/bin/umbractl is a symlink that resolves there) and install.sh's ~/.local/opt/umbra.
    /// </summary>
    internal static IEnumerable<string> Candidates(bool windows, string? umbraExe, string baseDir, string localAppData, string home)
    {
        if (!string.IsNullOrEmpty(umbraExe)) yield return umbraExe;
        var name = windows ? "Umbra.exe" : "umbra";
        yield return Path.Combine(baseDir, name);
        if (windows)
        {
            yield return Path.Combine(localAppData, "Programs", "Umbra", name);
            yield break;
        }
        yield return Path.GetFullPath(Path.Combine(baseDir, "..", "umbra", name));
        yield return Path.Combine(home, ".local", "opt", "umbra", "umbra", name);
    }

    /// <summary>The community widgets folder (mirrors AppPaths in Umbra.exe, including UMBRA_DATA_DIR).</summary>
    public static string CommunityDir =>
        Path.Combine(PortableMode.DataDir(AppContext.BaseDirectory), "widgets");
}
