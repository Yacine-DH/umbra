using System.Security.Cryptography;
using Umbra.Linux;

namespace Umbra;

/// <summary>
/// The pure half of <see cref="GnomeExtensionPin"/> (#187): the D-Bus contract with Umbra's GNOME Shell extension
/// (<c>linux/gnome-extension/umbra@aminechr54.github.io</c>), how a window is marked as Umbra's (a per-window random token
/// in its title, registered over D-Bus), the call arguments, the replies, the mapping between GDK's logical monitor
/// rectangles and the compositor's stage, and when Settings offers to install the extension. No GTK, no bus.
/// </summary>
static class GnomeShell
{
    public const string BusName = "io.github.aminechr54.Umbra.Shell";
    public const string ObjectPath = "/io/github/aminechr54/Umbra/Shell";
    public const string Interface = BusName;
    public const string Uuid = "umbra@aminechr54.github.io";

    /// <summary>The protocol version this host speaks; the extension's <c>GetVersion</c> must answer it.</summary>
    public const uint Version = 1;

    /// <summary>The window title prefix the extension looks for: <c>umbra:</c> followed by the token.</summary>
    public const string TitlePrefix = "umbra:";

    /// <summary>A fresh random token: 128 bits as 32 lowercase hex digits. One per window, never reused.</summary>
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>Exactly 32 lowercase hex digits (what the extension accepts).</summary>
    public static bool IsToken(string? s) => s is { Length: 32 } && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string Title(string token) =>
        IsToken(token) ? TitlePrefix + token : throw new ArgumentException("not a window token", nameof(token));

    /// <summary>The token in a window title, or null when the title is not exactly <c>umbra:&lt;token&gt;</c>.</summary>
    public static string? TokenFromTitle(string? title) =>
        title is not null && title.StartsWith(TitlePrefix, StringComparison.Ordinal) && IsToken(title[TitlePrefix.Length..]) ? title[TitlePrefix.Length..] : null;

    /// <summary>True when the extension answered a version this host can use.</summary>
    public static bool Compatible(uint? version) => version == Version;

    public static string AddArgs(string token, bool overlay) => $"({V.Str(Checked(token))}, {V.Str(overlay ? "overlay" : "widget")})";
    public static string TokenArgs(string token) => $"({V.Str(Checked(token))},)";
    public static string MoveArgs(string token, Point at) => $"({V.Str(Checked(token))}, {V.Int(at.X)}, {V.Int(at.Y)}, 0, 0)";
    public static string FocusArgs(string token, bool on) => $"({V.Str(Checked(token))}, {V.Bool(on)})";

    static string Checked(string token) => IsToken(token) ? token : throw new ArgumentException("not a window token", nameof(token));

    /// <summary>One monitor as <c>GetMonitors</c> reports it, in stage coordinates.</summary>
    public readonly record struct Monitor(Rectangle Bounds, Rectangle Work, string? Connector = null);

    /// <summary>True for the extension's refusal of a token another caller holds (<c>AddWindow</c>'s AccessDenied).</summary>
    public static bool TokenTaken(string? error) =>
        error is not null && (error.Contains("AccessDenied", StringComparison.Ordinal) || error.Contains("token in use", StringComparison.Ordinal));

    /// <summary>The primary monitor's work area from a <c>GetMonitors</c> reply, or null when the reply does not make sense.</summary>
    public static Rectangle? PrimaryWorkArea(int primary, IReadOnlyList<Monitor> monitors)
    {
        if (primary < 0 || primary >= monitors.Count) return null;
        var m = monitors[primary];
        var work = Rectangle.Intersect(m.Work, m.Bounds);
        return work.Width > 0 && work.Height > 0 ? work : null;
    }

    /// <summary>A <c>GetPointer</c> reply as a pointer state; null when the extension does not tell (the pointer is elsewhere).</summary>
    public static PointerState? Pointer(bool known, Point at, uint mask)
    {
        if (!known) return null;
        const uint Shift = 1, Control = 4, Buttons = (1 << 8) | (1 << 9) | (1 << 10); // Clutter.ModifierType, as GDK's
        return new PointerState(at, (mask & Buttons) != 0, (mask & Shift) != 0, (mask & Control) != 0);
    }

    /// <summary>A monitor rectangle and its connector name ("DP-1"; null or empty when unknown).</summary>
    public readonly record struct Output(Rectangle Bounds, string? Connector);

