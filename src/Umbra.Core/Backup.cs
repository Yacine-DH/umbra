using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Umbra;

/// <summary>
/// Backup and restore of settings, layout, theme choice, hotkeys and widget data as one JSON file.
/// Never contains command approvals (<c>approvals.json</c>): they must be given again on the machine that restores.
/// Restoring validates everything first (ids, sizes, value ranges) and only then touches disk.
/// </summary>
static class Backup
{
    public const string Format = "umbra-backup";
    public const int Version = 1;
    public const long MaxFileBytes = 32 * 1024 * 1024;

    /// <summary>The validated content of a backup file, ready to apply.</summary>
    public sealed record Parsed(Settings Settings, Dictionary<string, JsonObject> Data);

    /// <summary>Builds the backup document for the given settings and the widget data currently on disk.</summary>
    public static JsonObject Create(Settings settings)
    {
        var data = new JsonObject();
        foreach (var id in settings.Widgets.Keys.Where(InstanceId.IsValid))
        {
            var d = WidgetStore.All(id);
            if (d.Count > 0) data[id] = d;
        }
        return new JsonObject
        {
            ["format"] = Format,
            ["version"] = Version,
            ["createdAt"] = DateTime.UtcNow.ToString("o"),
            ["umbraVersion"] = AppInfo.Version,
            ["settings"] = settings.ToJson(),
            ["data"] = data,
        };
    }

    /// <summary>Parses and validates a backup file's text. Throws <see cref="InvalidDataException"/> with a user-readable reason.</summary>
    public static Parsed Parse(string text)
    {
        JsonObject root;
        try { root = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("Not a JSON object"); }
        catch (JsonException ex) { throw new InvalidDataException($"Not valid JSON: {ex.Message}"); }

        try
        {
            if (root["format"]?.GetValue<string>() != Format) throw new InvalidDataException("Not an Umbra backup file");
            if (root["version"]?.GetValue<int>() is not int v || v < 1 || v > Version)
                throw new InvalidDataException("This backup was made by a newer Umbra; update first");
            if (root["settings"] is not JsonObject sNode) throw new InvalidDataException("Backup has no settings");

            var s = Settings.FromJson(sNode);
            s.General.RefreshSeconds = Math.Clamp(s.General.RefreshSeconds, 30, 600);
            s.General.PeekSeconds = Math.Clamp(s.General.PeekSeconds, 1, 60);
            s.General.DefaultOpacity = Prefs.ClampOpacity(s.General.DefaultOpacity);
            s.General.DefaultScale = WidgetSettings.ClampScale(s.General.DefaultScale);
            s.General.Size = Prefs.ClampSize(s.General.Size);
            if (!Regex.IsMatch(s.General.Theme ?? "", @"^[a-z0-9][a-z0-9-]{0,39}$")) s.General.Theme = Theme.DefaultId;
            foreach (var id in s.Widgets.Keys)
                if (!InstanceId.IsValid(id)) throw new InvalidDataException($"Invalid widget id '{id}'");
            foreach (var k in s.Hotkeys.Keys.ToList())
                if (!Hotkey.IsAction(k) || Hotkey.Normalize(s.Hotkeys[k]) is not { } norm) s.Hotkeys.Remove(k);
                else s.Hotkeys[k] = norm;

            var data = new Dictionary<string, JsonObject>();
            if (root["data"] is JsonObject dNode)
                foreach (var (id, val) in dNode)
                {
                    if (!InstanceId.IsValid(id)) throw new InvalidDataException($"Invalid data id '{id}'");
                    if (val is not JsonObject obj) throw new InvalidDataException($"Data of '{id}' must be an object");
                    if (obj.Any(p => p.Key.Length is 0 or > WidgetStore.MaxKeyLength)) throw new InvalidDataException($"Data of '{id}' has an invalid key");
                    if (obj.ToJsonString().Length > WidgetStore.MaxBytes) throw new InvalidDataException($"Data of '{id}' is over the {WidgetStore.MaxBytes / 1024} KB cap");
                    data[id] = obj;
                }
            return new(s, data);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException($"Backup is invalid: {ex.Message}");
        }
    }

