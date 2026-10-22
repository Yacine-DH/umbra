using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Umbra;

/// <summary>A command or argument problem that is safe to show to the widget (and the user).</summary>
sealed class WidgetCommandException(string message) : Exception(message);

/// <summary>One named string parameter of a declared command. It can only ever become a single argv element.</summary>
public sealed class ParamSpec
{
    public const int DefaultMaxLength = 200, HardMaxLength = 1000;

    public int MaxLength { get; set; } = DefaultMaxLength;
    /// <summary>When set, the value must be exactly one of these (and may then start with "-").</summary>
    public List<string>? Choices { get; set; }
}

/// <summary>
/// A command a widget declares in permissions.commands: an exact executable and an argument array.
/// Never a shell line. Elements of <see cref="Run"/> after the first are literals, or exactly "${name}" for a
/// declared parameter. Validated when widget.json is parsed, so an invalid command makes the widget fail to load.
/// </summary>
public sealed partial class CommandSpec
{
    public const int MaxCommands = 16, MaxArgs = 32, MaxArgLength = 1024, MaxTimeoutSeconds = 120, DefaultTimeoutSeconds = 60;

    public string Id { get; set; } = "";
    public List<string> Run { get; set; } = new();
    public string Description { get; set; } = "";
    public Dictionary<string, ParamSpec> Params { get; set; } = new();
    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;
    /// <summary>The name shown for a script the user added in Settings (<see cref="UserScript"/>); null for a declared command.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string? Title { get; set; }
    /// <summary>True for a script the user added in Settings, false for one declared in widget.json.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool IsUserScript { get; set; }
    /// <summary>A user script marked "Keeps running" in Settings (a tunnel, a server): no time limit, stopped by the user or with the widget. Never set for a declared command.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool KeepsRunning { get; set; }
    /// <summary>The folder a user script runs in ("Start in"); null = the widget's data folder. Never set for a declared command.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string? WorkingDir { get; set; }
    /// <summary>A keeps-running script the user wants started whenever the widget is on (not part of <see cref="Hash"/>: it changes when, not what, runs).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool AutoStart { get; set; }
    /// <summary>A keeps-running script the user wants restarted after it exits on its own (not part of <see cref="Hash"/>).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool Restart { get; set; }
    /// <summary>A user script marked "Run as administrator" in Settings: runs elevated through the UAC prompt. Never set for a declared command.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool RunAsAdmin { get; set; }

