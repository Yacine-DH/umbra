using Umbra.Linux;

namespace Umbra;

/// <summary>
/// The Settings side of the GNOME Shell extension (#187): whether to offer it (<see cref="GnomeShell.OfferFor"/>, once, see
/// <see cref="TakeOffer"/>) and installing it for the user after their click and a host-drawn confirmation: the extension
/// folder shipped next to Umbra is copied to <c>$XDG_DATA_HOME/gnome-shell/extensions</c> and enabled with
/// <c>gnome-extensions enable</c>; GNOME Wayland loads it at the next login. Nothing here runs without that click.
/// </summary>
static class GnomeExtensionSetup
{
    static string DataHome => Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d
        ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

    static string Target => Path.Combine(DataHome, "gnome-shell", "extensions", GnomeShell.Uuid);
    static string Source => Path.Combine(AppPaths.AppDir, "gnome-extension", GnomeShell.Uuid);
    static string Marker => Path.Combine(AppPaths.DataDir, "gnome-extension-hint-shown");
    static bool Flatpak => LinuxAppPaths.IsFlatpak(File.Exists);

    static bool Installed() =>
        File.Exists(Path.Combine(Target, "metadata.json")) || File.Exists(Path.Combine("/usr/share/gnome-shell/extensions", GnomeShell.Uuid, "metadata.json"));

    /// <summary>
    /// What one Settings window shows: "install", "link" (Flatpak), "relogin" or null. The first offer is recorded, so the
    /// hint appears in one Settings window only (the user can still install it from linux/install.sh).
    /// </summary>
    public static string? TakeOffer()
    {
        var offer = GnomeShell.OfferFor(Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"), Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
            Program.Backend, Installed(), File.Exists(Marker));
        if (offer == GnomeShell.Offer.None) return null;
        try { File.WriteAllText(Marker, DateTime.UtcNow.ToString("O")); } catch (Exception ex) { Log.Write($"Could not record the GNOME hint: {ex.Message}"); }
        return offer == GnomeShell.Offer.Relogin ? "relogin" : Flatpak ? "link" : "install";
    }

    public static void Dismiss()
    {
        try { File.WriteAllText(Marker, DateTime.UtcNow.ToString("O")); } catch (Exception ex) { Log.Write($"Could not record the GNOME hint: {ex.Message}"); }
    }

    /// <summary>After the user's click: confirm in a host-drawn dialog, copy, enable, and say what happens next.</summary>
    public static async Task InstallAsync(Gtk.Window? parent)
    {
        if (Flatpak) { WebHost.OpenExternal(GnomeShell.FlatpakHint); return; }
        if (!Directory.Exists(Source)) { await Dialogs.InfoAsync(parent, "This Umbra build does not include the GNOME extension."); return; }
        if (!await Dialogs.ConfirmAsync(parent, "Install the Umbra GNOME Shell extension?",
                $"It is copied to {Target} and turned on for your user. GNOME starts it the next time you log in.", "Install", "Cancel")) return;
        try
        {
            if (Directory.Exists(Target)) Directory.Delete(Target, recursive: true);
            Directory.CreateDirectory(Target);
            foreach (var file in Directory.GetFiles(Source)) File.Copy(file, Path.Combine(Target, Path.GetFileName(file)));
            var enabled = Run("gnome-extensions", "enable", GnomeShell.Uuid);
            Dismiss();
            Log.Write($"GNOME extension installed in {Target}{(enabled ? " and enabled" : "")}");
            await Dialogs.InfoAsync(parent, enabled
                ? "Installed. Log out and back in, and Umbra pins your widgets through the extension."
                : $"Installed in {Target}. Turn it on in the Extensions app, then log out and back in.");
        }
        catch (Exception ex)
        {
            Log.Write($"GNOME extension install failed: {ex.Message}");
            await Dialogs.InfoAsync(parent, $"The extension could not be installed: {ex.Message}");
        }
    }

    static bool Run(string exe, params string[] args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null || !p.WaitForExit(5000)) return false;
            return p.ExitCode == 0;
        }
        catch (Exception ex) { Log.Verbose($"{exe} failed: {ex.Message}"); return false; }
    }
}
