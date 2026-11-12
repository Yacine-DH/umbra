namespace Umbra.Tests;

/// <summary>umbra.images: only small PNGs, only into a folder the user picked, under a name the host chooses.</summary>
public class ImageArchiveTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "umbra-img-" + Guid.NewGuid().ToString("N"));

    public ImageArchiveTests()
    {
        TestEnv.Init();
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    static string Png(int extra = 0)
    {
        var b = new byte[16 + extra];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0);
        return ImageArchive.DataUrlPrefix + Convert.ToBase64String(b);
    }

    ImageFolders Folders(out string folder)
    {
        folder = Path.Combine(_root, "pics");
        Directory.CreateDirectory(folder);
        var f = new ImageFolders(Path.Combine(_root, "image-folders.json"));
        f.Set("demo", folder);
        return f;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("data:image/jpeg;base64,AAAA")]
    [InlineData("https://example.com/a.png")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("data:image/png;base64,!!!")]
    [InlineData("data:image/png;base64,AAAAAAAAAAAAAAAAAAAAAAAA")] // base64 fine, not a PNG
    public void Anything_but_a_png_data_url_is_refused(string? input) =>
        Assert.Throws<ImageArchiveException>(() => ImageArchive.DecodePng(input));

    [Fact]
    public void Oversize_png_is_refused() =>
        Assert.Throws<ImageArchiveException>(() => ImageArchive.DecodePng(Png(ImageArchive.MaxBytes)));

    [Fact]
    public void Valid_png_decodes() => Assert.Equal(16, ImageArchive.DecodePng(Png()).Length);

    [Fact]
    public void File_names_come_from_the_host_and_never_collide()
    {
        var at = new DateTime(2026, 11, 11, 14, 30, 5);
        var taken = new HashSet<string>();
        var a = ImageArchive.FileName("Hearth", at, taken.Contains);
        taken.Add(a);
        var b = ImageArchive.FileName("Hearth", at, taken.Contains);
        Assert.Equal("Hearth 2026-11-11 14.30.05.png", a);
        Assert.Equal("Hearth 2026-11-11 14.30.05 (2).png", b);
        // a widget name can't smuggle a path or odd characters into the file name
        var evil = ImageArchive.FileName(@"..\..\x:/\*?", at, _ => false);
        Assert.DoesNotContain("\\", evil);
        Assert.DoesNotContain("/", evil);
        Assert.DoesNotContain("..", evil);
    }

    [Fact]
    public void Label_never_exposes_the_full_path()
    {
        Assert.Equal("Pics", ImageArchive.Label(Path.Combine("C:", "Users", "me", "Pics") + Path.DirectorySeparatorChar));
        Assert.Equal("", ImageArchive.Label(null));
    }

    [Fact]
    public void Save_needs_a_chosen_folder()
    {
        var f = new ImageFolders(Path.Combine(_root, "none.json"));
        Assert.Null(f.Get("demo"));
        Assert.Throws<ImageArchiveException>(() => f.Save("demo", "Hearth", Png()));
    }

    [Fact]
    public void Save_writes_only_inside_the_chosen_folder()
    {
        var f = Folders(out var folder);
        var name = f.Save("demo", "Hearth", Png(), freeBytes: _ => long.MaxValue);
        Assert.True(File.Exists(Path.Combine(folder, name)));
        Assert.Equal(folder, Path.GetDirectoryName(Path.Combine(folder, name)));
        Assert.Throws<ImageArchiveException>(() => f.Save("other", "Hearth", Png(), freeBytes: _ => long.MaxValue)); // another id has no folder
    }

    [Fact]
    public void Save_is_rate_limited_and_checks_disk_space()
    {
        var f = Folders(out _);
        var t = new DateTime(2026, 11, 11, 12, 0, 0);
        for (var i = 0; i < ImageArchive.MaxPerWindow; i++) f.Save("demo", "Hearth", Png(), t, _ => long.MaxValue);
        Assert.Throws<ImageArchiveException>(() => f.Save("demo", "Hearth", Png(), t, _ => long.MaxValue));
        f.Save("demo", "Hearth", Png(), t.AddMinutes(11), _ => long.MaxValue); // the window moved on
        Assert.Throws<ImageArchiveException>(() => f.Save("demo", "Hearth", Png(), t.AddMinutes(30), _ => 1000));
    }

    [Fact]
    public void Folder_is_forgotten_when_cleared_or_the_widget_stops_declaring_it()
    {
        var f = Folders(out _);
        f.ClearExcept(new[] { "demo" });
        Assert.NotNull(f.Get("demo"));
        f.ClearExcept(Array.Empty<string>());
        Assert.Null(f.Get("demo"));
    }

    [Fact]
    public void Folder_is_gone_when_the_directory_is_deleted()
    {
        var f = Folders(out var folder);
        Directory.Delete(folder);
        Assert.Null(f.Get("demo"));
    }

    [Fact]
    public void Update_that_adds_saveImages_needs_the_users_accept()
    {
        static WidgetManifest M(string p) => WidgetManifest.Parse($$"""{ "id": "demo", "name": "Demo", "version": "1.0.0", "permissions": {{p}} }""");
        var d = PermissionDiff.Compute(M("{}"), M("""{ "saveImages": true }"""));
        Assert.True(d.SaveImages);
        Assert.False(d.IsEmpty);
        Assert.Contains(d.Describe(), l => l.Contains("folder you choose"));
        Assert.True(PermissionDiff.Compute(M("""{ "saveImages": true }"""), M("""{ "saveImages": true }""")).IsEmpty);
    }

    [Fact]
    public void No_control_command_can_choose_a_folder_or_save_pictures()
    {
        foreach (var c in Protocol.Commands)
        {
            Assert.DoesNotContain("image", c.Cmd, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("picture", c.Cmd, StringComparison.OrdinalIgnoreCase);
        }
    }
}
