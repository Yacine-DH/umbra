using System.Text.Json;
using System.Text.Json.Nodes;

namespace Umbra;

/// <summary>
/// The one thing Umbra reports about itself: an anonymous "+1" for a catalog widget when it is freshly installed (not on
/// updates, reinstalls over an existing folder or file/folder/URL installs), so Browse and the website can show install
/// counts. The request is <c>GET {relay}/stats/install?w=&lt;widget id&gt;</c>: no user or machine id, no version, nothing
/// else; the relay stores only the per-widget counts (tools/umbra-relay/stats.js). Off with
/// <see cref="GeneralSettings.CountInstalls"/>. Nothing is sent until the host sets <see cref="Enabled"/> (tests never do).
/// </summary>
public static class InstallStats
{
    /// <summary>The relay host, the same Worker that Buddy and Hearth use. UMBRA_STATS_BASE overrides it (tests, a local wrangler dev).</summary>
    public static string Base =>
        Environment.GetEnvironmentVariable("UMBRA_STATS_BASE")?.TrimEnd('/') ?? "https://umbra-buddy-relay.umbra-server.workers.dev";

    /// <summary>Set by the host: true while the user allows install counting. Null (the default) never reports.</summary>
    public static Func<bool>? Enabled { get; set; }

    /// <summary>Replaces the HTTP GET (tests). Gets the full URL.</summary>
    public static Func<string, Task>? Send { get; set; }

    static readonly HttpClient Http = new(Prefs.CreateHandler()) { Timeout = TimeSpan.FromSeconds(10) };

    public static string InstallUrl(string id) => $"{Base}/stats/install?w={Uri.EscapeDataString(id)}";
    public static string CountsUrl => $"{Base}/stats/installs";

    /// <summary>A fresh catalog install of <paramref name="id"/> finished: report it in the background, never failing the install.</summary>
    public static void Installed(string id, bool fresh, bool local)
    {
        if (!fresh || local || !WidgetManifest.IdPattern().IsMatch(id) || Enabled?.Invoke() != true) return;
        var url = InstallUrl(id);
        _ = Task.Run(async () =>
        {
            try
            {
                if (Send is { } s) await s(url);
                else using (await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)) { }
            }
            catch (Exception ex) { Log.Write($"Install count not sent ({ex.GetType().Name})"); }
        });
    }

    /// <summary>Downloads the per-widget install totals; an empty map when offline or the answer is malformed.</summary>
    public static async Task<IReadOnlyDictionary<string, long>> FetchCountsAsync()
    {
        try { return Parse(JsonNode.Parse(await Http.GetStringAsync(CountsUrl))); }
        catch { return new Dictionary<string, long>(); }
    }

    /// <summary>Reads <c>{ widgets: { id: n } }</c>, keeping only valid widget ids with non-negative whole counts (at most 2000).</summary>
    public static IReadOnlyDictionary<string, long> Parse(JsonNode? root)
    {
        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        if (root?["widgets"] is not JsonObject o) return map;
        foreach (var (id, v) in o)
        {
            if (map.Count >= 2000) break;
            if (!WidgetManifest.IdPattern().IsMatch(id) || v is not JsonValue jv || jv.GetValueKind() != JsonValueKind.Number) continue;
            if (jv.TryGetValue<long>(out var n) && n >= 0) map[id] = n;
        }
        return map;
    }
}
