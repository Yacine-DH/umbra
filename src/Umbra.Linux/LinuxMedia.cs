using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Umbra.Linux;

/// <summary>
/// umbra.media on Linux: the MPRIS players on the session bus (<c>org.mpris.MediaPlayer2.*</c>: Firefox, Chromium,
/// Spotify, Rhythmbox, VLC...), the counterpart of <c>WindowsMedia</c>. Same JSON shape and the same rules
/// (<see cref="MediaService"/>, checked by the widget window). Album art is read only from <c>file://</c> URLs:
/// fetching an <c>https://</c> art URL (such as Spotify's CDN covers) would be a network request to a host the widget
/// never declared in <c>permissions.network</c>, so <c>https://</c> art URLs are never fetched.
/// </summary>
static partial class LinuxMedia
{
    const string Prefix = "org.mpris.MediaPlayer2.";
    const string PlayerIface = "org.mpris.MediaPlayer2.Player";
    const string Path = "/org/mpris/MediaPlayer2";
    const string PixbufLib = "libgdk_pixbuf-2.0.so.0";
    const string GObjectLib = "libgobject-2.0.so.0";
    const string GLibLib = "libglib-2.0.so.0";
    const int GdkColorspaceRgb = 0;
    const int GdkInterpBilinear = 2;

    static readonly object Gate = new();

    /// <summary>The player that last reported Playing: preferred when nothing plays now (the "most recent" one).</summary>
    static string? _lastPlaying;
    static string? _artKey, _art;

