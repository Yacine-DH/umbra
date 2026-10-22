using System.Text;
using System.Text.Json.Nodes;

namespace Umbra.Tests;

/// <summary>
/// permissions.commands on Linux, as far as it is pure: executable and argv validation, exact-executable matching, the scrubbed
/// environment, the output cap, the resolver and the argv that really reaches the OS. All of it runs on Windows with injected
/// inputs. The tests at the bottom spawn real processes and run only on Linux (CI's Ubuntu job); they run in the Ubuntu CI job and were run on Linux for #194.
/// </summary>
public class PosixCommandTests
{
    const string W = WidgetPlatforms.Windows, L = WidgetPlatforms.Linux;

    public PosixCommandTests() => TestEnv.Init();

    static CommandSpec Spec(string commandJson) => WidgetManifest.Parse(
        $$"""{ "id": "demo", "name": "Demo", "permissions": { "commands": [{{commandJson}}] } }""").Permissions.Commands[0];

    // ---------- executable and argv validation ----------

    [Theory]
    [InlineData("""{ "id": "x", "run": ["claude", "-p", "Hello"] }""")]
    [InlineData("""{ "id": "x", "run": ["/usr/bin/claude", "-p", "Hello"] }""")]
    [InlineData("""{ "id": "x", "run": ["/opt/my tool/bin/run", "--x"] }""")]
    [InlineData("""{ "id": "x", "run": ["/home/me/.local/bin/claude"] }""")]
    [InlineData("""{ "id": "x", "run": ["C:\\Tools\\app.exe"] }""")]
    [InlineData("""{ "id": "x", "run": ["xdg-open", "https://example.com"] }""")]
    [InlineData("""{ "id": "x", "run": ["python3", "-c", "print(1)"] }""")]
    public void Valid_executables_parse(string command) => Assert.NotEmpty(Spec(command).Run);

    [Theory]
    [InlineData("""{ "id": "x", "run": ["//server/share/app"] }""")]             // UNC on Windows
    [InlineData("""{ "id": "x", "run": ["/usr/bin/"] }""")]                      // a folder
    [InlineData("""{ "id": "x", "run": ["/usr/../bin/sh"] }""")]                 // traversal
    [InlineData("""{ "id": "x", "run": ["/usr/b\\in/x"] }""")]                   // backslash in a POSIX path
    [InlineData("""{ "id": "x", "run": ["/usr/bin/x*"] }""")]                    // wildcard
    [InlineData("""{ "id": "x", "run": ["bin/tool"] }""")]                       // relative path
    [InlineData("""{ "id": "x", "run": ["./tool"] }""")]
    [InlineData("""{ "id": "x", "run": ["~/tool"] }""")]
    [InlineData("""{ "id": "x", "run": ["claude -p hi"] }""")]                   // a shell line
    [InlineData("""{ "id": "x", "run": ["${exe}"], "params": { "exe": {} } }""")] // the program can't be a parameter
    [InlineData("""{ "id": "x", "run": ["/bin/true", "--q=${p}"], "params": { "p": {} } }""")]
    [InlineData("""{ "id": "x", "run": ["a", "bad\u0000arg"] }""")]
    public void Invalid_executables_and_arguments_are_rejected(string command) =>
        Assert.ThrowsAny<Exception>(() => Spec(command));

    [Theory]
    [InlineData("sh", true)]
    [InlineData("/bin/bash", true)]
    [InlineData("dash", true)]
    [InlineData("zsh", true)]
    [InlineData("fish", true)]
    [InlineData("python", true)]
    [InlineData("python3", true)]
    [InlineData("python3.12", true)]
    [InlineData("/usr/bin/perl", true)]
    [InlineData("ruby3.2", true)]
    [InlineData("node", true)]
    [InlineData("env", true)]
    [InlineData("/usr/bin/env", true)]
    [InlineData("xargs", true)]
    [InlineData("sudo", true)]
    [InlineData("flatpak-spawn", true)]
    [InlineData("/home/me/bin/backup.sh", true)]
    [InlineData("tool.py", true)]
    [InlineData("C:\\Tools\\run.ps1", true)]
    [InlineData("BASH.EXE", true)]
    [InlineData("claude", false)]
    [InlineData("/usr/bin/claude", false)]
    [InlineData("xdg-open", false)]
    [InlineData("pythonista", false)]
    [InlineData("nodejs-tool", false)]
    public void Interpreters_and_launchers_are_recognised(string exe, bool expected) =>
        Assert.Equal(expected, CommandSpec.IsInterpreterExe(exe));

