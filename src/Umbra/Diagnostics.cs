using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace Umbra;

/// <summary>
/// The "Copy diagnostics" block for bug reports (Settings > About, <c>umbractl diagnostics</c>): version, Windows build,
/// WebView2, monitors, a few switches, the enabled widgets and the last log lines. Deliberately contains no option values,
/// proxy address, widget data or file contents, and user folders in the log are replaced by environment-variable names.
/// </summary>
static class Diagnostics
{
    public const int LogLines = 30;

    public readonly record struct MonitorLine(int Width, int Height, int Dpi, bool Primary);
    public readonly record struct WidgetLine(string Id, string Version, string Source);

    /// <summary>Everything the report shows, gathered once so the formatting is a pure function.</summary>
    public sealed record Snapshot(
        string Version, bool Portable, bool SafeMode, string Windows, string WebView2,
        IReadOnlyList<MonitorLine> Monitors, bool HardwareAcceleration, string Proxy,
        IReadOnlyList<WidgetLine> Widgets, IReadOnlyList<string> Log);

    /// <summary>Reads the running app. Run on the UI thread (settings and windows are touched).</summary>
    public static Snapshot Collect(App app)
    {
        var g = app.Settings.General;
        var widgets = app.Manifests
            .Where(m => app.Settings.Instances(m.Id).Any(i => app.Settings.For(m, i).Enabled))
            .Select(m => new WidgetLine(m.Id, m.Version, m.IsPermanent ? "built-in" : "community"))
            .ToList();
        return new Snapshot(
            AppInfo.Version, AppPaths.IsPortable, app.SafeMode,
            WindowsText(), WebView2Version(), Monitors(), g.HardwareAcceleration, ProxyKind(g.Proxy),
            widgets, ScrubLog(Log.Tail(LogLines), Roots(g.Proxy), Names()));
    }

    /// <summary>"set" only says whether a proxy is configured, never where it points.</summary>
    internal static string ProxyKind(string? proxy) => proxy switch
    {
        null or "" or "system" => "system default",
        "none" => "none (direct connection)",
        _ => "custom proxy set",
    };

