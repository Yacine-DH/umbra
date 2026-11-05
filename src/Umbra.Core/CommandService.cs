using System.Text.Json.Nodes;

namespace Umbra;

/// <summary>What the approval prompt shows. Everything here comes from the manifest and the host, never from a run request.</summary>
public sealed record ApprovalRequest(string WidgetId, string WidgetName, string CommandId, string Description, string ExePath, string CommandLine, bool HasParams, bool IsInterpreter = false);

/// <summary>
/// The gate in front of <see cref="CommandRunner"/>: a widget may only run a command it declared, with validated
/// parameters, while it is enabled, after the user approved exactly that command. A first approval can only be asked
/// for right after a physical click on the widget, at most once per interval, and not after repeated refusals.
/// </summary>
sealed class CommandService
{
    public const int MaxConcurrent = 4, MaxDenials = 3;

    readonly CommandApprovals _approvals;
    readonly Func<ApprovalRequest, Task<bool>> _prompt;
    readonly Func<DateTime> _now;
    readonly object _gate = new();
    readonly Dictionary<string, (DateTime LastPrompt, int Denials)> _prompts = new();
    readonly HashSet<string> _running = new();
    bool _promptOpen;

    /// <summary>Minimum time between two approval prompts for one widget.</summary>
    public TimeSpan MinPromptInterval { get; set; } = TimeSpan.FromSeconds(20);
    /// <summary>Output cap per stream; tests lower it.</summary>
    public int MaxOutputBytes { get; set; } = CommandRunner.MaxOutputBytes;
    /// <summary>Where a widget's commands run: inside the data folder.</summary>
    public Func<string, string> WorkingDir { get; set; } = id => Path.Combine(AppPaths.DataDir, "data", "run", id);
    /// <summary>Lets a test shorten the declared timeout.</summary>
    public Func<CommandSpec, TimeSpan> Timeout { get; set; } = c => TimeSpan.FromSeconds(c.TimeoutSeconds);

    readonly UserScripts? _scripts;

    /// <param name="scripts">The user's own scripts (Settings); null = only declared commands.</param>
    public CommandService(CommandApprovals approvals, Func<ApprovalRequest, Task<bool>> prompt, Func<DateTime>? now = null, UserScripts? scripts = null)
    {
        _approvals = approvals;
        _prompt = prompt;
        _now = now ?? (() => DateTime.UtcNow);
        _scripts = scripts;
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
    public JsonArray Describe(IEnumerable<WidgetManifest> manifests, string? onlyWidget = null) =>
        _approvals.Describe(manifests, onlyWidget, All);

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
        var exe = ExeIdentity.Of(spec);
        if (exe is null) { _approvals.Revoke(m.Id, saved.Id); return (saved, false); }
        _approvals.Approve(m.Id, spec, exe);
        lock (_gate) foreach (var k in _prompts.Keys.Where(k => InstanceId.WidgetOf(k) == m.Id).ToList()) _prompts.Remove(k);
        return (saved, true);
    }

    /// <summary>The Settings page's Delete on a script: removes it, its saved code and its approval.</summary>
    public void DeleteUserScript(WidgetManifest m, string id)
    {
        if (!TakesUserScripts(m) || !_scripts!.Delete(m.Id, id)) throw new WidgetCommandException("That script no longer exists");
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
        var identity = ExeIdentity.Of(spec)
                  ?? throw new WidgetCommandException($"'{spec.Exe}' was not found on this PC (PATH). Install it, or ask the widget author for the full path.");

        if (!_approvals.IsApproved(m.Id, spec))
        {
            var status = _approvals.Status(m.Id, spec);
            if (!userGesture)
                throw new WidgetCommandException(status == "changed"
                    ? "This command changed since you approved it. Click the widget's button, or approve it in Settings > My widgets."
                    : "This command needs your approval first. Click the widget's button, or approve it in Settings > My widgets.");
            if (!await AskAsync(m, instance ?? m.Id, spec, identity)) throw new WidgetCommandException("This command was not allowed (refused or the prompt timed out)");
        }

        instance ??= m.Id;
        var key = $"{instance}/{spec.Id}";
        lock (_gate)
        {
            if (_running.Count >= MaxConcurrent) throw new WidgetCommandException("Too many commands are running; try again in a moment");
            if (!_running.Add(key)) throw new WidgetCommandException($"Command '{spec.Id}' is already running");
        }
        try
        {
            // A script's code is written again from scripts.json, so the file that runs holds what Settings showed.
            if (spec.IsUserScript) _scripts!.Materialize(m.Id, spec.Id);
            Log.Write($"[{m.Id}] run '{spec.Id}' ({identity.Path})");
            var r = await CommandRunner.RunAsync(identity.Path, args, WorkingDir(InstanceId.FileName(instance)), Timeout(spec), MaxOutputBytes, ct);
            Log.Write($"[{m.Id}] '{spec.Id}' exit {r.ExitCode}{(r.TimedOut ? " (timed out)" : "")} in {r.DurationMs} ms");
            return new JsonObject
            {
                ["exitCode"] = r.ExitCode,
                ["stdout"] = r.Stdout,
                ["stderr"] = r.Stderr,
                ["timedOut"] = r.TimedOut,
                ["truncated"] = r.Truncated,
                ["durationMs"] = r.DurationMs,
            };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            Log.Write($"[{m.Id}] '{spec.Id}' could not start: {ex.Message}");
            throw new WidgetCommandException($"The program could not be started: {ex.Message}");
        }
        finally
        {
            lock (_gate) _running.Remove(key);
        }
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
        var exe = ExeIdentity.Of(spec) ?? throw new WidgetCommandException($"'{spec.Exe}' was not found on this PC, so it can't be approved yet");
        _approvals.Approve(m.Id, spec, exe);
        lock (_gate) foreach (var k in _prompts.Keys.Where(k => InstanceId.WidgetOf(k) == m.Id).ToList()) _prompts.Remove(k);
    }
}
