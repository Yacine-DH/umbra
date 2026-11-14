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
    public readonly record struct Monitor(Rectangle Bounds, Rectangle Work);

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

    /// <summary>
    /// Maps a point between two descriptions of the same monitors: GDK's logical rectangles and the compositor's stage
    /// (which differ on a scaled monitor when Mutter lays monitors out in physical pixels). Monitors are matched by index;
    /// the point keeps its proportional place on the monitor that holds it (else the first). Lists of different lengths,
    /// or empty ones, leave the point as it is.
    /// </summary>
    public static Point Map(Point p, IReadOnlyList<Rectangle> from, IReadOnlyList<Rectangle> to)
    {
        if (from.Count == 0 || from.Count != to.Count) return p;
        var i = 0;
        for (var k = 0; k < from.Count; k++) if (from[k].Contains(p)) { i = k; break; }
        var (a, b) = (from[i], to[i]);
        if (a.Width <= 0 || a.Height <= 0) return p;
        return new Point(
            b.X + (int)Math.Round((p.X - a.X) * (double)b.Width / a.Width),
            b.Y + (int)Math.Round((p.Y - a.Y) * (double)b.Height / a.Height));
    }

    /// <summary>What Settings says about the extension: nothing, offer to install it, or tell the user to log in again.</summary>
    public enum Offer { None, Install, Relogin }

    /// <summary>
    /// The one-time Settings hint: only on a GNOME Wayland session that fell back to XWayland (the extension is not
    /// running), outside Flatpak (there it links to the extension page instead, see <see cref="FlatpakHint"/>), and only
    /// until the user dismissed it. Installed but not running means it was just installed or is disabled: log in again.
    /// </summary>
    public static Offer OfferFor(string? currentDesktop, string? sessionType, PinBackend backend, bool installed, bool dismissed)
    {
        if (dismissed || backend != PinBackend.X11) return Offer.None;
        var gnome = (currentDesktop ?? "").Split(':').Any(d => d.Trim().Equals("GNOME", StringComparison.OrdinalIgnoreCase));
        if (!gnome || !string.Equals(sessionType?.Trim(), "wayland", StringComparison.OrdinalIgnoreCase)) return Offer.None;
        return installed ? Offer.Relogin : Offer.Install;
    }

    /// <summary>Under Flatpak the extension cannot be copied from inside the sandbox: Settings links here instead.</summary>
    public const string FlatpakHint = "https://github.com/AmineChr54/umbra/tree/main/linux/gnome-extension";
}
