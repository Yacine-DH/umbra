using System.Text.Json.Nodes;

namespace Umbra.Linux;

/// <summary>How global hotkeys are registered on this session (#192).</summary>
enum HotkeyBackend
{
    /// <summary>Neither works (GNOME 46 on Wayland, no portal): Settings says "Not supported on this desktop".</summary>
    None,
    /// <summary>XGrabKey on the root window of a real X11 session.</summary>
    X11,
    /// <summary>org.freedesktop.portal.GlobalShortcuts (KDE Plasma 6, GNOME 48+); the desktop confirms bindings in its own dialog.</summary>
    Portal,
}

/// <summary>
/// The pure parts of Linux global hotkeys: which backend a session gets, how a stored combination ("Ctrl+Alt+E", the same
/// text as on Windows, <see cref="Hotkey"/>) becomes an X11 grab or a portal trigger, and the state Settings and
/// <c>umbractl hotkeys</c> show. Nothing here touches X or D-Bus.
/// </summary>
static class HotkeyRules
{
    /// <summary>
    /// A Wayland session never grabs through XWayland (it would only see keys while an X11 window has focus), so it uses the
    /// portal when the desktop offers GlobalShortcuts and nothing otherwise. A plain X11 session grabs on the X server (no dialog).
    /// <paramref name="portalVersion"/> is the portal interface's version property, 0 when it is missing.
    /// </summary>
    public static HotkeyBackend Choose(Func<string, string?> env, uint portalVersion)
    {
        if (!string.IsNullOrEmpty(env("WAYLAND_DISPLAY"))) return portalVersion >= 1 ? HotkeyBackend.Portal : HotkeyBackend.None;
        if (!string.IsNullOrEmpty(env("DISPLAY"))) return HotkeyBackend.X11;
        return portalVersion >= 1 ? HotkeyBackend.Portal : HotkeyBackend.None;
    }

    public const uint ShiftMask = 1, LockMask = 2, ControlMask = 4, Mod1Mask = 8 /* Alt */, Mod2Mask = 16 /* NumLock on most layouts */, Mod4Mask = 64 /* Super */;

    /// <summary>
    /// The modifier bit a key is mapped to in an X modifier map (XGetModifierMapping: 8 rows of <paramref name="perModifier"/>
    /// keycodes, Shift..Mod5), or 0 when it is not a modifier. Used to find Num Lock instead of assuming Mod2.
    /// </summary>
    public static uint ModifierBit(ReadOnlySpan<byte> map, int perModifier, byte keycode)
    {
        if (keycode == 0 || perModifier <= 0) return 0;
        for (var i = 0; i < Math.Min(map.Length, 8 * perModifier); i++)
            if (map[i] == keycode) return 1u << (i / perModifier);
        return 0;
    }

    /// <summary>The modifier combinations a grab is repeated for, so Caps Lock and Num Lock (bit <paramref name="numLock"/>, 0 when unmapped) don't matter.</summary>
    public static uint[] LockVariants(uint numLock) =>
        numLock == 0 || numLock == LockMask ? [0, LockMask] : [0, LockMask, numLock, LockMask | numLock];

    // Hotkey's virtual keys for the named keys, and their X keysym names.
    static readonly Dictionary<uint, string> NamedKeysyms = new()
    {
        [0x20] = "space", [0x0D] = "Return", [0x09] = "Tab", [0x08] = "BackSpace", [0x2D] = "Insert", [0x2E] = "Delete",
        [0x24] = "Home", [0x23] = "End", [0x21] = "Prior", [0x22] = "Next",
        [0x25] = "Left", [0x26] = "Up", [0x27] = "Right", [0x28] = "Down",
    };

    /// <summary>The X keysym name of a <see cref="Hotkey"/> virtual key ("e", "5", "F3", "Prior"...).</summary>
    public static string Keysym(uint vk) =>
        vk is >= 'A' and <= 'Z' ? ((char)(vk + 32)).ToString()
        : vk is >= '0' and <= '9' ? ((char)vk).ToString()
        : vk is >= 0x70 and <= 0x87 ? $"F{vk - 0x6F}"
        : NamedKeysyms.TryGetValue(vk, out var name) ? name
        : throw new ArgumentOutOfRangeException(nameof(vk));

