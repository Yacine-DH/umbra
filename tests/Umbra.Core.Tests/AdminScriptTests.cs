namespace Umbra.Tests;

/// <summary>User scripts marked "Run as administrator": the field, the hash, and the gate (a click, Settings-only approval, the UAC runner).</summary>
public class AdminScriptTests
{
    public AdminScriptTests() => TestEnv.Init();

    static string NewDir() => Path.Combine(TestEnv.DataDir, "adminscripts-" + Guid.NewGuid().ToString("N"));

    static WidgetManifest Runner() => WidgetManifest.Parse("""
        { "id": "runner", "name": "Runner", "permissions": { "userScripts": true, "commands": [] } }
        """);

    sealed record Launch(string Exe, IReadOnlyList<string> Args, string Dir, bool CreateDir, TimeSpan Timeout);

    /// <summary>A service whose elevated runs go to a fake instead of the UAC prompt; <c>prompts</c> counts the widget's approval prompts.</summary>
    static (CommandService Service, CommandApprovals Approvals, List<Launch> Launches, Func<int> Prompts) Service(RunResult? result = null)
    {
        var dir = NewDir();
        var approvals = new CommandApprovals(Path.Combine(dir, "approvals.json"));
        var scripts = new UserScripts(Path.Combine(dir, "scripts.json"), Path.Combine(dir, "scripts"));
        var launches = new List<Launch>();
        var prompts = 0;
        var svc = new CommandService(approvals, _ => { prompts++; return Task.FromResult(true); }, scripts: scripts)
        {
            WorkingDir = id => Path.Combine(dir, "run", id),
            RunElevated = (exe, args, wd, create, timeout, _, _) =>
            {
                launches.Add(new(exe, args, wd, create, timeout));
                return Task.FromResult(result ?? new RunResult(0, "", "", false, false, 5));
            },
        };
        return (svc, approvals, launches, () => prompts);
    }

    static UserScript Admin() => new() { Title = "Restart spooler", Kind = "powershell", Mode = "code", Code = "Restart-Service Spooler", RunAsAdmin = true, TimeoutSeconds = 30 };

    [Fact]
    public void A_script_that_runs_as_administrator_can_not_keep_running()
    {
        var s = Admin();
        s.KeepsRunning = true;
        var ex = Assert.Throws<InvalidOperationException>(s.Normalize);
        Assert.Contains("administrator", ex.Message);
    }

    [Fact]
    public void Running_as_administrator_is_part_of_the_hash_and_reaches_the_command()
    {
        var dir = NewDir();
        UserScript Make(bool admin) => new() { Id = "t", Title = "T", Kind = "program", Path = "C:\\Tools\\t.exe", RunAsAdmin = admin };
        var plain = Make(false).ToSpec(dir, "runner");
        var admin = Make(true).ToSpec(dir, "runner");
        Assert.False(plain.RunAsAdmin);
        Assert.True(admin.RunAsAdmin);
        Assert.NotEqual(plain.Hash(), admin.Hash());
        // Without the switch the hash is that of the same command declared in widget.json, so older approvals still hold.
        Assert.Equal(new CommandSpec { Id = "t", Run = ["C:\\Tools\\t.exe"] }.Hash(), plain.Hash());
    }

    [Fact]
    public void A_declared_command_can_not_ask_to_run_as_administrator()
    {
        Assert.ThrowsAny<Exception>(() => WidgetManifest.Parse("""
            { "id": "w", "name": "W", "permissions": { "commands": [{ "id": "x", "run": ["a"], "runAsAdmin": true }] } }
            """));
    }

    [WindowsFact]
    public async Task It_only_runs_after_a_click_and_through_the_elevated_runner()
    {
        var (svc, _, launches, _) = Service();
        var m = Runner();
        var (s, approved) = svc.SaveUserScript(m, Admin());
        Assert.True(approved);

        // A timer or the widget starting has no click: refused before anything is launched.
        var ex = await Assert.ThrowsAsync<WidgetCommandException>(() => svc.RunAsync(m, enabled: true, userGesture: false, s.Id, null, CancellationToken.None));
        Assert.Contains("click", ex.Message);
        Assert.Empty(launches);

        var r = await svc.RunAsync(m, enabled: true, userGesture: true, s.Id, null, CancellationToken.None);
        var launch = Assert.Single(launches);
        Assert.EndsWith("powershell.exe", launch.Exe, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-File", launch.Args);
        Assert.Equal(TimeSpan.FromSeconds(30), launch.Timeout);
        Assert.True(launch.CreateDir); // no Start in: the widget's data folder
        Assert.True(r["elevated"]!.GetValue<bool>());
        Assert.Equal(0, r["exitCode"]!.GetValue<int>());
        Assert.Equal("", r["stdout"]!.GetValue<string>());

        // The read-only listing says so.
        var row = svc.Describe([m]).Single(x => x!["command"]!.GetValue<string>() == s.Id)!;
        Assert.True(row["runAsAdmin"]!.GetValue<bool>());
    }

    [WindowsFact]
    public async Task It_is_never_approved_from_the_widget_prompt()
    {
        var (svc, approvals, launches, prompts) = Service();
        var m = Runner();
        var (s, _) = svc.SaveUserScript(m, Admin());
        approvals.Revoke("runner", s.Id);

        var ex = await Assert.ThrowsAsync<WidgetCommandException>(() => svc.RunAsync(m, enabled: true, userGesture: true, s.Id, null, CancellationToken.None));
        Assert.Contains("Settings", ex.Message);
        Assert.Equal(0, prompts());
        Assert.Empty(launches);
    }

    [WindowsFact]
    public async Task A_run_Windows_would_not_let_Umbra_end_is_reported()
    {
        var (svc, _, _, _) = Service(new RunResult(-1, "", "", TimedOut: true, Truncated: false, DurationMs: 30000, StillRunning: true));
        var m = Runner();
        var (s, _) = svc.SaveUserScript(m, Admin());
        var r = await svc.RunAsync(m, enabled: true, userGesture: true, s.Id, null, CancellationToken.None);
        Assert.True(r["timedOut"]!.GetValue<bool>());
        Assert.True(r["stillRunning"]!.GetValue<bool>());
    }

    [WindowsFact]
    public async Task A_script_without_the_switch_never_uses_the_elevated_runner()
    {
        var (svc, _, launches, _) = Service();
        var m = Runner();
        var plain = Admin();
        plain.RunAsAdmin = false;
        plain.Code = "Write-Output hi";
        var (s, _) = svc.SaveUserScript(m, plain);
        var r = await svc.RunAsync(m, enabled: true, userGesture: true, s.Id, null, CancellationToken.None);
        Assert.Empty(launches);
        Assert.Null(r["elevated"]);
        Assert.Equal("hi", r["stdout"]!.GetValue<string>().Trim());
    }
}
