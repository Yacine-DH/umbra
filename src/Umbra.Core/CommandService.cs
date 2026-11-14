using System.Text.Json.Nodes;

namespace Umbra;

/// <summary>What the approval prompt shows. Everything here comes from the manifest and the host, never from a run request.</summary>
public sealed record ApprovalRequest(string WidgetId, string WidgetName, string CommandId, string Description, string ExePath, string CommandLine, bool HasParams, bool IsInterpreter = false);

/// <summary>Whether a keeps-running script is up, for the widget, Settings and the tray.</summary>
public sealed record ServiceState(bool Running, DateTime? Since, int? LastExit, bool Restarting, int Restarts);

/// <summary>
/// The gate in front of <see cref="CommandRunner"/>: a widget may only run a command it declared, with validated
/// parameters, while it is enabled, after the user approved exactly that command. A first approval can only be asked
/// for right after a physical click on the widget, at most once per interval, and not after repeated refusals.
/// It also owns the user's scripts that keep running (<see cref="CommandSpec.KeepsRunning"/>, never a declared command):
/// one per widget and script, stopped by the user, with the widget, or when the script is edited or deleted.
/// </summary>
sealed class CommandService
{
    public const int MaxConcurrent = 4, MaxDenials = 3;
    /// <summary>At most this many scripts keep running at once, across all widgets.</summary>
    public const int MaxServices = 8;
    /// <summary>Delays before restart 1, 2, ...; after the last one it gives up.</summary>
    public static readonly TimeSpan[] RestartDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(45), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];
    /// <summary>A run at least this long counts as healthy and resets the restart counter.</summary>
    public static readonly TimeSpan StableAfter = TimeSpan.FromMinutes(5);

    readonly CommandApprovals _approvals;
    readonly Func<ApprovalRequest, Task<bool>> _prompt;
    readonly Func<DateTime> _now;
    readonly object _gate = new();
    readonly Dictionary<string, (DateTime LastPrompt, int Denials)> _prompts = new();
    /// <summary>Runs in progress ("instance/command"): Stop cancels one, and the widget reads its live output.</summary>
    readonly Dictionary<string, Run> _running = new();
    /// <summary>Keeps-running scripts ("widget/script"), running or ended (the last output stays until the widget closes).</summary>
    readonly Dictionary<string, Service> _services = new();
    bool _promptOpen;

    sealed class Run(DateTime since)
    {
        public CancellationTokenSource Stop { get; } = new();
        public OutputTail Tail { get; } = new();
        public DateTime Since { get; } = since;
    }

    sealed class Service(string widgetId, CommandSpec spec)
    {
        public string WidgetId { get; } = widgetId;
        public CommandSpec Spec { get; set; } = spec;
        public OutputTail Tail { get; } = new();
        public RunningProcess? Proc { get; set; }
        public DateTime Since { get; set; }
        public int? LastExit { get; set; }
        /// <summary>Restarts in a row that ended before <see cref="StableAfter"/>; reset by a long enough run or a manual start.</summary>
        public int Failures { get; set; }
        public int Restarts { get; set; }
        public CancellationTokenSource? RestartWait { get; set; }
    }

    /// <summary>Minimum time between two approval prompts for one widget.</summary>
    public TimeSpan MinPromptInterval { get; set; } = TimeSpan.FromSeconds(20);
    /// <summary>Output cap per stream; tests lower it.</summary>
    public int MaxOutputBytes { get; set; } = CommandRunner.MaxOutputBytes;
    /// <summary>Where a widget's commands run: inside the data folder.</summary>
    public Func<string, string> WorkingDir { get; set; } = id => Path.Combine(AppPaths.DataDir, "data", "run", id);
    /// <summary>Lets a test shorten the declared timeout.</summary>
    public Func<CommandSpec, TimeSpan> Timeout { get; set; } = c => TimeSpan.FromSeconds(c.TimeoutSeconds);
    /// <summary>The wait before a restart; tests make it short.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;
    /// <summary>Runs a script marked "Run as administrator" (exe, args, folder, create folder, timeout, live output); tests replace the UAC launch.</summary>
    public Func<string, IReadOnlyList<string>, string, bool, TimeSpan, CancellationToken, OutputTail, Task<RunResult>> RunElevated { get; set; }

    Task<RunResult> DefaultRunElevated(string exe, IReadOnlyList<string> args, string dir, bool create, TimeSpan timeout, CancellationToken ct, OutputTail tail) =>
        ElevatedRun.RunAsync(exe, args, dir, timeout, ct, create, tail, MaxOutputBytes);

    /// <summary>A keeps-running script of this widget started, ended, or is waiting to restart (raised on a pool thread).</summary>
    public event Action<string>? ServicesChanged;

    readonly UserScripts? _scripts;

    /// <param name="scripts">The user's own scripts (Settings); null = only declared commands.</param>
    public CommandService(CommandApprovals approvals, Func<ApprovalRequest, Task<bool>> prompt, Func<DateTime>? now = null, UserScripts? scripts = null)
    {
        _approvals = approvals;
        _prompt = prompt;
        _now = now ?? (() => DateTime.UtcNow);
        _scripts = scripts;
        RunElevated = DefaultRunElevated;
    }

    public CommandApprovals Approvals => _approvals;

    /// <summary>Whether the widget takes scripts the user adds in Settings.</summary>
    public bool TakesUserScripts(WidgetManifest m) => m.Permissions.UserScripts && _scripts is not null;

    /// <summary>The user's scripts for the widget (for the Settings editor); empty unless it declares permissions.userScripts.</summary>
    public IReadOnlyList<UserScript> UserScriptsOf(WidgetManifest m) => TakesUserScripts(m) ? _scripts!.For(m.Id) : [];

    /// <summary>Everything the widget may run: its declared commands, then the user's scripts when it declares permissions.userScripts.</summary>
    public IReadOnlyList<CommandSpec> All(WidgetManifest m) =>
        TakesUserScripts(m) ? [.. m.Permissions.Commands, .. _scripts!.Specs(m.Id)] : m.Permissions.Commands;

    /// <summary>A declared command wins over a script with the same id (a script can't take a declared id, but an update could add one).</summary>
    CommandSpec? Find(WidgetManifest m, string commandId) => All(m).FirstOrDefault(c => c.Id == commandId);

    /// <summary>Read-only listing for the control protocol and backups: declared commands and user scripts with their status.</summary>
    public JsonArray Describe(IEnumerable<WidgetManifest> manifests, string? onlyWidget = null)
    {
        var list = _approvals.Describe(manifests, onlyWidget, All);
        foreach (var o in list.OfType<JsonObject>())
        {
            if (o["userScript"] is null) continue;
            var widget = o["widget"]!.GetValue<string>();
            var m = manifests.FirstOrDefault(x => x.Id == widget);
            if (m is null || Find(m, o["command"]!.GetValue<string>()) is not { } spec) continue;
            if (spec.RunAsAdmin) o["runAsAdmin"] = true;
            if (!spec.KeepsRunning) continue;
            o["keepsRunning"] = true;
            o["running"] = State(widget, spec.Id).Running;
        }
        return list;
    }

    /// <summary>
    /// The Settings page's Save on a script the user wrote. Saving is the user's approval of exactly that command (they
    /// typed it in the host's own page), so it is approved when its program is found; otherwise it waits as "pending".
    /// Only <c>SettingsWindow</c> calls this: no control command, umbractl or MCP tool adds or approves a script.
    /// </summary>
    public (UserScript Script, bool Approved) SaveUserScript(WidgetManifest m, UserScript script)
    {
        if (!TakesUserScripts(m)) throw new WidgetCommandException("This widget doesn't take your own scripts");
        UserScript saved; CommandSpec spec;
        try { (saved, spec) = _scripts!.Save(m.Id, script, m.Permissions.Commands.Select(c => c.Id)); }
        catch (InvalidOperationException ex) { throw new WidgetCommandException(ex.Message); }
        // The old version never keeps running under the new one's name: stop it (the user starts the new one).
        StopService(m.Id, saved.Id, forget: true);
        var exe = ExeIdentity.Of(spec);
        if (exe is null) { _approvals.Revoke(m.Id, saved.Id); return (saved, false); }
        _approvals.Approve(m.Id, spec, exe);
        lock (_gate) foreach (var k in _prompts.Keys.Where(k => InstanceId.WidgetOf(k) == m.Id).ToList()) _prompts.Remove(k);
        return (saved, true);
    }

    /// <summary>The Settings page's Delete on a script: stops it, removes it, its saved code and its approval.</summary>
    public void DeleteUserScript(WidgetManifest m, string id)
    {
        if (!TakesUserScripts(m)) throw new WidgetCommandException("That script no longer exists");
        StopService(m.Id, id, forget: true);
        if (!_scripts!.Delete(m.Id, id)) throw new WidgetCommandException("That script no longer exists");
        _approvals.Revoke(m.Id, id);
    }

    /// <param name="enabled">The widget is switched on in Settings.</param>
    /// <param name="userGesture">The user physically pressed the mouse on this widget moments ago.</param>
    public async Task<JsonObject> RunAsync(WidgetManifest m, bool enabled, bool userGesture, string commandId, JsonObject? values, CancellationToken ct, string? instance = null)
    {
        if (!enabled) throw new WidgetCommandException("The widget is disabled");
        var spec = Find(m, commandId)
                   ?? throw new WidgetCommandException($"This widget did not declare a command '{commandId}'");

        // Everything that can be checked without the user is checked first, so a bad request never raises a prompt.
        var args = spec.BuildArgs(values);
        var identity = ExeIdentity.Of(spec, _approvals.Resolver)
                  ?? throw new WidgetCommandException($"'{spec.Exe}' was not found on this PC (PATH). Install it, or ask the widget author for the full path.");

        // A script that runs as administrator (only a user script can) never starts on its own: each run is a click on the
        // widget, then Windows' UAC prompt. Its approval is only ever given in Settings, never from the widget's prompt.
        if (spec.RunAsAdmin)
        {
            if (!spec.IsUserScript || spec.KeepsRunning) throw new WidgetCommandException($"Command '{spec.Id}' can't run as administrator");
            if (!userGesture) throw new WidgetCommandException("A script that runs as administrator only starts when you click Run on the widget");
            if (!_approvals.IsApproved(m.Id, spec))
                throw new WidgetCommandException("This script runs as administrator, so approve it in Settings > My widgets first");
        }

        if (!_approvals.IsApproved(m.Id, spec))
        {
            var status = _approvals.Status(m.Id, spec);
            if (!userGesture)
                throw new WidgetCommandException(status == "changed"
                    ? "This command changed since you approved it. Click the widget's button, or approve it in Settings > My widgets."
                    : "This command needs your approval first. Click the widget's button, or approve it in Settings > My widgets.");
            if (!await AskAsync(m, instance ?? m.Id, spec, identity)) throw new WidgetCommandException("This command was not allowed (refused or the prompt timed out)");
        }

        // A script the user marked "Keeps running" (only a user script can be) starts and the call returns at once.
        if (spec.KeepsRunning && spec.IsUserScript)
        {
            StartService(m.Id, spec, identity, args);
            return new JsonObject { ["started"] = true };
        }

        instance ??= m.Id;
        var key = $"{instance}/{spec.Id}";
        var run = new Run(_now());
        lock (_gate)
        {
            if (_running.Count >= MaxConcurrent) throw new WidgetCommandException("Too many commands are running; try again in a moment");
            if (!_running.TryAdd(key, run)) throw new WidgetCommandException($"Command '{spec.Id}' is already running");
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, run.Stop.Token);
        try
        {
            // A script's code is written again from scripts.json, so the file that runs holds what Settings showed.
            if (spec.IsUserScript) _scripts!.Materialize(m.Id, spec.Id);
            var dir = spec.WorkingDir ?? WorkingDir(InstanceId.FileName(instance));
            RunResult r;
            if (spec.RunAsAdmin)
            {
                Log.Write($"[{m.Id}] run '{spec.Id}' as administrator ({identity.Path})");
                run.Tail.Note("waiting for the Windows administrator prompt");
                r = await RunElevated(identity.Path, args, dir, spec.WorkingDir is null, Timeout(spec), linked.Token, run.Tail);
            }
            else
            {
                Log.Write($"[{m.Id}] run '{spec.Id}' ({identity.Path})");
                r = await CommandRunner.RunAsync(identity.Path, args, dir, Timeout(spec), MaxOutputBytes, linked.Token, run.Tail, createWorkingDir: spec.WorkingDir is null);
            }
            var stopped = run.Stop.IsCancellationRequested && !ct.IsCancellationRequested;
            Log.Write($"[{m.Id}] '{spec.Id}' exit {r.ExitCode}{(r.TimedOut ? " (timed out)" : stopped ? " (stopped)" : "")}{(r.StillRunning ? " (still running elevated)" : "")} in {r.DurationMs} ms");
            var result = new JsonObject
            {
                ["exitCode"] = r.ExitCode,
                ["stdout"] = r.Stdout,
                ["stderr"] = r.Stderr,
                ["timedOut"] = r.TimedOut,
                ["stopped"] = stopped,
                ["truncated"] = r.Truncated,
                ["durationMs"] = r.DurationMs,
            };
            if (spec.RunAsAdmin)
            {
                result["elevated"] = true;
                result["stillRunning"] = r.StillRunning;
            }
            return result;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            Log.Write($"[{m.Id}] '{spec.Id}' could not start: {ex.Message}");
            throw new WidgetCommandException($"The program could not be started: {ex.Message}");
        }
        finally
        {
            lock (_gate) _running.Remove(key);
            run.Stop.Dispose();
        }
    }

    // ---------- scripts that keep running ----------

    static string ServiceKey(string widgetId, string scriptId) => $"{widgetId}/{scriptId}";

    void StartService(string widgetId, CommandSpec spec, ExeIdentity identity, string[] args)
    {
        Service svc;
        lock (_gate)
        {
            if (_services.TryGetValue(ServiceKey(widgetId, spec.Id), out var old) && old.Proc is not null)
                throw new WidgetCommandException($"'{spec.Title ?? spec.Id}' is already running");
            if (_services.Values.Count(s => s.Proc is not null) >= MaxServices)
                throw new WidgetCommandException($"{MaxServices} scripts are already running; stop one first");
            svc = old ?? new Service(widgetId, spec);
            svc.Spec = spec;
            svc.Failures = 0; // a start by hand gives restarts a fresh budget
            svc.RestartWait?.Cancel();
            svc.RestartWait = null;
            _services[ServiceKey(widgetId, spec.Id)] = svc;
        }
        Launch(svc, identity, args);
    }

    /// <summary>Starts the process; the caller checked the gate. Throws <see cref="WidgetCommandException"/> when it can't start.</summary>
    void Launch(Service svc, ExeIdentity identity, string[] args)
    {
        var spec = svc.Spec;
        try
        {
            _scripts!.Materialize(svc.WidgetId, spec.Id);
            var proc = CommandRunner.Start(identity.Path, args, spec.WorkingDir ?? WorkingDir(svc.WidgetId), spec.WorkingDir is null, svc.Tail);
            lock (_gate)
            {
                svc.Proc = proc;
                svc.Since = _now();
                svc.LastExit = null;
            }
            svc.Tail.Note($"started {DateTime.Now:HH:mm:ss}");
            Log.Write($"[{svc.WidgetId}] '{spec.Id}' started, keeps running ({identity.Path})");
            ServicesChanged?.Invoke(svc.WidgetId);
            _ = WatchAsync(svc, proc);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            svc.Tail.Note($"could not start: {ex.Message}");
            Log.Write($"[{svc.WidgetId}] '{spec.Id}' could not start: {ex.Message}");
            throw new WidgetCommandException($"The program could not be started: {ex.Message}");
        }
    }

    async Task WatchAsync(Service svc, RunningProcess proc)
    {
        var code = await proc.Exited;
        TimeSpan? delay = null;
        CancellationTokenSource? wait = null;
        lock (_gate)
        {
            if (svc.Proc != proc) return;
            svc.Proc = null;
            svc.LastExit = code;
            if (_now() - svc.Since >= StableAfter) svc.Failures = 0;
            if (!proc.StopRequested && svc.Spec.Restart && svc.Failures < RestartDelays.Length
                && _services.TryGetValue(ServiceKey(svc.WidgetId, svc.Spec.Id), out var current) && current == svc)
            {
                delay = RestartDelays[svc.Failures++];
                svc.RestartWait = wait = new CancellationTokenSource();
            }
        }
        var why = proc.StopRequested ? "stopped" : $"exited with code {code}";
        svc.Tail.Note(delay is { } d ? $"{why}, restarting in {Format(d)}" : !proc.StopRequested && svc.Spec.Restart ? $"{why}, gave up after {RestartDelays.Length} restarts" : why);
        Log.Write($"[{svc.WidgetId}] '{svc.Spec.Id}' {why}{(delay is { } d2 ? $", restart in {Format(d2)}" : "")}");
        ServicesChanged?.Invoke(svc.WidgetId);
        if (delay is null || wait is null) return;

        try { await Delay(delay.Value, wait.Token); }
        catch (OperationCanceledException) { return; }
        lock (_gate)
        {
            if (svc.RestartWait != wait) return; // stopped, edited or started by hand meanwhile
            svc.RestartWait = null;
            svc.Restarts++;
        }
        wait.Dispose();
        try
        {
            // The same checks as a run without a click: still approved, program still the approved one.
            var identity = ExeIdentity.Of(svc.Spec) ?? throw new WidgetCommandException($"'{svc.Spec.Exe}' was not found on this PC");
            if (!_approvals.IsApproved(svc.WidgetId, svc.Spec)) throw new WidgetCommandException("it is no longer approved");
            Launch(svc, identity, svc.Spec.BuildArgs(null));
        }
        catch (WidgetCommandException ex)
        {
            svc.Tail.Note($"not restarted: {ex.Message}");
            Log.Write($"[{svc.WidgetId}] '{svc.Spec.Id}' not restarted: {ex.Message}");
            ServicesChanged?.Invoke(svc.WidgetId);
        }
    }

    static string Format(TimeSpan t) => t.TotalSeconds < 60 ? $"{t.TotalSeconds:0} s" : $"{t.TotalMinutes:0} min";

    /// <summary>Whether a keeps-running script is up (false for anything else).</summary>
    public ServiceState State(string widgetId, string scriptId)
    {
        lock (_gate)
            return _services.TryGetValue(ServiceKey(widgetId, scriptId), out var s)
                ? new(s.Proc is not null, s.Proc is not null ? s.Since : null, s.LastExit, s.RestartWait is not null, s.Restarts)
                : new(false, null, null, false, 0);
    }

    /// <summary>The scripts that keep running right now, for the tray: (widget, script id, name).</summary>
    public IReadOnlyList<(string WidgetId, string ScriptId, string Title)> RunningServices()
    {
        lock (_gate)
            return _services.Values.Where(s => s.Proc is not null || s.RestartWait is not null)
                .Select(s => (s.WidgetId, s.Spec.Id, s.Spec.Title ?? s.Spec.Id)).ToList();
    }

    /// <summary>
    /// What a script printed so far, for the widget's live output: a keeps-running script of this widget, else this copy's
    /// run in progress. Text is the last <see cref="OutputTail.DefaultMax"/> characters.
    /// </summary>
    public JsonObject Output(string widgetId, string instance, string commandId)
    {
        lock (_gate)
        {
            if (_services.TryGetValue(ServiceKey(widgetId, commandId), out var s))
                return new JsonObject
                {
                    ["keepsRunning"] = true,
                    ["running"] = s.Proc is not null,
                    ["since"] = s.Proc is not null ? new DateTimeOffset(s.Since).ToUnixTimeMilliseconds() : null,
                    ["exitCode"] = s.LastExit,
                    ["restarting"] = s.RestartWait is not null,
                    ["restarts"] = s.Restarts,
                    ["text"] = s.Tail.Text(),
                };
            if (_running.TryGetValue($"{instance}/{commandId}", out var r))
                return new JsonObject
                {
                    ["keepsRunning"] = false,
                    ["running"] = true,
                    ["since"] = new DateTimeOffset(r.Since).ToUnixTimeMilliseconds(),
                    ["text"] = r.Tail.Text(),
                };
        }
        return new JsonObject { ["keepsRunning"] = false, ["running"] = false, ["text"] = "" };
    }

    /// <summary>
    /// Stops a script: this widget's keeps-running one with that id (also a pending restart), else this copy's run in
    /// progress. Returns false when nothing was running. Stopping only ever ends something; it needs no approval.
    /// </summary>
    public bool Stop(string widgetId, string instance, string commandId)
    {
        if (StopService(widgetId, commandId, forget: false)) return true;
        lock (_gate)
        {
            if (!_running.TryGetValue($"{instance}/{commandId}", out var r)) return false;
            r.Stop.Cancel();
            return true;
        }
    }

    /// <summary>Stops a keeps-running script and any restart it waits for; <paramref name="forget"/> also drops its output.</summary>
    bool StopService(string widgetId, string scriptId, bool forget)
    {
        RunningProcess? proc;
        bool any;
        lock (_gate)
        {
            if (!_services.TryGetValue(ServiceKey(widgetId, scriptId), out var s)) return false;
            any = s.Proc is not null || s.RestartWait is not null;
            s.RestartWait?.Cancel();
            s.RestartWait = null;
            proc = s.Proc;
            if (forget) _services.Remove(ServiceKey(widgetId, scriptId));
        }
        proc?.Stop();
        if (any && proc is null) ServicesChanged?.Invoke(widgetId); // only a restart was pending: nothing will exit to report it
        return any;
    }

    /// <summary>The widget's last window closed (disabled, removed, layout switch, exit): every script it keeps running stops.</summary>
    public void StopWidget(string widgetId)
    {
        List<string> ids;
        lock (_gate) ids = _services.Values.Where(s => s.WidgetId == widgetId).Select(s => s.Spec.Id).ToList();
        foreach (var id in ids) StopService(widgetId, id, forget: true);
    }

    /// <summary>
    /// A window of the widget opened while it is enabled: start the keeps-running scripts the user set to start
    /// automatically, when they are approved and not already up. Problems are logged, never prompted.
    /// </summary>
    public void AutoStart(WidgetManifest m)
    {
        if (!TakesUserScripts(m)) return;
        foreach (var spec in _scripts!.Specs(m.Id).Where(s => s.KeepsRunning && s.AutoStart))
        {
            var state = State(m.Id, spec.Id);
            if (state.Running || state.Restarting) continue;
            try
            {
                if (!_approvals.IsApproved(m.Id, spec)) throw new WidgetCommandException("not approved");
                var identity = ExeIdentity.Of(spec) ?? throw new WidgetCommandException($"'{spec.Exe}' was not found on this PC");
                StartService(m.Id, spec, identity, spec.BuildArgs(null));
            }
            catch (WidgetCommandException ex) { Log.Write($"[{m.Id}] '{spec.Id}' not started automatically: {ex.Message}"); }
        }
    }

    /// <summary>The Settings page's Start on a keeps-running script (the caller checked the widget is on). Only approved scripts start.</summary>
    public void StartFromSettings(WidgetManifest m, string scriptId)
    {
        var spec = Find(m, scriptId);
        if (spec is not { IsUserScript: true, KeepsRunning: true }) throw new WidgetCommandException("Only a script that keeps running can be started here");
        if (!_approvals.IsApproved(m.Id, spec)) throw new WidgetCommandException("Approve the script first");
        var identity = ExeIdentity.Of(spec) ?? throw new WidgetCommandException($"'{spec.Exe}' was not found on this PC");
        StartService(m.Id, spec, identity, spec.BuildArgs(null));
    }

    async Task<bool> AskAsync(WidgetManifest m, string instance, CommandSpec spec, ExeIdentity exe)
    {
        lock (_gate)
        {
            if (_promptOpen) throw new WidgetCommandException("An approval prompt is already open");
            var (last, denials) = _prompts.GetValueOrDefault(instance);
            if (denials >= MaxDenials) throw new WidgetCommandException("You refused this widget's commands several times; approve it in Settings > My widgets if you want it to run");
            if (last != default && _now() - last < MinPromptInterval) throw new WidgetCommandException("Please wait a moment before asking again");
            _promptOpen = true;
            _prompts[instance] = (_now(), denials);
        }
        bool allowed;
        try
        {
            allowed = await _prompt(new ApprovalRequest(m.Id, m.Name, spec.Id, spec.Description, exe.Path, spec.DisplayLine(), spec.Params.Count > 0, spec.IsInterpreter));
        }
        finally
        {
            lock (_gate) _promptOpen = false;
        }
        lock (_gate)
        {
            var (last, denials) = _prompts[instance];
            _prompts[instance] = (last, allowed ? 0 : denials + 1);
        }
        if (allowed) _approvals.Approve(m.Id, spec, exe);
        else Log.Write($"[{m.Id}] command '{spec.Id}' refused by the user");
        return allowed;
    }

    /// <summary>The Settings page's Approve button. <paramref name="expectedHash"/> is what the page showed, so a command that changed in between is not approved blind.</summary>
    public void ApproveFromSettings(WidgetManifest m, string commandId, string expectedHash)
    {
        var spec = Find(m, commandId) ?? throw new WidgetCommandException("Unknown command");
        if (!string.Equals(spec.Hash(), expectedHash, StringComparison.Ordinal)) throw new WidgetCommandException("The command changed; review it again");
        var exe = ExeIdentity.Of(spec, _approvals.Resolver) ?? throw new WidgetCommandException($"'{spec.Exe}' was not found on this PC, so it can't be approved yet");
        _approvals.Approve(m.Id, spec, exe);
        lock (_gate) foreach (var k in _prompts.Keys.Where(k => InstanceId.WidgetOf(k) == m.Id).ToList()) _prompts.Remove(k);
    }
}
