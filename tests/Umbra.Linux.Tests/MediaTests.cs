using System.Buffers.Binary;
using System.IO.Compression;

namespace Umbra.Linux.Tests;

/// <summary>umbra.media on Linux (#190, #367): which MPRIS player counts, the answer's shape and the album-art policy.</summary>
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
    public void Only_local_images_within_source_bounds_become_art()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var png = Path.Combine(dir.FullName, "a.png");
            File.WriteAllBytes(png, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]);
            var txt = Path.Combine(dir.FullName, "a.txt");
            File.WriteAllText(txt, "not an image");
            var svg = Path.Combine(dir.FullName, "a.svg");
            File.WriteAllText(svg, "<svg onload=alert(1)>");
            var bigJunk = Path.Combine(dir.FullName, "big.bin");
            File.WriteAllBytes(bigJunk, new byte[MediaService.MaxArtBytes + 1]);
            Assert.StartsWith("data:image/png;base64,", LinuxMedia.ArtDataUrl(new Uri(png).AbsoluteUri));
            Assert.Null(LinuxMedia.ArtDataUrl(new Uri(txt).AbsoluteUri));
            Assert.Null(LinuxMedia.ArtDataUrl(new Uri(svg).AbsoluteUri));
            Assert.Null(LinuxMedia.ScaleArt(svg));
            Assert.Null(LinuxMedia.ArtDataUrl(new Uri(bigJunk).AbsoluteUri));
            Assert.Null(LinuxMedia.ArtDataUrl("https://i.scdn.co/image/abc")); // never fetched: an undeclared request
            Assert.Null(LinuxMedia.ArtDataUrl(null));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Big_png_art_becomes_a_small_jpeg_of_the_same_shape()
    {
        if (!OperatingSystem.IsLinux()) return;
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var bigPng = Path.Combine(dir.FullName, "cover.png");
            File.WriteAllBytes(bigPng, NoisyPng(1000, 600));
            Assert.True(new FileInfo(bigPng).Length > MediaService.MaxArtBytes);

            var dataUrl = LinuxMedia.ArtDataUrl(new Uri(bigPng).AbsoluteUri);
            Assert.NotNull(dataUrl);
            Assert.StartsWith("data:image/jpeg;base64,", dataUrl);
            var jpeg = Convert.FromBase64String(dataUrl["data:image/jpeg;base64,".Length..]);
            Assert.Equal("image/jpeg", MediaService.SniffImage(jpeg));
            Assert.InRange(jpeg.Length, 1, MediaService.MaxArtBytes);
            Assert.Equal((512, 307), JpegSize(jpeg));

            // Small art keeps its dimensions when passed through ScaleArt.
            var smallPng = Path.Combine(dir.FullName, "small.png");
            File.WriteAllBytes(smallPng, NoisyPng(120, 120));
            var smallJpeg = LinuxMedia.ScaleArt(smallPng);
            Assert.NotNull(smallJpeg);
            Assert.Equal((120, 120), JpegSize(smallJpeg));
        }
        finally { dir.Delete(true); }
    }

    static byte[] NoisyPng(int width, int height)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8; // 8-bit
        ihdr[9] = 6; // RGBA
        WriteChunk(ms, "IHDR"u8, ihdr);

        var raw = new byte[height * (1 + width * 4)];
        new Random(7).NextBytes(raw);
        for (var y = 0; y < height; y++)
        {
            var row = y * (1 + width * 4);
            raw[row] = 0; // filter: None
            for (var x = 0; x < width; x++) raw[row + 1 + x * 4 + 3] = 255;
        }
        using var idat = new MemoryStream();
        using (var zlib = new ZLibStream(idat, CompressionLevel.NoCompression, leaveOpen: true))
            zlib.Write(raw);
        WriteChunk(ms, "IDAT"u8, idat.ToArray());
        WriteChunk(ms, "IEND"u8, []);
        return ms.ToArray();
    }

    static void WriteChunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buf, data.Length);
        s.Write(buf);
        s.Write(type);
        s.Write(data);
        var crc = 0xFFFFFFFFu;
        foreach (var b in type) crc = CrcStep(crc, b);
        foreach (var b in data) crc = CrcStep(crc, b);
        BinaryPrimitives.WriteUInt32BigEndian(buf, ~crc);
        s.Write(buf);
    }

    static uint CrcStep(uint crc, byte b)
    {
        crc ^= b;
        for (var i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        return crc;
    }

    static (int Width, int Height) JpegSize(ReadOnlySpan<byte> b)
    {
        var i = 2;
        while (i + 8 < b.Length && b[i] == 0xFF)
        {
            var marker = b[i + 1];
            var len = BinaryPrimitives.ReadUInt16BigEndian(b[(i + 2)..]);
            if (marker is 0xC0 or 0xC1 or 0xC2)
                return (BinaryPrimitives.ReadUInt16BigEndian(b[(i + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(b[(i + 5)..]));
            i += 2 + len;
        }
        return (0, 0);
    }
}
