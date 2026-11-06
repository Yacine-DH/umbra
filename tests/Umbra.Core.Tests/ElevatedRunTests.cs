namespace Umbra.Tests;

/// <summary>
/// The helper behind "Run as administrator" (#332), run here without UAC (<c>elevate: false</c>): the same encoded
/// PowerShell, pipe and line protocol, so output, exit codes, arguments, folder, environment and Stop are tested for real.
/// The scripts it runs are files in <c>elevated\</c>, copied next to the tests.
/// </summary>
public class ElevatedRunTests
{
    public ElevatedRunTests() => TestEnv.Init();

    static string NewDir() => Path.Combine(TestEnv.DataDir, "elevated-" + Guid.NewGuid().ToString("N"));

    /// <summary>Runs one of the test scripts through the helper, as a user script would run.</summary>
    static Task<RunResult> Run(string script, IEnumerable<string>? args = null, string? dir = null, TimeSpan? timeout = null,
        CancellationToken ct = default, OutputTail? tail = null) =>
        ElevatedRun.RunAsync(ElevatedRun.PowerShellPath,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(AppContext.BaseDirectory, "elevated", script), .. args ?? []],
            dir ?? NewDir(), timeout ?? TimeSpan.FromSeconds(60), ct, createWorkingDir: true, tail, elevate: false);

    [WindowsFact]
    public async Task Output_errors_and_the_exit_code_come_back()
    {
        var tail = new OutputTail();
        var r = await Run("output.ps1", tail: tail);
        Assert.Equal(3, r.ExitCode);
        Assert.Equal("line one\nline two\n", r.Stdout);
        Assert.Equal("oops\n", r.Stderr);
        Assert.False(r.TimedOut);
        Assert.False(r.StillRunning);
        Assert.Contains("line two", tail.Text()); // live output while it runs
    }

    [WindowsFact]
    public async Task Arguments_arrive_exactly_as_given()
    {
        string[] args = ["plain", "with space", "quote\"inside", "trailing\\", "C:\\Some Folder\\", ""];
        var r = await Run("args.ps1", args);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(args.Select(a => $"[{a}]"), r.Stdout.TrimEnd('\n').Split('\n'));
    }

    [WindowsFact]
    public async Task It_runs_in_its_folder_with_the_scrubbed_environment()
    {
        Environment.SetEnvironmentVariable("UMBRA_TEST_VALUE", "outside");
        try
        {
            var dir = NewDir();
            var lines = (await Run("where.ps1", dir: dir)).Stdout.TrimEnd('\n').Split('\n');
            Assert.Equal(Path.GetFullPath(dir).TrimEnd('\\'), lines[0].TrimEnd('\\'), ignoreCase: true);
            Assert.Equal("value=", lines[1]); // Umbra's own variables never reach the script
            Assert.Equal("color=1", lines[2]);
        }
        finally { Environment.SetEnvironmentVariable("UMBRA_TEST_VALUE", null); }
    }

    [WindowsFact]
    public async Task Stop_ends_the_program_and_what_it_started()
    {
        var marker = Path.Combine(TestEnv.DataDir, "elevated-stop-" + Guid.NewGuid().ToString("N"));
        var tail = new OutputTail();
        using var cts = new CancellationTokenSource();
        var run = Run("stop.ps1", [marker], ct: cts.Token, tail: tail);
        // Stop once the script says it started its grandchild (a fixed delay raced PowerShell's startup on a busy machine).
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!run.IsCompleted && !tail.Text().Contains("started") && DateTime.UtcNow < deadline) await Task.Delay(50);
        cts.Cancel();
        var r = await run;
        Assert.False(r.TimedOut); // stopped, not timed out
        Assert.False(r.StillRunning);
        Assert.Contains("started", r.Stdout);
        await Task.Delay(7000); // past the grandchild's 5 s
        Assert.False(File.Exists(marker));
    }

    [WindowsFact]
    public async Task A_timeout_is_reported_and_the_program_ended()
    {
        var r = await Run("slow.ps1", timeout: TimeSpan.FromSeconds(3));
        Assert.True(r.TimedOut);
        Assert.False(r.StillRunning);
        Assert.Equal(-1, r.ExitCode);
    }

    [WindowsFact]
    public async Task A_program_that_does_not_exist_says_why()
    {
        var r = await ElevatedRun.RunAsync("C:\\does-not-exist\\tool.exe", [], NewDir(), TimeSpan.FromSeconds(60), elevate: false);
        Assert.NotEqual(0, r.ExitCode);
        Assert.NotEmpty(r.Stderr);
    }

    [Fact]
    public void A_command_too_long_for_Windows_is_refused()
    {
        // Random text, so compressing the job can't shrink it under the limit.
        var huge = Enumerable.Range(0, 30).Select(_ => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(500))).ToList();
        Assert.Throws<WidgetCommandException>(() => ElevatedRun.EncodedHelper("p", "t", "C:\\x.exe", huge, "C:\\", new Dictionary<string, string>()));
    }

    [Fact]
    public void A_long_PATH_still_fits_on_the_command_line()
    {
        // 26,192 characters on a real machine with a 3,246-character PATH before the job was compressed; this PATH is 10,000+.
        var path = string.Join(';', Enumerable.Range(0, 200).Select(i => $"C:\\Program Files\\Vendor {i}\\Tool {i % 7}\\bin"));
        var env = new Dictionary<string, string> { ["PATH"] = path, ["PSModulePath"] = path, ["SystemRoot"] = "C:\\Windows" };
        var encoded = ElevatedRun.EncodedHelper("umbra-admin-" + Guid.NewGuid().ToString("N"), new string('A', 32),
            "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe", ["-NoProfile", "-File", "C:\\Users\\someone\\AppData\\Roaming\\Umbra\\scripts\\x\\y.ps1"],
            "C:\\Users\\someone\\AppData\\Roaming\\Umbra\\data\\script-runner", env);
        Assert.True(encoded.Length < 16000, $"{encoded.Length} characters");
    }

    [WindowsFact]
    public async Task A_long_environment_reaches_the_program_intact()
    {
        // Through the compressed job and back out in the helper: the program sees the exact PATH Umbra gave it.
        var r = await Run("path.ps1");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(CommandRunner.ScrubbedEnvironment()["PATH"], r.Stdout.TrimEnd('\n'));
    }
}
