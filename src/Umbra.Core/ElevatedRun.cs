using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Umbra;

/// <summary>
/// Runs a user script marked "Run as administrator" (#332) and brings its output back. A medium-integrity Umbra can't
/// redirect the pipes of an elevated child, so Windows PowerShell (from System32, never PATH) is started elevated through
/// ShellExecute "runas" (Windows' UAC prompt, every run) with a fixed host-written helper as -EncodedCommand. The helper
/// connects to a one-instance named pipe Umbra created, starts the approved program with the same argv, folder and scrubbed
/// environment as a normal run (no shell, no window), and sends its output back line by line. What runs comes only from
/// the helper's own command line; the pipe carries output one way, and Umbra closing it (Stop, timeout, Umbra exiting) makes
/// the helper end the program and everything it started.
/// </summary>
static class ElevatedRun
{
    /// <summary>Win32 ERROR_CANCELLED: the user said No to the UAC prompt.</summary>
    const int ErrorCancelled = 1223;
    /// <summary>Windows' command-line limit is 32767 characters; keep room for the rest of the line.</summary>
    const int MaxEncodedLength = 30000;

    /// <summary>Windows PowerShell 5.1 from System32: always there, and never looked up on PATH for an elevated run.</summary>
    public static string PowerShellPath => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>
    /// The helper. Line protocol on the pipe (UTF-8): the helper sends the token, then "O&lt;stdout line&gt;", "E&lt;stderr line&gt;"
    /// and finally "X&lt;exit code&gt;". Umbra never writes; when its end closes, the helper ends the program's process tree.
    /// Written for Windows PowerShell 5.1 (.NET Framework: no ArgumentList, so arguments are quoted by the C runtime rules).
    /// </summary>
    const string Helper = """
        $ErrorActionPreference = 'Stop'
        $job = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__JOB__')) | ConvertFrom-Json
        $enc = New-Object Text.UTF8Encoding $false
        $pipe = New-Object IO.Pipes.NamedPipeClientStream '.', $job.pipe, ([IO.Pipes.PipeDirection]::InOut), ([IO.Pipes.PipeOptions]::Asynchronous)
        $pipe.Connect(10000)
        $w = New-Object IO.StreamWriter $pipe, $enc
        $w.AutoFlush = $true
        $r = New-Object IO.StreamReader $pipe, $enc
        $w.WriteLine($job.token)
        function Quote([string]$a) {
            if ($a.Length -gt 0 -and $a -notmatch '[\s"]') { return $a }
            $b = New-Object Text.StringBuilder
            [void]$b.Append([char]34)
            $n = 0
            foreach ($ch in $a.ToCharArray()) {
                if ($ch -eq [char]92) { $n++; continue }
                if ($ch -eq [char]34) { [void]$b.Append([char]92, 2 * $n + 1) }
                elseif ($n -gt 0) { [void]$b.Append([char]92, $n) }
                $n = 0
                [void]$b.Append($ch)
            }
            [void]$b.Append([char]92, 2 * $n)
            [void]$b.Append([char]34)
            $b.ToString()
        }
        $psi = New-Object Diagnostics.ProcessStartInfo $job.exe
        $psi.Arguments = (@($job.args) | ForEach-Object { Quote $_ }) -join ' '
        $psi.WorkingDirectory = $job.cwd
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.RedirectStandardInput = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.StandardOutputEncoding = $enc
        $psi.StandardErrorEncoding = $enc
        $psi.EnvironmentVariables.Clear()
        foreach ($v in $job.env.PSObject.Properties) { $psi.EnvironmentVariables[$v.Name] = [string]$v.Value }
        $code = 1
        $p = $null
        try {
            try { $p = [Diagnostics.Process]::Start($psi) }
            catch { $w.WriteLine('E' + $_.Exception.Message); exit 1 }
            $p.StandardInput.Close()
            $stop = $r.ReadLineAsync()
            $o = $p.StandardOutput.ReadLineAsync()
            $e = $p.StandardError.ReadLineAsync()
            while ($o -or $e) {
                $wait = New-Object 'Collections.Generic.List[Threading.Tasks.Task]'
                $wait.Add($stop)
                if ($o) { $wait.Add($o) }
                if ($e) { $wait.Add($e) }
                [void][Threading.Tasks.Task]::WaitAny($wait.ToArray())
                if ($stop.IsCompleted) { break }
                if ($o -and $o.IsCompleted) {
                    if ($null -eq $o.Result) { $o = $null } else { $w.WriteLine('O' + $o.Result); $o = $p.StandardOutput.ReadLineAsync() }
                }
                if ($e -and $e.IsCompleted) {
                    if ($null -eq $e.Result) { $e = $null } else { $w.WriteLine('E' + $e.Result); $e = $p.StandardError.ReadLineAsync() }
                }
            }
            if (-not $stop.IsCompleted) {
                $p.WaitForExit()
                $code = $p.ExitCode
                $w.WriteLine('X' + $code)
            }
        }
        finally {
            if ($p -and -not $p.HasExited) {
                try { & (Join-Path ([Environment]::SystemDirectory) 'taskkill.exe') /T /F /PID $p.Id 2>&1 | Out-Null } catch { }
            }
        }
        exit $code
        """;