    internal static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal) { "id", "run", "description", "params", "timeoutSeconds" };

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,39}$")] private static partial Regex IdPattern();
    [GeneratedRegex(@"^\$\{([a-z][A-Za-z0-9]{0,31})\}$")] private static partial Regex PlaceholderPattern();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$")] private static partial Regex BareExePattern();
    [GeneratedRegex(@"^[A-Za-z]:[\\/][^\0*?""<>|]{1,250}$")] private static partial Regex WindowsAbsoluteExePattern();
    // POSIX absolute path. No leading "//" (on Windows that is a UNC path) and no backslash or wildcard characters.
    [GeneratedRegex(@"^/(?!/)[^\0*?""<>|\\]{1,250}$")] private static partial Regex PosixAbsoluteExePattern();

    /// <summary>
    /// True for an absolute local path in either OS's form: a drive letter (no UNC) or a POSIX path from "/". The form is
    /// checked the same everywhere so a catalog widget validates identically on every host; whether it exists is up to the
    /// platform's <see cref="IExecutableResolver"/>, which accepts only its own form.
    /// </summary>
    internal static bool IsAbsoluteExe(string exe) => WindowsAbsoluteExePattern().IsMatch(exe) || PosixAbsoluteExePattern().IsMatch(exe);

    /// <summary>The executable: a bare name looked up on PATH, or an absolute local path (drive letter or POSIX, no UNC).</summary>
    public string Exe => Run[0];

    public IEnumerable<string> Args => Run.Skip(1);

    // Programs that run whatever code their arguments or input name: shells, script hosts, runtimes, and launchers that start
    // another program ("env", "sudo", "xargs"...). Free-text parameters are never allowed into these (see Validate).
    static readonly HashSet<string> Interpreters = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "wscript", "cscript", "mshta", "node", "python", "pythonw", "py", "bash", "sh", "wsl",
        "rundll32", "regsvr32", "msiexec",
        "dash", "ash", "zsh", "fish", "ksh", "csh", "tcsh", "busybox", "nodejs", "deno", "bun", "perl", "ruby", "php", "lua", "osascript",
        "env", "xargs", "sudo", "su", "doas", "pkexec", "nohup", "setsid", "nice", "ionice", "timeout", "systemd-run", "flatpak", "flatpak-spawn",
        "awk", "gawk", "sed", "find", "xterm",
    };
    // python3, python3.12, perl5.38, ruby3.2...: a bare name may carry a version.
    [GeneratedRegex(@"^(python|pypy|perl|ruby|php|lua|node)[0-9][0-9.]*$", RegexOptions.IgnoreCase)] private static partial Regex VersionedInterpreter();
    static readonly string[] ScriptSuffixes = { ".ps1", ".bat", ".cmd", ".vbs", ".js", ".mjs", ".cjs", ".sh", ".bash", ".py", ".pl", ".rb", ".php", ".lua" };

    /// <summary>True when the executable runs arbitrary code by design (a shell, script host, runtime, launcher or script file).</summary>
    public bool IsInterpreter => IsInterpreterExe(Run[0]);

    /// <summary>The rule behind <see cref="IsInterpreter"/> for any executable string (name or path, either slash).</summary>
    internal static bool IsInterpreterExe(string exe)
    {
        var file = exe.Replace('\\', '/');
        file = file[(file.LastIndexOf('/') + 1)..];
        if (ScriptSuffixes.Any(s => file.EndsWith(s, StringComparison.OrdinalIgnoreCase))) return true;
        var name = file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".com", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
        return Interpreters.Contains(name) || VersionedInterpreter().IsMatch(name);
    }

    /// <summary>The parameter a literal-or-placeholder argument stands for, or null for a literal.</summary>
    internal static string? PlaceholderOf(string arg) =>
        PlaceholderPattern().Match(arg) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>Checks the whole spec. Throws <see cref="InvalidOperationException"/> naming the problem.</summary>
    public void Validate()
    {
        if (!IdPattern().IsMatch(Id)) throw new InvalidOperationException($"command id '{Id}' must be 1-40 lowercase letters, digits and dashes");
        string Where() => $"command '{Id}'";
        if (Run.Count is < 1 or > MaxArgs) throw new InvalidOperationException($"{Where()}: \"run\" needs 1-{MaxArgs} elements (executable first)");
        if (Run.Any(a => a is null || a.Length > MaxArgLength || a.Any(c => c == '\0' || (char.IsControl(c) && c is not ('\t'))))
            || Run.Skip(1).Any(a => a.Contains('\n') || a.Contains('\r')))
            throw new InvalidOperationException($"{Where()}: arguments must be strings of at most {MaxArgLength} characters without control characters");
        if (!BareExePattern().IsMatch(Exe) && !IsAbsoluteExe(Exe))
            throw new InvalidOperationException($"{Where()}: executable '{Exe}' must be a plain program name (e.g. \"claude\") or an absolute path like C:\\Tools\\app.exe or /usr/bin/app");
        if (Exe.EndsWith('/') || Exe.EndsWith('\\') || Exe.Contains("..")) throw new InvalidOperationException($"{Where()}: executable path is invalid");
        if (Description.Length > 200) throw new InvalidOperationException($"{Where()}: description is longer than 200 characters");
        if (TimeoutSeconds is < 1 or > MaxTimeoutSeconds) throw new InvalidOperationException($"{Where()}: timeoutSeconds must be 1-{MaxTimeoutSeconds}");

        var used = new HashSet<string>();
        foreach (var arg in Args)
        {
            if (arg.Contains("${") && PlaceholderOf(arg) is not string name)
                throw new InvalidOperationException($"{Where()}: '{arg}': a parameter must be a whole argument written exactly as ${{name}}, not part of a longer string");
            if (PlaceholderOf(arg) is string p)
            {
                if (!Params.ContainsKey(p)) throw new InvalidOperationException($"{Where()}: argument ${{{p}}} is not declared in \"params\"");
                used.Add(p);
            }
        }
        foreach (var (name, spec) in Params)
        {
            if (!Regex.IsMatch(name, "^[a-z][A-Za-z0-9]{0,31}$")) throw new InvalidOperationException($"{Where()}: invalid parameter name '{name}'");
            if (!used.Contains(name)) throw new InvalidOperationException($"{Where()}: parameter '{name}' is declared but never used in \"run\"");
            if (spec is null || spec.MaxLength is < 1 or > ParamSpec.HardMaxLength) throw new InvalidOperationException($"{Where()}: parameter '{name}' maxLength must be 1-{ParamSpec.HardMaxLength}");
            // Free text into an interpreter is code execution: only fixed choices are allowed there.
            if (IsInterpreter && spec.Choices is null)
                throw new InvalidOperationException($"{Where()}: '{Exe}' runs arbitrary code, so parameter '{name}' needs fixed \"choices\"");
            if (spec.Choices is { } ch && (ch.Count is < 1 or > 64 || ch.Any(c => c is null || c.Length > spec.MaxLength || c.Length == 0 || HasBadChars(c))))
                throw new InvalidOperationException($"{Where()}: parameter '{name}' choices must be 1-64 non-empty strings within maxLength");
        }
    }

    static bool HasBadChars(string s) => s.Any(c => c == '\0' || (char.IsControl(c) && c is not ('\t' or '\n' or '\r')));

    /// <summary>Validates the declarations of a parsed manifest: count, unique ids and each spec.</summary>
    internal static void ValidateAll(IReadOnlyList<CommandSpec> list)
    {
        if (list.Count > MaxCommands) throw new InvalidOperationException($"at most {MaxCommands} commands per widget");
        var seen = new HashSet<string>();
        foreach (var c in list)
        {
            c.Validate();
            if (!seen.Add(c.Id)) throw new InvalidOperationException($"duplicate command id '{c.Id}'");
        }
    }

    /// <summary>Rejects unknown keys in the raw permissions.commands JSON, so a typo can't silently widen or narrow a command.</summary>
    internal static void CheckKeys(JsonElement commands)
    {
        if (commands.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("permissions.commands must be an array");
        foreach (var c in commands.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("each command must be an object");
            foreach (var p in c.EnumerateObject())
                if (!KnownKeys.Contains(p.Name)) throw new InvalidOperationException($"unknown key '{p.Name}' in a command");
            if (c.TryGetProperty("params", out var ps) && ps.ValueKind == JsonValueKind.Object)
                foreach (var p in ps.EnumerateObject())
                    if (p.Value.ValueKind != JsonValueKind.Object || p.Value.EnumerateObject().Any(k => k.Name is not ("maxLength" or "choices")))
                        throw new InvalidOperationException($"parameter '{p.Name}' may only have maxLength and choices");
        }
    }

    /// <summary>
    /// Builds the argument list for a run request. Every declared parameter must be given as a string and nothing
    /// else may be given; each value becomes exactly one argv element.
    /// </summary>
    public string[] BuildArgs(JsonObject? values)
    {
        values ??= new JsonObject();
        foreach (var (key, _) in values)
            if (!Params.ContainsKey(key)) throw new WidgetCommandException($"Command '{Id}' has no parameter '{key}'");
        var resolved = new Dictionary<string, string>();
        foreach (var (name, spec) in Params)
        {
            if (values[name] is not JsonValue v || v.GetValueKind() != JsonValueKind.String)
                throw new WidgetCommandException($"Parameter '{name}' is required and must be a string");
            resolved[name] = CheckValue(name, spec, v.GetValue<string>());
        }
        return Args.Select(a => PlaceholderOf(a) is string p ? resolved[p] : a).ToArray();
    }

    string CheckValue(string name, ParamSpec spec, string value)
    {
        if (spec.Choices is { } choices)
            return choices.Contains(value, StringComparer.Ordinal) ? value : throw new WidgetCommandException($"Parameter '{name}' must be one of: {string.Join(", ", choices)}");
        if (value.Length == 0 || value.Length > spec.MaxLength)
            throw new WidgetCommandException($"Parameter '{name}' must be 1-{spec.MaxLength} characters");
        if (HasBadChars(value)) throw new WidgetCommandException($"Parameter '{name}' contains control characters");
        // A free value must not be able to turn into an option of the program ("-rf", "--config").
        if (value[0] is '-' or '/') throw new WidgetCommandException($"Parameter '{name}' must not start with '-' or '/'");
        return value;
    }

    /// <summary>
    /// SHA-256 over everything that decides what runs (id, executable, arguments, parameter limits, timeout, and for a user
    /// script whether it keeps running, its folder and whether it runs as administrator), not the
    /// description. Approvals are stored against this, so any change to the command asks the user again.
    /// </summary>
    public string Hash()
    {
        var o = new JsonObject
        {
            ["id"] = Id,
            ["run"] = new JsonArray(Run.Select(r => (JsonNode?)r).ToArray()),
            ["timeout"] = TimeoutSeconds,
        };
        var ps = new JsonObject();
        foreach (var (name, spec) in Params.OrderBy(p => p.Key, StringComparer.Ordinal))
            ps[name] = new JsonObject
            {
                ["max"] = spec.MaxLength,
                ["choices"] = spec.Choices is null ? null : new JsonArray(spec.Choices.Select(c => (JsonNode?)c).ToArray()),
            };
        o["params"] = ps;
        // Only present when set, so the hashes (and approvals) of every other command stay what they were.
        if (KeepsRunning) o["keepsRunning"] = true;
        if (WorkingDir is not null) o["cwd"] = WorkingDir;
        if (RunAsAdmin) o["admin"] = true;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(o.ToJsonString()))).ToLowerInvariant();
    }

    /// <summary>The command as the user sees it, e.g. claude -p "Hello" ${model}. For display only, never executed.</summary>
    public string DisplayLine() => string.Join(' ', Run.Select(Quote));

    static string Quote(string s) => s.Length == 0 || s.Any(c => c is ' ' or '\t' or '"') ? "\"" + s.Replace("\"", "\\\"") + "\"" : s;
}
