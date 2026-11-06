namespace Umbra.Tests;

public class NotificationConsentTests
{
    static WidgetManifest Manifest(bool notifications = true) =>
        WidgetManifest.Parse($$"""{ "id": "hearth", "name": "Hearth", "permissions": { "notifications": {{(notifications ? "true" : "false")}} } }""");

    static (Settings S, WidgetSettings Ws) Fresh()
    {
        var s = new Settings();
        s.Widgets["hearth"] = new WidgetSettings();
        s.Widgets["hearth#2"] = new WidgetSettings();
        s.Widgets["todo"] = new WidgetSettings();
        return (s, s.Widgets["hearth"]);
    }

    [Fact]
    public void Needs_the_permission()
    {
        var (s, ws) = Fresh();
        var e = Assert.Throws<NotifyConsentException>(() => new NotificationConsent().Begin(Manifest(false), ws, s.General, true));
        Assert.Contains("permissions.notifications", e.Message);
    }

    [Fact]
    public void Already_allowed_or_turned_off_never_prompts_even_without_a_click()
    {
        var (s, ws) = Fresh();
        var c = new NotificationConsent();
        ws.Notifications = true;
        Assert.Equal(NotifyAsk.Allowed, c.Begin(Manifest(), ws, s.General, false));
        ws.Notifications = null; s.General.DefaultNotifications = true;
        Assert.Equal(NotifyAsk.Allowed, c.Begin(Manifest(), ws, s.General, false));
        ws.Notifications = false; // the user's Off (Settings or "Don't ask again") wins over the default
        Assert.Equal(NotifyAsk.Blocked, c.Begin(Manifest(), ws, s.General, true));
    }

    [Fact]
    public void Undecided_prompts_only_after_a_click()
    {
        var (s, ws) = Fresh();
        var c = new NotificationConsent();
        var e = Assert.Throws<NotifyConsentException>(() => c.Begin(Manifest(), ws, s.General, false));
        Assert.Contains("Click the widget", e.Message);
        Assert.Equal(NotifyAsk.Prompt, c.Begin(Manifest(), ws, s.General, true));
    }

    [Fact]
    public void One_prompt_at_a_time_and_rate_limited_per_widget()
    {
        var (s, ws) = Fresh();
        var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var c = new NotificationConsent(() => t);
        Assert.Equal(NotifyAsk.Prompt, c.Begin(Manifest(), ws, s.General, true));
        Assert.Contains("already open", Assert.Throws<NotifyConsentException>(() => c.Begin(Manifest(), ws, s.General, true)).Message);
        Assert.False(c.End(s, "hearth", null)); // timed out: nothing stored
        Assert.Null(ws.Notifications);
        Assert.Contains("wait", Assert.Throws<NotifyConsentException>(() => c.Begin(Manifest(), ws, s.General, true)).Message);
        t = t.AddSeconds(21);
        Assert.Equal(NotifyAsk.Prompt, c.Begin(Manifest(), ws, s.General, true));
    }

    [Fact]
    public void Allow_turns_on_every_undecided_copy_of_that_widget_only()
    {
        var (s, ws) = Fresh();
        s.Widgets["hearth#3"] = new WidgetSettings { Notifications = false }; // a copy the user turned off stays off
        var c = new NotificationConsent();
        Assert.Equal(NotifyAsk.Prompt, c.Begin(Manifest(), ws, s.General, true));
        Assert.True(c.End(s, "hearth", NotifyChoice.Allow));
        Assert.True(s.Widgets["hearth"].Notifications);
        Assert.True(s.Widgets["hearth#2"].Notifications);
        Assert.False(s.Widgets["hearth#3"].Notifications);
        Assert.Null(s.Widgets["todo"].Notifications);
        Assert.Equal(NotifyAsk.Allowed, c.Begin(Manifest(), s.Widgets["hearth#2"], s.General, false));
    }

    [Fact]
    public void Dont_ask_again_turns_them_off_for_good()
    {
        var (s, ws) = Fresh();
        var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var c = new NotificationConsent(() => t);
        c.Begin(Manifest(), ws, s.General, true);
        Assert.False(c.End(s, "hearth", NotifyChoice.Never));
        Assert.False(ws.Notifications);
        Assert.False(s.Widgets["hearth#2"].Notifications);
        t = t.AddHours(1);
        Assert.Equal(NotifyAsk.Blocked, c.Begin(Manifest(), ws, s.General, true));
        Assert.Equal(NotifyAsk.Blocked, new NotificationConsent().Begin(Manifest(), ws, s.General, true)); // after a restart too
    }

    [Fact]
    public void Not_now_stores_nothing_and_stops_asking_until_restart()
    {
        var (s, ws) = Fresh();
        var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var c = new NotificationConsent(() => t);
        c.Begin(Manifest(), ws, s.General, true);
        Assert.False(c.End(s, "hearth", NotifyChoice.NotNow));
        Assert.Null(ws.Notifications);
        t = t.AddHours(1);
        Assert.Equal(NotifyAsk.Blocked, c.Begin(Manifest(), s.Widgets["hearth#2"], s.General, true));
        Assert.Equal(NotifyAsk.Prompt, new NotificationConsent(() => t).Begin(Manifest(), ws, s.General, true));
    }
}