    [Fact]
    public void Free_text_into_a_Linux_interpreter_is_refused_at_load_time()
    {
        Assert.ThrowsAny<Exception>(() => Spec("""{ "id": "x", "run": ["bash", "-c", "${cmd}"], "params": { "cmd": {} } }"""));
        Assert.ThrowsAny<Exception>(() => Spec("""{ "id": "x", "run": ["/usr/bin/env", "${prog}"], "params": { "prog": {} } }"""));
        Assert.ThrowsAny<Exception>(() => Spec("""{ "id": "x", "run": ["python3", "${script}"], "params": { "script": {} } }"""));
        var ok = Spec("""{ "id": "x", "run": ["python3", "${script}"], "params": { "script": { "choices": ["a.py", "b.py"] } } }""");
        Assert.Equal(new[] { "python3", "a.py" }, new[] { ok.Exe }.Concat(ok.BuildArgs(new JsonObject { ["script"] = "a.py" })));
    }

    [Fact]
    public void A_parameter_value_stays_one_argument_and_cannot_become_an_option()
    {
        var spec = Spec("""{ "id": "x", "run": ["claude", "-p", "${prompt}"], "params": { "prompt": {} } }""");
        Assert.Equal(new[] { "-p", "hello world; rm -rf / `x` $(y)" }, spec.BuildArgs(new JsonObject { ["prompt"] = "hello world; rm -rf / `x` $(y)" }));
        Assert.Throws<WidgetCommandException>(() => spec.BuildArgs(new JsonObject { ["prompt"] = "--config=/evil" }));
        Assert.Throws<WidgetCommandException>(() => spec.BuildArgs(new JsonObject { ["prompt"] = "/etc/passwd" }));
        Assert.Throws<WidgetCommandException>(() => spec.BuildArgs(new JsonObject { ["prompt"] = "bad\0byte" }));
        Assert.Equal(new[] { "-p", "two\nlines" }, spec.BuildArgs(new JsonObject { ["prompt"] = "two\nlines" })); // still exactly one argument
    }

    [Fact]
    public void The_command_hash_binds_the_executable_form()
    {
        var a = Spec("""{ "id": "x", "run": ["claude", "-p", "Hello"] }""");
        var b = Spec("""{ "id": "x", "run": ["/usr/bin/claude", "-p", "Hello"] }""");
        Assert.NotEqual(a.Hash(), b.Hash()); // a different executable string is a changed command: asked again
    }

    // ---------- resolution ----------

    static IExecutableResolver Posix(IEnumerable<string> files, string? path) =>
        new PosixExecutableResolver(new HashSet<string>(files, StringComparer.Ordinal).Contains, name => name == "PATH" ? path : null);

    [Fact]
    public void A_bare_name_is_found_on_the_first_absolute_path_entry_that_has_it()
    {
        var r = Posix(new[] { "/usr/local/bin/claude", "/usr/bin/claude", "/usr/bin/other" }, "/home/me/bin:/usr/local/bin:/usr/bin");
        Assert.Equal("/usr/local/bin/claude", r.Resolve("claude", false));
        Assert.Equal("/usr/bin/other", r.Resolve("other", false));
        Assert.Null(r.Resolve("missing", false));
    }

    [Fact]
    public void Empty_and_relative_path_entries_never_search_the_current_folder()
    {
        // ":" and "::" mean "the current folder", which for a command is the widget's own writable data folder.
        var r = Posix(new[] { "/planted/claude", "bin/claude", "./claude", "claude" }, ":bin::.:./bin:relative");
        Assert.Null(r.Resolve("claude", false));
        Assert.Null(Posix(new[] { "claude" }, null).Resolve("claude", false));
    }

    [Fact]
    public void An_absolute_path_is_used_as_given_or_not_at_all()
    {
        var r = Posix(new[] { "/opt/tool/run" }, "/usr/bin");
        Assert.Equal("/opt/tool/run", r.Resolve("/opt/tool/run", false));
        Assert.Null(r.Resolve("/opt/tool/missing", false));
        Assert.Null(r.Resolve("/opt/tool/../tool/run", false));
    }

    [Theory]
    [InlineData("C:\\Tools\\app.exe")]      // a Windows path is not a program here
    [InlineData("C:/Tools/app.exe")]
    [InlineData("//server/share/app")]
    [InlineData("sub/dir/tool")]            // only bare names are searched
    [InlineData("tool:x")]
    [InlineData("")]
    public void The_Posix_resolver_rejects_other_forms(string exe) =>
        Assert.Null(Posix(new[] { exe, "/usr/bin/" + exe }, "/usr/bin").Resolve(exe, false));

