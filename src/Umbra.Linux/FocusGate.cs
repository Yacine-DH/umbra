namespace Umbra;

/// <summary>What a widget window does with a page's request for the keyboard.</summary>
enum FocusDecision
{
    /// <summary>Nothing: no unused physical press on this window just now, or the window is hidden or closed.</summary>
    Ignore,
    /// <summary>Lend the focus: make the window focusable through the pin backend and focus it, and start the lease.</summary>
    Grant,
    /// <summary>The window already has the keyboard (the user's click gave it): only move the focus inside the page.</summary>
    Inside,
}

/// <summary>
/// The keyboard focus policy of a widget window (#188), the same for every pin backend: X11 (input hint, no
/// <c>WM_TAKE_FOCUS</c>), layer-shell (keyboard mode NONE / ON_DEMAND) and the GNOME extension (<c>SetFocusable</c>, which
/// checks the compositor's own press again). The backends only switch "may be focused" on and off; when is decided here.
/// <list type="bullet">
/// <item>Not focusable is the normal state: a window never takes the keyboard by appearing, by a page call (<c>focus()</c>,
/// <c>autofocus</c>, <c>umbra.requestFocus()</c>, a raw <c>focus</c> message) or on a timer.</item>
/// <item>Granted only when the page asks within <see cref="PressMs"/> of a physical mouse press GTK delivered to this view,
/// and one press is one grant: a page asking again later (after the user clicked elsewhere) gets nothing.</item>
/// <item>Revoked when the window loses the keyboard (the user clicked or switched elsewhere), when a grant was not taken
/// within <see cref="LeaseMs"/> (so a later focus fallback cannot hand it the keyboard), when the window is hidden or
/// closed (a disabled widget's window closes). Edit mode turning on or off ends an unused press and a pending grant; a
/// focus the user's click already gave stays until the user moves on.</item>
/// </list>
/// Times are <see cref="Environment.TickCount64"/> milliseconds, passed in so the policy is testable.
/// </summary>
sealed class FocusGate
{
    /// <summary>How long after a physical press the page may ask for the keyboard.</summary>
    public const long PressMs = 1000;

    /// <summary>How long a grant keeps the window focusable without it becoming active.</summary>
    public const uint LeaseMs = 1500;

    long _pressAt = long.MinValue / 2;
    bool _pressUnused;
    bool _granted;   // the backend was told "focusable"
    bool _active;    // the window has the keyboard
    bool _closed;
    long _lease;     // the generation of the current grant

    /// <summary>True while the backend has this window marked focusable.</summary>
    public bool Granted => _granted;

    /// <summary>A physical mouse press reached this window's view.</summary>
    public void Pressed(long now)
    {
        _pressAt = now;
        _pressUnused = true;
    }

    /// <summary>The page asked for the keyboard. <paramref name="token"/> is the lease for <see cref="LeaseExpired"/>.</summary>
    public FocusDecision Request(long now, bool shown, out long token)
    {
        token = 0;
        if (_closed || !shown) return FocusDecision.Ignore;
        if (_active) return FocusDecision.Inside;
        if (!_pressUnused || now - _pressAt is < 0 or > PressMs) return FocusDecision.Ignore;
        _pressUnused = false; // one press, one grant
        _granted = true;
        token = ++_lease;
        return FocusDecision.Grant;
    }

    /// <summary>The window got the keyboard. Returns false when it was never granted (the backend must take it back).</summary>
    public bool Activated()
    {
        if (!_granted) return false;
        _active = true;
        return true;
    }

    /// <summary>The window lost the keyboard: the backend makes it unfocusable again (always; the lent focus is used up).</summary>
    public void Deactivated()
    {
        _active = false;
        _pressUnused = false;
        Revoke();
    }

    /// <summary>The lease timer of grant <paramref name="token"/> ran out. True when the backend must revoke it.</summary>
    public bool LeaseExpired(long token) => token == _lease && _granted && !_active && Revoke();

    /// <summary>Edit mode turned on or off: an unused press and a grant not taken yet end. True when the backend must revoke.</summary>
    public bool EditModeChanged()
    {
        _pressUnused = false;
        return _granted && !_active && Revoke();
    }

    /// <summary>The window was hidden. True when the backend must revoke.</summary>
    public bool Hidden()
    {
        _pressUnused = false;
        _active = false;
        return Revoke();
    }

    /// <summary>The window is closing (closed, removed, or its widget disabled): nothing is granted again.</summary>
    public void Closed()
    {
        _closed = true;
        _active = false;
        Revoke();
    }

    bool Revoke()
    {
        _lease++;
        if (!_granted) return false;
        _granted = false;
        return true;
    }
}
