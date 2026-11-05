using System.Text.Json;
using System.Text.Json.Nodes;

namespace Umbra;

/// <summary>One saved user decision: this widget may run this exact command (identified by its hash).</summary>
public sealed record CommandApproval(string WidgetId, string CommandId, string Hash, DateTime ApprovedAt, string? ExePath = null, string? ExeHash = null);

/// <summary>The program file a command resolved to at some moment: its full path and SHA-256.</summary>
public sealed record ExeIdentity(string Path, string Sha256)
{
    static readonly Dictionary<(string, long, long), string> Cache = new();

    /// <summary>Resolves the spec's executable the way a run would and fingerprints the file; null when it isn't found.</summary>
    public static ExeIdentity? Of(CommandSpec spec)
    {
        var path = CommandRunner.ResolveExe(spec.Exe, allowScripts: spec.Params.Count == 0);
        if (path is null) return null;
        try
        {
            var info = new FileInfo(path);
            var key = (path.ToLowerInvariant(), info.Length, info.LastWriteTimeUtc.Ticks);
            lock (Cache)
                if (Cache.TryGetValue(key, out var cached)) return new(path, cached);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs)).ToLowerInvariant();
            lock (Cache) Cache[key] = hash;
            return new(path, hash);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

/// <summary>
/// Which widget commands the user has approved: %APPDATA%\Umbra\approvals.json, kept apart from settings.json so the
/// Settings page's "save" can't write it. Entries are keyed by widget id + command id and bound to the command's hash.
/// Only the host UI (the approval prompt, the Settings page) calls <see cref="Approve"/>; automation can only read.
/// </summary>
sealed class CommandApprovals(string path)
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    static CommandApprovals? _shared;
    /// <summary>The app-wide store at the data folder.</summary>
    public static CommandApprovals Shared => _shared ??= new(Path.Combine(AppPaths.DataDir, "approvals.json"));

    readonly object _gate = new();
    List<CommandApproval>? _items;

    List<CommandApproval> Items => _items ??= Load();

    List<CommandApproval> Load()
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<List<CommandApproval>>(File.ReadAllText(path), Json) ?? new();
        }
        catch (Exception ex)
        {
            // An unreadable file means no approvals: failing closed.
            Log.Write($"approvals.json unreadable, treating all commands as unapproved: {ex.Message}");
        }
        return new();
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Items, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) { Log.Write($"Could not save approvals: {ex.Message}"); }
    }

    public IReadOnlyList<CommandApproval> List()
    {
        lock (_gate) return Items.ToList();
    }

    /// <summary>"approved" (same command as approved), "changed" (approved before, but the command differs now) or "pending".</summary>
    public string Status(string widgetId, CommandSpec spec)
    {
        CommandApproval? a;
        lock (_gate) a = Items.FirstOrDefault(x => x.WidgetId == widgetId && x.CommandId == spec.Id);
        if (a is null) return "pending";
        if (!string.Equals(a.Hash, spec.Hash(), StringComparison.Ordinal)) return "changed";
        // The approval is also bound to the program file it resolved to: a different path or different bytes need a new approval.
        // (An approval without a recorded file, e.g. one written by an older build, is treated as changed.)
        var exe = ExeIdentity.Of(spec);
        return exe is not null && a.ExeHash == exe.Sha256 && string.Equals(a.ExePath, exe.Path, StringComparison.OrdinalIgnoreCase) ? "approved" : "changed";
    }

    public bool IsApproved(string widgetId, CommandSpec spec) => Status(widgetId, spec) == "approved";

    /// <param name="exe">The program being approved; defaults to what the spec resolves to now.</param>
    public void Approve(string widgetId, CommandSpec spec, ExeIdentity? exe = null)
    {
        exe ??= ExeIdentity.Of(spec);
        lock (_gate)
        {
            Items.RemoveAll(x => x.WidgetId == widgetId && x.CommandId == spec.Id);
            Items.Add(new CommandApproval(widgetId, spec.Id, spec.Hash(), DateTime.UtcNow, exe?.Path, exe?.Sha256));
            Save();
        }
        Log.Write($"[{widgetId}] command '{spec.Id}' approved");
    }

    /// <summary>Removes one approval (commandId) or all of a widget's. Returns how many were removed.</summary>
    public int Revoke(string widgetId, string? commandId = null)
    {
        lock (_gate)
        {
            var n = Items.RemoveAll(x => x.WidgetId == widgetId && (commandId is null || x.CommandId == commandId));
            if (n > 0) Save();
            if (n > 0) Log.Write($"[{widgetId}] command approval revoked ({commandId ?? "all"})");
            return n;
        }
    }

    /// <summary>The pseudo command id under which a widget's one-time camera approval is stored (command ids can't start with @).</summary>
    public const string CameraKey = "@camera";

    public bool CameraApproved(string widgetId)
    {
        lock (_gate) return Items.Any(x => x.WidgetId == widgetId && x.CommandId == CameraKey);
    }

    /// <summary>Only the host's camera prompt calls this.</summary>
    public void ApproveCamera(string widgetId)
    {
        lock (_gate)
        {
            Items.RemoveAll(x => x.WidgetId == widgetId && x.CommandId == CameraKey);
            Items.Add(new CommandApproval(widgetId, CameraKey, "camera", DateTime.UtcNow));
            Save();
        }
        Log.Write($"[{widgetId}] camera approved");
    }

    public int RevokeCamera(string widgetId) => Revoke(widgetId, CameraKey);

    /// <summary>Drops every camera approval whose widget is not in <paramref name="declaring"/> (removed, or no longer declares camera).</summary>
    public int RevokeCameraExcept(IEnumerable<string> declaring)
    {
        var keep = declaring.ToHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            var n = Items.RemoveAll(x => x.CommandId == CameraKey && !keep.Contains(x.WidgetId));
            if (n > 0) { Save(); Log.Write($"{n} camera approval(s) dropped (widget removed or no longer declares camera)"); }
            return n;
        }
    }

    /// <summary>
    /// For the control protocol: every command of the given widgets with its approval status (read-only).
    /// <paramref name="commands"/> lists a widget's commands (declared plus the user's scripts); default: the declared ones.
    /// </summary>
    public JsonArray Describe(IEnumerable<WidgetManifest> manifests, string? onlyWidget = null, Func<WidgetManifest, IEnumerable<CommandSpec>>? commands = null)
    {
        var list = new JsonArray();
        foreach (var m in manifests.Where(m => onlyWidget is null || m.Id == onlyWidget))
            foreach (var c in commands?.Invoke(m) ?? m.Permissions.Commands)
            {
                var a = List().FirstOrDefault(x => x.WidgetId == m.Id && x.CommandId == c.Id);
                list.Add(new JsonObject
                {
                    ["widget"] = m.Id,
                    ["command"] = c.Id,
                    ["run"] = c.DisplayLine(),
                    ["description"] = c.Description,
                    ["status"] = Status(m.Id, c),
                    ["approvedAt"] = a?.ApprovedAt.ToString("o"),
                });
                if (c.IsUserScript) ((JsonObject)list[^1]!)["userScript"] = true; // only on the user's own scripts
            }
        return list;
    }
}
