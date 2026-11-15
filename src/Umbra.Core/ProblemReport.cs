using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Umbra;

/// <summary>Where the report box was opened from, and (for a widget or an error) what it was about.</summary>
public sealed record ReportContext(string Where, string? Widget = null, string? WidgetVersion = null, string? Error = null)
{
    /// <summary>Entry points the relay accepts: Settings > About, the tray, an error message, the "stopped working" page.</summary>
    public static readonly IReadOnlySet<string> Places = new HashSet<string>(StringComparer.Ordinal) { "about", "tray", "error", "crash" };

    /// <summary>A context from a Settings page message (<c>where</c>, <c>widget</c>, <c>error</c>).</summary>
    public static ReportContext FromMessage(JsonNode? msg)
    {
        static string? S(JsonNode? m, string k) => m?[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        return From(S(msg, "where"), S(msg, "widget"), null, S(msg, "error"));
    }

    /// <summary>A context from the Settings page's message: unknown places become "about", bad widget ids are dropped.</summary>
    public static ReportContext From(string? where, string? widget, string? widgetVersion, string? error)
    {
        var id = widget is { Length: > 0 } w && WidgetManifest.IdPattern().IsMatch(w) ? w : null;
        return new(where is { } p && Places.Contains(p) ? p : "about", id,
            id is null ? null : ProblemReport.CleanVersion(widgetVersion), ProblemReport.Shorten(error, ProblemReport.MaxError));
    }
}

/// <summary>
/// Settings > Report a problem: what the user typed, sent anonymously to the relay (<c>POST {relay}/stats/report</c>,
/// tools/umbra-relay/reports.js) and read by the maintainer on the private stats page. Besides the text it carries only
/// the Umbra version, the OS ("Windows 11 (26200)", "Linux, KDE, wayland"), where the box was opened and, when it was about
/// a widget, that widget's id and version, plus the error text the user saw (user folders and names scrubbed). No user,
/// machine or install id, no log, no settings. Sent only when the user presses Send; no control command, umbractl or MCP
/// tool can send one. Independent of <see cref="GeneralSettings.CountInstalls"/>, because the user asks for it each time.
/// </summary>
public static class ProblemReport
{
    public const int MinText = 10, MaxText = 2000, MaxError = 300;

    /// <summary>At most one report per <see cref="Gap"/> and <see cref="PerDay"/> a day from this process (the relay caps all senders).</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromSeconds(30);
    public const int PerDay = 10;

    /// <summary>Replaces the HTTP POST (tests): gets the URL and JSON body, returns the relay's JSON answer.</summary>
    public static Func<string, string, Task<string>>? Send { get; set; }

    /// <summary>The clock for the rate limit (tests).</summary>
    public static Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    static readonly HttpClient Http = new(Prefs.CreateHandler()) { Timeout = TimeSpan.FromSeconds(15) };
    static readonly object Gate = new();
    static readonly List<DateTimeOffset> Sent = new();

    public static string Url => $"{InstallStats.Base}/stats/report";

    /// <summary>Why <paramref name="text"/> can't be sent, or null. Lengths count code points, like the relay.</summary>
    public static string? Validate(string? text)
    {
        var n = Runes(text?.Trim() ?? "");
        if (n < MinText) return $"Please write a little more (at least {MinText} characters).";
        if (n > MaxText) return $"That's a bit long: please keep it under {MaxText} characters.";
        return null;
    }

    /// <summary>"1.2.3" style versions only (what the relay accepts), else null.</summary>
    public static string? CleanVersion(string? v) =>
        v is { Length: > 0 and <= 40 } && Regex.IsMatch(v, @"^\d{1,4}\.\d{1,4}\.\d{1,6}([.-][0-9A-Za-z.-]{1,20})?$") ? v : null;

    /// <summary>Trimmed, control characters removed, cut to <paramref name="max"/> characters with an ellipsis; null when empty.</summary>
    public static string? Shorten(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = Regex.Replace(s.Replace("\r\n", "\n").Replace('\r', '\n'), @"[\u0000-\u0008\u000B-\u001F\u007F]", "").Trim();
        if (t.Length == 0) return null;
        if (Runes(t) <= max) return t;
        var sb = new StringBuilder();
        foreach (var r in t.EnumerateRunes().Take(max - 1)) sb.Append(r.ToString());
        return sb.Append('…').ToString();
    }

    /// <summary>
    /// Removes what could identify the user from an error message: their home folder (any <c>X:\Users\name</c> or
    /// <c>/home/name</c>), user and machine names, and the query of any URL (keys and tokens live there).
    /// </summary>
    public static string Scrub(string s, string? home, string? user, string? machine)
    {
        if (home is { Length: > 2 } h) s = s.Replace(h.TrimEnd('\\', '/'), "~", StringComparison.OrdinalIgnoreCase);
        s = Regex.Replace(s, @"[A-Za-z]:[\\/]+Users[\\/]+[^\\/\s""']+", "~", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"/(home|Users)/[^/\s""']+", "~");
        s = Regex.Replace(s, @"(https?://[^\s?#""']+)[?#][^\s""']*", "$1");
        foreach (var (name, token) in new[] { (user, "<user>"), (machine, "<machine>") })
            if (name is { Length: >= 3 }) s = Regex.Replace(s, $@"(?<![\w-]){Regex.Escape(name)}(?![\w-])", token, RegexOptions.IgnoreCase);
        return s;
    }

    /// <summary>The OS as the report shows it. Pure: the caller passes what it read.</summary>
    public static string OsText(bool windows, int build, string? desktop, string? session)
    {
        if (windows) return $"Windows {(build >= 22000 ? "11" : "10")} ({build})";
        static string? Part(string? v) => v is { Length: > 0 } ? Regex.Replace(v.Replace(':', '/'), @"[^\w ./-]", "").Trim() is { Length: > 0 } c ? c[..Math.Min(c.Length, 20)] : null : null;
        return string.Join(", ", new[] { "Linux", Part(desktop), Part(session) }.Where(p => p is not null));
    }

