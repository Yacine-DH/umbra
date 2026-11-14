namespace Umbra.Linux.Tests;

/// <summary>The keyboard focus policy shared by the X11, layer-shell and GNOME extension backends (#188).</summary>
public sealed class FocusGateTests
{
    [Fact]
    public void A_page_asking_without_a_press_gets_nothing_however_often_it_asks()
    {
        var gate = new FocusGate();
        for (var t = 0L; t < 10_000; t += 100)
            Assert.Equal(FocusDecision.Ignore, gate.Request(t, shown: true, out _));
        Assert.False(gate.Granted);
    }

    [Fact]
    public void A_request_right_after_a_physical_press_is_granted()
    {
        var gate = new FocusGate();
        gate.Pressed(5_000);
        Assert.Equal(FocusDecision.Grant, gate.Request(5_000 + FocusGate.PressMs, shown: true, out var token));
        Assert.True(gate.Granted);
        Assert.NotEqual(0, token);
    }

    [Fact]
    public void A_request_too_long_after_the_press_or_from_before_it_is_ignored()
    {
        var gate = new FocusGate();
        gate.Pressed(5_000);
        Assert.Equal(FocusDecision.Ignore, gate.Request(5_000 + FocusGate.PressMs + 1, shown: true, out _));
        gate.Pressed(9_000);
        Assert.Equal(FocusDecision.Ignore, gate.Request(8_999, shown: true, out _)); // a clock going backwards is no press
    }

    [Fact]
    public void One_press_is_one_grant_so_the_page_cannot_take_the_focus_back_after_the_user_moved_on()
    {
        var gate = new FocusGate();
        gate.Pressed(0);
        Assert.Equal(FocusDecision.Grant, gate.Request(10, shown: true, out _));
        Assert.True(gate.Activated());
        gate.Deactivated(); // the user clicked the editor 300 ms after the widget
        Assert.False(gate.Granted);
        // The page keeps asking every 100 ms, still inside the press window: nothing.
        for (var t = 400L; t <= FocusGate.PressMs; t += 100)
            Assert.Equal(FocusDecision.Ignore, gate.Request(t, shown: true, out _));
    }

    [Fact]
    public void A_grant_not_taken_in_time_is_withdrawn_and_a_newer_grant_supersedes_its_timer()
    {
        var gate = new FocusGate();
        gate.Pressed(0);
        gate.Request(1, shown: true, out var first);
        Assert.True(gate.LeaseExpired(first));
        Assert.False(gate.Granted);

        gate.Pressed(2_000);
        gate.Request(2_001, shown: true, out var second);
        gate.Pressed(2_500);
        gate.Deactivated();
        gate.Pressed(2_600);
        gate.Request(2_601, shown: true, out var third);
        Assert.False(gate.LeaseExpired(second)); // an older timer does not end the newer grant
        Assert.True(gate.Granted);
        Assert.True(gate.LeaseExpired(third));
    }

    [Fact]
    public void A_grant_the_window_took_is_kept_until_it_loses_the_keyboard()
    {
        var gate = new FocusGate();
        gate.Pressed(0);
        gate.Request(1, shown: true, out var token);
        Assert.True(gate.Activated());
        Assert.False(gate.LeaseExpired(token));
        Assert.True(gate.Granted);
        // While it has the keyboard, the page may move the focus inside itself (the user is typing in it).
        Assert.Equal(FocusDecision.Inside, gate.Request(60_000, shown: true, out _));
        gate.Deactivated();
        Assert.False(gate.Granted);
        Assert.Equal(FocusDecision.Ignore, gate.Request(60_100, shown: true, out _));
    }

    [Fact]
    public void Becoming_active_without_a_grant_is_reported()
    {
        var gate = new FocusGate();
        Assert.False(gate.Activated());
        Assert.Equal(FocusDecision.Ignore, gate.Request(0, shown: true, out _)); // and does not open the door to the page
    }

    [Fact]
    public void A_hidden_or_closed_window_gets_nothing_and_loses_its_grant()
    {
        var gate = new FocusGate();
        gate.Pressed(0);
        Assert.Equal(FocusDecision.Ignore, gate.Request(1, shown: false, out _));
        gate.Pressed(10);
        gate.Request(11, shown: true, out _);
        gate.Activated();
        Assert.True(gate.Hidden());
        Assert.False(gate.Granted);

        gate.Pressed(100);
        gate.Closed();
        Assert.Equal(FocusDecision.Ignore, gate.Request(101, shown: true, out _));
        Assert.False(gate.Granted);
    }

    [Fact]
    public void Edit_mode_changing_ends_an_unused_press_and_a_pending_grant_but_not_a_focus_in_use()
    {
        var gate = new FocusGate();
        gate.Pressed(0);
        Assert.False(gate.EditModeChanged());
        Assert.Equal(FocusDecision.Ignore, gate.Request(1, shown: true, out _));

        gate.Pressed(100);
        gate.Request(101, shown: true, out _);
        Assert.True(gate.EditModeChanged()); // granted, never active: withdrawn
        Assert.False(gate.Granted);

        gate.Pressed(200);
        gate.Request(201, shown: true, out _);
        gate.Activated();
        Assert.False(gate.EditModeChanged()); // the user's click gave it the keyboard: it keeps it until the user moves on
        Assert.True(gate.Granted);
    }
}
