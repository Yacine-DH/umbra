using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Umbra.Tests;

public class MediaArtTests
{
    /// <summary>A noisy PNG (so it can't compress well), like the ~700 KB covers Firefox hands to Windows for web players.</summary>
    static async Task<InMemoryRandomAccessStream> NoisyPngAsync(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        new Random(7).NextBytes(pixels);
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, pixels);
        await encoder.FlushAsync();
        return stream;
    }

    [Fact]
    public async Task Big_png_art_becomes_a_small_jpeg_of_the_same_shape()
    {
        using var png = await NoisyPngAsync(1000, 600);
        Assert.True(png.Size > MediaService.MaxArtBytes); // the old code dropped this art

        var jpeg = await WindowsMedia.ScaleArtAsync(png);

        Assert.NotNull(jpeg);
        Assert.Equal("image/jpeg", MediaService.SniffImage(jpeg));
        Assert.InRange(jpeg.Length, 1, MediaService.MaxArtBytes);
        using var back = new InMemoryRandomAccessStream();
        await back.WriteAsync(jpeg.AsBuffer());
        var decoded = await BitmapDecoder.CreateAsync(back);
        Assert.Equal((512u, 307u), (decoded.PixelWidth, decoded.PixelHeight));
    }

    [Fact]
    public async Task Small_art_keeps_its_size()
    {
        using var png = await NoisyPngAsync(120, 120);
        var jpeg = await WindowsMedia.ScaleArtAsync(png);
        using var back = new InMemoryRandomAccessStream();
        await back.WriteAsync(jpeg!.AsBuffer());
        var decoded = await BitmapDecoder.CreateAsync(back);
        Assert.Equal((120u, 120u), (decoded.PixelWidth, decoded.PixelHeight));
    }

    [Fact]
    public async Task Something_that_is_not_a_picture_is_refused()
    {
        using var junk = new InMemoryRandomAccessStream();
        await junk.WriteAsync("<svg onload=alert(1)>"u8.ToArray().AsBuffer());
        await Assert.ThrowsAnyAsync<Exception>(() => WindowsMedia.ScaleArtAsync(junk));
    }
}
