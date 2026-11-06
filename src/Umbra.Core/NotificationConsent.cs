namespace Umbra;

/// <summary>The user's answer in the host-drawn notification prompt. A prompt that times out has no answer (null).</summary>
public enum NotifyChoice { Allow, NotNow, Never }

/// <summary>What <see cref="NotificationConsent.Begin"/> decided: already allowed, blocked without asking, or show the prompt now.</summary>
public enum NotifyAsk { Allowed, Blocked, Prompt }

/// <summary>A reason <c>umbra.requestNotifications</c> was refused; the message goes back to the page.</summary>
public sealed class NotifyConsentException(string message) : Exception(message);

/// <summary>
/// <c>umbra.requestNotifications</c>: the host-drawn "Allow notifications?" prompt. Asked only for a widget that declares
/// <c>permissions.notifications</c> and whose switch is unset (<see cref="WidgetSettings.Notifications"/> null), only after
/// a physical left click on it (<c>ClickGate</c>), one prompt at a time and at most one per <see cref="MinPromptInterval"/>
/// per widget. "Allow" turns the switch on and "Don't ask again" turns it off for every copy of the widget whose switch was
/// unset (Settings > My widgets can change either later); "Not now" changes nothing but stops asking until Umbra restarts.
/// The page learns only whether notifications are allowed. No control command, umbractl or MCP tool shows or answers it.
/// </summary>
public sealed class NotificationConsent(Func<DateTime>? now = null)
{
    public TimeSpan MinPromptInterval { get; set; } = TimeSpan.FromSeconds(20);

    readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);
    readonly object _gate = new();
    readonly Dictionary<string, DateTime> _lastPrompt = new();
    readonly HashSet<string> _notNow = new();
    bool _open;

    /// <summary>
    /// Decides whether to show the prompt. Returns <see cref="NotifyAsk.Prompt"/> only after reserving the single prompt slot:
    /// the caller must then call <see cref="End"/>. Throws <see cref="NotifyConsentException"/> when it may not ask now.
    /// </summary>
    /// <param name="userClick">A physical left click on the widget just now, outside edit mode.</param>
    public NotifyAsk Begin(WidgetManifest m, WidgetSettings ws, GeneralSettings g, bool userClick)
    {
        if (!m.Permissions.Notifications) throw new NotifyConsentException("This widget has no permissions.notifications");
        if (Prefs.EffectiveNotifications(ws, g)) return NotifyAsk.Allowed;
        if (ws.Notifications == false) return NotifyAsk.Blocked; // off in Settings, or "Don't ask again"
        lock (_gate)
        {
            if (_notNow.Contains(m.Id)) return NotifyAsk.Blocked;
            if (!userClick) throw new NotifyConsentException("Click the widget first: Umbra asks only right after a click on the widget");
            if (_open) throw new NotifyConsentException("A notification prompt is already open");
            if (_lastPrompt.TryGetValue(m.Id, out var last) && _now() - last < MinPromptInterval)
                throw new NotifyConsentException("Please wait a moment before asking again");
            _open = true;
            _lastPrompt[m.Id] = _now();
            return NotifyAsk.Prompt;
        }
    }

    /// <summary>Releases the prompt slot and applies the answer to every copy of the widget. Returns whether notifications are now allowed.</summary>
    public bool End(Settings s, string widgetId, NotifyChoice? choice)
    {
        lock (_gate)
        {
            _open = false;
            if (choice == NotifyChoice.NotNow) _notNow.Add(widgetId);
        }
        if (choice is NotifyChoice.Allow or NotifyChoice.Never)
            foreach (var (instance, ws) in s.Widgets)
                if (InstanceId.WidgetOf(instance) == widgetId && ws.Notifications is null) ws.Notifications = choice == NotifyChoice.Allow;
        return choice == NotifyChoice.Allow;
    }
}