    [Fact]
    public void No_extension_is_added_on_Linux()
    {
        var r = Posix(new[] { "/usr/bin/tool.exe" }, "/usr/bin");
        Assert.Null(r.Resolve("tool", true));
    }

    [Theory]
    [InlineData("/usr/bin/true")]
    [InlineData("/bin/sh")]
    [InlineData("//server/share/app.exe")]
    [InlineData("\\\\server\\share\\app.exe")]
    public void Windows_resolution_never_reads_a_posix_style_path_relative_to_the_current_drive(string exe) =>
        Assert.Null(CommandRunner.ResolveExe(exe, allowScripts: true, pathVariable: "C:\\Windows\\System32"));

    [Fact]
    public void ExeIdentity_finds_nothing_through_a_resolver_that_finds_nothing()
    {
        var spec = Spec("""{ "id": "x", "run": ["claude"] }""");
        Assert.Null(ExeIdentity.Of(spec, Posix(Array.Empty<string>(), "/usr/bin")));
    }

    // ---------- exact-executable matching ----------

    static readonly string Hash = new('a', 64), Other = new('b', 64);
    static CommandApproval Approval(string? path, string? hash) => new("w", "c", "h", DateTime.UtcNow, path, hash);

    [Fact]
    public void On_Linux_a_different_case_is_a_different_program()
    {
        var a = Approval("/opt/Tool/run", Hash);
        Assert.True(ExeIdentity.Matches(a, new ExeIdentity("/opt/Tool/run", Hash), L));
        Assert.False(ExeIdentity.Matches(a, new ExeIdentity("/opt/tool/run", Hash), L));
    }

    [Fact]
    public void On_Windows_the_case_of_the_path_does_not_matter()
    {
        var a = Approval("C:\\Tools\\App.exe", Hash);
        Assert.True(ExeIdentity.Matches(a, new ExeIdentity("c:\\tools\\app.EXE", Hash), W));
    }

    [Fact]
    public void Changed_bytes_a_missing_program_or_an_old_approval_never_match()
    {
        var a = Approval("/usr/bin/claude", Hash);
        Assert.False(ExeIdentity.Matches(a, new ExeIdentity("/usr/bin/claude", Other), L)); // same path, different file content
        Assert.False(ExeIdentity.Matches(a, new ExeIdentity("/usr/local/bin/claude", Hash), L)); // same content, different path
        Assert.False(ExeIdentity.Matches(a, null, L));                                      // the program is gone
        Assert.False(ExeIdentity.Matches(Approval(null, null), new ExeIdentity("/usr/bin/claude", Hash), L)); // approval from an older build
        Assert.False(ExeIdentity.Matches(Approval("/usr/bin/claude", null), new ExeIdentity("/usr/bin/claude", Hash), L));
    }

