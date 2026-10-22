using System.Text.Json.Nodes;

namespace Umbra;

/// <summary>
/// The optional <c>platforms</c> field of widget.json (<c>["windows", "linux"]</c>; absent = both) and the pure policy built on it:
/// whether a manifest or a catalog entry can run on the current OS. The current OS is always passed in, so every rule is testable on any host.
/// Browse shows the restriction and refuses to install; a local install of an incompatible widget fails with <see cref="Require"/>.
/// </summary>
public static class WidgetPlatforms
{
    public const string Windows = "windows", Linux = "linux", Other = "other";

    /// <summary>The values a manifest may list.</summary>
    public static readonly string[] Known = [Windows, Linux];

    /// <summary>The OS this process runs on: "windows", "linux" or "other" (which no manifest can list, so only unrestricted widgets run there).</summary>
    public static string Current =>
        OperatingSystem.IsWindows() ? Windows : OperatingSystem.IsLinux() ? Linux : Other;

    /// <summary>Throws when the field is present but empty, repeats a value or lists anything but <see cref="Known"/>. Null (absent) is valid.</summary>
    public static void Validate(IReadOnlyCollection<string>? declared)
    {
        if (declared is null) return;
        if (declared.Count == 0) throw new InvalidOperationException($"platforms: list at least one of {string.Join(", ", Known)} (or leave the field out for both)");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in declared)
        {
            if (p is null || !Known.Contains(p, StringComparer.Ordinal))
                throw new InvalidOperationException($"platforms: unknown platform '{p}' (allowed: {string.Join(", ", Known)}, lowercase)");
            if (!seen.Add(p)) throw new InvalidOperationException($"platforms: '{p}' is listed twice");
        }
    }

    /// <summary>True when a widget declaring <paramref name="declared"/> (null = both platforms) can run on <paramref name="current"/>.</summary>
    public static bool Supports(IEnumerable<string>? declared, string current) =>
        declared is null || declared.Contains(current, StringComparer.Ordinal);

    /// <summary>
    /// The same decision for an entry of the downloaded catalog index. The index is remote data, so it is read defensively:
    /// a missing or non-array <c>platforms</c> means "both" (the install still validates the real widget.json), and an array
    /// counts only its string items.
    /// </summary>
    public static bool Supports(JsonNode? catalogEntry, string current)
    {
        if (catalogEntry is not JsonObject o || !o.TryGetPropertyValue("platforms", out var node) || node is not JsonArray arr) return true;
        return Supports(arr.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).Where(s => s is not null).Select(s => s!).ToList(), current);
    }

    /// <summary>"Windows only" / "Linux only" for a widget restricted to one platform, else null (runs on both, nothing to flag).</summary>
    public static string? Label(IReadOnlyCollection<string>? declared) =>
        declared is { Count: 1 } ? $"{DisplayName(declared.First())} only" : null;

    /// <summary>"Windows", "Linux", or the value itself for anything else.</summary>
    public static string DisplayName(string platform) => platform switch
    {
        Windows => "Windows",
        Linux => "Linux",
        _ => platform,
    };

    /// <summary>Throws a message safe to show to the user when <paramref name="declared"/> excludes <paramref name="current"/>.</summary>
    public static void Require(string widgetId, IEnumerable<string>? declared, string current)
    {
        if (Supports(declared, current)) return;
        var list = string.Join(" and ", declared!.Select(DisplayName));
        throw new InvalidOperationException($"'{widgetId}' is only for {list}, so it can't be installed on {DisplayName(current)}.");
    }

    /// <summary>Catalog entry variant of <see cref="Require"/>, checked before anything is downloaded.</summary>
    public static void Require(JsonNode? catalogEntry, string current)
    {
        if (Supports(catalogEntry, current)) return;
        var id = catalogEntry?["id"]?.GetValue<string>() ?? "this widget";
        var names = ((catalogEntry?["platforms"] as JsonArray) ?? []).Select(x => DisplayName(x?.ToString() ?? "")).ToList();
        var only = names.Count > 0 ? $"only for {string.Join(" and ", names)}" : "not for any known platform";
        throw new InvalidOperationException($"'{id}' is {only}, so it can't be installed on {DisplayName(current)}.");
    }
}
