using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Umbra;

/// <summary>
/// "umbractl validate": everything an install checks (<see cref="Community.PrepareLocal"/>: paths, file types, 5 MB,
/// id = folder, entry file, manifest rules, manifestVersion / minUmbraVersion) plus the catalog's manifest rules
/// (unknown keys, option types, hosts) and a few lints for common mistakes. Errors block a pack; warnings do not.
/// </summary>
public static partial class WidgetLint
{
    static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        "$schema", "id", "name", "description", "author", "version", "manifestVersion", "minUmbraVersion", "changelog", "entry", "preview",
        "icon", "width", "height", "themeable", "private", "defaultPosition", "permissions", "options",
    };

    static readonly HashSet<string> PermissionKeys = new(StringComparer.Ordinal) { "hosts", "system", "notifications", "media", "commands", "camera", "userScripts" };
    static readonly HashSet<string> OptionTypes = new(StringComparer.Ordinal) { "text", "number", "select", "toggle" };

    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,}$", RegexOptions.IgnoreCase)]
    private static partial Regex HostPattern();

    [GeneratedRegex(@"font-size\s*:\s*(\d+(?:\.\d+)?)px", RegexOptions.IgnoreCase)]
    private static partial Regex FontSizePx();

    public sealed record Result(string? Id, string? Version, List<string> Errors, List<string> Warnings, int Files, long Bytes)
    {
        public bool Ok => Errors.Count == 0;

        public JsonObject ToJson() => new()
        {
            ["ok"] = Ok,
            ["id"] = Id,
            ["version"] = Version,
            ["files"] = Files,
            ["bytes"] = Bytes,
            ["errors"] = new JsonArray(Errors.Select(e => (JsonNode?)e).ToArray()),
            ["warnings"] = new JsonArray(Warnings.Select(w => (JsonNode?)w).ToArray()),
        };
    }

    /// <summary>
    /// Validates a widget folder and writes the .umbrawidget package. <paramref name="output"/> defaults to the folder path plus
    /// ".umbrawidget"; it must be a full path with that extension, outside the folder. An existing file is only replaced when
    /// <paramref name="overwrite"/> is true (the CLI sets it; the MCP server strips it). Throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public static JsonObject Pack(string folder, string? output, bool overwrite)
    {
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder)) throw new InvalidOperationException("path must be the full path of a widget folder");
        var trimmed = Path.GetFullPath(folder).TrimEnd('\\', '/');
        output ??= trimmed + Community.PackageExtension;
        if (!Path.IsPathFullyQualified(output) || !output.EndsWith(Community.PackageExtension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"out must be a full path ending in {Community.PackageExtension}");
        output = Path.GetFullPath(output); // resolves any ".." in a full path
        if (output.StartsWith(trimmed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("out must be outside the widget folder (the package would contain itself)");
        var lint = Run(trimmed);
        if (!lint.Ok) throw new InvalidOperationException($"Not packed, fix these first: {string.Join("; ", lint.Errors)}");
        if (File.Exists(output) && !overwrite) throw new InvalidOperationException($"'{output}' already exists; delete it or pass --force to replace it");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var manifest = Community.WritePackage(trimmed, output);
        var bytes = File.ReadAllBytes(output);
        return new JsonObject
        {
            ["path"] = output, ["id"] = manifest.Id, ["version"] = manifest.Version, ["bytes"] = bytes.Length,
            ["sha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
            ["warnings"] = new JsonArray(lint.Warnings.Select(w => (JsonNode?)w).ToArray()),
        };
    }

    /// <summary>Checks a widget folder or a .zip / .umbrawidget. Nothing is installed or written.</summary>
    public static Result Run(string path)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        string? id = null, version = null;
        int files = 0;
        long bytes = 0;

        try
        {
            using var pending = Community.PrepareLocal(path);
            id = pending.Manifest.Id;
            version = pending.Manifest.Version;
            var manifestText = File.ReadAllText(pending.PathOf("widget.json"));
            CheckManifest(manifestText, pending.Manifest, pending, errors, warnings);
            foreach (var rel in pending.Files())
            {
                files++;
                bytes += new FileInfo(pending.PathOf(rel)).Length;
                LintFile(rel, pending.PathOf(rel), errors, warnings);
            }
        }
        catch (InvalidOperationException ex)
        {
            errors.Add(ex.Message);
        }
        return new Result(id, version, errors, warnings, files, bytes);
    }

    static void CheckManifest(string text, WidgetManifest m, Community.PendingInstall pending, List<string> errors, List<string> warnings)
    {
        var files = pending.Files().ToHashSet(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        foreach (var p in root.EnumerateObject())
            if (!KnownKeys.Contains(p.Name)) errors.Add($"unknown key \"{p.Name}\" in widget.json (typo?)");

        if (string.IsNullOrWhiteSpace(m.Name)) errors.Add("\"name\" is required");
        if (!Regex.IsMatch(m.Version, @"^\d+\.\d+\.\d+$")) errors.Add("\"version\" must look like 1.2.3");
        if (string.IsNullOrWhiteSpace(m.Description)) warnings.Add("no \"description\": Browse and Settings show it");
        if (string.IsNullOrWhiteSpace(m.Author)) warnings.Add("no \"author\"");
        if (m.Width is < 80 or > 1200 || m.Height is < 80 or > 1200) errors.Add("\"width\" and \"height\" must be between 80 and 1200");
        if (m.Preview is null) warnings.Add("no \"preview\": add a 2x screenshot (umbractl capture <id> preview.png) for Browse");
        else if (!files.Contains(m.Preview)) errors.Add($"preview \"{m.Preview}\" not found");
        if (m.Icon is not null && !Regex.IsMatch(m.Icon, @"^[A-Za-z0-9_-]+\.png$")) errors.Add("\"icon\" must be a .png in the widget folder (letters, digits, - or _)");
        else if (m.Icon is not null && !files.Contains(m.Icon)) errors.Add($"icon \"{m.Icon}\" not found");

        foreach (var h in m.Permissions.Hosts)
        {
            if (!HostPattern().IsMatch(h)) errors.Add($"invalid host \"{h}\" (hostnames only, no scheme, port or path)");
        }
        if (m.Permissions.Hosts.Count != m.Permissions.Hosts.Distinct(StringComparer.OrdinalIgnoreCase).Count()) warnings.Add("permissions.hosts lists a host twice");
        if (root.TryGetProperty("permissions", out var perms) && perms.ValueKind == JsonValueKind.Object)
            foreach (var p in perms.EnumerateObject())
                if (!PermissionKeys.Contains(p.Name)) errors.Add($"unknown permission \"{p.Name}\" (hosts, system, notifications, media, commands, camera, userScripts)");

        foreach (var (key, spec) in m.Options)
        {
            var type = spec?["type"] is JsonValue tv && tv.TryGetValue<string>(out var s) ? s : null;
            if (type is null || !OptionTypes.Contains(type)) errors.Add($"option \"{key}\" has unknown type \"{type}\" (text, number, select, toggle)");
            else if (type == "select" && spec!["choices"] is not JsonArray { Count: > 0 }) errors.Add($"select option \"{key}\" needs \"choices\"");
            if (spec is JsonObject o && !o.ContainsKey("default")) warnings.Add($"option \"{key}\" has no \"default\"");
            if (spec is JsonObject o2 && !o2.ContainsKey("label")) warnings.Add($"option \"{key}\" has no \"label\"");
        }
    }

    static void LintFile(string rel, string full, List<string> errors, List<string> warnings)
    {
        var ext = Path.GetExtension(rel).ToLowerInvariant();
        if (ext is not (".html" or ".js" or ".mjs" or ".css")) return;
        var text = File.ReadAllText(full);
        if (ext is ".html" or ".js" or ".mjs" && text.Contains("chrome.webview", StringComparison.Ordinal))
            warnings.Add($"{rel}: uses chrome.webview; widgets talk to the host only through window.umbra (it is not available to widget code)");
        if (ext is ".html" or ".css")
            foreach (Match m in FontSizePx().Matches(text))
                if (double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) < 11)
                {
                    warnings.Add($"{rel}: font-size {m.Groups[1].Value}px is below 11px and hard to read on a desktop widget");
                    break;
                }
        if (ext is ".html" && Regex.IsMatch(text, @"<(?:script|link|img)\b[^>]*(?:src|href)\s*=\s*[""']http://", RegexOptions.IgnoreCase))
            errors.Add($"{rel}: loads a resource over http://; only https hosts declared in permissions.hosts are reachable");
    }
}
