namespace Umbra.Tests;

/// <summary>The user's own scripts (permissions.userScripts): validation, the command they become, the store and the gate.</summary>
public class UserScriptTests
{
    public UserScriptTests() => TestEnv.Init();

    static string NewDir() => Path.Combine(TestEnv.DataDir, "userscripts-" + Guid.NewGuid().ToString("N"));

    static UserScripts Store(string dir) => new(Path.Combine(dir, "scripts.json"), Path.Combine(dir, "scripts"));

    static WidgetManifest Runner(bool userScripts = true) => WidgetManifest.Parse($$"""
        { "id": "runner", "name": "Runner", "permissions": { "userScripts": {{(userScripts ? "true" : "false")}},
          "commands": [{ "id": "disk-free", "run": ["a"] }] } }
        """);

    static (CommandService Service, CommandApprovals Approvals, string Dir) Service()
    {
        var dir = NewDir();
        var approvals = new CommandApprovals(Path.Combine(dir, "approvals.json"));
        return (new CommandService(approvals, _ => Task.FromResult(false), scripts: Store(dir)) { WorkingDir = id => Path.Combine(dir, "run", id) }, approvals, dir);
    }

    [Theory]
    [InlineData("", new string[0])]
    [InlineData("a b  c", new[] { "a", "b", "c" })]
    [InlineData("--path \"D:\\My Photos\" -v", new[] { "--path", "D:\\My Photos", "-v" })]
    [InlineData("say \\\"hi\\\"", new[] { "say", "\"hi\"" })]
    [InlineData("\"\"", new[] { "" })]
    public void Arguments_split_like_a_command_line(string text, string[] expected) =>
        Assert.Equal(expected, UserScript.SplitArgs(text));

    [Fact]
    public void An_unclosed_quote_is_refused() =>
        Assert.Throws<InvalidOperationException>(() => UserScript.SplitArgs("a \"b"));

    [Theory]
    [InlineData("", "powershell", "file", "C:\\s.ps1", "", 60)]                  // no name
    [InlineData("X", "cmd", "file", "C:\\s.ps1", "", 60)]                        // unknown type
    [InlineData("X", "powershell", "file", "s.ps1", "", 60)]                     // relative path
    [InlineData("X", "python", "file", "\\\\server\\s.py", "", 60)]              // UNC
    [InlineData("X", "powershell", "file", "", "", 60)]                          // no file
    [InlineData("X", "powershell", "code", "", "  ", 60)]                        // no code
    [InlineData("X", "bash", "code", "", "echo a\necho b", 60)]                  // bash code is one line
    [InlineData("X", "powershell", "file", "C:\\s.ps1", "", 0)]
    [InlineData("X", "powershell", "file", "C:\\s.ps1", "", 121)]
    [InlineData("X", "program", "file", "tools\\app.exe", "", 60)]               // relative program
    public void Bad_scripts_are_refused(string title, string kind, string mode, string path, string code, int timeout)
    {
        var s = new UserScript { Id = "x", Title = title, Kind = kind, Mode = mode, Path = path, Code = code, TimeoutSeconds = timeout };
        Assert.Throws<InvalidOperationException>(() => { s.Normalize(); s.ToSpec("C:\\scripts", "runner"); });
    }

    [Fact]
    public void Arguments_with_placeholders_are_refused()
    {
        var s = new UserScript { Id = "x", Title = "X", Kind = "program", Path = "pwsh", Args = "${p}" };
        s.Normalize();
        Assert.Throws<InvalidOperationException>(() => s.ToSpec("C:\\scripts", "runner"));
    }

