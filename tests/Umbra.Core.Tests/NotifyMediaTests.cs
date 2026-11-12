namespace Umbra.Tests;

public class NotifyMediaTests
{
    [Fact]
    public void Notifier_limits_five_per_ten_minutes_per_widget()
    {
        var n = new Notifier();
        var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 5; i++) Assert.True(n.TryAcquire("a", t.AddSeconds(i)));
        Assert.False(n.TryAcquire("a", t.AddMinutes(5)));
        Assert.True(n.TryAcquire("b", t.AddMinutes(5)));
        Assert.True(n.TryAcquire("a", t.AddMinutes(10)));
    }

    [Fact]
    public void Clean_flattens_and_caps_text()
    {
        Assert.Equal("a b", Notifier.Clean("a\nb  ", 10));
        Assert.Equal(5, Notifier.Clean(new string('x', 50), 5).Length);
        Assert.Null(Notifier.CleanTag("  "));
        Assert.Equal("x", Notifier.CleanTag(" x "));
    }

    [Fact]
    public void ParseActions_accepts_up_to_two_and_rejects_the_rest()
    {
        Assert.Empty(Notifier.ParseActions(null, out _)!);
        var ok = Notifier.ParseActions(System.Text.Json.Nodes.JsonNode.Parse("""[{"id":"snooze","label":"Snooze"},{"id":"done","label":"Done"}]"""), out var e1)!;
        Assert.Null(e1);
        Assert.Equal([new NotifyAction("snooze", "Snooze"), new NotifyAction("done", "Done")], ok);
        Assert.Null(Notifier.ParseActions(System.Text.Json.Nodes.JsonNode.Parse("""[{"id":"a","label":"A"},{"id":"b","label":"B"},{"id":"c","label":"C"}]"""), out var e2));
        Assert.NotNull(e2);
        Assert.Null(Notifier.ParseActions(System.Text.Json.Nodes.JsonNode.Parse("""[{"id":"a"}]"""), out _));
        Assert.Null(Notifier.ParseActions(System.Text.Json.Nodes.JsonNode.Parse("""[{"id":"a","label":"A"},{"id":"a","label":"B"}]"""), out _));
        Assert.Null(Notifier.ParseActions(System.Text.Json.Nodes.JsonNode.Parse("""{"id":"a","label":"A"}"""), out _));
    }

    [Fact]
    public void ToastXml_escapes_text_and_names_the_widget()
    {
        var xml = Notifier.ToastXml("My <Widget>", "A & B", "say \"hi\"", [new NotifyAction("x\"y", "Go <now>")]);
        var doc = System.Xml.Linq.XDocument.Parse(xml); // well-formed despite the markup characters in the text
        var texts = doc.Descendants("text").Select(t => t.Value).ToList();
        Assert.Equal(["A & B", "say \"hi\"", "My <Widget>"], texts);
        Assert.Equal("attribution", (string?)doc.Descendants("text").Last().Attribute("placement"));
        var action = Assert.Single(doc.Descendants("action"));
        Assert.Equal("Go <now>", (string?)action.Attribute("content"));
        Assert.Equal("action:x\"y", (string?)action.Attribute("arguments"));
        Assert.Empty(System.Xml.Linq.XDocument.Parse(Notifier.ToastXml("W", "T", "", [])).Descendants("action"));
        Assert.Equal(2, System.Xml.Linq.XDocument.Parse(Notifier.ToastXml("W", "T", "", [])).Descendants("text").Count()); // title + attribution, no empty body
    }

    [Fact]
    public void ToastKey_is_short_stable_and_distinct()
    {
        Assert.Equal(Notifier.ToastKey("sp500#2"), Notifier.ToastKey("sp500#2"));
        Assert.NotEqual(Notifier.ToastKey("a"), Notifier.ToastKey("b"));
        Assert.Equal(16, Notifier.ToastKey(new string('x', 500)).Length);
    }

    [Fact]
    public void Notify_url_must_be_https_on_a_declared_host()
    {
        var m = WidgetManifest.Parse("""{ "id": "np", "name": "N", "permissions": { "hosts": ["example.com"], "notifications": true } }""");
        Assert.True(m.AllowsUrl("https://example.com/x"));
        Assert.False(m.AllowsUrl("http://example.com/x"));
        Assert.False(m.AllowsUrl("https://evil.test/x"));
    }

    [Fact]
    public void Manifest_validates_media_and_notifications()
    {
        var m = WidgetManifest.Parse("""{ "id": "np", "name": "N", "permissions": { "media": ["read", "control"], "notifications": true } }""");
        Assert.True(m.Permissions.Notifications);
        Assert.Equal(2, m.Permissions.Media.Count);
        Assert.Throws<InvalidOperationException>(() =>
            WidgetManifest.Parse("""{ "id": "np", "name": "N", "permissions": { "media": ["record"] } }"""));
    }

    [Fact]
    public void Media_sniffs_images_and_commands()
    {
        Assert.Equal("image/png", MediaService.SniffImage([0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0, 0]));
        Assert.Equal("image/jpeg", MediaService.SniffImage([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Null(MediaService.SniffImage("<svg onload=x>"u8));
        Assert.True(MediaService.IsCommand("next"));
        Assert.False(MediaService.IsCommand("shutdown"));
    }

    [Theory]
    [InlineData("308046B0AF4A39CB", "Firefox", "Firefox")] // the display name always wins
    [InlineData("308046B0AF4A39CB", null, null)] // Firefox's id is a hash: never shown
    [InlineData("{6D809377-6AF0-444B-8957-A3773F02200E}\\VLC\\vlc.exe", null, "vlc")]
    [InlineData("Spotify.exe", null, "Spotify")]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", null, "Spotify")]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", null, "ZuneMusic")]
    [InlineData("Chrome", "  ", "Chrome")]
    [InlineData("6D809377-6AF0-444B-8957-A3773F02200E", null, null)]
    [InlineData("", null, null)]
    [InlineData(null, null, null)]
    public void Media_app_label_is_readable_or_null(string? id, string? display, string? expected) =>
        Assert.Equal(expected, MediaService.AppLabel(id, display));

    [Fact]
    public void Media_art_scales_to_the_edge_keeping_its_shape()
    {
        Assert.Equal((300u, 300u), MediaService.ArtSize(300, 300));
        Assert.Equal((512u, 512u), MediaService.ArtSize(1200, 1200));
        Assert.Equal((512u, 288u), MediaService.ArtSize(1280, 720));
        Assert.Equal((1u, 512u), MediaService.ArtSize(1, 5000));
        Assert.Equal((0u, 0u), MediaService.ArtSize(0, 100));
    }
}
