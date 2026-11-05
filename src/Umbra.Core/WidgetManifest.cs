using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Umbra;

public enum WidgetSource { BuiltIn, Community }

/// <summary>Describes one widget folder (widgets/&lt;id&gt;/widget.json).</summary>
public sealed partial class WidgetManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    /// <summary>The widget.json format this widget is written for (missing = 1). A newer one than <see cref="SupportedManifestVersion"/> is refused.</summary>
    public int ManifestVersion { get; set; } = 1;
    /// <summary>Oldest Umbra release the widget works on ("1.9.0"), or null. A newer one than the running app is refused.</summary>
    public string? MinUmbraVersion { get; set; }
    /// <summary>What changed in this version (max 500 characters); shown when an update asks for new access.</summary>
    public string? Changelog { get; set; }
    public string Entry { get; set; } = "index.html";
    public string? Preview { get; set; }
    /// <summary>Small PNG (up to 128 px, 64 KB) next to widget.json, drawn on the group tab strip; null = a letter badge.</summary>
    public string? Icon { get; set; }
    public int Width { get; set; } = 320;
    public int Height { get; set; } = 180;
    /// <summary>False opts the widget out of themes (it keeps the default look).</summary>
    public bool Themeable { get; set; } = true;
    /// <summary>
    /// A widget shared outside the catalog and installed from a file (Settings > Browse > Install from file). It shows a Private
    /// badge, is never offered a catalog update, and a catalog widget with the same id cannot replace it. Grants no extra trust.
    /// </summary>
    public bool Private { get; set; }
    public PointSpec? DefaultPosition { get; set; }
    public PermissionSpec Permissions { get; set; } = new();
    /// <summary>Option schema shown in Settings: key -> {label, type, default, choices?}.</summary>
    public JsonObject Options { get; set; } = new();

    [JsonIgnore] public List<string> OptionOrder { get; set; } = new();
    [JsonIgnore] public string Folder { get; set; } = "";
    [JsonIgnore] public WidgetSource Source { get; set; }

    public sealed class PointSpec { public int X { get; set; } public int Y { get; set; } }
    public sealed class PermissionSpec
    {
        public List<string> Hosts { get; set; } = new();
        /// <summary>Read-only machine totals the widget may read with <c>umbra.system()</c>: any of cpu, memory, network.</summary>
        public List<string> System { get; set; } = new();
        /// <summary>Allows <c>umbra.notify</c>; the user must also switch notifications on for the widget.</summary>
        public bool Notifications { get; set; }
        /// <summary>Windows media session access for <c>umbra.media</c>: "read" (now playing) and/or "control" (play, pause, next, previous).</summary>
        public List<string> Media { get; set; } = new();
        /// <summary>Local programs the widget may ask to run; each needs the user's approval (see <see cref="CommandSpec"/>).</summary>
        public List<CommandSpec> Commands { get; set; } = new();
        /// <summary>
        /// Lets the user add their own scripts for this widget in Settings (<see cref="UserScript"/>). Grants nothing by
        /// itself: each script is written by the user in the host's Settings page, and the widget can only run it by id.
        /// </summary>
        public bool UserScripts { get; set; }
        /// <summary>
        /// Allows <c>umbra.timelapse</c>: the host takes webcam stills and renders them to a video. Recording starts only
        /// from a click on the widget, shows a red dot on the widget and in the tray, and the page never sees a frame.
        /// </summary>
        public bool Camera { get; set; }
    }

    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Highest widget.json <c>manifestVersion</c> this build understands. Raise it only with a format change.</summary>
    public const int SupportedManifestVersion = 1;

    /// <summary>The running Umbra version ("1.8.0"), from the assembly (Directory.Build.props).</summary>
    public static string AppVersion { get; } =
        (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(WidgetManifest).Assembly)?.InformationalVersion
         ?? typeof(WidgetManifest).Assembly.GetName().Version!.ToString(3)).Split('+')[0];

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex SemVer();

    /// <summary>
    /// Why this Umbra can't run a widget with these manifest fields (a clear "update Umbra" sentence), or null when it can.
    /// <paramref name="appVersion"/> defaults to <see cref="AppVersion"/>.
    /// </summary>
    public static string? Incompatibility(int manifestVersion, string? minUmbraVersion, string? appVersion = null)
    {
        appVersion ??= AppVersion;
        if (manifestVersion > SupportedManifestVersion)
            return $"it uses widget format {manifestVersion}, newer than this Umbra (v{appVersion}) understands. Update Umbra to use it";
        if (minUmbraVersion is { Length: > 0 } min && Community.CompareVersions(min, appVersion) > 0)
            return $"it needs Umbra {min} or newer and this is v{appVersion}. Update Umbra to use it";
        return null;
    }

    /// <summary>Same as <see cref="Incompatibility(int, string?, string?)"/> for this manifest.</summary>
    public string? Incompatibility(string? appVersion = null) => Incompatibility(ManifestVersion, MinUmbraVersion, appVersion);

    /// <summary>Throws <see cref="InvalidOperationException"/> naming the widget when this Umbra can't run it.</summary>
    public void RequireCompatible(string? appVersion = null)
    {
        if (Incompatibility(appVersion) is { } why) throw new InvalidOperationException($"Can't install {(Name.Length > 0 ? Name : Id)}: {why}.");
    }

    /// <summary>Lowercase letters, digits and dashes; also used as the folder name.</summary>
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,39}$")]
    public static partial Regex IdPattern();

    /// <summary>Page URL inside WebView2. Built-ins and community widgets live on separate virtual hosts.</summary>
    public string PageUrl => Source == WidgetSource.BuiltIn
        ? VirtualHosts.Url($"widgets/{Folder}/{Entry}")
        : VirtualHosts.CommunityUrl($"{Folder}/{Entry}");

    /// <summary>
    /// True for widgets that ship with the app and cannot be removed. A legacy id (it used to be built in and is
    /// now a catalog widget) can still be sitting in an old install folder; that copy is removable.
    /// </summary>
    public bool IsPermanent => Source == WidgetSource.BuiltIn && !Restore.LegacyIds.Contains(Id);

    public static List<WidgetManifest> DiscoverAll()
    {
        var skipped = new List<(string Id, string Name, string Why)>();
        var all = Discover(AppPaths.CommunityDir, WidgetSource.Community, skipped);
        var builtIn = Discover(AppPaths.WidgetsDir, WidgetSource.BuiltIn, skipped)
            // A catalog copy of a legacy id replaces the stale built-in one left by an old install.
            .Where(b => !Restore.LegacyIds.Contains(b.Id) || all.All(c => c.Id != b.Id));
        var builtInList = builtIn.ToList();
        Skipped = skipped;
        var community = all.Where(c => builtInList.All(b => b.Id != c.Id));
        return builtInList.Concat(community).ToList();
    }

    /// <summary>First line only, control characters removed, at most 500 characters (it is shown in a native dialog).</summary>
    public static string? SanitizeChangelog(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = text.Trim().Split((char)10, 2)[0];
        var clean = new string(line.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length > 500) clean = clean[..500];
        return clean.Length == 0 ? null : clean;
    }

    /// <summary>
    /// Checks the optional layout hints of each option: <c>group</c> (non-empty string, max 40 chars) and
    /// <c>advanced</c> (bool). Settings folds those options into collapsed sections. Throws <see cref="JsonException"/>.
    /// </summary>
    public static void ValidateOptionLayout(JsonObject options)
    {
        foreach (var (key, spec) in options)
        {
            if (spec is not JsonObject o) continue;
            if (o.TryGetPropertyValue("group", out var g)
                && !(g is JsonValue gv && gv.TryGetValue<string>(out var s) && s.Trim().Length > 0 && s.Length <= 40))
                throw new JsonException($"option '{key}': \"group\" must be a non-empty string of at most 40 characters");
            if (o.TryGetPropertyValue("advanced", out var a)
                && !(a is JsonValue av && av.GetValueKind() is JsonValueKind.True or JsonValueKind.False))
                throw new JsonException($"option '{key}': \"advanced\" must be true or false");
        }
    }

    /// <summary>Parses widget.json text. Throws on invalid JSON; does not check the id.</summary>
    public static WidgetManifest Parse(string text)
    {
        var m = JsonSerializer.Deserialize<WidgetManifest>(text, Json) ?? throw new JsonException("widget.json is empty");
        // Settings shows options in the order they are written in widget.json.
        using (var doc = JsonDocument.Parse(text))
        {
            if (doc.RootElement.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Object)
                m.OptionOrder = opts.EnumerateObject().Select(p => p.Name).ToList();
            // Commands are strict: any problem fails the whole widget rather than loading a half-understood command.
            if (doc.RootElement.TryGetProperty("permissions", out var perms) && perms.ValueKind == JsonValueKind.Object
                && perms.TryGetProperty("commands", out var cmds))
                CommandSpec.CheckKeys(cmds);
        }
        ValidateOptionLayout(m.Options);
        if (m.ManifestVersion < 1) throw new JsonException("\"manifestVersion\" must be 1 or higher");
        if (m.MinUmbraVersion is not null && !SemVer().IsMatch(m.MinUmbraVersion)) throw new JsonException("\"minUmbraVersion\" must look like 1.9.0");
        m.Changelog = SanitizeChangelog(m.Changelog);
        CommandSpec.ValidateAll(m.Permissions.Commands);
        SystemMetrics.Validate(m.Permissions.System);
        MediaService.Validate(m.Permissions.Media);
        return m;
    }

    /// <summary>Widgets on disk that this Umbra can't run (made for a newer Umbra), from the last <see cref="DiscoverAll"/>: id, name, reason.</summary>
    public static IReadOnlyList<(string Id, string Name, string Why)> Skipped { get; private set; } = Array.Empty<(string, string, string)>();

    internal static List<WidgetManifest> Discover(string root, WidgetSource source, List<(string Id, string Name, string Why)>? skipped = null)
    {
        var list = new List<WidgetManifest>();
        if (!Directory.Exists(root)) return list;
        foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d))
        {
            var file = Path.Combine(dir, "widget.json");
            if (!File.Exists(file)) continue;
            try
            {
                var m = Parse(File.ReadAllText(file));
                if (!IdPattern().IsMatch(m.Id)) continue;
                // A widget for a newer Umbra stays on disk untouched and loads again once Umbra is updated.
                if (m.Incompatibility() is { } why) { Log.Write($"Skipping widget {m.Id}: {why}"); skipped?.Add((m.Id, m.Name, why)); continue; }
                m.Folder = Path.GetFileName(dir);
                m.Source = source;
                list.Add(m);
            }
            catch (Exception ex)
            {
                Log.Write($"Skipping widget {dir}: {ex.Message}");
            }
        }
        return list;
    }

    /// <summary>Default value for every option declared in the manifest.</summary>
    public JsonObject DefaultOptions()
    {
        var o = new JsonObject();
        foreach (var (key, spec) in Options)
            o[key] = spec?["default"]?.DeepClone();
        return o;
    }

    public bool AllowsHost(string host) =>
        Permissions.Hosts.Any(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));

    public bool AllowsUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps
        && AllowsHost(u.Host);
}