    [Fact]
    public void Each_type_becomes_a_fixed_argv_without_a_shell()
    {
        UserScript S(string kind, string mode, string path = "", string code = "", string args = "")
        {
            var s = new UserScript { Id = "job", Title = "Job", Kind = kind, Mode = mode, Path = path, Code = code, Args = args };
            s.Normalize();
            return s;
        }
        Assert.Equal(["powershell", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", "C:\\s\\b.ps1", "-Full", "a b"],
            S("powershell", "file", "C:\\s\\b.ps1", args: "-Full \"a b\"").ToSpec("D:\\d", "runner").Run);
        Assert.Equal(["python", "C:\\s\\r.py"], S("python", "file", "C:\\s\\r.py").ToSpec("D:\\d", "runner").Run);
        Assert.Equal(["bash", "~/bin/sync.sh"], S("bash", "file", "~/bin/sync.sh").ToSpec("D:\\d", "runner").Run);
        Assert.Equal(["bash", "-c", "date && uptime"], S("bash", "code", code: "date && uptime\n").ToSpec("D:\\d", "runner").Run);
        Assert.Equal(["C:\\Tools\\app.exe", "--x"], S("program", "code", "C:\\Tools\\app.exe", args: "--x").ToSpec("D:\\d", "runner").Run);

        var spec = S("powershell", "code", code: "Get-Date").ToSpec("D:\\d", "runner");
        Assert.True(spec.IsUserScript);
        Assert.Equal("Job", spec.Title);
        Assert.Equal("Job", spec.Description); // the approval prompt shows the name when there is no description
        Assert.Equal("-File", spec.Run[^2]);
        Assert.EndsWith(".ps1", spec.Run[^1]);
        Assert.StartsWith(Path.Combine("D:\\d", "runner", "job-"), spec.Run[^1]);
        Assert.Empty(spec.Params);
    }

    [Fact]
    public void Other_code_means_another_command_hash()
    {
        UserScript Code(string c) { var s = new UserScript { Id = "job", Title = "Job", Kind = "python", Mode = "code", Code = c }; s.Normalize(); return s; }
        var a = Code("print(1)").ToSpec("D:\\d", "w");
        var b = Code("print(2)").ToSpec("D:\\d", "w");
        Assert.NotEqual(a.Hash(), b.Hash());
        Assert.Equal(a.Hash(), Code("print(1)").ToSpec("D:\\d", "w").Hash());
    }

    [Fact]
    public void Ids_come_from_the_name_and_never_take_a_declared_or_used_id()
    {
        var store = Store(NewDir());
        var reserved = new[] { "disk-free" };
        UserScript New(string title) => new() { Title = title, Kind = "program", Path = "C:\\Tools\\app.exe" };
        Assert.Equal("back-up-photos", store.Save("w", New("Back up: photos!"), reserved).Script.Id);
        Assert.Equal("back-up-photos-2", store.Save("w", New("Back up photos"), reserved).Script.Id);
        Assert.Equal("disk-free-2", store.Save("w", New("Disk free"), reserved).Script.Id);
        Assert.Equal("script", store.Save("w", New("!!!"), reserved).Script.Id);
        Assert.Equal(4, store.For("w").Count);
        Assert.Empty(store.For("other"));
    }

    [Fact]
    public void Editing_keeps_the_id_and_a_missing_script_is_refused()
    {
        var dir = NewDir();
        var store = Store(dir);
        var (s, _) = store.Save("w", new UserScript { Title = "Report", Kind = "python", Path = "C:\\s\\r.py" }, []);
        s.Title = "Weekly report";
        s.Path = "C:\\s\\weekly.py";
        Assert.Equal("report", store.Save("w", s, []).Script.Id);
        var reloaded = Store(dir).For("w").Single(); // persisted
        Assert.Equal(("report", "Weekly report", "C:\\s\\weekly.py"), (reloaded.Id, reloaded.Title, reloaded.Path));
        Assert.Throws<InvalidOperationException>(() => store.Save("w", new UserScript { Id = "gone", Title = "G", Kind = "python", Path = "C:\\g.py" }, []));
    }

    [Fact]
    public void Code_is_written_to_a_file_rewritten_before_runs_and_removed_with_the_script()
    {
        var dir = NewDir();
        var store = Store(dir);
        var (s, spec) = store.Save("w", new UserScript { Title = "Hello", Kind = "powershell", Mode = "code", Code = "\"héllo\"\nGet-Date" }, []);
        var file = spec.Run[^1];
        Assert.True(File.Exists(file));
        var bytes = File.ReadAllBytes(file);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]); // Windows PowerShell needs the BOM for non-ASCII text
        Assert.Contains("\r\nGet-Date", File.ReadAllText(file));

        File.WriteAllText(file, "Remove-Item C:\\ -Recurse"); // edited behind Umbra's back
        store.Materialize("w", s.Id);
        Assert.Contains("Get-Date", File.ReadAllText(file));

