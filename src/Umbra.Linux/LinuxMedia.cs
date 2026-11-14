using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

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

    /// <summary>Largest width or height (px) accepted before decoding a cover (guards against decompression bombs).</summary>
    public const int MaxArtDimension = 8192;
    /// <summary>Largest pixel count (width * height) accepted before decoding a cover (~160 MB RGBA upper bound).</summary>
    public const long MaxArtPixels = 40_000_000L;

    const int O_NONBLOCK = 0x800, O_CLOEXEC = 0x80000, AT_EMPTY_PATH = 0x1000;
    const uint STATX_TYPE = 0x1, STATX_INO = 0x100, STATX_SIZE = 0x200, STATX_MTIME = 0x40;
    const uint S_IFMT = 0xF000, S_IFREG = 0x8000;
    static int ONoFollow => RuntimeInformation.ProcessArchitecture is Architecture.Arm or Architecture.Arm64 ? 0x8000 : 0x20000;

    static readonly object Gate = new();

    /// <summary>The player that last reported Playing: preferred when nothing plays now (the "most recent" one).</summary>
    static string? _lastPlaying;
    static string? _artKey, _art;

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string path, int flags);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int statx(int dirfd, string path, int flags, uint mask, [Out] byte[] buf);

    [LibraryImport(PixbufLib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr gdk_pixbuf_loader_new_with_type(string imageType, ref IntPtr error);

    [LibraryImport(PixbufLib)]
    private static unsafe partial int gdk_pixbuf_loader_write(IntPtr loader, byte* buf, nuint count, ref IntPtr error);

    [LibraryImport(PixbufLib)]
    private static partial int gdk_pixbuf_loader_close(IntPtr loader, ref IntPtr error);

    [LibraryImport(PixbufLib)]
    private static partial IntPtr gdk_pixbuf_loader_get_pixbuf(IntPtr loader);

    [LibraryImport(PixbufLib)]
    private static partial void gdk_pixbuf_loader_set_size(IntPtr loader, int width, int height);

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

    [LibraryImport(GObjectLib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nuint g_signal_connect_data(IntPtr instance, string detailedSignal, IntPtr cHandler,
        IntPtr data, IntPtr destroyData, int connectFlags);

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
    /// PNG or JPEG. Opened once without following a final symlink or blocking on a FIFO, checked for decompression bombs, and
    /// when larger than <see cref="MediaService.MaxArtBytes"/> (up to <see cref="MediaService.MaxArtSourceBytes"/>) decoded from
    /// memory through the pinned PNG/JPEG <c>GdkPixbufLoader</c> and scaled down to JPEG. Both successes and failures are cached
    /// per file so polling does not re-read or re-decode.
    /// </summary>
    public static string? ArtDataUrl(string? artUrl)
    {
        if (artUrl is null || !Uri.TryCreate(artUrl, UriKind.Absolute, out var u) || !u.IsFile) return null;
        try
        {
            if (OpenArtFile(u.LocalPath) is not { } opened) return null;
            using var handle = opened.Handle;
            lock (Gate) if (opened.Key == _artKey) return _art;
            var bytes = ReadBounded(handle, opened.Length);
            string? art = null;
            if (bytes is not null && MediaService.SniffImage(bytes) is { } mime && !PngDimensionsTooBig(bytes))
            {
                if (bytes.Length <= MediaService.MaxArtBytes)
                    art = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
                else if (ScaleArt(bytes) is { } jpeg)
                    art = $"data:image/jpeg;base64,{Convert.ToBase64String(jpeg)}";
            }
            lock (Gate) { _artKey = opened.Key; _art = art; }
            return art;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    static (SafeFileHandle Handle, int Length, string Key)? OpenArtFile(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null) return null;
        if (OperatingSystem.IsLinux())
        {
            var handle = new SafeFileHandle(open(path, O_NONBLOCK | O_CLOEXEC | ONoFollow), ownsHandle: true);
            if (handle.IsInvalid) { handle.Dispose(); return null; }
            var stx = new byte[256];
            if (statx((int)handle.DangerousGetHandle(), "", AT_EMPTY_PATH, STATX_TYPE | STATX_INO | STATX_SIZE | STATX_MTIME, stx) != 0
                || (BitConverter.ToUInt16(stx, 28) & S_IFMT) != S_IFREG)
            {
                handle.Dispose();
                return null;
            }
            var size = BitConverter.ToUInt64(stx, 40);
            if (size is 0 or > MediaService.MaxArtSourceBytes) { handle.Dispose(); return null; }
            var ino = BitConverter.ToUInt64(stx, 32);
            var mtimeSec = BitConverter.ToInt64(stx, 112);
            var mtimeNsec = BitConverter.ToUInt32(stx, 120);
            return (handle, (int)size, $"{path}\n{ino}\n{size}\n{mtimeSec}:{mtimeNsec}");
        }
        if (!info.Exists || info.Length is 0 or > MediaService.MaxArtSourceBytes) return null;
        var fs = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return (fs.SafeFileHandle, (int)info.Length, $"{info.FullName}\n{info.Length}\n{info.LastWriteTimeUtc.Ticks}");
    }

    static byte[]? ReadBounded(SafeFileHandle handle, int expectedLength)
    {
        using var src = new FileStream(handle, FileAccess.Read, bufferSize: 4096, isAsync: false);
        var buf = new byte[expectedLength];
        var total = 0;
        int n;
        while (total < buf.Length && (n = src.Read(buf, total, buf.Length - total)) > 0) total += n;
        if (total == 0 || src.ReadByte() != -1) return null; // grew past the stat size while reading
        return total == buf.Length ? buf : buf[..total];
    }

    static bool PngDimensionsTooBig(ReadOnlySpan<byte> b)
    {
        if (b.Length < 24 || b[0] != 0x89 || b[12] != 'I' || b[13] != 'H' || b[14] != 'D' || b[15] != 'R') return false;
        var w = BinaryPrimitives.ReadUInt32BigEndian(b[16..]);
        var h = BinaryPrimitives.ReadUInt32BigEndian(b[20..]);
        return w == 0 || h == 0 || w > MaxArtDimension || h > MaxArtDimension || (ulong)w * h > (ulong)MaxArtPixels;
    }

    /// <summary>Reads a local PNG or JPEG once and scales it with <see cref="ScaleArt(ReadOnlySpan{byte})"/>.</summary>
    internal static byte[]? ScaleArt(string path)
    {
        try
        {
            if (OpenArtFile(path) is not { } opened) return null;
            using var handle = opened.Handle;
            var bytes = ReadBounded(handle, opened.Length);
            return bytes is null ? null : ScaleArt(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Scales an in-memory PNG or JPEG to at most <see cref="MediaService.ArtEdge"/> on its long side and encodes as opaque RGB
    /// JPEG within <see cref="MediaService.MaxArtBytes"/>. Uses <c>gdk_pixbuf_loader_new_with_type</c> so only the PNG or JPEG
    /// loader can run, and caps pixel dimensions in <c>size-prepared</c> before pixel allocation.
    /// </summary>
    internal static unsafe byte[]? ScaleArt(ReadOnlySpan<byte> source)
    {
        if (!OperatingSystem.IsLinux() || source.Length is 0 or > MediaService.MaxArtSourceBytes) return null;
        if (MediaService.SniffImage(source) is not { } mime || PngDimensionsTooBig(source)) return null;
        var type = mime == "image/png" ? "png" : "jpeg";
        var err = IntPtr.Zero;
        var loader = gdk_pixbuf_loader_new_with_type(type, ref err);
        if (err != IntPtr.Zero) g_error_free(err);
        if (loader == IntPtr.Zero) return null;
        try
        {
            var tooBig = 0;
            g_signal_connect_data(loader, "size-prepared",
                (IntPtr)(delegate* unmanaged<IntPtr, int, int, IntPtr, void>)&OnSizePrepared, (IntPtr)(&tooBig), IntPtr.Zero, 0);
            fixed (byte* srcPtr = source)
            {
                var wErr = IntPtr.Zero;
                var wOk = gdk_pixbuf_loader_write(loader, srcPtr, (nuint)source.Length, ref wErr);
                if (wErr != IntPtr.Zero) g_error_free(wErr);
                var cErr = IntPtr.Zero;
                var cOk = gdk_pixbuf_loader_close(loader, ref cErr);
                if (cErr != IntPtr.Zero) g_error_free(cErr);
                if (wOk == 0 || cOk == 0 || tooBig != 0) return null;
            }
            var raw = gdk_pixbuf_loader_get_pixbuf(loader); // owned by loader; unreffing loader frees it
            if (raw == IntPtr.Zero) return null;
            var oriented = gdk_pixbuf_apply_embedded_orientation(raw);
            if (oriented == IntPtr.Zero) return null;
            try
            {
                var srcW = gdk_pixbuf_get_width(oriented);
                var srcH = gdk_pixbuf_get_height(oriented);
                if (srcW <= 0 || srcH <= 0 || srcW > MaxArtDimension || srcH > MaxArtDimension || (long)srcW * srcH > MaxArtPixels)
                    return null;
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
        finally { g_object_unref(loader); }
    }

    [UnmanagedCallersOnly]
    static unsafe void OnSizePrepared(IntPtr loader, int width, int height, IntPtr userData)
    {
        if (width <= 0 || height <= 0 || width > MaxArtDimension || height > MaxArtDimension || (long)width * height > MaxArtPixels)
        {
            *(int*)userData = 1;
            gdk_pixbuf_loader_set_size(loader, 1, 1);
        }
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
        var artUrl = S("mpris:artUrl");
        foreach (var v in props.Values) v.Dispose();
        foreach (var v in meta.Values) v.Dispose();
        if (artUrl is not null && await Task.Run(() => ArtDataUrl(artUrl)) is { } art) o["art"] = art;
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