    [Fact]
    public void An_approval_is_void_when_the_resolved_file_changes()
    {
        var dir = Path.Combine(TestEnv.DataDir, "exact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "tool");
        File.WriteAllText(exe, "v1");
        var resolver = new FixedResolver(exe);
        var approvals = new CommandApprovals(Path.Combine(dir, "approvals.json")) { Resolver = resolver };
        var spec = Spec("""{ "id": "x", "run": ["tool"] }""");

        Assert.Equal("pending", approvals.Status("w", spec));
        approvals.Approve("w", spec);
        Assert.Equal("approved", approvals.Status("w", spec));

        File.WriteAllText(exe, "v2-different-bytes");
        Assert.Equal("changed", approvals.Status("w", spec));          // same command, same path, new program file: asked again

        resolver.Path = Path.Combine(dir, "elsewhere");
        File.WriteAllText(resolver.Path, "v2-different-bytes");
        Assert.Equal("changed", approvals.Status("w", spec));
    }

    sealed class FixedResolver(string path) : IExecutableResolver
    {
        public string Path { get; set; } = path;
        public string? Resolve(string exe, bool allowScripts) => File.Exists(Path) ? Path : null;
    }

    // ---------- scrubbed environment ----------

    static Func<string, string?> Env(IDictionary<string, string> vars) => n => vars.TryGetValue(n, out var v) ? v : null;

    static readonly Dictionary<string, string> LinuxHost = new()
    {
        ["PATH"] = "/usr/local/bin:/usr/bin:/bin", ["HOME"] = "/home/me", ["USER"] = "me", ["LOGNAME"] = "me", ["LANG"] = "en_US.UTF-8",
        ["XDG_RUNTIME_DIR"] = "/run/user/1000", ["XDG_CONFIG_HOME"] = "/home/me/.config", ["TZ"] = "UTC", ["TMPDIR"] = "/tmp",
        // everything below must never reach a widget's program
        ["LD_PRELOAD"] = "libgtk4-layer-shell.so.0", ["LD_LIBRARY_PATH"] = "/opt/umbra/lib", ["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=/run/user/1000/bus",
        ["DISPLAY"] = ":0", ["WAYLAND_DISPLAY"] = "wayland-0", ["SSH_AUTH_SOCK"] = "/tmp/ssh-agent", ["GITHUB_TOKEN"] = "ghp_secret",
        ["ANTHROPIC_API_KEY"] = "sk-secret", ["UMBRA_DATA_DIR"] = "/home/me/.local/share/umbra", ["UMBRA_CATALOG_BASE"] = "file:///x",
        ["GTK_PATH"] = "/x", ["WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS"] = "1", ["SHELL"] = "/bin/bash", ["XDG_SESSION_TYPE"] = "wayland",
    };

    [Fact]
    public void The_Linux_environment_keeps_only_the_allow_list()
    {
        var env = CommandEnvironment.Scrub(L, Env(LinuxHost));
        Assert.Equal(new[] { "HOME", "LANG", "LOGNAME", "NO_COLOR", "PATH", "TMPDIR", "TZ", "USER", "XDG_CONFIG_HOME", "XDG_RUNTIME_DIR" }, env.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("/home/me", env["HOME"]);
        Assert.Equal("1", env["NO_COLOR"]);
        foreach (var secret in new[] { "LD_PRELOAD", "LD_LIBRARY_PATH", "DBUS_SESSION_BUS_ADDRESS", "DISPLAY", "WAYLAND_DISPLAY", "SSH_AUTH_SOCK", "GITHUB_TOKEN", "ANTHROPIC_API_KEY", "UMBRA_DATA_DIR", "UMBRA_CATALOG_BASE", "GTK_PATH", "SHELL" })
            Assert.DoesNotContain(secret, env.Keys);
    }

    [Fact]
    public void Linux_variable_names_are_case_sensitive()
    {
        var env = CommandEnvironment.Scrub(L, Env(new Dictionary<string, string> { ["path"] = "/evil", ["home"] = "/evil", ["PATH"] = "/usr/bin" }));
        Assert.Equal("/usr/bin", env["PATH"]);
        Assert.DoesNotContain("home", env.Keys);
        Assert.False(env.ContainsKey("path"));
    }

    [Fact]
    public void Empty_variables_are_left_out_and_NO_COLOR_is_always_set()
    {
        var env = CommandEnvironment.Scrub(L, Env(new Dictionary<string, string> { ["HOME"] = "", ["LANG"] = "C" }));
        Assert.Equal(new[] { "LANG", "NO_COLOR" }, env.Keys.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void The_Linux_search_path_loses_empty_and_relative_entries()
    {
        Assert.Equal("/usr/bin:/bin", CommandEnvironment.SanitizePosixPath(":/usr/bin::bin:./x:/bin:"));
        var env = CommandEnvironment.Scrub(L, Env(new Dictionary<string, string> { ["PATH"] = ".:bin:" }));
        Assert.DoesNotContain("PATH", env.Keys); // nothing safe left: no PATH at all rather than a current-folder one
    }

    [Fact]
    public void The_Windows_allow_list_is_unchanged_and_case_insensitive()
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = "C:\\Windows", ["Path"] = "C:\\Windows\\System32;.;relative", ["TEMP"] = "C:\\T", ["ANTHROPIC_API_KEY"] = "sk", ["UMBRA_DATA_DIR"] = "x", ["HOME"] = "/nope",
        };
        var env = CommandEnvironment.Scrub(W, Env(vars));
        Assert.Equal("C:\\Windows", env["systemroot"]);
        Assert.DoesNotContain("ANTHROPIC_API_KEY", env.Keys);
        Assert.DoesNotContain("UMBRA_DATA_DIR", env.Keys);
        Assert.DoesNotContain("HOME", env.Keys);
        Assert.Equal("C:\\Windows\\System32;.;relative", env["PATH"]); // Windows keeps PATH as it is (resolution skips relative entries)
        Assert.Contains("PSModulePath", CommandEnvironment.WindowsAllowList);
        Assert.Equal(27, CommandEnvironment.WindowsAllowList.Count);
    }

    [Fact]
    public void An_unknown_platform_passes_nothing_but_NO_COLOR()
    {
        var env = CommandEnvironment.Scrub(WidgetPlatforms.Other, Env(LinuxHost));
        Assert.Equal(new[] { "NO_COLOR" }, env.Keys.ToArray());
    }

    [Fact]
    public void The_live_scrub_never_passes_a_secret_on_this_host()
    {
        Environment.SetEnvironmentVariable("UMBRA_TEST_SECRET_TOKEN", "x");
        try { Assert.DoesNotContain("UMBRA_TEST_SECRET_TOKEN", CommandRunner.ScrubbedEnvironment().Keys); }
        finally { Environment.SetEnvironmentVariable("UMBRA_TEST_SECRET_TOKEN", null); }
    }

    // ---------- the argv that reaches the OS ----------

    static readonly IReadOnlyDictionary<string, string> ScrubbedEnv = new Dictionary<string, string> { ["PATH"] = "/usr/bin", ["HOME"] = "/home/me", ["NO_COLOR"] = "1" };

    [Fact]
    public void Windows_starts_the_program_itself()
    {
        var plan = CommandLaunch.Plan(W, false, "setsid", "C:\\Tools\\app.exe", new[] { "-x", "a b" }, "C:\\run", ScrubbedEnv);
        Assert.Equal("C:\\Tools\\app.exe", plan.FileName);
        Assert.Equal(new[] { "-x", "a b" }, plan.Args);
        Assert.Equal("C:\\run", plan.WorkingDirectory);
    }

    [Fact]
    public void Linux_starts_the_program_as_leader_of_its_own_group_with_everything_after_a_double_dash()
    {
        var args = new[] { "-p", "--evil", "; rm -rf /", "$(x)", "" };
        var plan = CommandLaunch.Plan(L, false, "/usr/bin/setsid", "/usr/bin/claude", args, "/data/run/w", ScrubbedEnv);
        Assert.Equal("/usr/bin/setsid", plan.FileName);
        Assert.Equal(new[] { "--", "/usr/bin/claude", "-p", "--evil", "; rm -rf /", "$(x)", "" }, plan.Args);
        Assert.Same(ScrubbedEnv, plan.Environment);
    }

    [Fact]
    public void A_Flatpak_runs_the_program_on_the_host_with_only_the_scrubbed_variables()
    {
        var plan = CommandLaunch.Plan(L, true, "/usr/bin/setsid", "/usr/bin/claude", new[] { "-p", "--host", "x" }, "/home/me/.var/app/io.github.aminechr54.Umbra/data/run/w", ScrubbedEnv);
        Assert.Equal("/usr/bin/setsid", plan.FileName);
        var a = plan.Args.ToList();
        Assert.Equal(new[] { "--", "flatpak-spawn", "--host", "--watch-bus", "--clear-env" }, a.Take(5));
        Assert.Equal("--directory=/home/me/.var/app/io.github.aminechr54.Umbra/data/run/w", a[5]);
        Assert.Equal(new[] { "--env=HOME=/home/me", "--env=NO_COLOR=1", "--env=PATH=/usr/bin" }, a.Skip(6).Take(3));
        // The program and its arguments come after the second "--", so none can be read as an option of flatpak-spawn.
        Assert.Equal(new[] { "--", "/usr/bin/claude", "-p", "--host", "x" }, a.Skip(9));
    }

    [Fact]
    public void Flatpak_is_detected_by_its_marker_file_only()
    {
        Assert.True(CommandLaunch.IsFlatpak(p => p == "/.flatpak-info"));
        Assert.False(CommandLaunch.IsFlatpak(_ => false));
    }

    // ---------- output cap ----------

    [Fact]
    public async Task Output_beyond_the_cap_is_dropped_but_the_stream_is_still_drained()
    {
        var cap = new OutputCapture(1000);
        var source = new CountingStream(new byte[250_000]);
        await cap.PumpAsync(source);
        Assert.True(cap.Truncated);
        Assert.Equal(1000, Encoding.UTF8.GetByteCount(cap.Text()));
        Assert.Equal(250_000, source.Total);   // read to the end, so a child never blocks on a full pipe
    }

    [Fact]
    public async Task Output_at_or_under_the_cap_is_kept_whole()
    {
        var cap = new OutputCapture(1000);
        await cap.PumpAsync(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 1000))));
        Assert.False(cap.Truncated);
        Assert.Equal(1000, cap.Text().Length);

        var small = new OutputCapture(1000);
        await small.PumpAsync(new MemoryStream(Encoding.UTF8.GetBytes("hello")));
        Assert.Equal("hello", small.Text());
        Assert.False(small.Truncated);
    }

