namespace Umbra.Linux.Tests;

/// <summary>What umbra.notify sends to org.freedesktop.Notifications (#189). The rate limit itself is tested in Core (Notifier).</summary>
public sealed class NotificationsTests
{
    [Fact]
    public void Actions_have_the_body_click_only_with_a_url_then_the_widget_buttons()
    {
        NotifyAction[] buttons = [new("snooze", "Snooze"), new("done", "Done")];
        Assert.Equal("['default', 'Open', 'a:snooze', 'Snooze', 'a:done', 'Done']", LinuxNotifications.ActionList(true, buttons));
        Assert.Equal("['a:snooze', 'Snooze', 'a:done', 'Done']", LinuxNotifications.ActionList(false, buttons));
        Assert.Equal("@as []", LinuxNotifications.ActionList(false, []));
    }

    [Fact]
    public void Body_markup_is_escaped() =>
        Assert.Equal("a &amp; &lt;b&gt;x&lt;/b&gt;", LinuxNotifications.EscapeMarkup("a & <b>x</b>"));
}
