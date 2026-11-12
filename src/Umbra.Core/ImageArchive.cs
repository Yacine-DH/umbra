using System.Text.Json;

namespace Umbra;

/// <summary>
/// <c>umbra.images</c> (permission <c>saveImages</c>): a widget may hand the host a PNG to keep in its picture folder
/// (Pictures\Umbra\&lt;widget name&gt;, or another folder the user picked with the host's own folder dialog). The page never
/// supplies a path or a file name, and never reads anything back.
/// Pure rules live here so they are unit tested; the dialog and the click gate are in the Windows host.
/// </summary>
public static class ImageArchive
{
    public const string DataUrlPrefix = "data:image/png;base64,";
    /// <summary>Largest picture the host writes (decoded bytes).</summary>
    public const int MaxBytes = 1_000_000;
    /// <summary>Pictures one widget copy may write per 10 minutes (a widget draining a full gallery is fine; a loop is not).</summary>
    public const int MaxPerWindow = 30;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    static readonly byte[] PngMagic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>The PNG bytes of a <c>data:image/png;base64,...</c> string, or throws <see cref="ImageArchiveException"/>.</summary>
    public static byte[] DecodePng(string? dataUrl)
    {
        if (dataUrl is null || !dataUrl.StartsWith(DataUrlPrefix, StringComparison.Ordinal))
            throw new ImageArchiveException("Only PNG pictures can be saved");
        var b64 = dataUrl.AsSpan(DataUrlPrefix.Length);
        if (b64.Length == 0 || b64.Length > MaxBytes / 3 * 4 + 8) throw new ImageArchiveException("The picture is too large");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64.ToString()); }
        catch (FormatException) { throw new ImageArchiveException("The picture is not valid"); }
        if (bytes.Length > MaxBytes) throw new ImageArchiveException("The picture is too large");
        if (bytes.Length < PngMagic.Length + 8 || !bytes.AsSpan(0, PngMagic.Length).SequenceEqual(PngMagic))
            throw new ImageArchiveException("The picture is not a PNG");
        return bytes;
    }

    /// <summary>A file name the host chooses: "&lt;prefix&gt; 2026-11-11 14.30.05.png", with " (2)", " (3)"... when taken. <paramref name="exists"/> checks the folder.</summary>
    public static string FileName(string prefix, DateTime now, Func<string, bool> exists)
    {
        var stem = $"{CleanName(prefix)} {now:yyyy-MM-dd HH.mm.ss}";
        var name = stem + ".png";
        for (var n = 2; exists(name); n++)
        {
            if (n > 999) throw new ImageArchiveException("Too many pictures with the same time");
            name = $"{stem} ({n}).png";
        }
        return name;
    }

    /// <summary>A widget name reduced to letters, digits, spaces and dashes (at most 40), safe as a file or folder name; "Umbra" when nothing is left.</summary>
    public static string CleanName(string? name)
    {
        var clean = new string((name ?? "").Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-').ToArray()).Trim();
        if (clean.Length == 0) clean = "Umbra";
        if (clean.Length > 40) clean = clean[..40].Trim();
        return clean;
    }

    /// <summary>What the page may learn about a folder: its last segment, never the full path.</summary>
    public static string Label(string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return "";
        var trimmed = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }
}

/// <summary>A refused save, with a sentence that is safe to show the page.</summary>
public sealed class ImageArchiveException(string message) : Exception(message);

/// <summary>
/// Where a widget's saved pictures go: <c>&lt;defaultRoot&gt;\&lt;widget name&gt;</c> (Pictures\Umbra\Hearth), created on the first
/// save, unless the user picked another folder in the host's dialog (Settings > My widgets, or the widget's own button right
/// after a click). The pick is kept in %APPDATA%\Umbra\image-folders.json (not settings.json, so no page can write it) and
/// cleared when the widget stops declaring <c>saveImages</c>. The page names neither folder.
/// </summary>
public sealed class ImageFolders(string path, string? defaultRoot = null)
{
    static ImageFolders? _shared;
    public static ImageFolders Shared => _shared ??= new(Path.Combine(AppPaths.DataDir, "image-folders.json"), PicturesRoot());

