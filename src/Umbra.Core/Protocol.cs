using System.IO.Pipes;
using System.Text.Json.Nodes;

namespace Umbra;

/// <summary>
/// The control protocol between a running Umbra and its clients (umbractl, the MCP server, tests).
/// One request per connection over a per-user named pipe, one JSON object per line:
///   request  {"cmd":"list","args":{...}}
///   response {"ok":true,"result":...}  or  {"ok":false,"error":"..."}
/// The legacy plain-text line "settings" (sent by a second Umbra.exe launch) is still understood.
/// Shared source: linked into Umbra.exe and umbractl.exe.
/// </summary>
public static class Protocol
{
    /// <summary>Per-user pipe/mutex name; portable copies add a hash of their data folder so they never collide with an installed copy or each other.</summary>
    public static string PipeName =>
        PortableMode.IsPortable(AppContext.BaseDirectory)
            ? $"Umbra-{Environment.UserName}-{PortableMode.Hash(PortableMode.DataDir(AppContext.BaseDirectory))}"
            : $"Umbra-{Environment.UserName}";

    /// <summary>Every command the host understands, with a one-line description (also used by umbractl help).</summary>
    public static readonly (string Cmd, string Help)[] Commands =
    {
        ("status", "Version, paths, open widgets and general settings"),
        ("list", "Every installed widget copy (id \"sp500\", extra copies \"sp500#2\") with its settings, option schema and window bounds"),
        ("configure", "Change one widget copy: {id (or instance id), enabled?, x?, y?, monitor? (number or name from status.monitors, or primary), scale? (0.5-3); scale null = follow defaultScale, refreshSeconds? (30-600, null = global), opacity? (0.2-1, null = defaultOpacity), notifications? (true|false, null = defaultNotifications), options?{key:value}}"),
        ("add-instance", "Run another copy of a widget with its own options, position and data: {id}; returns the new copy (id like \"sp500#2\")"),
        ("remove-instance", "Remove an extra copy and its data: {id: \"sp500#2\"}; the first copy can only be disabled"),
        ("edit-mode", "Turn edit mode (Arrange widgets: the global lock off) on or off, or read it: {on?}. In edit mode widgets can be moved, resized and zoomed by hand; otherwise all are locked. Saved"),
        ("lock-all", "Lock every widget: edit mode off, no moving, resizing or zooming by hand (the default)"),
        ("unlock-all", "Unlock every widget to arrange it by hand: edit mode on"),
        ("safe-mode", "Safe mode: community widgets stay closed, nothing is deleted or disabled in settings: {on?}. Not saved; the next normal start restores everything (also Umbra.exe --safe)"),
        ("layouts", "Named layouts (positions, sizes, enabled widgets): {op?: list|save|apply|rename|delete, name, to? (new name, for rename)}; no op = list. Saving pins a layout to the current monitor setup"),
        ("groups", "Tab groups: widget copies that share one screen slot, one visible at a time: {op?: list|create|join|leave|activate|next|previous|reorder|configure|icon|delete, group? (id like g1), id? (widget copy), name?, index? (reorder), cycleSeconds? (configure; 0 = off, else 5-3600), icon? (icon op: a name from shared/tabicons, empty = default)}; create makes a group from copy id, or an empty one without id; no op = list"),
        ("theme", "Read or change the active theme: {id?}; no id = read. Changes apply live to every widget"),
        ("themes", "Installed themes plus the community theme catalog, with installed/updateAvailable flags and the active id"),
        ("install-theme", "Install or update a community theme from the catalog: {id}"),
        ("remove-theme", "Uninstall a community theme: {id} (the default theme is used if it was active)"),
        ("set-general", "Change general settings: {refreshSeconds?, animations?, startup?, checkForUpdates?, hideOnFullscreen?, slowOnEnergySaver?, peekSeconds? (1-60), cornerRadius? (default|sharp|round), animationSpeed? (normal|calm|off), size? (0.5-1.5, multiplies every widget), defaultScale? (0.5-3, widgets that follow it update), defaultOpacity? (0.2-1), defaultNotifications?, logLevel? (errors|normal|verbose), developerMode?, hardwareAcceleration? (restart), startupDelaySeconds? (0-300), proxy? (system|none|https://host:port, restart), snapGrid? (0-200 px, 0 = off), snapEdges?, snapGap? (0-100 px, 0 = touch), snap? (master switch for snapping), snapMode? (shift = snap only while Shift is held, the default; always = snap unless Shift is held), borderEnabled?, borderColor? (#rrggbb), accent? (#rrggbb, windows = follow the Windows accent, empty = theme), lightText?, uiFont?, fontScale? (0.8-1.4), reduceTransparency?}"),
        ("peek", "Raise all widgets above other windows for a few seconds without activating them, then return them to the bottom: {seconds?} (also sets the default)"),
        ("hotkeys", "Global hotkeys (edit-mode, refresh-all, toggle-widgets, peek, open-settings) with status: {action?, keys?, reset?}; action+keys sets one (empty keys = off), reset clears all; no args = read. Reply: hotkeys[] {action, label, keys, status active|off|failed|pending (Linux portal: waiting for the desktop's dialog), error?, note?}, backend (win32|x11|portal|none), supported"),
        ("check-update", "Look for a newer Umbra release on GitHub now and report whether one is available (installing is left to the user: tray menu or Settings > About)"),
        ("reset-position", "Move a widget back to its default spot: {id}"),
        ("refresh", "Make widgets fetch fresh data now: {id?}"),
        ("reload", "Re-scan widget folders (after editing files on disk)"),
        ("catalog", "The community catalog from GitHub, with installed/update flags"),
        ("install", "Install or update a community widget from the catalog: {id}, acceptPermissions? (an update that adds hosts, media, notifications, camera or commands is refused, listing the new access, unless true)"),
        ("install-local", "Install or replace a widget from a folder or .zip/.umbrawidget on this PC: {path (full path)}, acceptPermissions? (a widget that declares hosts, system, media, notifications, camera or commands is refused, listing the access, unless true). Same checks as the catalog plus zip-slip protection. CLI only: no MCP tool"),
        ("install-url", "Install or replace a widget from an https:// address of a .umbrawidget or .zip: {url, acceptPermissions? (a widget that declares hosts, system, media, notifications or commands is refused, listing the access, unless true), overwrite? (needed to replace an installed widget or take a catalog widget id)}. HTTPS only (no IP addresses, local names, names that resolve to private addresses, credentials or odd ports), at most 5 redirects (each checked), 5 MB, 60 s, then the same checks as install-local. CLI only: no MCP tool"),
        ("validate-widget", "Read-only check of a widget folder or .zip/.umbrawidget on this PC, without installing: {path (full path)}; returns {ok, id, version, files, bytes, errors[], warnings[]} (install rules plus manifest rules and lints)"),
        ("pack-widget", "Validate a widget folder and write it as a deterministic .umbrawidget package (a zip with widget.json at its root): {path (full folder path), out? (full path ending in .umbrawidget; default <folder>.umbrawidget next to the folder), overwrite?}; refused when validation has errors or when the file exists (overwrite? true replaces it; the MCP server strips out and overwrite and only writes next to the folder). Installing it still needs the user's permission review"),
        ("update-all", "Update every installed community widget that has a newer catalog version, one after another; a failure doesn't stop the rest; an update that adds permissions is skipped (listed in failed with the new access, never accepted here): returns {updated:[{id,version}], failed:[{id,error}]}"),
        ("remove", "Uninstall a community widget and delete its saved data for good: {id} (Settings > Remove keeps it 7 days for Undo)"),
        ("create-widget", "Write a local widget: {id, files{path:content}, overwrite?}; binary files as base64"),
        ("approvals", "Read-only: the local commands widgets declare and whether the user approved them: {id?}. Approving is a human action in Settings"),
        ("data", "A widget copy's saved data (umbra.store): {id, op?: get|set|remove|clear, key?, value?}; no op = get (all keys without key)"),
        ("privacy", "Read-only privacy overview per widget: reachable hosts, declared commands with approval status, saved-data size and recent blocked requests: {id?}"),
        ("backup", "Write settings, layout, theme, hotkeys and widget data to one JSON file (never command approvals): {path}"),
        ("restore", "Replace settings, layout and widget data from a backup file written by backup: {path}. Validated first; approvals are not restored"),
        ("capture","PNG of a widget or a Settings page: {target: <id or instance id>|settings:<home|widgets|browse|themes|layouts|hotkeys|general|privacy|about>, path?}"),
        ("open-settings", "Open the Settings window: {page?}"),
        ("logs", "Last lines of umbra.log: {lines?}"),
        ("diagnostics", "Read-only Markdown block for bug reports: version, Windows build, WebView2, monitors, GPU/proxy on or off, enabled widgets and the last 30 log lines with user paths hidden (no option values, proxy address or widget data)"),
        ("quit", "Exit Umbra"),
    };