    /// <summary>The helper with this run's job (pipe, token, program, argv, folder, environment) baked in, as -EncodedCommand text.</summary>
    internal static string EncodedHelper(string pipe, string token, string exePath, IReadOnlyList<string> args, string workingDir, IReadOnlyDictionary<string, string> env)
    {
        var job = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["pipe"] = pipe, ["token"] = token, ["exe"] = exePath, ["args"] = args, ["cwd"] = workingDir, ["env"] = env,
        });
        var script = Helper.Replace("__JOB__", Convert.ToBase64String(job));
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        if (encoded.Length > MaxEncodedLength) throw new WidgetCommandException("This command is too long to run as administrator");
        return encoded;
    }

    /// <summary>
    /// Runs <paramref name="exePath"/> with <paramref name="args"/> elevated and returns its exit code and output (capped at
    /// <paramref name="maxBytes"/> characters per stream). Output also goes to <paramref name="tail"/> as it arrives. On timeout or
    /// <paramref name="ct"/> the pipe closes and the helper ends the program; if the helper is still there 5 s later, the
    /// result says it may still be running. <paramref name="elevate"/> false runs the same helper without UAC (tests).
    /// </summary>
    public static async Task<RunResult> RunAsync(string exePath, IReadOnlyList<string> args, string workingDir, TimeSpan timeout,
        CancellationToken ct = default, bool createWorkingDir = true, OutputTail? tail = null, int maxBytes = CommandRunner.MaxOutputBytes, bool elevate = true)
    {
        if (!OperatingSystem.IsWindows()) throw new WidgetCommandException("Running as administrator is only supported on Windows");
        if (createWorkingDir) Directory.CreateDirectory(workingDir);
        else if (!Directory.Exists(workingDir)) throw new WidgetCommandException($"The Start in folder {workingDir} doesn't exist");

        var pipeName = $"umbra-admin-{Guid.NewGuid():N}";
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        // One instance only, created before the helper exists: nobody else can serve this name to it.
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var encoded = EncodedHelper(pipeName, token, exePath, args, workingDir, CommandRunner.ScrubbedEnvironment());

        var psi = new ProcessStartInfo(PowerShellPath)
        {
            UseShellExecute = elevate,
            Verb = elevate ? "runas" : "",
            CreateNoWindow = !elevate,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = workingDir,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-EncodedCommand", encoded })
            psi.ArgumentList.Add(a);

        Process? started;
        // ShellExecute waits for the UAC answer: keep that off the caller's (UI) thread.
        try { started = await Task.Run(() => Process.Start(psi)); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            throw new WidgetCommandException("You said No to the Windows administrator prompt, so it didn't run");
        }
        using var proc = started ?? throw new WidgetCommandException("Windows started the program but gave Umbra no handle to it");
        var sw = Stopwatch.StartNew();

        var output = new Output(maxBytes, tail);
        using var pumpStop = new CancellationTokenSource();
        var pump = output.PumpAsync(server, token, pumpStop.Token);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var timedOut = false;
        var stillRunning = false;
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            server.Dispose(); // the helper sees its pipe close and ends the program's whole tree
            try { await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
            stillRunning = !proc.HasExited;
        }
        // The last lines may still be in the pipe when the helper exits.
        await Task.WhenAny(pump, Task.Delay(TimeSpan.FromSeconds(2)));
        pumpStop.Cancel();
        sw.Stop();

        var exit = timedOut || !proc.HasExited ? -1 : output.ExitCode ?? proc.ExitCode;
        var stderr = output.Err;
        if (!output.Connected && !timedOut && !ct.IsCancellationRequested)
            stderr = "Umbra couldn't read the output of this run as administrator.";
        return new RunResult(exit, output.Out, stderr, timedOut, output.Truncated, sw.ElapsedMilliseconds, stillRunning);
    }

    /// <summary>The helper's lines, split into stdout and stderr, capped, and fed to the live tail.</summary>
    sealed class Output(int max, OutputTail? tail)
    {
        readonly StringBuilder _out = new(), _err = new();
        readonly object _gate = new();
        public bool Truncated { get; private set; }
        public bool Connected { get; private set; }
        public int? ExitCode { get; private set; }
        public string Out { get { lock (_gate) return _out.ToString(); } }
        public string Err { get { lock (_gate) return _err.ToString(); } }

        public async Task PumpAsync(NamedPipeServerStream server, string token, CancellationToken ct)
        {
            try
            {
                await server.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                // Only the helper Umbra started knows the token; any other client gets nothing shown.
                if (await reader.ReadLineAsync(ct) != token) return;
                Connected = true;
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    if (line.Length == 0) continue;
                    var text = line[1..];
                    switch (line[0])
                    {
                        case 'O': Add(_out, text); break;
                        case 'E': Add(_err, text); break;
                        case 'X' when int.TryParse(text, out var code): ExitCode = code; break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
        }

        void Add(StringBuilder sb, string line)
        {
            lock (_gate)
            {
                var room = max - sb.Length;
                if (room > 0) sb.Append(line.Length + 1 <= room ? line + "\n" : (line + "\n")[..room]);
                if (line.Length + 1 > room) Truncated = true;
            }
            tail?.Append(line + "\n");
        }
    }
}
