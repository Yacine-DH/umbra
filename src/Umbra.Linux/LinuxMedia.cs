using System.Text.Json.Nodes;

namespace Umbra.Linux;

/// <summary>
/// umbra.media on Linux: the MPRIS players on the session bus (<c>org.mpris.MediaPlayer2.*</c>: Firefox, Chromium,
/// Spotify, Rhythmbox, VLC...), the counterpart of <c>WindowsMedia</c>. Same JSON shape and the same rules
/// (<see cref="MediaService"/>, checked by the widget window). Album art is read only from <c>file://</c> URLs:
/// fetching an <c>https://</c> art URL would be a network request the widget never declared, so none is passed.
/// </summary>
static class LinuxMedia
{
    const string Prefix = "org.mpris.MediaPlayer2.";
    const string PlayerIface = "org.mpris.MediaPlayer2.Player";
    const string Path = "/org/mpris/MediaPlayer2";

    /// <summary>The player that last reported Playing: preferred when nothing plays now (the "most recent" one).</summary>
    static string? _lastPlaying;

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

    /// <summary>A local art file as a data URL: only file:// (no network), at most <see cref="MediaService.MaxArtBytes"/>, a real image.</summary>
    public static string? ArtDataUrl(string? artUrl)
    {
        if (artUrl is null || !Uri.TryCreate(artUrl, UriKind.Absolute, out var u) || !u.IsFile) return null;
        try
        {
            var info = new FileInfo(u.LocalPath);
            if (!info.Exists || info.Length is 0 or > MediaService.MaxArtBytes || info.LinkTarget is not null) return null;
            var bytes = File.ReadAllBytes(info.FullName);
            return MediaService.SniffImage(bytes) is { } mime ? $"data:{mime};base64,{Convert.ToBase64String(bytes)}" : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
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