    public static JsonObject Request(string cmd, JsonObject? args = null) =>
        new() { ["cmd"] = cmd, ["args"] = args ?? new JsonObject() };

    public static JsonObject Ok(JsonNode? result) => new() { ["ok"] = true, ["result"] = result };

    public static JsonObject Error(string message) => new() { ["ok"] = false, ["error"] = message };

    /// <summary>Client side: send one request to the running Umbra and wait for its reply (<paramref name="pipeName"/> is for tests).</summary>
    public static async Task<JsonObject> SendAsync(JsonObject request, int connectTimeoutMs = 1500, CancellationToken ct = default, string? pipeName = null)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName ?? PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(connectTimeoutMs, ct);
        using var reader = new StreamReader(pipe);
        using var writer = new StreamWriter(pipe) { AutoFlush = true };
        await writer.WriteLineAsync(request.ToJsonString().AsMemory(), ct);
        var line = await reader.ReadLineAsync(ct) ?? throw new IOException("Umbra closed the connection without replying");
        return JsonNode.Parse(line) as JsonObject ?? throw new IOException("Umbra sent an invalid reply");
    }
}

/// <summary>
/// Portable mode: an empty portable.txt next to Umbra.exe keeps all data in &lt;AppDir&gt;\data.
/// UMBRA_DATA_DIR still wins over the marker. Shared source (Umbra.exe and umbractl.exe, which sits next to it).
/// </summary>
public static class PortableMode
{
    public const string Marker = "portable.txt";

