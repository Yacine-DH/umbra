using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Umbra;

/// <summary>Tray icon text and the "update ready" badge. Pure parts are unit tested.</summary>
static partial class Tray
{
    /// <summary>NotifyIcon.Text throws above this many characters.</summary>
    public const int MaxTooltip = 127;

    /// <summary>"Umbra · 6 widgets" plus " · Edit mode", " · Safe mode" and " · Update ready" when they apply; a timelapse label ("Recording timelapse") comes first so it is never cut.</summary>
    public static string Tooltip(int widgets, bool editMode, bool safeMode, bool updateReady, string? timelapse = null, int scripts = 0)
    {
        var s = (timelapse is null ? "Umbra · " : $"Umbra · {timelapse} · ") + $"{widgets} widget{(widgets == 1 ? "" : "s")}";
        if (scripts > 0) s += $" · {scripts} script{(scripts == 1 ? "" : "s")} running";
        if (editMode) s += " · Edit mode";
        if (safeMode) s += " · Safe mode";
        if (updateReady) s += " · Update ready";
        return s.Length <= MaxTooltip ? s : s[..MaxTooltip];
    }

    /// <summary>Text for a menu item: one line, at most 60 characters, and "&amp;" shown as itself (not an access key).</summary>
    public static string MenuText(string s)
    {
        s = new string(s.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        if (s.Length > 60) s = s[..59] + "…";
        return s.Replace("&", "&&");
    }

    /// <summary>Command-line flag of the replacement process started by "Restart Umbra"; it waits for the old instance to exit.</summary>
    public const string RestartArg = "--restart";

    /// <summary>How long a restarted Umbra waits for the previous instance to exit.</summary>
    public static readonly TimeSpan RestartWait = SingleInstance.RestartWait;

    /// <summary>The "update ready" dot.</summary>
    public static readonly Color UpdateColor = Color.FromArgb(255, 255, 159, 28);
    /// <summary>The "camera is recording" dot (a timelapse is running).</summary>
    public static readonly Color RecordingColor = Color.FromArgb(255, 232, 17, 35);

    /// <summary>
    /// The app icon at the tray's current icon size with a small dot (orange by default) in the bottom-right corner. The dark ring
    /// keeps the dot visible on light and dark taskbars. Returns an icon the caller owns (dispose it when replaced).
    /// </summary>
    public static Icon WithBadge(Icon source, Size size, Color? color = null)
    {
        using var baseIcon = new Icon(source, size);
        using var bmp = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawIcon(baseIcon, new Rectangle(Point.Empty, size));
            var d = Math.Max(6, (int)Math.Round(size.Width * 0.46));
            var ring = Math.Max(1f, size.Width / 14f);
            var dot = new RectangleF(size.Width - d - ring / 2, size.Height - d - ring / 2, d, d);
            using var fill = new SolidBrush(color ?? UpdateColor);
            using var pen = new Pen(Color.FromArgb(255, 24, 24, 28), ring);
            g.FillEllipse(fill, dot);
            g.DrawEllipse(pen, dot);
        }
        var hIcon = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(hIcon).Clone(); }
        finally { DestroyIcon(hIcon); }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr hIcon);
}
