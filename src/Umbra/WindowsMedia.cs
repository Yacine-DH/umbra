using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Windows.Graphics.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Umbra;

/// <summary><c>umbra.media</c> on Windows: the current System Media Transport Controls session (rules in <see cref="MediaService"/>).</summary>
static class WindowsMedia
{
    static readonly object Gate = new();
    static readonly Dictionary<string, string?> AppNames = new(StringComparer.OrdinalIgnoreCase);
    static string? _artKey, _art;

    static async Task<GlobalSystemMediaTransportControlsSession?> CurrentAsync()
    {
        var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        return mgr.GetCurrentSession();
    }

    /// <summary>The current session as { playing, status, title, artist, album, app, canPlay..., art? }; null when nothing is playing.</summary>
    public static async Task<JsonObject?> NowPlayingAsync()
    {
        var s = await CurrentAsync();
        if (s is null) return null;
        var props = await s.TryGetMediaPropertiesAsync();
        var pb = s.GetPlaybackInfo();
        var c = pb.Controls;
        var o = new JsonObject
        {
            ["status"] = pb.PlaybackStatus.ToString().ToLowerInvariant(),
            ["playing"] = pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            ["title"] = props.Title,
            ["artist"] = props.Artist,
            ["album"] = props.AlbumTitle,
            ["app"] = AppName(s.SourceAppUserModelId),
            ["canPlay"] = c.IsPlayEnabled || c.IsPlayPauseToggleEnabled,
            ["canPause"] = c.IsPauseEnabled || c.IsPlayPauseToggleEnabled,
            ["canNext"] = c.IsNextEnabled,
            ["canPrevious"] = c.IsPreviousEnabled,
        };
        try
        {
            if (props.Thumbnail is { } t)
            {
                using var stream = await t.OpenReadAsync();
                if (stream.Size is > 0 and <= MediaService.MaxArtSourceBytes)
                {
                    // Every widget polls this every few seconds: scale each picture once, keyed by the song and the art's size,
                    // so a new cover for the same song (players often send it a moment after the title) is still picked up.
                    var key = $"{s.SourceAppUserModelId}\n{props.Title}\n{props.Artist}\n{props.AlbumTitle}\n{stream.Size}";
                    string? art;
                    lock (Gate) art = key == _artKey ? _art : null;
                    if (art is null && await ScaleArtAsync(stream) is { } jpeg)
                    {
                        art = $"data:image/jpeg;base64,{Convert.ToBase64String(jpeg)}";
                        lock (Gate) { _artKey = key; _art = art; }
                    }
                    if (art is not null) o["art"] = art;
                }
            }
        }
        catch { /* art is optional; WinRT decode failures surface as many exception types */ }
        return o;
    }

    /// <summary>
    /// Any picture Windows can decode, as a JPEG no larger than <see cref="MediaService.ArtEdge"/> on its long side and
    /// <see cref="MediaService.MaxArtBytes"/> in all; null when it can't be decoded. Browsers hand over ~700 KB PNGs, which the
    /// old size cap simply dropped.
    /// </summary>
    internal static async Task<byte[]?> ScaleArtAsync(IRandomAccessStream source)
    {
        source.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(source);
        var (w, h) = MediaService.ArtSize(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
        if (w == 0) return null;
        var transform = new BitmapTransform { ScaledWidth = w, ScaledHeight = h, InterpolationMode = BitmapInterpolationMode.Fant };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        using var output = new InMemoryRandomAccessStream();
        var quality = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(0.88, Windows.Foundation.PropertyType.Single) };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, quality);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        if (output.Size is 0 or > MediaService.MaxArtBytes) return null;
        var bytes = new byte[output.Size];
        output.Seek(0);
        using var reader = new DataReader(output);
        await reader.LoadAsync((uint)output.Size);
        reader.ReadBytes(bytes);
        return MediaService.SniffImage(bytes) == "image/jpeg" ? bytes : null;
    }

    /// <summary>The app's display name from the Start menu's app list (shell:AppsFolder, so Firefox's hash id reads "Firefox"), cached per id; see <see cref="MediaService.AppLabel"/>.</summary>
    static string? AppName(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return null;
        lock (Gate)
            if (AppNames.TryGetValue(appId, out var known)) return known;
        var name = MediaService.AppLabel(appId, ShellDisplayName(appId));
        lock (Gate)
        {
            if (AppNames.Count > 64) AppNames.Clear();
            AppNames[appId] = name;
        }
        return name;
    }

    static string? ShellDisplayName(string appId)
    {
        if (appId.IndexOfAny(['\0', '\r', '\n']) >= 0 || appId.Length > 512) return null;
        try
        {
            var iid = typeof(IShellItem).GUID;
            if (SHCreateItemFromParsingName(@"shell:AppsFolder\" + appId, IntPtr.Zero, ref iid, out var item) != 0 || item is null) return null;
            try
            {
                if (item.GetDisplayName(0 /* SIGDN_NORMALDISPLAY */, out var p) != 0 || p == IntPtr.Zero) return null;
                try { return Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
            }
            finally { Marshal.ReleaseComObject(item); }
        }
        catch (COMException) { return null; }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem? item);

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint sigdn, out IntPtr name);
    }

    public static async Task<bool> ControlAsync(string command)
    {
        var s = await CurrentAsync();
        if (s is null) return false;
        return command switch
        {
            "play" => await s.TryPlayAsync(),
            "pause" => await s.TryPauseAsync(),
            "toggle" => await s.TryTogglePlayPauseAsync(),
            "next" => await s.TrySkipNextAsync(),
            "previous" => await s.TrySkipPreviousAsync(),
            _ => false,
        };
    }
}
