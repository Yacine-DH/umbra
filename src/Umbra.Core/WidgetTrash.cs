using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Umbra;

/// <summary>
/// Settings > Remove does not delete: the widget folder, every copy's umbra.store data and the copies' settings
/// (options, position, size) move to %APPDATA%\Umbra\trash\&lt;id&gt;-&lt;timestamp&gt;\ so Undo can put them back.
/// Entries older than <see cref="Retention"/> are purged at startup. The trash is never read by backups.
/// The working folders of <c>permissions.commands</c> runs (data\run) are not kept and not restored.
/// umbractl / MCP <c>remove</c> still deletes for good (<see cref="Community.Remove"/>).
/// </summary>
static partial class WidgetTrash
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    const string MetaFile = "trash.json";
    const string WidgetFolder = "widget";
    const string DataFolder = "data";

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,39}-[0-9]{14}(?:-[0-9]+)?$")]
    private static partial Regex TokenPattern();

    static string Root => Path.Combine(AppPaths.DataDir, "trash");

    /// <summary>
    /// Copies the widget's data and settings and moves its folder into the trash; returns the undo token
    /// (the entry's folder name), or null when there was nothing to keep. The caller then runs <see cref="Community.Remove"/>.
    /// The folder moves last, so a failure leaves the installed widget untouched.
    /// </summary>
    public static string? Move(Settings settings, string widgetId, DateTime? nowUtc = null)
    {
        if (!WidgetManifest.IdPattern().IsMatch(widgetId)) return null;
        var source = Path.Combine(AppPaths.CommunityDir, widgetId);
        if (!Directory.Exists(source)) return null;

        var now = nowUtc ?? DateTime.UtcNow;
        var baseName = $"{widgetId}-{now:yyyyMMddHHmmss}";
        var token = baseName;
        for (var n = 2; Directory.Exists(Path.Combine(Root, token)); n++) token = $"{baseName}-{n}";
        var entry = Path.Combine(Root, token);
        try
        {
            Directory.CreateDirectory(entry);
            var copies = new Settings();
            foreach (var inst in settings.Instances(widgetId))
                if (settings.Widgets.TryGetValue(inst, out var ws)) copies.Widgets[inst] = ws;
            WidgetStore.CopyAllTo(widgetId, Path.Combine(entry, DataFolder));
            File.WriteAllText(Path.Combine(entry, MetaFile), new JsonObject
            {
                ["widgetId"] = widgetId,
                ["trashedAt"] = now.ToString("o"),
                ["settings"] = copies.ToJson(),
            }.ToJsonString());
            Directory.Move(source, Path.Combine(entry, WidgetFolder));
        }
        catch
        {
            try { Directory.Delete(entry, recursive: true); } catch { }
            throw;
        }
        ImageFolders.Shared.Clear(widgetId); // the chosen folder is per installed widget too
        CommandApprovals.Shared.RevokeCamera(widgetId); // consent is per installed widget: Undo asks again
        Log.Write($"Moved widget {widgetId} to the trash ({token})");
        return token;
    }

    /// <summary>
    /// Puts a trashed widget back: folder, data of every copy, and the copies' settings into <paramref name="settings"/>
    /// (the caller saves and reloads). Returns the widget id. Throws <see cref="InvalidOperationException"/> with a
    /// readable reason when the entry is gone, invalid, or the widget was installed again in the meantime.
    /// </summary>
    public static string Restore(Settings settings, string token)
    {
        if (!TokenPattern().IsMatch(token)) throw new InvalidOperationException("Invalid undo token");
        var entry = Path.Combine(Root, token);
        if (!Directory.Exists(entry)) throw new InvalidOperationException("Nothing to undo: the removed widget is no longer in the trash");

        JsonObject meta;
        try { meta = JsonNode.Parse(File.ReadAllText(Path.Combine(entry, MetaFile))) as JsonObject ?? throw new InvalidOperationException(); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException("The trash entry is damaged and cannot be restored");
        }
        var widgetId = meta["widgetId"]?.GetValue<string>() ?? "";
        if (!WidgetManifest.IdPattern().IsMatch(widgetId) || !token.StartsWith(widgetId + "-", StringComparison.Ordinal))
            throw new InvalidOperationException("The trash entry is damaged and cannot be restored");

        var target = Path.Combine(AppPaths.CommunityDir, widgetId);
        if (Directory.Exists(target)) throw new InvalidOperationException($"'{widgetId}' is installed again; remove it first to restore the old copy");
        var folder = Path.Combine(entry, WidgetFolder);
        if (!Directory.Exists(folder)) throw new InvalidOperationException("The trash entry is damaged and cannot be restored");

        Community.ValidateFolder(folder, widgetId); // the trash is user-writable: same rules as an install before it goes live
        Directory.Move(folder, target);
        WidgetStore.RestoreFrom(Path.Combine(entry, DataFolder), widgetId);
        if (meta["settings"] is JsonNode s)
            foreach (var (inst, ws) in Settings.FromJson(s).Widgets)
                if (InstanceId.IsValid(inst) && InstanceId.WidgetOf(inst) == widgetId) settings.Widgets[inst] = ws;
        try { Directory.Delete(entry, recursive: true); } catch (IOException ex) { Log.Write($"Could not clear trash entry {token}: {ex.Message}"); }
        Log.Write($"Restored widget {widgetId} from the trash");
        return widgetId;
    }

    /// <summary>Deletes entries older than <see cref="Retention"/> (unreadable ones by folder age). Returns how many.</summary>
    public static int Purge(DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        if (!Directory.Exists(Root)) return 0;
        var purged = 0;
        foreach (var dir in Directory.GetDirectories(Root))
        {
            try
            {
                if (now - TrashedAt(dir) < Retention) continue;
                Directory.Delete(dir, recursive: true);
                purged++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Write($"Could not purge trash entry {Path.GetFileName(dir)}: {ex.Message}");
            }
        }
        if (purged > 0) Log.Write($"Purged {purged} trash entr{(purged == 1 ? "y" : "ies")} older than {Retention.TotalDays:0} days");
        return purged;
    }

    static DateTime TrashedAt(string dir)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(Path.Combine(dir, MetaFile)))?["trashedAt"]?.GetValue<string>() is { } s
                && DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t))
                return t.ToUniversalTime();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException) { }
        return Directory.GetCreationTimeUtc(dir);
    }
}