        s.Code = "Get-Date -Format o"; // new code: new file, the old one is cleaned up
        var file2 = store.Save("w", s, []).Spec.Run[^1];
        Assert.NotEqual(file, file2);
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(file2));

        Assert.True(store.Delete("w", s.Id));
        Assert.False(File.Exists(file2));
        Assert.False(store.Delete("w", s.Id));
        Assert.Empty(Store(dir).For("w"));
    }

    [Fact]
    public void A_widget_without_userScripts_gets_none()
    {
        var (svc, _, _) = Service();
        var plain = Runner(userScripts: false);
        Assert.False(svc.TakesUserScripts(plain));
        Assert.Throws<WidgetCommandException>(() => svc.SaveUserScript(plain, new UserScript { Title = "X", Kind = "program", Path = "C:\\x.exe" }));
        Assert.Equal(["disk-free"], svc.All(plain).Select(c => c.Id));
        // A service without a store (older wiring) never offers them either.
        Assert.False(new CommandService(new CommandApprovals(Path.Combine(NewDir(), "a.json")), _ => Task.FromResult(false)).TakesUserScripts(Runner()));
    }

    [Fact]
    public void Saved_scripts_join_the_declared_commands_and_the_read_only_listing()
    {
        var (svc, approvals, _) = Service();
        var m = Runner();
        var (s, approved) = svc.SaveUserScript(m, new UserScript { Title = "Missing tool", Kind = "program", Path = "C:\\does-not-exist\\tool.exe" });
        Assert.False(approved); // a program that isn't there can't be approved yet
        Assert.Equal(["disk-free", "missing-tool"], svc.All(m).Select(c => c.Id));
        var row = svc.Describe([m]).Single(r => r!["command"]!.GetValue<string>() == "missing-tool")!;
        Assert.True(row["userScript"]!.GetValue<bool>());
        Assert.Equal("pending", row["status"]!.GetValue<string>());
        Assert.Equal("pending", approvals.Status("runner", svc.All(m)[1]));

        svc.DeleteUserScript(m, s.Id);
        Assert.Equal(["disk-free"], svc.All(m).Select(c => c.Id));
        Assert.Throws<WidgetCommandException>(() => svc.DeleteUserScript(m, s.Id));
    }

    [Fact]
    public async Task An_unapproved_script_does_not_run_without_a_click()
    {
        var (svc, _, _) = Service();
        var m = Runner();
        var (s, _) = svc.SaveUserScript(m, new UserScript { Title = "Missing", Kind = "program", Path = "C:\\does-not-exist\\tool.exe" });
        await Assert.ThrowsAsync<WidgetCommandException>(() => svc.RunAsync(m, enabled: true, userGesture: false, s.Id, null, CancellationToken.None));
        await Assert.ThrowsAsync<WidgetCommandException>(() => svc.RunAsync(m, enabled: true, userGesture: false, "nope", null, CancellationToken.None));
    }

    [WindowsFact]
    public async Task Saving_approves_the_script_and_it_runs_what_settings_showed()
    {
        var (svc, approvals, _) = Service();
        var m = Runner();
        var (s, approved) = svc.SaveUserScript(m, new UserScript { Title = "Hello", Kind = "powershell", Mode = "code", Code = "Write-Output \"hi $($args[0])\"", Args = "there", TimeoutSeconds = 30 });
        Assert.True(approved);
        var r = await svc.RunAsync(m, enabled: true, userGesture: false, s.Id, null, CancellationToken.None);
        Assert.Equal(0, r["exitCode"]!.GetValue<int>());
        Assert.Equal("hi there", r["stdout"]!.GetValue<string>().Trim());

        // Changing the code changes the command, and saving approves the new one (the old approval doesn't carry over silently).
        s.Code = "Write-Output changed";
        Assert.True(svc.SaveUserScript(m, s).Approved);
        Assert.Equal("approved", approvals.Status("runner", svc.All(m).Single(c => c.Id == s.Id)));
        r = await svc.RunAsync(m, enabled: true, userGesture: false, s.Id, null, CancellationToken.None);
        Assert.Equal("changed", r["stdout"]!.GetValue<string>().Trim());

        // Deleting takes the approval away too.
        svc.DeleteUserScript(m, s.Id);
        Assert.DoesNotContain(approvals.List(), a => a.CommandId == s.Id);
    }

    [Fact]
    public void Automation_has_no_way_to_add_or_approve_a_script()
    {
        Assert.DoesNotContain(Protocol.Commands, c => c.Cmd.Contains("script", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_knows_the_userScripts_permission()
    {
        var dir = NewDir();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "widget.json"), """{ "id": "lintme", "name": "L", "version": "1.0.0", "permissions": { "hosts": [], "userScripts": true } }""");
        File.WriteAllText(Path.Combine(dir, "index.html"), "<html></html>");
        Assert.DoesNotContain(WidgetLint.Run(dir).Errors, e => e.Contains("userScripts"));
    }

    [Fact]
    public void An_update_that_adds_userScripts_asks_first()
    {
        var diff = PermissionDiff.Compute(Runner(userScripts: false), Runner());
        Assert.False(diff.IsEmpty);
        Assert.Contains(diff.Describe(), l => l.Contains("scripts you add yourself"));
        Assert.True(PermissionDiff.Compute(Runner(), Runner()).IsEmpty);
    }
}