    [Fact]
    public async Task The_default_cap_is_one_megabyte()
    {
        Assert.Equal(1024 * 1024, CommandRunner.MaxOutputBytes);
        var cap = new OutputCapture(CommandRunner.MaxOutputBytes);
        await cap.PumpAsync(new MemoryStream(new byte[CommandRunner.MaxOutputBytes + 1]));
        Assert.True(cap.Truncated);
        Assert.Equal(CommandRunner.MaxOutputBytes, cap.Text().Length);
    }

    sealed class CountingStream(byte[] data) : MemoryStream(data)
    {
        public int Total { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await base.ReadAsync(buffer, cancellationToken);
            Total += n;
            return n;
        }
    }

    // ---------- real processes (Linux only; first run is on Linux CI) ----------

    static string Tmp() => Path.Combine(TestEnv.DataDir, "posix-" + Guid.NewGuid().ToString("N"));

    [LinuxFact]
    public async Task A_command_runs_with_the_scrubbed_environment_in_its_working_folder()
    {
        Environment.SetEnvironmentVariable("UMBRA_TEST_SECRET_TOKEN", "leak");
        try
        {
            var cwd = Tmp();
            var r = await CommandRunner.RunAsync("/bin/sh", new[] { "-c", "echo \"cwd=$PWD\"; echo \"secret=${UMBRA_TEST_SECRET_TOKEN:-none}\"; echo \"nocolor=$NO_COLOR\"" }, cwd, TimeSpan.FromSeconds(20));
            Assert.Equal(0, r.ExitCode);
            Assert.Contains($"cwd={cwd}", r.Stdout);
            Assert.Contains("secret=none", r.Stdout);
            Assert.Contains("nocolor=1", r.Stdout);
        }
        finally { Environment.SetEnvironmentVariable("UMBRA_TEST_SECRET_TOKEN", null); }
    }