    /// <summary>The OS of this machine.</summary>
    public static string CurrentOs() => OsText(OperatingSystem.IsWindows(), Environment.OSVersion.Version.Build,
        Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"), Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"));

    /// <summary>The one-line "What's sent" text under the box, e.g. "Umbra 1.9.0 · Windows 11 (26200) · Clock 1.2.0".</summary>
    public static string Summary(ReportContext ctx, string version, string os, string? widgetName)
    {
        var parts = new List<string> { $"Umbra {version}", os };
        if (ctx.Widget is { } w) parts.Add($"{widgetName ?? w}{(ctx.WidgetVersion is { } v ? " " + v : "")}");
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// The widget's version and name come from its installed manifest, never from the page; a widget that isn't installed
    /// is left out. Returns the resolved context and the widget's display name.
    /// </summary>
    public static (ReportContext Ctx, string? Name) Resolve(ReportContext ctx, IEnumerable<WidgetManifest> manifests)
    {
        if (ctx.Widget is not { } id) return (ctx with { WidgetVersion = null }, null);
        var m = manifests.FirstOrDefault(x => x.Id == id);
        return m is null ? (ctx with { Widget = null, WidgetVersion = null }, null) : (ctx with { WidgetVersion = CleanVersion(m.Version) }, m.Name);
    }

    /// <summary>The host's <c>openReport</c> message for the Settings page: the resolved context and the "Also sent" line.</summary>
    public static JsonObject OpenMessage(ReportContext ctx, IEnumerable<WidgetManifest> manifests, string? os = null)
    {
        var (c, name) = Resolve(ctx, manifests);
        return new JsonObject
        {
            ["type"] = "openReport",
            ["ctx"] = new JsonObject { ["where"] = c.Where, ["widget"] = c.Widget, ["error"] = c.Error },
            ["summary"] = Summary(c, AppInfo.Version, os ?? CurrentOs(), name),
        };
    }

    /// <summary>Handles the page's <c>sendReport</c>: resolves the context, sends, and returns the <c>reportResult</c> message.</summary>
    public static async Task<JsonObject> SendMessageAsync(JsonNode? msg, IEnumerable<WidgetManifest> manifests)
    {
        var (ctx, _) = Resolve(ReportContext.FromMessage(msg), manifests);
        var text = msg?["text"] is JsonValue v && v.TryGetValue<string>(out var t) ? t : "";
        var error = await SendAsync(text, ctx);
        return new JsonObject { ["type"] = "reportResult", ["ok"] = error is null, ["error"] = error };
    }

    /// <summary>The JSON body the relay stores, field for field (reports.js <c>parseReport</c>).</summary>
    public static JsonObject Payload(string text, ReportContext ctx, string version, string os, Func<string, string>? scrub = null)
    {
        scrub ??= s => Scrub(s, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName, Environment.MachineName);
        var o = new JsonObject { ["text"] = text.Trim(), ["version"] = version, ["os"] = os, ["where"] = ctx.Where };
        if (ctx.Widget is { } w)
        {
            o["widget"] = w;
            if (ctx.WidgetVersion is { } v) o["widgetVersion"] = v;
        }
        if (ctx.Error is { } e && Shorten(scrub(e), MaxError) is { } clean) o["error"] = clean;
        return o;
    }

    /// <summary>Whether this process may send another report now (and records it when it may).</summary>
    internal static string? Take(DateTimeOffset now)
    {
        lock (Gate)
        {
            Sent.RemoveAll(t => now - t > TimeSpan.FromDays(1));
            if (Sent.Count > 0 && now - Sent[^1] < Gap) return "You just sent one. Give it a few seconds.";
            if (Sent.Count >= PerDay) return "That's a lot of reports for one day. Thanks! Please try again tomorrow.";
            Sent.Add(now);
            return null;
        }
    }

    /// <summary>Gives back the slot of a report that didn't arrive, so the user can retry right away.</summary>
    static void Release(DateTimeOffset at) { lock (Gate) Sent.Remove(at); }

    /// <summary>Forgets the rate limit (tests).</summary>
    internal static void ResetLimit() { lock (Gate) Sent.Clear(); }

    /// <summary>Sends a report. Null when the relay stored it, else a message for the user. Never throws.</summary>
    public static async Task<string?> SendAsync(string text, ReportContext ctx, string? os = null)
    {
        if (Validate(text) is { } invalid) return invalid;
        var now = Now();
        if (Take(now) is { } limited) return limited;
        var body = Payload(text, ctx, AppInfo.Version, os ?? CurrentOs()).ToJsonString();
        try
        {
            string answer;
            if (Send is { } s) answer = await s(Url, body);
            else
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var res = await Http.PostAsync(Url, content);
                answer = await res.Content.ReadAsStringAsync();
            }
            var node = JsonNode.Parse(answer);
            if (node?["ok"]?.GetValue<bool>() == true) { Log.Write("Problem report sent"); return null; }
            var err = node?["error"]?.GetValue<string>() ?? "unknown answer";
            Log.Write($"Problem report refused: {err}");
            Release(now);
            return $"The report wasn't accepted ({err}).";
        }
        catch (Exception ex)
        {
            Log.Write($"Problem report not sent ({ex.GetType().Name})");
            Release(now);
            return "Couldn't reach the server. Check your connection and try again.";
        }
    }

    /// <summary>Counts Unicode code points, the way the relay's <c>[...text].length</c> does.</summary>
    static int Runes(string s) => s.EnumerateRunes().Count();
}
