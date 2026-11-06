namespace Umbra.Tests;

/// <summary>User scripts that keep running (#322): the fields, the hash, start/stop, live output, restarts and auto-start.</summary>
public class KeepRunningScriptTests
{
    public KeepRunningScriptTests() => TestEnv.Init();

    static string NewDir() => Path.Combine(TestEnv.DataDir, "keeprunning-" + Guid.NewGuid().ToString("N"));

    static WidgetManifest Runner() => WidgetManifest.Parse("""
        { "id": "runner", "name": "Runner", "permissions": { "userScripts": true, "commands": [] } }
        """);

    static (CommandService Service, CommandApprovals Approvals, string Dir) Service()
    {
        var dir = NewDir();
        var approvals = new CommandApprovals(Path.Combine(dir, "approvals.json"));
        var scripts = new UserScripts(Path.Combine(dir, "scripts.json"), Path.Combine(dir, "scripts"));
        var svc = new CommandService(approvals, _ => Task.FromResult(false), scripts: scripts)
        {
            WorkingDir = id => Path.Combine(dir, "run", id),
            Delay = (_, ct) => Task.Delay(20, ct), // restarts without the real 5 s .. 5 min waits
        };
        return (svc, approvals, dir);
    }

    static async Task Until(Func<bool> condition, string what, int seconds = 20)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(50);
        }
    }

    const string Loop = "while ($true) { \"tick\"; Start-Sleep -Milliseconds 200 }";

    [Theory]
    [InlineData("Scripts")]          // relative
    [InlineData("C:\\a\\..\\b")]     // ..
    [InlineData("\\\\server\\share")] // UNC
    public void A_start_in_folder_must_be_a_full_local_path(string startIn)
    {
        var s = new UserScript { Title = "X", Kind = "powershell", Mode = "code", Code = "1", StartIn = startIn };
        Assert.Throws<InvalidOperationException>(s.Normalize);
    }

    [Fact]
    public void Start_in_is_trimmed_and_a_drive_root_kept()
    {
        var s = new UserScript { Title = "X", Kind = "powershell", Mode = "code", Code = "1", StartIn = " \"C:\\Scripts\\\" " };
        s.Normalize();
        Assert.Equal("C:\\Scripts", s.StartIn);
        s.StartIn = "C:\\";
        s.Normalize();
        Assert.Equal("C:\\", s.StartIn);
    }

    [Fact]
    public void A_typed_shortcut_is_refused()
    {
        var s = new UserScript { Title = "X", Kind = "program", Path = "C:\\Users\\me\\Desktop\\tunnel.lnk" };
        var ex = Assert.Throws<InvalidOperationException>(s.Normalize);
        Assert.Contains("Browse", ex.Message);
    }

    [Fact]
    public void Auto_start_and_restart_only_apply_to_a_script_that_keeps_running()
    {
        var s = new UserScript { Title = "X", Kind = "powershell", Mode = "code", Code = "1", AutoStart = true, Restart = true };
        s.Normalize();
        Assert.False(s.AutoStart);
        Assert.False(s.Restart);
        s.KeepsRunning = s.AutoStart = s.Restart = true;
        s.Normalize();
        Assert.True(s.AutoStart && s.Restart);
    }

    [Fact]
    public void Keeping_running_and_the_folder_are_part_of_the_hash_and_nothing_else_changes()
    {
        var dir = NewDir();
        UserScript Make() => new() { Id = "t", Title = "T", Kind = "program", Path = "C:\\Tools\\t.exe" };
        var plain = Make().ToSpec(dir, "runner");
        // Without the new fields the hash is that of the same command declared in widget.json, so older approvals still hold.
        var declared = new CommandSpec { Id = "t", Run = ["C:\\Tools\\t.exe"] };
        Assert.Equal(declared.Hash(), plain.Hash());

        var keeps = Make(); keeps.KeepsRunning = true;
        var folder = Make(); folder.StartIn = "C:\\Tools";
        var auto = Make(); auto.KeepsRunning = true; auto.AutoStart = auto.Restart = true;
        Assert.NotEqual(plain.Hash(), keeps.ToSpec(dir, "runner").Hash());
        Assert.NotEqual(plain.Hash(), folder.ToSpec(dir, "runner").Hash());
        Assert.Equal(keeps.ToSpec(dir, "runner").Hash(), auto.ToSpec(dir, "runner").Hash()); // when it starts isn't what runs
    }

    [Fact]
    public void A_declared_command_can_not_keep_running()
    {
        Assert.ThrowsAny<Exception>(() => WidgetManifest.Parse("""
            { "id": "w", "name": "W", "permissions": { "commands": [{ "id": "srv", "run": ["a"], "keepsRunning": true }] } }
            """));
        Assert.ThrowsAny<Exception>(() => WidgetManifest.Parse("""
            { "id": "w", "name": "W", "permissions": { "commands": [{ "id": "srv", "run": ["a"], "cwd": "C:\\" }] } }
            """));
    }

    [Fact]
    public void Automation_can_not_start_or_stop_a_script()
    {
        Assert.DoesNotContain(Protocol.Commands, c => c.Cmd is "run" or "start" or "stop" or "kill"
            || c.Cmd.Contains("script", StringComparison.OrdinalIgnoreCase) || c.Cmd.Contains("command", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_output_tail_keeps_the_last_characters()
    {
        var tail = new OutputTail(10);
        tail.Append("0123456789");
        tail.Append("abc");
        Assert.Equal("3456789abc", tail.Text());
        tail.Note("x");
        Assert.EndsWith("[Umbra] x\n", tail.Text());
        Assert.Equal(10, tail.Text().Length);
    }

    [Fact]
    public void An_unapproved_script_is_not_started_automatically()
    {
        var (svc, _, _) = Service();
        var m = Runner();
        var (s, approved) = svc.SaveUserScript(m, new UserScript { Title = "Missing", Kind = "program", Path = "C:\\does-not-exist\\tool.exe", KeepsRunning = true, AutoStart = true });
        Assert.False(approved);
        svc.AutoStart(m); // logs, never throws or prompts
        Assert.False(svc.State("runner", s.Id).Running);
    }

    [WindowsFact]
    public async Task A_script_that_keeps_running_starts_streams_and_stops()
    {
        var (svc, _, dir) = Service();
        var m = Runner();
        var changes = 0;
        svc.ServicesChanged += id => { if (id == "runner") Interlocked.Increment(ref changes); };
        Directory.CreateDirectory(Path.Combine(dir, "here"));
        var (s, approved) = svc.SaveUserScript(m, new UserScript { Title = "Ticker", Kind = "powershell", Mode = "code", KeepsRunning = true,
            Code = "(Get-Location).Path; " + Loop, StartIn = Path.Combine(dir, "here") });
        Assert.True(approved);

        var r = await svc.RunAsync(m, enabled: true, userGesture: false, s.Id, null, CancellationToken.None);
        Assert.True(r["started"]!.GetValue<bool>());
        await Until(() => svc.Output("runner", "runner", s.Id)["text"]!.GetValue<string>().Contains("tick"), "live output");
        Assert.Contains(Path.Combine(dir, "here"), svc.Output("runner", "runner", s.Id)["text"]!.GetValue<string>()); // ran in Start in
        Assert.True(svc.State("runner", s.Id).Running);
        Assert.Single(svc.RunningServices());
        await Assert.ThrowsAsync<WidgetCommandException>(() => svc.RunAsync(m, enabled: true, userGesture: false, s.Id, null, CancellationToken.None));

        Assert.True(svc.Stop("runner", "runner", s.Id));
        await Until(() => !svc.State("runner", s.Id).Running, "stopped");
        Assert.Contains("[Umbra] stopped", svc.Output("runner", "runner", s.Id)["text"]!.GetValue<string>());
        Assert.Empty(svc.RunningServices());
        Assert.True(changes >= 2); // started, stopped
        Assert.False(svc.Stop("runner", "runner", s.Id)); // nothing left to stop
    }

    [WindowsFact]
    public async Task Closing_the_widget_editing_or_deleting_stops_it()
    {
        var (svc, _, _) = Service();
        var m = Runner();
        var (s, _) = svc.SaveUserScript(m, new UserScript { Title = "Ticker", Kind = "powershell", Mode = "code", KeepsRunning = true, Code = Loop });

        await svc.RunAsync(m, enabled: true, userGesture: false, s.Id, null, CancellationToken.None);
        svc.StopWidget("runner");
        await Until(() => !svc.State("runner", s.Id).Running, "stopped with the widget");

        await svc.RunAsync(m, enabled: true, userGesture: false, s.Id, null, CancellationToken.None);
        s.Description = "edited";
        svc.SaveUserScript(m, s);
        await Until(() => svc.RunningServices().Count == 0, "stopped on edit");

        await svc.RunAsync(m, enabled: true, userGesture: false, s.Id, null, CancellationToken.None);
        svc.DeleteUserScript(m, s.Id);
        await Until(() => svc.RunningServices().Count == 0, "stopped on delete");

        // A disabled widget starts nothing.
        await Assert.ThrowsAsync<WidgetCommandException>(() => svc.RunAsync(m, enabled: false, userGesture: true, s.Id, null, CancellationToken.None));
    }

    [WindowsFact]
    public async Task A_script_that_exits_is_restarted_then_given_up_on()
    {
        var (svc, _, _) = Service();
        var m = Runner();
        var (s, _) = svc.SaveUserScript(m, new UserScript { Title = "Flaky", Kind = "powershell", Mode = "code", KeepsRunning = true, Restart = true, Code = "\"up\"; exit 3" });

        await svc.RunAsync(m, enabled: true, userGesture: false, s.Id, null, CancellationToken.None);
        await Until(() => svc.Output("runner", "runner", s.Id)["text"]!.GetValue<string>().Contains("gave up"), "giving up", seconds: 60);
        var state = svc.State("runner", s.Id);
        Assert.False(state.Running);
        Assert.False(state.Restarting);
        Assert.Equal(CommandService.RestartDelays.Length, state.Restarts);
        Assert.Equal(3, state.LastExit);
    }

    [WindowsFact]
    public async Task Stop_ends_a_run_in_progress()
    {
        var (svc, _, _) = Service();
        var m = Runner();
        var (s, _) = svc.SaveUserScript(m, new UserScript { Title = "Slow", Kind = "powershell", Mode = "code", Code = "\"begin\"; Start-Sleep -Seconds 60", TimeoutSeconds = 90 });

        var run = svc.RunAsync(m, enabled: true, userGesture: false, s.Id, null, CancellationToken.None);
        await Until(() => svc.Output("runner", "runner", s.Id)["text"]!.GetValue<string>().Contains("begin"), "live output of a run");
        Assert.True(svc.Stop("runner", "runner", s.Id));
        var r = await run.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(r["stopped"]!.GetValue<bool>());
        Assert.False(r["timedOut"]!.GetValue<bool>());
    }

    [WindowsFact]
    public async Task Auto_start_starts_approved_scripts_once()
    {
        var (svc, _, _) = Service();
        var m = Runner();
        var (s, _) = svc.SaveUserScript(m, new UserScript { Title = "Auto", Kind = "powershell", Mode = "code", KeepsRunning = true, AutoStart = true, Code = Loop });
        svc.AutoStart(m);
        svc.AutoStart(m); // a second window of the widget: still one process
        Assert.True(svc.State("runner", s.Id).Running);
        Assert.Single(svc.RunningServices());
        svc.StopWidget("runner");
        await Until(() => !svc.State("runner", s.Id).Running, "stopped");
    }
}
