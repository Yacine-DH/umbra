using System.Diagnostics;
using System.Text;

namespace Umbra;

/// <summary>What a finished (or killed) command produced.</summary>
/// <param name="StillRunning">A run as administrator that timed out or was stopped, but whose helper hadn't ended it 5 s later.</param>
public sealed record RunResult(int ExitCode, string Stdout, string Stderr, bool TimedOut, bool Truncated, long DurationMs, bool StillRunning = false);

/// <summary>
/// Starts one already-approved command: no shell, no window, scrubbed environment, output and time capped.
/// It does no policy checks itself; <see cref="CommandService"/> decides whether a run is allowed.
/// </summary>
static class CommandRunner
{
    public const int MaxOutputBytes = 1024 * 1024;

    static readonly string[] ProgramExtensions = { ".exe", ".com" };
    static readonly string[] ScriptExtensions = { ".cmd", ".bat" };

    /// <summary>Only the variables on <see cref="CommandEnvironment"/>'s allow-list reach the child. Tokens, API keys and UMBRA_* are never passed on.</summary>
    public static Dictionary<string, string> ScrubbedEnvironment() =>
        CommandEnvironment.Scrub(WidgetPlatforms.Current, Environment.GetEnvironmentVariable);

    /// <summary>
    /// Finds the program file for a declared executable: an absolute path as given, or a bare name searched on PATH
    /// (absolute PATH entries only, never the current folder). .cmd/.bat are only found when <paramref name="allowScripts"/>.
    /// A POSIX-style path ("/usr/bin/x", which Windows would read relative to the current drive) is never accepted. Other OSes use <see cref="ExecutableResolver"/>.
    /// </summary>
    public static string? ResolveExe(string exe, bool allowScripts, string? pathVariable = null)
    {
        if (exe.StartsWith('/') || exe.StartsWith('\\')) return null;
        var exts = allowScripts ? ProgramExtensions.Concat(ScriptExtensions).ToArray() : ProgramExtensions;
        var given = Path.GetExtension(exe);
        var names = exts.Contains(given, StringComparer.OrdinalIgnoreCase) ? new[] { exe } : exts.Select(e => exe + e).ToArray();
        if (Path.IsPathRooted(exe))
            return names.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
        foreach (var dir in (pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var d = dir.Trim().Trim('"');
            if (!Path.IsPathRooted(d)) continue;
            foreach (var n in names)
            {
                try
                {
                    var full = Path.Combine(d, n);
                    if (File.Exists(full)) return full;
                }
                catch (ArgumentException) { }
            }
        }
        return null;
    }

    static ProcessStartInfo StartInfo(string exePath, IReadOnlyList<string> args, string workingDir, bool createWorkingDir)
    {
        var platform = WidgetPlatforms.Current;
        if (platform == WidgetPlatforms.Other) throw new WidgetCommandException("Widget commands are not supported on this platform");
        if (createWorkingDir) Directory.CreateDirectory(workingDir);
        else if (!Directory.Exists(workingDir)) throw new WidgetCommandException($"The Start in folder {workingDir} doesn't exist");
        // Fail closed in a Flatpak: exePath was resolved inside the sandbox, so it may name a different file than the host's.
        // CommandLaunch.Plan can build the flatpak-spawn line, but it is not wired in until host programs can be identified.
        if (platform == WidgetPlatforms.Linux && CommandLaunch.IsFlatpak(File.Exists))
            throw new WidgetCommandException("Widget commands are not available in the Flatpak build yet");
        var plan = CommandLaunch.Plan(platform, flatpak: false, SetsidPath(), exePath, args, workingDir, ScrubbedEnvironment());
        var psi = new ProcessStartInfo(plan.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = plan.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in plan.Args) psi.ArgumentList.Add(a);
        psi.Environment.Clear();
        foreach (var (k, v) in plan.Environment) psi.Environment[k] = v;
        return psi;
    }

    /// <summary>util-linux <c>setsid</c> at its usual absolute path (never a PATH lookup), else the bare name.</summary>
    static string SetsidPath() =>
        OperatingSystem.IsWindows() ? "setsid" : new[] { "/usr/bin/setsid", "/bin/setsid" }.FirstOrDefault(ExecutableResolver.IsExecutableFile) ?? "setsid";

    /// <summary>Windows: a kill-on-close job object. Linux: the process group setsid made.</summary>
    static IProcessContainment CreateContainment() =>
        OperatingSystem.IsWindows() ? JobContainment.Create()
        : OperatingSystem.IsLinux() ? new ProcessGroupContainment()
        : throw new WidgetCommandException("Widget commands are not supported on this platform");

    /// <summary>
    /// Runs <paramref name="exePath"/> with <paramref name="args"/> (each one argv element). After <paramref name="timeout"/>
    /// or when <paramref name="ct"/> fires, the whole process tree is killed. Output beyond <paramref name="maxBytes"/> per stream is dropped.
    /// </summary>
    /// <param name="tail">Also gets the output as it arrives (live output in the widget); null = only the result.</param>
    /// <param name="createWorkingDir">True for the widget's data folder; false for a "Start in" folder the user picked, which must exist.</param>
    public static async Task<RunResult> RunAsync(string exePath, IReadOnlyList<string> args, string workingDir,
        TimeSpan timeout, int maxBytes = MaxOutputBytes, CancellationToken ct = default, OutputTail? tail = null, bool createWorkingDir = true)
    {
        var psi = StartInfo(exePath, args, workingDir, createWorkingDir);
        var sw = Stopwatch.StartNew();
        // The containment makes every descendant die with the run: on timeout, when the run ends, and on Windows also if Umbra itself exits.
        using var containment = CreateContainment();
        return await RunContainedAsync(psi, containment, sw, maxBytes, timeout, ct, tail);
    }

    static async Task<RunResult> RunContainedAsync(ProcessStartInfo psi, IProcessContainment containment, Stopwatch sw, int maxBytes, TimeSpan timeout, CancellationToken ct, OutputTail? tail)
    {
        using var proc = Process.Start(psi) ?? throw new WidgetCommandException("The program could not be started");
        containment.Attach(proc);
        try { proc.StandardInput.Close(); } catch (IOException) { }
        var stdout = new OutputCapture(maxBytes);
        var stderr = new OutputCapture(maxBytes);
        var pumps = Task.WhenAll(stdout.PumpAsync(proc.StandardOutput.BaseStream, tail), stderr.PumpAsync(proc.StandardError.BaseStream, tail));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var timedOut = false;
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            await containment.KillAsync(proc);
            try { proc.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
        }
        // A grandchild that inherited the pipes could keep them open: don't wait for them forever.
        await Task.WhenAny(pumps, Task.Delay(TimeSpan.FromSeconds(2)));
        sw.Stop();
        var exit = proc.HasExited ? proc.ExitCode : -1;
        return new RunResult(timedOut ? -1 : exit, stdout.Text(), stderr.Text(), timedOut, stdout.Truncated || stderr.Truncated, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Starts a user script that keeps running (a tunnel, a server): no time limit, and its output is only kept as the
    /// last characters in <paramref name="tail"/>. It sits in a kill-on-close job (Windows) or its own process group (Linux), so
    /// it and everything it started end on <see cref="RunningProcess.Stop"/>, when the handle is disposed, and when Umbra exits
    /// (on Windows even on a crash; on Linux Umbra's exit kills the group).
    /// </summary>
    public static RunningProcess Start(string exePath, IReadOnlyList<string> args, string workingDir, bool createWorkingDir, OutputTail tail)
    {
        var psi = StartInfo(exePath, args, workingDir, createWorkingDir);
        var containment = CreateContainment();
        Process? proc = null;
        try
        {
            proc = Process.Start(psi) ?? throw new WidgetCommandException("The program could not be started");
            containment.Attach(proc);
            try { proc.StandardInput.Close(); } catch (IOException) { }
            var pumps = Task.WhenAll(new OutputCapture(0).PumpAsync(proc.StandardOutput.BaseStream, tail), new OutputCapture(0).PumpAsync(proc.StandardError.BaseStream, tail));
            return new RunningProcess(proc, containment, pumps);
        }
        catch
        {
            containment.Dispose();
            if (proc is not null)
            {
                try { proc.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                proc.Dispose();
            }
            throw;
        }
    }
}

/// <summary>
/// The last <see cref="Max"/> characters a command wrote, stdout and stderr in the order they arrived: the live output a
/// widget shows while a script runs, and all that is kept of a script that keeps running.
/// </summary>
public sealed class OutputTail(int max = OutputTail.DefaultMax)
{
    public const int DefaultMax = 64 * 1024;

    readonly StringBuilder _text = new();
    readonly object _gate = new();

    public int Max => max;

    public void Append(string s)
    {
        if (s.Length == 0) return;
        lock (_gate)
        {
            _text.Append(s);
            if (_text.Length > max) _text.Remove(0, _text.Length - max);
        }
    }

    /// <summary>A line from Umbra itself ("started", "exited with code 1"), on a line of its own.</summary>
    public void Note(string line)
    {
        lock (_gate)
        {
            var nl = _text.Length > 0 && _text[^1] != '\n' ? "\n" : "";
            Append($"{nl}[Umbra] {line}\n");
        }
    }

    public string Text()
    {
        lock (_gate) return _text.ToString();
    }
}

/// <summary>A started keeps-running script: its process in its own containment (job object or process group).</summary>
sealed class RunningProcess : IDisposable
{
    readonly Process _proc;
    readonly IProcessContainment _containment;
    readonly object _gate = new();
    bool _closed, _exited;

    internal RunningProcess(Process proc, IProcessContainment containment, Task pumps)
    {
        _proc = proc;
        _containment = containment;
        Exited = WaitAsync(pumps);
    }

    public DateTime StartedAt { get; } = DateTime.UtcNow;
    /// <summary>Set by <see cref="Stop"/>: the exit was asked for, not the script's own doing.</summary>
    public bool StopRequested { get; private set; }
    /// <summary>Completes with the exit code once the process ended (its output read, its containment closed).</summary>
    public Task<int> Exited { get; }

    async Task<int> WaitAsync(Task pumps)
    {
        await _proc.WaitForExitAsync();
        // A grandchild that inherited the pipes could keep them open: don't wait for them forever.
        await Task.WhenAny(pumps, Task.Delay(TimeSpan.FromSeconds(2)));
        var code = _proc.ExitCode;
        lock (_gate) _exited = true;
        Dispose(); // closing the containment also ends whatever the script left behind
        _proc.Dispose();
        return code;
    }

    /// <summary>Ends the script and everything it started.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_closed || _exited) return;
            StopRequested = true;
            _ = _containment.KillAsync(_proc);
            try { _proc.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            _containment.Dispose();
        }
    }
}
