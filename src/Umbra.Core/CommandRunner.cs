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

    /// <summary>Only these variables reach the child. Tokens, API keys and UMBRA_* are never passed on.</summary>
    static readonly string[] EnvAllowList =
    {
        "SystemRoot", "windir", "SystemDrive", "ComSpec", "PATH", "PATHEXT", "TEMP", "TMP", "USERNAME", "USERDOMAIN",
        "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA", "ProgramData", "ProgramFiles", "ProgramFiles(x86)",
        "ProgramW6432", "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432", "NUMBER_OF_PROCESSORS",
        "PROCESSOR_ARCHITECTURE", "OS", "COMPUTERNAME", "PSModulePath",
    };

    public static Dictionary<string, string> ScrubbedEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in EnvAllowList)
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } v) env[name] = v;
        env["NO_COLOR"] = "1";
        return env;
    }

    /// <summary>
    /// Finds the program file for a declared executable: an absolute path as given, or a bare name searched on PATH
    /// (absolute PATH entries only, never the current folder). .cmd/.bat are only found when <paramref name="allowScripts"/>.
    /// </summary>
    public static string? ResolveExe(string exe, bool allowScripts, string? pathVariable = null)
    {
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

    /// <summary>Capped, drained copy of a child's output stream; also feeds the live <see cref="OutputTail"/> when there is one.</summary>
    sealed class Capture(int max)
    {
        readonly MemoryStream _buf = new();
        readonly object _gate = new();
        public bool Truncated { get; private set; }

        public async Task PumpAsync(Stream s, OutputTail? tail = null)
        {
            var chunk = new byte[8192];
            var decoder = Encoding.UTF8.GetDecoder(); // a character split across two reads stays whole
            var chars = new char[Encoding.UTF8.GetMaxCharCount(chunk.Length)];
            int n;
            try
            {
                while ((n = await s.ReadAsync(chunk)) > 0)
                {
                    lock (_gate)
                    {
                        var room = max - (int)_buf.Length;
                        if (room > 0) _buf.Write(chunk, 0, Math.Min(room, n));
                        if (n > room) Truncated = true; // keep reading so the child never blocks on a full pipe
                    }
                    tail?.Append(new string(chars, 0, decoder.GetChars(chunk, 0, n, chars, 0)));
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        }

        public string Text()
        {
            lock (_gate) return Encoding.UTF8.GetString(_buf.GetBuffer(), 0, (int)_buf.Length);
        }
    }

    static ProcessStartInfo StartInfo(string exePath, IReadOnlyList<string> args, string workingDir, bool createWorkingDir)
    {
        // Containing the process tree is Windows-only so far (job objects); Linux gets process groups in #194.
        if (!OperatingSystem.IsWindows()) throw new WidgetCommandException("Widget commands are not supported on this platform yet");
        if (createWorkingDir) Directory.CreateDirectory(workingDir);
        else if (!Directory.Exists(workingDir)) throw new WidgetCommandException($"The Start in folder {workingDir} doesn't exist");
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = workingDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment.Clear();
        foreach (var (k, v) in ScrubbedEnvironment()) psi.Environment[k] = v;
        return psi;
    }

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
        // The job makes every descendant die with the job: on timeout, when the run ends, or if Umbra itself exits.
        var job = ProcessJob.CreateKillOnCloseJob();
        if (job == IntPtr.Zero) throw new WidgetCommandException("Could not create a job object to contain the program");
        try
        {
            return await RunInJobAsync(psi, job, sw, maxBytes, timeout, ct, tail);
        }
        finally
        {
            ProcessJob.TerminateJobObject(job, 1);
            ProcessJob.CloseHandle(job);
        }
    }

    static async Task<RunResult> RunInJobAsync(ProcessStartInfo psi, IntPtr job, Stopwatch sw, int maxBytes, TimeSpan timeout, CancellationToken ct, OutputTail? tail)
    {
        using var proc = Process.Start(psi) ?? throw new WidgetCommandException("The program could not be started");
        if (!ProcessJob.AddToJob(job, proc.Handle))
        {
            try { proc.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            throw new WidgetCommandException("Could not contain the program in a job object");
        }
        try { proc.StandardInput.Close(); } catch (IOException) { }
        var stdout = new Capture(maxBytes);
        var stderr = new Capture(maxBytes);
        var pumps = Task.WhenAll(stdout.PumpAsync(proc.StandardOutput.BaseStream, tail), stderr.PumpAsync(proc.StandardError.BaseStream, tail));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var timedOut = false;
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            ProcessJob.TerminateJobObject(job, 1);
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
    /// last characters in <paramref name="tail"/>. It sits in a kill-on-close job, so it and everything it started end on
    /// <see cref="RunningProcess.Stop"/>, when the handle is disposed, and when Umbra exits (even on a crash).
    /// </summary>
    public static RunningProcess Start(string exePath, IReadOnlyList<string> args, string workingDir, bool createWorkingDir, OutputTail tail)
    {
        var psi = StartInfo(exePath, args, workingDir, createWorkingDir);
        var job = ProcessJob.CreateKillOnCloseJob();
        if (job == IntPtr.Zero) throw new WidgetCommandException("Could not create a job object to contain the program");
        Process? proc = null;
        try
        {
            proc = Process.Start(psi) ?? throw new WidgetCommandException("The program could not be started");
            if (!ProcessJob.AddToJob(job, proc.Handle)) throw new WidgetCommandException("Could not contain the program in a job object");
            try { proc.StandardInput.Close(); } catch (IOException) { }
            var pumps = Task.WhenAll(new Capture(0).PumpAsync(proc.StandardOutput.BaseStream, tail), new Capture(0).PumpAsync(proc.StandardError.BaseStream, tail));
            return new RunningProcess(proc, job, pumps);
        }
        catch
        {
            ProcessJob.TerminateJobObject(job, 1);
            ProcessJob.CloseHandle(job);
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

/// <summary>A started keeps-running script: its process in its own kill-on-close job.</summary>
sealed class RunningProcess : IDisposable
{
    readonly Process _proc;
    readonly IntPtr _job;
    readonly object _gate = new();
    bool _closed, _exited;

    internal RunningProcess(Process proc, IntPtr job, Task pumps)
    {
        _proc = proc;
        _job = job;
        Exited = WaitAsync(pumps);
    }

    public DateTime StartedAt { get; } = DateTime.UtcNow;
    /// <summary>Set by <see cref="Stop"/>: the exit was asked for, not the script's own doing.</summary>
    public bool StopRequested { get; private set; }
    /// <summary>Completes with the exit code once the process ended (its output read, its job closed).</summary>
    public Task<int> Exited { get; }

    async Task<int> WaitAsync(Task pumps)
    {
        await _proc.WaitForExitAsync();
        // A grandchild that inherited the pipes could keep them open: don't wait for them forever.
        await Task.WhenAny(pumps, Task.Delay(TimeSpan.FromSeconds(2)));
        var code = _proc.ExitCode;
        lock (_gate) _exited = true;
        Dispose(); // closing the job also ends whatever the script left behind
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
            ProcessJob.TerminateJobObject(_job, 1);
            try { _proc.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            ProcessJob.TerminateJobObject(_job, 1);
            ProcessJob.CloseHandle(_job);
        }
    }
}
