namespace Umbra;

/// <summary>
/// What an update adds to what is already installed: hosts, system metrics, media access, notifications, camera and
/// commands (new, or changed so their hash differs). Removed permissions are not listed. Pure, so it is unit tested;
/// <see cref="Community"/> refuses to replace an installed widget until the user accepted a non-empty diff.
/// </summary>
public sealed class PermissionDiff
{
    public IReadOnlyList<string> Hosts { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> System { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Media { get; init; } = Array.Empty<string>();
    public bool Notifications { get; init; }
    public bool Camera { get; init; }
    public bool SaveImages { get; init; }
    /// <summary>The update newly lets the user add their own scripts in Settings (nothing runs until the user writes one).</summary>
    public bool UserScripts { get; init; }
    /// <summary>Commands that are new or whose executable, arguments or limits changed (the stored approval no longer matches).</summary>
    public IReadOnlyList<CommandSpec> Commands { get; init; } = Array.Empty<CommandSpec>();

    public bool IsEmpty => Hosts.Count == 0 && System.Count == 0 && Media.Count == 0 && !Notifications && !Camera && !SaveImages && !UserScripts && Commands.Count == 0;

    /// <summary>The permissions of <paramref name="updated"/> that <paramref name="installed"/> does not have. A null installed manifest means everything is new.</summary>
    public static PermissionDiff Compute(WidgetManifest? installed, WidgetManifest updated)
    {
        var old = installed?.Permissions ?? new WidgetManifest.PermissionSpec();
        var now = updated.Permissions;
        var oldHashes = old.Commands.Select(c => c.Hash()).ToHashSet(StringComparer.Ordinal);
        return new PermissionDiff
        {
            Hosts = Added(old.Hosts, now.Hosts),
            System = Added(old.System, now.System),
            Media = Added(old.Media, now.Media),
            Notifications = now.Notifications && !old.Notifications,
            Camera = now.Camera && !old.Camera,
            SaveImages = now.SaveImages && !old.SaveImages,
            UserScripts = now.UserScripts && !old.UserScripts,
            Commands = now.Commands.Where(c => !oldHashes.Contains(c.Hash())).ToList(),
        };
    }

    static List<string> Added(IEnumerable<string> before, IEnumerable<string> after)
    {
        var had = before.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return after.Where(x => !had.Contains(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>One plain-English line per added permission, for the confirmation prompt and the CLI.</summary>
    public IReadOnlyList<string> Describe()
    {
        var lines = new List<string>();
        foreach (var h in Hosts) lines.Add($"Can connect to {h}");
        foreach (var s in System) lines.Add($"Can read {s} usage");
        foreach (var m in Media)
            lines.Add(m.Equals("control", StringComparison.OrdinalIgnoreCase) ? "Can control media playback (play, pause, next, previous)" : "Can read what media is playing");
        if (Notifications) lines.Add("Can show notifications");
        if (Camera) lines.Add("Can record timelapses with your webcam (only after you click it; a red dot shows while recording)");
        if (SaveImages) lines.Add(@"Can save pictures into Pictures\Umbra\<widget name>, or another folder you choose in Settings");
        if (UserScripts) lines.Add("Can run scripts you add yourself in Settings (you write and approve each one)");
        foreach (var c in Commands) lines.Add($"Can ask to run a program: {c.DisplayLine()} (you still approve it before it runs)");
        return lines;
    }
}

/// <summary>Thrown instead of replacing an installed widget when the update adds permissions the user has not accepted.</summary>
public sealed class PermissionsChangedException : InvalidOperationException
{
    public PermissionDiff Diff { get; }

    public PermissionsChangedException(string widgetName, string version, PermissionDiff diff)
        : base($"{widgetName} {version} wants new access: {string.Join("; ", diff.Describe())}")
    {
        Diff = diff;
    }
}
