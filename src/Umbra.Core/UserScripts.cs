using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Umbra;

/// <summary>
/// A script the user added in Settings for a widget that declares <c>permissions.userScripts</c> (Script Runner). It
/// becomes an ordinary <see cref="CommandSpec"/> (no parameters), so it goes through the same approval gate, runner,
/// timeout and output cap as a declared command. Only the Settings page writes these; no control command, umbractl or
/// MCP tool can add, change or approve one.
/// </summary>
public sealed partial class UserScript
{
    public const int MaxTitle = 60, MaxDescription = 200, MaxCode = 64 * 1024, MaxPath = 250;
    public static readonly string[] Kinds = ["powershell", "python", "bash", "program"];

    /// <summary>Stable command id, picked from the title when the script is created; kept when it is renamed.</summary>
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>powershell, python, bash or program.</summary>
    public string Kind { get; set; } = "powershell";
    /// <summary>"file" (run <see cref="Path"/>) or "code" (run <see cref="Code"/>, which the host saves to a file).</summary>
    public string Mode { get; set; } = "file";
    /// <summary>The script file, or for a program the executable.</summary>
    public string Path { get; set; } = "";
    public string Code { get; set; } = "";
    /// <summary>Extra arguments, written like a command line ("a b" stays one argument). Never passed through a shell.</summary>
    public string Args { get; set; } = "";
    public int TimeoutSeconds { get; set; } = CommandSpec.DefaultTimeoutSeconds;

    [GeneratedRegex(@"^[A-Za-z]:[\\/]")] private static partial Regex AbsoluteWindowsPath();

    public bool IsCode => Mode == "code" && Kind != "program";

    /// <summary>The file extension the host writes code to.</summary>
    string Extension => Kind == "python" ? ".py" : ".ps1";

    /// <summary>Splits an argument string like a command line: whitespace separates, double quotes group, \" is a quote.</summary>
    public static List<string> SplitArgs(string text)
    {
        var list = new List<string>();
        var cur = new StringBuilder();
        bool quoted = false, any = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && text[i + 1] == '"') { cur.Append('"'); i++; any = true; }
            else if (c == '"') { quoted = !quoted; any = true; }
            else if (!quoted && char.IsWhiteSpace(c)) { if (any) { list.Add(cur.ToString()); cur.Clear(); any = false; } }
            else { cur.Append(c); any = true; }
        }
        if (quoted) throw new InvalidOperationException("Arguments have an unclosed quote");
        if (any) list.Add(cur.ToString());
        return list;
    }

    /// <summary>Trims and checks what the user typed. Throws <see cref="InvalidOperationException"/> with a sentence for the user.</summary>
    public void Normalize()
    {
        Title = (Title ?? "").Trim();
        Description = (Description ?? "").Trim();
        Path = (Path ?? "").Trim().Trim('"');
        Args = (Args ?? "").Trim();
        Code = (Code ?? "").Replace("\r\n", "\n");
        Kind = (Kind ?? "").Trim().ToLowerInvariant();
        Mode = Kind == "program" ? "file" : (Mode ?? "").Trim().ToLowerInvariant();

        if (Title.Length is 0 or > MaxTitle || Title.Any(char.IsControl)) throw new InvalidOperationException($"Give the script a name of 1-{MaxTitle} characters");
        if (Description.Length > MaxDescription || Description.Any(char.IsControl)) throw new InvalidOperationException($"The description can be at most {MaxDescription} characters on one line");
        if (!Kinds.Contains(Kind)) throw new InvalidOperationException("Pick PowerShell, Python, Bash or Program");
        if (Mode is not ("file" or "code")) throw new InvalidOperationException("Pick a script file or write the code");
        if (TimeoutSeconds is < 1 or > CommandSpec.MaxTimeoutSeconds) throw new InvalidOperationException($"The time limit must be 1-{CommandSpec.MaxTimeoutSeconds} seconds");
        if (Args.Contains('\n') || Args.Contains('\r')) throw new InvalidOperationException("Arguments must be on one line");

        if (IsCode)
        {
            if (Code.Trim().Length == 0) throw new InvalidOperationException("Write the script's code");
            if (Code.Length > MaxCode || Code.Contains('\0')) throw new InvalidOperationException("The code is too long (64 KB at most)");
            if (Kind == "bash" && Code.Trim().Contains('\n'))
                throw new InvalidOperationException("Bash code must be a single line here. For a longer script, save it as a .sh file and pick that file");
            Path = "";
        }
        else
        {
            Code = "";
            if (Path.Length is 0 or > MaxPath || Path.Any(char.IsControl))
                throw new InvalidOperationException(Kind == "program" ? "Pick the program to run" : "Pick the script file to run");
            // PowerShell and Python get a Windows path; Bash gets the path as bash sees it (~/bin/sync.sh, /mnt/c/...).
            if (Kind is "powershell" or "python" && !AbsoluteWindowsPath().IsMatch(Path))
                throw new InvalidOperationException("Use the full path of the script, like C:\\Scripts\\backup.ps1");
        }
    }

    /// <summary>Where the host keeps this script's code: <c>&lt;scriptsDir&gt;\&lt;widget&gt;\&lt;id&gt;-&lt;code hash&gt;.ps1</c>, so other code means another path (and a new approval).</summary>
    public string CodeFile(string scriptsDir, string widgetId)
    {
        var tag = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Code))).ToLowerInvariant()[..12];
        return System.IO.Path.Combine(scriptsDir, widgetId, $"{Id}-{tag}{Extension}");
    }

    /// <summary>The command this script runs, validated like a declared one.</summary>
    public CommandSpec ToSpec(string scriptsDir, string widgetId)
    {
        var args = SplitArgs(Args);
        var target = IsCode ? CodeFile(scriptsDir, widgetId) : Path;
        List<string> run = Kind switch
        {
            "powershell" => ["powershell", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", target],
            "python" => ["python", target],
            "bash" when IsCode => ["bash", "-c", Code.Trim()],
            "bash" => ["bash", target],
            _ => [target],
        };
        run.AddRange(args);
        var spec = new CommandSpec
        {
            Id = Id, Run = run, TimeoutSeconds = TimeoutSeconds,
            Description = Description.Length > 0 ? Description : Title,
            Title = Title, IsUserScript = true,
        };
        try { spec.Validate(); }
        catch (InvalidOperationException ex)
        {
            // The spec's message names the command id; say it in the user's terms.
            var why = ex.Message.Contains("executable") ? "the program must be a plain name (like pwsh) or a full path like C:\\Tools\\app.exe"
                : ex.Message.Contains("${") || ex.Message.Contains("parameter") ? "arguments can't contain ${...}"
                : ex.Message[(ex.Message.IndexOf(':') + 1)..].Trim();
            throw new InvalidOperationException($"This script can't be saved: {why}");
        }
        return spec;
    }
}