    /// <summary>Pictures\Umbra, or null where the system has no Pictures folder (then a folder has to be picked).</summary>
    static string? PicturesRoot()
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return string.IsNullOrEmpty(pictures) ? null : Path.Combine(pictures, "Umbra");
    }

    /// <summary>The default folder for a widget (it may not exist yet), or null without a Pictures folder.</summary>
    public string? Default(string widgetName) => defaultRoot is null ? null : Path.Combine(defaultRoot, ImageArchive.CleanName(widgetName));

    /// <summary>Where the next picture goes: the picked folder while it exists, otherwise the default.</summary>
    public string? Resolve(string widgetId, string widgetName) => Get(widgetId) ?? Default(widgetName);

    readonly object _gate = new();
    readonly Dictionary<string, List<DateTime>> _recent = new();

    Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex) { Log.Write($"image-folders.json unreadable: {ex.Message}"); }
        return new();
    }

    /// <summary>The folder the user picked for this widget id, or null (none picked, or it is gone).</summary>
    public string? Get(string widgetId)
    {
        lock (_gate)
            return Load().TryGetValue(widgetId, out var f) && Path.IsPathFullyQualified(f) && Directory.Exists(f) ? f : null;
    }

    /// <summary>Only the host's folder dialog calls this, with a folder the user picked.</summary>
    public void Set(string widgetId, string folder)
    {
        lock (_gate)
        {
            var all = Load();
            all[widgetId] = folder;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(all));
        }
    }

    /// <summary>Forget the folder (Settings, removal, or the widget stopped declaring saveImages).</summary>
    public void Clear(string widgetId)
    {
        lock (_gate)
        {
            var all = Load();
            if (all.Remove(widgetId)) File.WriteAllText(path, JsonSerializer.Serialize(all));
        }
    }

    /// <summary>Forget every folder except those of <paramref name="keep"/> (widgets that still declare saveImages).</summary>
    public void ClearExcept(IEnumerable<string> keep)
    {
        lock (_gate)
        {
            var all = Load();
            var set = keep.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var gone = all.Keys.Where(k => !set.Contains(k)).ToList();
            if (gone.Count == 0) return;
            foreach (var k in gone) all.Remove(k);
            File.WriteAllText(path, JsonSerializer.Serialize(all));
        }
    }

    /// <summary>
    /// Writes one picture into the widget's folder (<see cref="Resolve"/>, created when missing) under a host-chosen name and
    /// returns that name. Refuses without any folder, over the rate limit, or for anything that is not a small PNG.
    /// </summary>
    public string Save(string widgetId, string widgetName, string? dataUrl, DateTime? now = null, Func<string, long>? freeBytes = null)
    {
        var folder = Resolve(widgetId, widgetName) ?? throw new ImageArchiveException("Choose a folder first");
        var bytes = ImageArchive.DecodePng(dataUrl);
        var at = now ?? DateTime.Now;
        lock (_gate)
        {
            if (!_recent.TryGetValue(widgetId, out var list)) _recent[widgetId] = list = new();
            list.RemoveAll(t => at - t > ImageArchive.Window);
            if (list.Count >= ImageArchive.MaxPerWindow) throw new ImageArchiveException("Too many pictures saved in a short time");
            Directory.CreateDirectory(folder);
            var free = freeBytes?.Invoke(folder) ?? new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace;
            if (free < bytes.Length + 10_000_000L) throw new ImageArchiveException("The folder's drive is almost full");
            var name = ImageArchive.FileName(widgetName, at, n => File.Exists(Path.Combine(folder, n)));
            File.WriteAllBytes(Path.Combine(folder, name), bytes);
            list.Add(at);
            return name;
        }
    }
}
