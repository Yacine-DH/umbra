namespace Umbra.Tests;

public class TourTests
{
    [Theory]
    [InlineData(false, true, false, Tour.Plan.ShowNow)]   // first run
    [InlineData(false, true, true, Tour.Plan.ShowNow)]
    [InlineData(false, false, false, Tour.Plan.Offer)]    // existing profile, first start with the tour
    [InlineData(false, false, true, Tour.Plan.None)]      // the update balloon goes first; offer waits for the next start
    [InlineData(true, true, false, Tour.Plan.None)]       // never repeats
    [InlineData(true, false, false, Tour.Plan.None)]
    public void Decide_shows_once(bool done, bool firstRun, bool announced, Tour.Plan expected) =>
        Assert.Equal(expected, Tour.Decide(done, firstRun, announced));

    [Fact]
    public void Tour_flag_defaults_to_false_and_is_kept_in_the_file()
    {
        var s = new Settings();
        Assert.False(s.General.TourDone);
        s.General.TourDone = true;
        Assert.True(Settings.FromJson(s.ToJson()).General.TourDone);
        Assert.False(Settings.FromJson(System.Text.Json.Nodes.JsonNode.Parse("{}")!).General.TourDone); // an old settings.json without the field
    }
}