    [LibraryImport(PixbufLib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr gdk_pixbuf_new_from_file(string filename, ref IntPtr error);

    [LibraryImport(PixbufLib)]
    private static partial IntPtr gdk_pixbuf_apply_embedded_orientation(IntPtr src);

    [LibraryImport(PixbufLib)]
    private static partial int gdk_pixbuf_get_width(IntPtr pixbuf);

    [LibraryImport(PixbufLib)]
    private static partial int gdk_pixbuf_get_height(IntPtr pixbuf);

    [LibraryImport(PixbufLib)]
    private static partial IntPtr gdk_pixbuf_new(int colorspace, int hasAlpha, int bitsPerSample, int width, int height);

    [LibraryImport(PixbufLib)]
    private static partial void gdk_pixbuf_composite_color(IntPtr src, IntPtr dest, int destX, int destY, int destWidth, int destHeight,
        double offsetX, double offsetY, double scaleX, double scaleY, int interpType, int overallAlpha,
        int checkX, int checkY, int checkSize, uint color1, uint color2);

    [LibraryImport(PixbufLib, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int gdk_pixbuf_save_to_bufferv(IntPtr pixbuf, out IntPtr buffer, out nuint bufferSize, string type,
        IntPtr* optionKeys, IntPtr* optionValues, ref IntPtr error);

    [LibraryImport(GObjectLib)]
    private static partial void g_object_unref(IntPtr obj);

    [LibraryImport(GLibLib)]
    private static partial void g_free(IntPtr mem);

    [LibraryImport(GLibLib)]
    private static partial void g_error_free(IntPtr error);

    /// <summary>
    /// Which player counts: one that is playing (the last one seen playing first, if several are), else the last one seen
    /// playing, else a paused one, else the first. Pure, so the choice is unit tested.
    /// </summary>
    public static string? Choose(IReadOnlyList<(string Name, string Status)> players, string? lastPlaying)
    {
        if (players.Count == 0) return null;
        var playing = players.Where(p => p.Status == "Playing").Select(p => p.Name).ToList();
        if (playing.Count > 0) return playing.Contains(lastPlaying ?? "") ? lastPlaying : playing[0];
        if (lastPlaying is not null && players.Any(p => p.Name == lastPlaying)) return lastPlaying;
        return players.FirstOrDefault(p => p.Status == "Paused").Name ?? players[0].Name;
    }

    /// <summary>
    /// The umbra.media "get" answer from MPRIS values: PlaybackStatus ("Playing"/"Paused"/"Stopped"), the Metadata entries
    /// (xesam:title, xesam:artist as a list, xesam:album), the Can* properties and the player's Identity.
    /// </summary>
    public static JsonObject Map(string status, string? title, IReadOnlyList<string> artists, string? album, string? app,
        bool canPlay, bool canPause, bool canNext, bool canPrevious) => new()
    {
        ["status"] = status.ToLowerInvariant(),
        ["playing"] = status == "Playing",
        ["title"] = title ?? "",
        ["artist"] = string.Join(", ", artists.Where(a => a.Length > 0)),
        ["album"] = album ?? "",
        ["app"] = app ?? "",
        ["canPlay"] = canPlay,
        ["canPause"] = canPause,
        ["canNext"] = canNext,
        ["canPrevious"] = canPrevious,
    };

    /// <summary>
    /// A local art file as a data URL: only <c>file://</c> (no network), at most <see cref="MediaService.MaxArtBytes"/>, a real
    /// image. Files already within <see cref="MediaService.MaxArtBytes"/> are passed as-is; larger files up to
    /// <see cref="MediaService.MaxArtSourceBytes"/> (browsers hand out ~700 KB–2 MB PNGs) are scaled down to at most
    /// <see cref="MediaService.ArtEdge"/> px on the long side and encoded as JPEG, cached per file so polling does not re-encode.
    /// </summary>
    public static string? ArtDataUrl(string? artUrl)
    {
        if (artUrl is null || !Uri.TryCreate(artUrl, UriKind.Absolute, out var u) || !u.IsFile) return null;
        try
        {
            var info = new FileInfo(u.LocalPath);
            if (!info.Exists || info.Length is 0 or > MediaService.MaxArtSourceBytes || info.LinkTarget is not null) return null;
            var key = $"{info.FullName}\n{info.Length}\n{info.LastWriteTimeUtc.Ticks}";
            lock (Gate) if (key == _artKey) return _art;
            string? art = null;
            if (info.Length <= MediaService.MaxArtBytes)
            {
                var bytes = File.ReadAllBytes(info.FullName);
                if (MediaService.SniffImage(bytes) is { } mime)
                    art = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
            }
            else if (ScaleArt(info.FullName) is { } jpeg)
            {
                art = $"data:image/jpeg;base64,{Convert.ToBase64String(jpeg)}";
            }
            if (art is not null) lock (Gate) { _artKey = key; _art = art; }
            return art;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Any local PNG or JPEG <c>GdkPixbuf</c> can decode, scaled to at most <see cref="MediaService.ArtEdge"/> on its long side and
    /// flattened to opaque RGB JPEG within <see cref="MediaService.MaxArtBytes"/>; null when it is not a PNG/JPEG or cannot be decoded.
    /// </summary>
    internal static unsafe byte[]? ScaleArt(string path)
    {
        if (!OperatingSystem.IsLinux()) return null;
        Span<byte> header = stackalloc byte[12];
        using (var fs = File.OpenRead(path))
            if (fs.Read(header) < header.Length || MediaService.SniffImage(header) is null) return null;
        var err = IntPtr.Zero;
        var raw = gdk_pixbuf_new_from_file(path, ref err);
        if (err != IntPtr.Zero) g_error_free(err);
        if (raw == IntPtr.Zero) return null;
        var oriented = gdk_pixbuf_apply_embedded_orientation(raw);
        g_object_unref(raw);
        if (oriented == IntPtr.Zero) return null;
        try
        {
            var srcW = gdk_pixbuf_get_width(oriented);
            var srcH = gdk_pixbuf_get_height(oriented);
            if (srcW <= 0 || srcH <= 0) return null;
            var (w, h) = MediaService.ArtSize((uint)srcW, (uint)srcH);
            if (w == 0 || h == 0) return null;
            var rgb = gdk_pixbuf_new(GdkColorspaceRgb, 0, 8, (int)w, (int)h);
            if (rgb == IntPtr.Zero) return null;
            try
            {
                gdk_pixbuf_composite_color(oriented, rgb, 0, 0, (int)w, (int)h, 0, 0,
                    (double)w / srcW, (double)h / srcH, GdkInterpBilinear, 255, 0, 0, 16, 0, 0);
                fixed (byte* k = "quality\0"u8, v = "88\0"u8)
                {
                    var keys = stackalloc IntPtr[2] { (IntPtr)k, IntPtr.Zero };
                    var vals = stackalloc IntPtr[2] { (IntPtr)v, IntPtr.Zero };
                    var saveErr = IntPtr.Zero;
                    var ok = gdk_pixbuf_save_to_bufferv(rgb, out var buf, out var size, "jpeg", keys, vals, ref saveErr);
                    if (saveErr != IntPtr.Zero) g_error_free(saveErr);
                    if (ok == 0 || buf == IntPtr.Zero) return null;
                    try
                    {
                        if (size is 0 or > (nuint)MediaService.MaxArtBytes) return null;
                        var bytes = new byte[(int)size];
                        Marshal.Copy(buf, bytes, 0, bytes.Length);
                        return MediaService.SniffImage(bytes) == "image/jpeg" ? bytes : null;
                    }
                    finally { g_free(buf); }
                }
            }
            finally { g_object_unref(rgb); }
        }
        finally { g_object_unref(oriented); }
    }

    static async Task<List<(string Name, string Status)>> PlayersAsync(IntPtr bus)
    {
        using var reply = await DBus.CallAsync(bus, "org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "ListNames");
        using var names = reply[0];
        var list = new List<(string, string)>();
        for (var i = 0; i < names.Count; i++)
        {
            using var n = names[i];
            var name = n.Str;
            if (!name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            try
            {
                using var r = await DBus.CallAsync(bus, name, Path, "org.freedesktop.DBus.Properties", "Get",
                    $"({V.Str(PlayerIface)}, {V.Str("PlaybackStatus")})", 1500);
                using var v = r[0];
                list.Add((name, v.AsString ?? "Stopped"));
            }
            catch (DBusException) { } // a player that doesn't answer is skipped
        }
        return list;
    }

    static async Task<string?> CurrentAsync(IntPtr bus)
    {
        var players = await PlayersAsync(bus);
        var chosen = Choose(players, _lastPlaying);
        if (chosen is not null && players.Any(p => p.Name == chosen && p.Status == "Playing")) _lastPlaying = chosen;
        return chosen;
    }

    /// <summary>The current player as { status, playing, title, artist, album, app, canPlay..., art? }; null when there is none.</summary>
    public static async Task<JsonObject?> NowPlayingAsync()
    {
        var bus = DBus.Session;
        if (bus == IntPtr.Zero) return null;
        var name = await CurrentAsync(bus);
        if (name is null) return null;
        using var all = await DBus.CallAsync(bus, name, Path, "org.freedesktop.DBus.Properties", "GetAll", $"({V.Str(PlayerIface)},)");
        using var dictV = all[0];
        var props = dictV.Dict();
        bool B(string k) => props.TryGetValue(k, out var v) && v.Type == "b" && v.Bool;
        var meta = props.TryGetValue("Metadata", out var m) ? m.Dict() : new();
        string? S(string k) => meta.TryGetValue(k, out var v) ? v.AsString : null;
        var artists = new List<string>();
        if (meta.TryGetValue("xesam:artist", out var av))
        {
            if (av.Type == "as") for (var i = 0; i < av.Count; i++) { using var a = av[i]; artists.Add(a.Str); }
            else if (av.AsString is { } one) artists.Add(one);
        }
        string? identity = null;
        try
        {
            using var r = await DBus.CallAsync(bus, name, Path, "org.freedesktop.DBus.Properties", "Get", $"({V.Str("org.mpris.MediaPlayer2")}, {V.Str("Identity")})", 1500);
            using var idV = r[0];
            identity = idV.AsString;
        }
        catch (DBusException) { }
        var status = props.TryGetValue("PlaybackStatus", out var st) ? st.AsString ?? "Stopped" : "Stopped";
        var o = Map(status, S("xesam:title"), artists, S("xesam:album"), identity ?? name[Prefix.Length..],
            B("CanPlay"), B("CanPause"), B("CanGoNext"), B("CanGoPrevious"));
        if (ArtDataUrl(S("mpris:artUrl")) is { } art) o["art"] = art;
        foreach (var v in props.Values) v.Dispose();
        foreach (var v in meta.Values) v.Dispose();
        return o;
    }

    /// <summary>play, pause, toggle, next or previous on the current player. False when there is none.</summary>
    public static async Task<bool> ControlAsync(string command)
    {
        var bus = DBus.Session;
        if (bus == IntPtr.Zero) return false;
        var name = await CurrentAsync(bus);
        if (name is null) return false;
        var method = command switch
        {
            "play" => "Play", "pause" => "Pause", "toggle" => "PlayPause", "next" => "Next", "previous" => "Previous",
            _ => throw new ArgumentException("Unknown media command"),
        };
        using var _ = await DBus.CallAsync(bus, name, Path, PlayerIface, method);
        return true;
    }
}