    /// <summary>Reads a backup file (size-capped) and validates it.</summary>
    public static Parsed ReadFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new InvalidDataException($"File not found: {path}");
        if (info.Length > MaxFileBytes) throw new InvalidDataException("Backup file is too large");
        return Parse(File.ReadAllText(path));
    }

    /// <summary>Replaces the saved data of every given widget copy with the backup's (copies not in the backup are cleared).</summary>
    public static void WriteData(Parsed p, IEnumerable<string> existingIds)
    {
        foreach (var id in existingIds.Where(i => !p.Data.ContainsKey(i))) WidgetStore.Delete(id);
        foreach (var (id, obj) in p.Data)
        {
            WidgetStore.Delete(id);
            foreach (var (k, v) in obj) WidgetStore.Set(id, k, v?.DeepClone());
        }
    }

    /// <summary>
    /// Read-only privacy overview per installed widget: reachable hosts, declared commands and approval status,
    /// saved data size, blocked requests and crashed-process events found in the log, and whether it gave up restarting.
    /// </summary>
    public static JsonArray Privacy(IUmbraHost app, string? onlyWidget = null)
    {
        var blocked = new Dictionary<string, List<string>>();
        foreach (var line in Log.Tail(2000))
        {
            var m = Regex.Match(line, @"\[([^\]]+)\] (?:sandbox blocked|blocked fetch:) (.+)$");
            if (!m.Success) continue;
            var w = InstanceId.IsValid(m.Groups[1].Value) ? InstanceId.WidgetOf(m.Groups[1].Value) : m.Groups[1].Value;
            if (!blocked.TryGetValue(w, out var l)) blocked[w] = l = new();
            l.Add(m.Groups[2].Value.Trim());
        }
        var crashes = new Dictionary<string, int>();
        foreach (var line in Log.Tail(2000))
        {
            var m = CrashRecovery.LogLine.Match(line);
            if (!m.Success) continue;
            var w = InstanceId.IsValid(m.Groups[1].Value) ? InstanceId.WidgetOf(m.Groups[1].Value) : m.Groups[1].Value;
            crashes[w] = crashes.GetValueOrDefault(w) + 1;
        }
        var result = new JsonArray();
        foreach (var m in app.Manifests.Where(m => onlyWidget is null || m.Id == onlyWidget))
        {
            long bytes = 0;
            var copies = new JsonArray();
            foreach (var inst in app.Settings.Instances(m.Id))
            {
                var all = WidgetStore.All(inst);
                var size = all.Count == 0 ? 0 : all.ToJsonString().Length;
                bytes += size;
                copies.Add(new JsonObject { ["instance"] = inst, ["keys"] = all.Count, ["bytes"] = size });
            }
            var b = blocked.GetValueOrDefault(m.Id) ?? new();
            result.Add(new JsonObject
            {
                ["widget"] = m.Id, ["name"] = m.Name.Length > 0 ? m.Name : m.Id,
                ["hosts"] = new JsonArray(m.Permissions.Hosts.Select(h => (JsonNode?)h).ToArray()),
                ["commands"] = app.Commands.Describe(new[] { m }),
                ["camera"] = new JsonObject { ["declared"] = m.Permissions.Camera, ["approved"] = m.Permissions.Camera && app.CameraApproved(m.Id) },
                ["data"] = new JsonObject { ["bytes"] = bytes, ["copies"] = copies },
                ["blocked"] = new JsonObject
                {
                    ["count"] = b.Count,
                    ["recent"] = new JsonArray(b.TakeLast(10).Select(u => (JsonNode?)u).ToArray()),
                },
                ["crashes"] = new JsonObject { ["count"] = crashes.GetValueOrDefault(m.Id), ["stopped"] = CrashRecovery.IsStopped(m.Id) },
            });
        }
        // Widgets this Umbra can't run (made for a newer Umbra) stay on disk with their data; say so here.
        foreach (var (id, name, why) in WidgetManifest.Skipped.Where(s => onlyWidget is null || s.Id == onlyWidget))
            result.Add(new JsonObject
            {
                ["widget"] = id, ["name"] = name, ["incompatible"] = $"Not loaded: {why}.",
                ["hosts"] = new JsonArray(), ["commands"] = new JsonArray(),
                ["data"] = new JsonObject { ["bytes"] = 0, ["copies"] = new JsonArray() },
                ["blocked"] = new JsonObject { ["count"] = 0, ["recent"] = new JsonArray() },
                ["crashes"] = new JsonObject { ["count"] = 0, ["stopped"] = false },
            });
        return result;
    }
}