    /// <summary>A stored combination as an X11 grab: modifier mask and keysym name. Null when the text is not a valid hotkey.</summary>
    public static (uint Mask, string Keysym)? ToX11(string? keys)
    {
        if (!Hotkey.TryParse(keys, out var mods, out var vk, out _)) return null;
        uint mask = 0;
        if ((mods & Hotkey.Ctrl) != 0) mask |= ControlMask;
        if ((mods & Hotkey.Alt) != 0) mask |= Mod1Mask;
        if ((mods & Hotkey.Shift) != 0) mask |= ShiftMask;
        if ((mods & Hotkey.Win) != 0) mask |= Mod4Mask;
        return (mask, Keysym(vk));
    }

    /// <summary>The modifier state of a key event with Caps Lock and Num Lock taken out, to compare with a grab's mask.</summary>
    public static uint CleanState(uint state) => state & (ShiftMask | ControlMask | Mod1Mask | Mod4Mask);

    /// <summary>
    /// A stored combination as the portal's preferred trigger (the XDG shortcuts format: CTRL, ALT, SHIFT, LOGO, then a keysym
    /// name), e.g. "CTRL+ALT+e". Null when invalid. The desktop may still let the user pick something else in its dialog.
    /// </summary>
    public static string? ToPortalTrigger(string? keys)
    {
        if (!Hotkey.TryParse(keys, out var mods, out var vk, out _)) return null;
        var parts = new List<string>();
        if ((mods & Hotkey.Ctrl) != 0) parts.Add("CTRL");
        if ((mods & Hotkey.Alt) != 0) parts.Add("ALT");
        if ((mods & Hotkey.Shift) != 0) parts.Add("SHIFT");
        if ((mods & Hotkey.Win) != 0) parts.Add("LOGO");
        parts.Add(Keysym(vk));
        return string.Join('+', parts);
    }

    /// <summary>The shortcuts to bind through the portal: (id, description, preferred trigger) for every valid, non-conflicting entry, in action order.</summary>
    public static List<(string Id, string Description, string Trigger)> PortalShortcuts(IReadOnlyDictionary<string, string> map)
    {
        var problems = Hotkey.Validate(map);
        var list = new List<(string, string, string)>();
        foreach (var (id, label) in Hotkey.Actions)
            if (map.TryGetValue(id, out var keys) && !problems.ContainsKey(id) && ToPortalTrigger(keys) is { } trigger)
                list.Add((id, label, trigger));
        return list;
    }

    /// <summary>What a backend knows about one action right now.</summary>
    public sealed record ActionState(string Status, string? Error = null, string? Note = null);

    /// <summary>
    /// The hotkey list for Settings and the control protocol, in the Windows shape (action, label, keys, status off|active|failed,
    /// error) plus "pending" (waiting for the desktop's dialog) and an optional neutral note (e.g. the trigger the desktop chose).
    /// </summary>
    public static JsonArray Describe(IReadOnlyDictionary<string, string> map, HotkeyBackend backend, IReadOnlyDictionary<string, ActionState> states)
    {
        var problems = Hotkey.Validate(map);
        return new JsonArray(Hotkey.Actions.Select(a =>
        {
            map.TryGetValue(a.Id, out var keys);
            var off = string.IsNullOrWhiteSpace(keys);
            var o = new JsonObject { ["action"] = a.Id, ["label"] = a.Label, ["keys"] = off ? null : Hotkey.Normalize(keys) ?? keys };
            ActionState s = off ? new("off")
                : problems.TryGetValue(a.Id, out var why) ? new("failed", why)
                : backend == HotkeyBackend.None ? new("failed", "Global shortcuts are not supported on this desktop")
                : states.TryGetValue(a.Id, out var known) ? known
                : new("failed", "Not registered");
            o["status"] = s.Status;
            if (s.Error is not null) o["error"] = s.Error;
            if (s.Note is not null) o["note"] = s.Note;
            return (JsonNode?)o;
        }).ToArray());
    }

    /// <summary>The backend's name for <c>umbractl hotkeys</c> and the Settings capability.</summary>
    public static string Name(HotkeyBackend b) => b switch { HotkeyBackend.X11 => "x11", HotkeyBackend.Portal => "portal", _ => "none" };

    /// <summary>
    /// The object path the portal will use for a request made with <paramref name="token"/> by the connection named
    /// <paramref name="uniqueName"/> (":1.42" becomes "1_42"), so the Response signal can be subscribed before the call.
    /// </summary>
    public static string RequestPath(string uniqueName, string token) =>
        $"/org/freedesktop/portal/desktop/request/{uniqueName.TrimStart(':').Replace('.', '_')}/{token}";
}
