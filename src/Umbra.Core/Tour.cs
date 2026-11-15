namespace Umbra;

/// <summary>When the welcome tour (#241) runs: automatically on a first run, offered once to an existing profile, never repeated.</summary>
public static class Tour
{
    public enum Plan { None, ShowNow, Offer }

    /// <summary>
    /// A first run shows the tour. An existing profile that never saw it gets a one-time offer, but not on a start that already
    /// announces an update or runs in safe mode (one balloon at a time; the offer waits for the next start). Once <paramref name="done"/> is set nothing repeats.
    /// </summary>
    public static Plan Decide(bool done, bool firstRun, bool updateAnnounced) =>
        done ? Plan.None : firstRun ? Plan.ShowNow : updateAnnounced ? Plan.None : Plan.Offer;
}