    public static bool IsPortable(string appDir) => File.Exists(Path.Combine(appDir, Marker));

    /// <summary>The data folder (not created): UMBRA_DATA_DIR, else &lt;appDir&gt;\data when portable, else %APPDATA%\Umbra (Linux: $XDG_DATA_HOME/umbra).</summary>
    public static string DataDir(string appDir) => DataDir(appDir, Environment.GetEnvironmentVariable("UMBRA_DATA_DIR"));

    /// <summary>Same as <see cref="DataDir(string)"/> with the UMBRA_DATA_DIR value passed in (testable).</summary>
    public static string DataDir(string appDir, string? envOverride)
    {
        if (envOverride is { Length: > 0 } custom) return custom;
        if (IsPortable(appDir)) return Path.Combine(appDir, "data");
        return OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Umbra")
            : Linux.LinuxAppPaths.Resolve(n => n == "UMBRA_DATA_DIR" ? null : Environment.GetEnvironmentVariable(n), appDir, _ => false,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).DataDir;
    }

    /// <summary>Short stable hash of a folder path (case-insensitive) for pipe and mutex names. Not a secret.</summary>
    public static string Hash(string dir)
    {
        var full = Path.GetFullPath(dir).TrimEnd('\\', '/').ToLowerInvariant();
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(full)), 0, 4).ToLowerInvariant();
    }
}