    /// <summary>
    /// Maps a point between two descriptions of the same monitors: GDK's logical rectangles and the compositor's stage
    /// (which differ on a scaled monitor when Mutter lays monitors out in physical pixels). The monitor holding the point
    /// is paired with the one of the same connector on the other side (their order is not guaranteed to agree), and the
    /// point keeps its proportional place on it. No monitor holding the point, or no connector to pair it with: unchanged.
    /// </summary>
    public static Point Map(Point p, IReadOnlyList<Output> from, IReadOnlyList<Output> to)
    {
        var i = -1;
        for (var k = 0; k < from.Count; k++) if (from[k].Bounds.Contains(p)) { i = k; break; }
        if (i < 0 || string.IsNullOrEmpty(from[i].Connector)) return p;
        var j = -1;
        for (var k = 0; k < to.Count; k++) if (to[k].Connector == from[i].Connector) { j = k; break; }
        if (j < 0) return p;
        var (a, b) = (from[i].Bounds, to[j].Bounds);
        if (a.Width <= 0 || a.Height <= 0) return p;
        return new Point(
            b.X + (int)Math.Round((p.X - a.X) * (double)b.Width / a.Width),
            b.Y + (int)Math.Round((p.Y - a.Y) * (double)b.Height / a.Height));
    }

    /// <summary>What Settings says about the extension: nothing, offer to install it, or tell the user to turn it on.</summary>
    public enum Offer { None, Install, TurnOn }

    /// <summary>
    /// The one-time Settings hint: only on a GNOME Wayland session that fell back to XWayland (the extension is not
    /// running), never under Flatpak (the extension identifies the caller by pid, which a sandboxed app cannot pass:
    /// the D-Bus proxy's pid is not the window's; #196 decides), and only until the user dismissed it. Installed but not
    /// running means disabled, just installed (no login since) or a version this Umbra does not speak: turn it on.
    /// </summary>
    public static Offer OfferFor(string? currentDesktop, string? sessionType, PinBackend backend, bool installed, bool dismissed, bool flatpak)
    {
        if (dismissed || flatpak || backend != PinBackend.X11) return Offer.None;
        var gnome = (currentDesktop ?? "").Split(':').Any(d => d.Trim().Equals("GNOME", StringComparison.OrdinalIgnoreCase));
        if (!gnome || !string.Equals(sessionType?.Trim(), "wayland", StringComparison.OrdinalIgnoreCase)) return Offer.None;
        return installed ? Offer.TurnOn : Offer.Install;
    }

    /// <summary>
    /// <c>org.gnome.shell enabled-extensions</c> with this extension added, from <c>gsettings get</c> output (<c>['a', 'b']</c>
    /// or <c>@as []</c>), as GVariant text for <c>gsettings set</c>; the other entries are kept as they are. Null when the
    /// text cannot be read (then nothing is written) or the extension is already listed.
    /// </summary>
    public static string? EnabledWith(string gsettingsValue)
    {
        var list = ParseStrings(gsettingsValue);
        if (list is null || list.Contains(Uuid)) return null;
        list.Add(Uuid);
        return V.Strings(list);
    }

    /// <summary>
    /// <c>org.gnome.shell disabled-extensions</c> without this extension (that list wins over the enabled one), or null
    /// when it is not listed or the text cannot be read.
    /// </summary>
    public static string? DisabledWithout(string gsettingsValue)
    {
        var list = ParseStrings(gsettingsValue);
        if (list is null || !list.Remove(Uuid)) return null;
        while (list.Remove(Uuid)) { }
        return V.Strings(list);
    }

    /// <summary>A GVariant string array as gsettings prints it, or null when it is anything else.</summary>
    public static List<string>? ParseStrings(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("@as ", StringComparison.Ordinal)) t = t[4..].TrimStart();
        if (t.Length < 2 || t[0] != '[' || t[^1] != ']') return null;
        var list = new List<string>();
        var i = 1;
        while (true)
        {
            while (i < t.Length - 1 && (t[i] == ' ' || t[i] == ',')) i++;
            if (i >= t.Length - 1) return list;
            var q = t[i];
            if (q != '\'' && q != '"') return null;
            var sb = new System.Text.StringBuilder();
            i++;
            while (i < t.Length - 1 && t[i] != q) { if (t[i] == '\\' && i + 1 < t.Length - 1) i++; sb.Append(t[i]); i++; }
            if (i >= t.Length - 1) return null;
            list.Add(sb.ToString());
            i++;
        }
    }
}
