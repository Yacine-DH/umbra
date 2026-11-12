using System.Reflection;
using System.Text.Json.Nodes;

namespace Umbra;

/// <summary>
/// What <see cref="Control"/> (and so umbractl, MCP and tests) needs from a running Umbra. The Windows <c>App</c>
/// implements it; another platform's host implements the same surface so automation behaves identically.
/// </summary>
interface IUmbraHost
{
    /// <summary>Runs <paramref name="work"/> on the UI thread and returns its result.</summary>
    Task<T> OnUi<T>(Func<Task<T>> work);

    Settings Settings { get; }
    List<WidgetManifest> Manifests { get; }
    CommandService Commands { get; }
    IUpdater Updater { get; }

    bool EditMode { get; }
    bool SafeMode { get; }
    bool WidgetsHidden { get; }
    bool HiddenByFullscreen { get; }
    bool Peeking { get; }
    bool SettingsOpen { get; }

    /// <summary>The connected monitors, in the order <c>status.monitors</c> numbers them.</summary>
    List<MonitorInfo> Monitors();
    /// <summary>How widget windows are tied to the desktop, for <c>status</c> ("x11", "none"); null on Windows, which has one way.</summary>
    string? PinBackend => null;
    /// <summary>Whether Umbra starts when the user signs in.</summary>
    bool StartupEnabled { get; set; }
    /// <summary>The text report for <c>umbractl diagnostics</c>.</summary>
    string Diagnostics();

    IEnumerable<IWidgetView> OpenWindows { get; }
    IWidgetView? Window(string instance);
    ISettingsView ShowSettings(bool background = false);
    (WidgetManifest Manifest, string Instance)? Resolve(string id);

    void SaveSettings();
    void SyncWindows();
    void SettingsChangedElsewhere();
    void RefreshEditPills();
    void ReloadWidgets();
    void SetTheme(string id);
    void SetEditMode(bool on);
    void SetSafeMode(bool on);
    void Peek();
    void QuitSoon();

    JsonObject DescribeHotkeys();
    void SetHotkey(string action, string keys);
    void ResetHotkeys();

    JsonArray LayoutsJson();
    void SaveLayout(string name);
    int ApplyLayout(string name);
    void RenameLayout(string name, string newName);
    bool DeleteLayout(string name);

    /// <summary>Opens the host-drawn review for a widget package (.umbrawidget); nothing is installed without a click on it.</summary>
    Task<bool> OpenPackageAsync(string path);

    /// <summary>True when the user approved this widget for the camera (Data &amp; privacy).</summary>
    bool CameraApproved(string widgetId);

    JsonArray GroupsJson();
    string CreateGroup(string name, string? instance);
    void JoinGroup(string groupId, string instance);
    void LeaveGroup(string instance);
    void ActivateTab(string groupId, string instance);
    void StepTab(string groupId, int direction);
    void ReorderTab(string groupId, string instance, int index);
    void ConfigureGroup(string groupId, string? name, int? cycleSeconds);
    void DeleteGroup(string groupId);
    void SetTabIcon(string instance, string? icon);

    void ResetPosition(string instance);
    string AddInstance(WidgetManifest m);
    void RemoveInstance(string instance);
    void CloseWidget(string id);
    void ForgetInstances(string widgetId);
    Task<UpdateAllResult> UpdateAllWidgetsAsync(Action<int, int, string>? progress = null);
    void RestoreBackup(Backup.Parsed backup);
}

/// <summary>One open widget window, as automation sees it.</summary>
interface IWidgetView
{
    string Instance { get; }
    WidgetManifest Manifest { get; }
    int Left { get; }
    int Top { get; }
    int Width { get; }
    int Height { get; }
    void PushSettings();
    void MoveToMonitor(string monitor);
    void RefreshData();
    void StoreChanged(string? key);
    Task<byte[]> CaptureAsync();
}

/// <summary>The Settings window, as automation sees it.</summary>
interface ISettingsView
{
    Task ShowPageAsync(string page);
    Task<byte[]> CaptureAsync(string page);
    void Close();
}

/// <summary>The app update check, as automation sees it (installing is never exposed to automation).</summary>
interface IUpdater
{
    ReleaseInfo? Available { get; }
    Task CheckAsync(bool manual, bool download = false);
    JsonObject ToJson();
}

/// <summary>Facts about the running build.</summary>
static class AppInfo
{
    /// <summary>The product version without build metadata, e.g. "1.7.2" (Directory.Build.props versions every assembly alike).</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";
}
