namespace Umbra;

/// <summary>The program to start, its arguments and its whole environment (nothing is inherited).</summary>
public sealed record LaunchPlan(string FileName, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Environment, string WorkingDirectory);

/// <summary>
/// Turns an approved command (resolved executable, validated argv) into the process that is really started. Pure, so the argv that
/// reaches the OS is tested on any host.
/// <list type="bullet">
/// <item>Windows: the program itself; a job object contains it.</item>
/// <item>Linux: <c>setsid -- program args</c>, so the child leads its own process group and a timeout can kill the group.</item>
/// <item>Linux in a Flatpak: the same, around <c>flatpak-spawn --host -- program args</c>, because the sandbox cannot see host programs.
/// Built and tested here but <b>not wired in</b>: <see cref="CommandRunner"/> refuses to run in a Flatpak until a host program can be identified for the approval.</item>
/// </list>
/// The program and every argument stay separate argv elements after a <c>--</c>, so none of them can turn into an option of
/// <c>setsid</c> or <c>flatpak-spawn</c>.
/// </summary>
public static class CommandLaunch
{
    /// <summary>Whether this process runs inside a Flatpak sandbox: the /.flatpak-info file Flatpak mounts into every app.</summary>
    public static bool IsFlatpak(Func<string, bool> fileExists) => fileExists("/.flatpak-info");

    /// <param name="platform">"windows" or "linux".</param>
    /// <param name="flatpak">Linux only: start the program on the host through flatpak-spawn.</param>
    /// <param name="setsidPath">Linux only: the setsid program (from util-linux), as an absolute path or a name.</param>
    /// <param name="env">The scrubbed environment from <see cref="CommandEnvironment.Scrub"/>.</param>
    public static LaunchPlan Plan(string platform, bool flatpak, string setsidPath, string exePath, IReadOnlyList<string> args,
        string workingDir, IReadOnlyDictionary<string, string> env)
    {
        if (platform != WidgetPlatforms.Linux) return new LaunchPlan(exePath, args, env, workingDir);

        var inner = new List<string> { exePath };
        inner.AddRange(args);
        if (flatpak)
        {
            // The host starts the program with exactly the scrubbed variables (--clear-env drops everything the session helper
            // would otherwise pass) in the working folder; --watch-bus ends it if Umbra disappears from the bus.
            var spawn = new List<string> { "flatpak-spawn", "--host", "--watch-bus", "--clear-env", "--directory=" + workingDir };
            spawn.AddRange(env.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"--env={kv.Key}={kv.Value}"));
            spawn.Add("--");
            spawn.AddRange(inner);
            inner = spawn;
        }
        var all = new List<string> { "--" };
        all.AddRange(inner);
        return new LaunchPlan(setsidPath, all, env, workingDir);
    }
}