    public static string Format(Snapshot s)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### Umbra diagnostics");
        sb.AppendLine();
        sb.AppendLine($"- **Umbra:** {s.Version}, {(s.Portable ? "portable" : "installed")}, safe mode {OnOff(s.SafeMode)}");
        sb.AppendLine($"- **Windows:** {s.Windows}");
        sb.AppendLine($"- **WebView2 runtime:** {s.WebView2}");
        sb.AppendLine($"- **Monitors:** {(s.Monitors.Count == 0 ? "none found" : string.Join("; ", s.Monitors.Select(MonitorText)))}");
        sb.AppendLine($"- **GPU acceleration:** {OnOff(s.HardwareAcceleration)}");
        sb.AppendLine($"- **Proxy:** {s.Proxy}");
        sb.AppendLine($"- **Enabled widgets ({s.Widgets.Count}):** {(s.Widgets.Count == 0 ? "none" : string.Join(", ", s.Widgets.Select(w => $"{w.Id} {w.Version} ({w.Source})")))}");
        sb.AppendLine();
        sb.AppendLine($"<details><summary>Last {s.Log.Count} log lines</summary>");
        sb.AppendLine();
        sb.AppendLine("```text");
        foreach (var line in s.Log) sb.AppendLine(line.Replace("```", "'''"));
        sb.AppendLine("```");
        sb.AppendLine("</details>");
        return sb.ToString();
    }

    static string OnOff(bool on) => on ? "on" : "off";

    internal static string MonitorText(MonitorLine m) =>
        $"{m.Width}x{m.Height} at {Math.Round(m.Dpi * 100.0 / 96)}%{(m.Primary ? " (primary)" : "")}";

    /// <summary>"Windows 11 Pro 24H2 (build 26200.1234)". The registry still says "Windows 10" for Windows 11, so the build number decides.</summary>
    internal static string WindowsText(string? productName, string? displayVersion, string? build, int? revision)
    {
        var name = string.IsNullOrWhiteSpace(productName) ? "Windows" : productName.Trim();
        if (int.TryParse(build, out var b) && b >= 22000 && name.StartsWith("Windows 10", StringComparison.Ordinal))
            name = "Windows 11" + name["Windows 10".Length..];
        var text = string.IsNullOrWhiteSpace(displayVersion) ? name : $"{name} {displayVersion.Trim()}";
        if (!string.IsNullOrWhiteSpace(build)) text += $" (build {build}{(revision is { } r ? "." + r : "")})";
        return $"{text}, {RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}";
    }

    static string WindowsText()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return WindowsText(key?.GetValue("ProductName") as string, (key?.GetValue("DisplayVersion") ?? key?.GetValue("ReleaseId")) as string,
                key?.GetValue("CurrentBuild") as string, key?.GetValue("UBR") is int ubr ? ubr : null);
        }
        catch { return Environment.OSVersion.VersionString; }
    }

    static string WebView2Version()
    {
        try { return CoreWebView2Environment.GetAvailableBrowserVersionString() is { Length: > 0 } v ? v : "not installed"; }
        catch { return "not found"; }
    }

    static List<MonitorLine> Monitors() =>
        Screens.All().Select(m => new MonitorLine(m.Bounds.Width, m.Bounds.Height, MonitorDpi(m.Bounds), m.Primary)).ToList();

    /// <summary>Effective DPI of the monitor containing the rectangle's centre (96 when Windows can't say).</summary>
    static int MonitorDpi(Rectangle bounds) =>
        (int)Math.Round(Native.MonitorScale(new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2)) * 96);

    /// <summary>Folders to hide in the log, longest first so the most specific one wins.</summary>
    static List<(string Path, string Token)> Roots(string? proxy)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var roots = new List<(string, string)>
        {
            (AppPaths.AppDir, "<umbra>"),
            (appData, "%APPDATA%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%"),
        };
        // A custom or portable data folder gets its own name unless it already sits under %APPDATA%.
        if (!AppPaths.DataDir.StartsWith(appData, StringComparison.OrdinalIgnoreCase)) roots.Add((AppPaths.DataDir, "<data>"));
        if (proxy is { Length: > 0 } p && p is not ("system" or "none")) roots.Add((p, "<proxy>"));
        return roots;
    }

    /// <summary>The Windows user and machine names, replaced wherever they appear in a log line.</summary>
    static List<(string Name, string Token)> Names() =>
        new() { (Environment.UserName, "<user>"), (Environment.MachineName, "<machine>") };

    /// <summary>
    /// Replaces known folders (and any other <c>X:\Users\name</c>) with environment-variable style names, hides a configured
    /// proxy address, strips the query and fragment from every http(s) URL (they can carry keys and tokens), replaces the
    /// given user and machine names, and caps each line's length, so a pasted log doesn't reveal the Windows user name.
    /// </summary>
    internal static List<string> ScrubLog(IEnumerable<string> lines, IEnumerable<(string Path, string Token)> roots, IEnumerable<(string Name, string Token)>? names = null) =>
        LogScrub.Scrub(lines, roots, names);

    /// <summary>The bug template with the version and Windows build filled in (issue-form fields are prefilled by id in the query).</summary>
    internal static string IssueUrl(string repo, string version, string windows) =>
        $"https://github.com/{repo}/issues/new?template=bug.yml&version={Uri.EscapeDataString(version)}&windows={Uri.EscapeDataString(windows)}";

    /// <summary>The "Windows version and display scaling" prefill: Windows build plus the primary monitor's scaling.</summary>
    internal static string IssueWindows(Snapshot s) =>
        s.Monitors.Where(m => m.Primary).Select(m => (MonitorLine?)m).FirstOrDefault() is { } p
            ? $"{s.Windows}, {MonitorText(p)}" : s.Windows;
}