    [LinuxFact]
    public async Task Output_over_the_cap_is_truncated_on_a_real_process()
    {
        var r = await CommandRunner.RunAsync("/bin/sh", new[] { "-c", "head -c 3000000 /dev/zero | tr '\\0' x" }, Tmp(), TimeSpan.FromSeconds(30), maxBytes: 100_000);
        Assert.True(r.Truncated);
        Assert.Equal(100_000, r.Stdout.Length);
    }

    [LinuxFact]
    public async Task A_command_past_its_timeout_is_killed_together_with_its_children()
    {
        // The issue's case: sh -c "sleep 600 & sleep 600". The background sleep's pid is printed first so the test can look for it.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await CommandRunner.RunAsync("/bin/sh", new[] { "-c", "sleep 600 & echo $!; sleep 600" }, Tmp(), TimeSpan.FromSeconds(2));
        Assert.True(r.TimedOut);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"took {sw.Elapsed}");
        var child = int.Parse(r.Stdout.Trim().Split('\n')[0]);

        // The orphan is reaped by init (or a subreaper) a moment after it dies.
        var gone = false;
        for (var i = 0; i < 50 && !gone; i++)
        {
            gone = !Directory.Exists($"/proc/{child}") || File.ReadAllText($"/proc/{child}/stat").Contains(") Z ");
            if (!gone) await Task.Delay(100);
        }
        Assert.True(gone, $"background child {child} is still running");
    }

    [LinuxFact]
    public async Task Cancelling_a_run_kills_the_group_too()
    {
        using var cts = new CancellationTokenSource();
        var run = CommandRunner.RunAsync("/bin/sh", new[] { "-c", "sleep 600 & echo $!; sleep 600" }, Tmp(), TimeSpan.FromSeconds(60), ct: cts.Token);
        await Task.Delay(1000);
        cts.Cancel();
        var r = await run;
        Assert.False(r.TimedOut);
        var child = int.Parse(r.Stdout.Trim().Split('\n')[0]);
        await Task.Delay(500);
        Assert.True(!Directory.Exists($"/proc/{child}") || File.ReadAllText($"/proc/{child}/stat").Contains(") Z "));
    }
}
