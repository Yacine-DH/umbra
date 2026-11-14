namespace Umbra.Linux.Tests;

/// <summary>umbra.media on Linux (#190): which MPRIS player counts, the answer's shape and the album-art policy.</summary>
public sealed class MediaTests
{
    [Fact]
    public void A_playing_player_wins_and_the_last_one_playing_breaks_ties()
    {
        Assert.Equal("b", LinuxMedia.Choose([("a", "Paused"), ("b", "Playing")], null));
        Assert.Equal("c", LinuxMedia.Choose([("b", "Playing"), ("c", "Playing")], "c"));
        Assert.Equal("a", LinuxMedia.Choose([("a", "Paused"), ("b", "Stopped")], "a"));
        Assert.Equal("b", LinuxMedia.Choose([("a", "Stopped"), ("b", "Paused")], "gone"));
        Assert.Equal("a", LinuxMedia.Choose([("a", "Stopped")], null));
        Assert.Null(LinuxMedia.Choose([], "a"));
    }

    [Fact]
    public void Metadata_maps_to_the_windows_shape()
    {
        var o = LinuxMedia.Map("Paused", "Song", ["A", "", "B"], null, "Firefox", true, false, true, false);
        Assert.Equal("paused", (string)o["status"]!);
        Assert.False((bool)o["playing"]!);
        Assert.Equal("A, B", (string)o["artist"]!);
        Assert.Equal("", (string)o["album"]!);
        Assert.Equal("Firefox", (string)o["app"]!);
        Assert.Equal([true, false, true, false], new[] { "canPlay", "canPause", "canNext", "canPrevious" }.Select(k => (bool)o[k]!));
    }

    [Fact]
    public void Only_small_local_images_become_art()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var png = Path.Combine(dir.FullName, "a.png");
            File.WriteAllBytes(png, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]);
            var txt = Path.Combine(dir.FullName, "a.txt");
            File.WriteAllText(txt, "not an image");
            var big = Path.Combine(dir.FullName, "big.png");
            File.WriteAllBytes(big, new byte[MediaService.MaxArtBytes + 1]);
            Assert.StartsWith("data:image/png;base64,", LinuxMedia.ArtDataUrl(new Uri(png).AbsoluteUri));
            Assert.Null(LinuxMedia.ArtDataUrl(new Uri(txt).AbsoluteUri));
            Assert.Null(LinuxMedia.ArtDataUrl(new Uri(big).AbsoluteUri));
            Assert.Null(LinuxMedia.ArtDataUrl("https://i.scdn.co/image/abc")); // never fetched: an undeclared request
            Assert.Null(LinuxMedia.ArtDataUrl(null));
        }
        finally { dir.Delete(true); }
    }
}