/// <summary>
/// The user's own scripts, per widget id: <c>%APPDATA%\Umbra\scripts.json</c>, kept out of settings.json and widget.json so a
/// widget update never removes them. Code the user wrote is saved under <c>%APPDATA%\Umbra\scripts\&lt;widget&gt;\</c> and written
/// again from this file before each run, so what runs is what Settings showed.
/// </summary>
sealed partial class UserScripts(string path, string scriptsDir)
{
    public const int MaxPerWidget = 32;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    static UserScripts? _shared;
    /// <summary>The app-wide store at the data folder.</summary>
    public static UserScripts Shared => _shared ??= new(System.IO.Path.Combine(AppPaths.DataDir, "scripts.json"), System.IO.Path.Combine(AppPaths.DataDir, "scripts"));

    public string ScriptsDir => scriptsDir;

    readonly object _gate = new();
    Dictionary<string, List<UserScript>>? _items;
    Dictionary<string, List<UserScript>> Items => _items ??= Load();

    sealed class FileModel { public Dictionary<string, List<UserScript>> Widgets { get; set; } = new(); }

    Dictionary<string, List<UserScript>> Load()
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path), Json)?.Widgets ?? new();
        }
        catch (Exception ex) { Log.Write($"scripts.json unreadable, no user scripts loaded: {ex.Message}"); }
        return new();
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new FileModel { Widgets = Items }, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) { Log.Write($"Could not save scripts.json: {ex.Message}"); throw new InvalidOperationException($"Could not save the script: {ex.Message}"); }
    }

    /// <summary>Copies of the widget's scripts, in the user's order.</summary>
    public IReadOnlyList<UserScript> For(string widgetId)
    {
        lock (_gate) return Items.GetValueOrDefault(widgetId)?.Select(Clone).ToList() ?? [];
    }

    /// <summary>The widget's scripts as commands. A script that no longer validates (file edited by hand) is skipped and logged.</summary>
    public IReadOnlyList<CommandSpec> Specs(string widgetId)
    {
        var list = new List<CommandSpec>();
        foreach (var s in For(widgetId))
        {
            try { s.Normalize(); list.Add(s.ToSpec(scriptsDir, widgetId)); }
            catch (InvalidOperationException ex) { Log.Write($"[{widgetId}] user script '{s.Id}' skipped: {ex.Message}"); }
        }
        return list;
    }

    [GeneratedRegex("[^a-z0-9]+")] private static partial Regex NonSlug();

    /// <summary>
    /// Adds (empty <see cref="UserScript.Id"/>) or replaces a script. <paramref name="reserved"/> are the widget's declared
    /// command ids, which a script can never take. Returns the stored script and its command.
    /// </summary>
    public (UserScript Script, CommandSpec Spec) Save(string widgetId, UserScript input, IEnumerable<string> reserved)
    {
        var s = Clone(input);
        s.Normalize();
        var taken = reserved.ToHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            var list = Items.TryGetValue(widgetId, out var l) ? l : Items[widgetId] = new();
            UserScript? old = null;
            if (s.Id.Length > 0)
            {
                old = list.FirstOrDefault(x => x.Id == s.Id) ?? throw new InvalidOperationException("That script no longer exists");
            }
            else
            {
                if (list.Count >= MaxPerWidget) throw new InvalidOperationException($"A widget can have at most {MaxPerWidget} scripts");
                var stem = NonSlug().Replace(s.Title.ToLowerInvariant(), "-").Trim('-');
                if (stem.Length == 0) stem = "script";
                if (stem.Length > 34) stem = stem[..34].TrimEnd('-');
                var id = stem;
                for (var n = 2; taken.Contains(id) || list.Any(x => x.Id == id); n++) id = $"{stem}-{n}";
                s.Id = id;
            }
            var spec = s.ToSpec(scriptsDir, widgetId);
            if (old is null) list.Add(s);
            else list[list.IndexOf(old)] = s;
            Save();
            WriteCode(widgetId, s, cleanup: true);
            Log.Write($"[{widgetId}] user script '{s.Id}' {(old is null ? "added" : "changed")}");
            return (Clone(s), spec);
        }
    }

    /// <summary>Removes a script and its saved code. Returns false when there was none.</summary>
    public bool Delete(string widgetId, string id)
    {
        lock (_gate)
        {
            if (!Items.TryGetValue(widgetId, out var list) || list.RemoveAll(x => x.Id == id) == 0) return false;
            if (list.Count == 0) Items.Remove(widgetId);
            Save();
        }
        DeleteCode(widgetId, id, keep: null);
        Log.Write($"[{widgetId}] user script '{id}' deleted");
        return true;
    }

    /// <summary>Writes a code script's file from the stored code (before every run), so an edited file runs what Settings shows.</summary>
    public void Materialize(string widgetId, string id)
    {
        UserScript? s;
        lock (_gate) s = Items.GetValueOrDefault(widgetId)?.FirstOrDefault(x => x.Id == id);
        if (s is not null) WriteCode(widgetId, Clone(s), cleanup: false);
    }

    void WriteCode(string widgetId, UserScript s, bool cleanup)
    {
        if (!s.IsCode || s.Kind == "bash") { if (cleanup) DeleteCode(widgetId, s.Id, keep: null); return; }
        var file = s.CodeFile(scriptsDir, widgetId);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        // Windows PowerShell 5.1 reads a file without a BOM as ANSI, so .ps1 gets one; Python reads UTF-8 either way.
        var text = s.Code.Replace("\n", "\r\n");
        File.WriteAllText(file, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: s.Kind == "powershell"));
        if (cleanup) DeleteCode(widgetId, s.Id, keep: file);
    }

    void DeleteCode(string widgetId, string id, string? keep)
    {
        var dir = System.IO.Path.Combine(scriptsDir, widgetId);
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir, id + "-*"))
        {
            // Only this id's files ("backup-<12 hex>.ps1"), not "backup-2-<hash>.ps1".
            var rest = System.IO.Path.GetFileNameWithoutExtension(f)[(id.Length + 1)..];
            if (rest.Length != 12 || !rest.All(Uri.IsHexDigit)) continue;
            if (keep is not null && string.Equals(f, keep, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    static UserScript Clone(UserScript s) => new()
    {
        Id = s.Id, Title = s.Title, Description = s.Description, Kind = s.Kind, Mode = s.Mode,
        Path = s.Path, Code = s.Code, Args = s.Args, TimeoutSeconds = s.TimeoutSeconds,
    };
}
