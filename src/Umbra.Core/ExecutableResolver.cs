namespace Umbra;

/// <summary>
/// Finds the program file a command's declared executable stands for. The answer is what the user approves and what runs, so a
/// resolver never searches the current folder and never accepts a form of path that belongs to another OS.
/// </summary>
public interface IExecutableResolver
{
    /// <param name="exe">A bare program name or an absolute path, as declared (already validated by <c>CommandSpec</c>).</param>
    /// <param name="allowScripts">Windows only: also find .cmd and .bat files (they are only started for commands without parameters).</param>
    /// <returns>The full path of the file, or null when it isn't found.</returns>
    string? Resolve(string exe, bool allowScripts);
}

/// <summary>Windows rules: .exe/.com (and .cmd/.bat when allowed), drive-letter paths, absolute PATH entries only.</summary>
sealed class WindowsExecutableResolver(Func<string, string?>? getEnv = null) : IExecutableResolver
{
    public string? Resolve(string exe, bool allowScripts) =>
        CommandRunner.ResolveExe(exe, allowScripts, (getEnv ?? Environment.GetEnvironmentVariable)("PATH"));
}

/// <summary>
/// POSIX rules: an absolute path as given, or a bare name searched on the absolute entries of PATH (an empty entry means "the
/// current folder" and is skipped, like every relative entry). No extension is added. The file test is injected, so the search
/// logic runs on any OS; <see cref="ExecutableResolver.IsExecutableFile"/> is the real one.
/// </summary>
sealed class PosixExecutableResolver(Func<string, bool> isExecutableFile, Func<string, string?> getEnv) : IExecutableResolver
{
    public string? Resolve(string exe, bool allowScripts)
    {
        if (exe.Length == 0 || exe.Contains('\0') || exe.Contains('\\')) return null;
        if (exe.StartsWith('/'))
            return exe.StartsWith("//", StringComparison.Ordinal) || exe.Split('/').Contains("..") ? null : isExecutableFile(exe) ? exe : null;
        if (exe.Contains('/') || exe.Contains(':')) return null; // only a bare name is searched
        foreach (var dir in (getEnv("PATH") ?? "").Split(':'))
        {
            if (!dir.StartsWith('/')) continue;
            var candidate = dir.TrimEnd('/') + "/" + exe;
            if (isExecutableFile(candidate)) return candidate;
        }
        return null;
    }
}

/// <summary>Finds nothing, so every command reports "not found" and can't be approved or run.</summary>
sealed class NoExecutableResolver : IExecutableResolver
{
    public string? Resolve(string exe, bool allowScripts) => null;
}

/// <summary>Picks the resolver of the OS Umbra runs on.</summary>
public static class ExecutableResolver
{
    /// <summary>
    /// The resolver for this OS. <c>CommandApprovals</c> takes another one when a test or a host needs it. Inside a Flatpak it finds
    /// nothing: the sandbox sees its own runtime files, not the host programs a command would really start, so approving and hashing a
    /// sandbox file would bind the approval to the wrong program. Commands stay off there until the maintainer decides how a host
    /// program is identified (see the pull request for #194).
    /// </summary>
    public static IExecutableResolver Default { get; } =
        OperatingSystem.IsLinux() ? (CommandLaunch.IsFlatpak(File.Exists) ? new NoExecutableResolver() : new PosixExecutableResolver(IsExecutableFile, Environment.GetEnvironmentVariable))
        : new WindowsExecutableResolver();

    /// <summary>
    /// A regular file with an execute permission bit. Thin wrapper over the Unix file mode: it is not run on Windows (always false there)
    /// and has not been run on Linux by its author.
    /// </summary>
    public static bool IsExecutableFile(string path)
    {
        if (OperatingSystem.IsWindows()) return false;
        try
        {
            if (!File.Exists(path)) return false; // follows symlinks, and a directory is not a file
            return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
