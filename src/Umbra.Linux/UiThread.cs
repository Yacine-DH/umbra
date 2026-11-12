namespace Umbra;

/// <summary>
/// Puts work on the GTK main loop (the UI thread): the Linux counterpart of the WinForms synchronization context.
/// <c>g_idle_add</c> is thread-safe, so pipe requests and timers on worker threads post through here.
/// </summary>
sealed class UiThread : SynchronizationContext
{
    const int PriorityDefault = 0; // G_PRIORITY_DEFAULT: ahead of redraws and idle work, like a posted window message

    public override void Post(SendOrPostCallback d, object? state) =>
        GLib.Functions.IdleAdd(PriorityDefault, () =>
        {
            try { d(state); }
            catch (Exception ex) { Log.Write($"UI exception: {ex}"); }
            return false;
        });

    public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException("Use Post: a blocking Send could deadlock the main loop.");

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>Runs <paramref name="tick"/> on the UI thread every <paramref name="ms"/> until it returns false.</summary>
    public static void Every(uint ms, Func<bool> tick) =>
        GLib.Functions.TimeoutAdd(PriorityDefault, ms, () =>
        {
            try { return tick(); }
            catch (Exception ex) { Log.Write($"UI timer exception: {ex}"); return true; }
        });
}
