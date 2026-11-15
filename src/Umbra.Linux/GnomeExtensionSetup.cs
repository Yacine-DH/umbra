using Umbra.Linux;

namespace Umbra;

/// <summary>
/// The Settings side of the GNOME Shell extension (#187): whether to offer it (<see cref="GnomeShell.OfferFor"/>, once, see
/// <see cref="TakeOffer"/>) and installing it for the user after their click and a host-drawn confirmation: the extension
/// folder shipped next to Umbra is copied to <c>$XDG_DATA_HOME/gnome-shell/extensions</c> and added to
/// <c>org.gnome.shell enabled-extensions</c> (appended, the rest of the list kept), so it starts at the next login.
/// <c>gnome-extensions enable</c> cannot be used: GNOME Shell only knows extensions it found when it started. Nothing
/// here runs without that click; <c>linux/install.sh</c> never enables it.
/// </summary>
static class GnomeExtensionSetup
{
    static string DataHome => Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d
        ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

    static string Target => Path.Combine(DataHome, "gnome-shell", "extensions", GnomeShell.Uuid);
    static string Source => Path.Combine(AppPaths.AppDir, "gnome-extension", GnomeShell.Uuid);
    static string Marker => Path.Combine(AppPaths.DataDir, "gnome-extension-hint-shown");

    static bool Installed() =>
        File.Exists(Path.Combine(Target, "metadata.json")) || File.Exists(Path.Combine("/usr/share/gnome-shell/extensions", GnomeShell.Uuid, "metadata.json"));

    /// <summary>
    /// What one Settings window shows: "install", "turnOn" or null. The first offer is recorded, so the hint appears in one
    /// Settings window only (the user can still install it from linux/install.sh).
    /// </summary>
    public static string? TakeOffer()
    {
        var offer = GnomeShell.OfferFor(Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"), Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
            Program.Backend, Installed(), File.Exists(Marker), LinuxAppPaths.IsFlatpak(File.Exists));
        if (offer == GnomeShell.Offer.None) return null;
        Dismiss();
        return offer == GnomeShell.Offer.TurnOn ? "turnOn" : "install";
    }

    public static void Dismiss()
    {
        try { File.WriteAllText(Marker, DateTime.UtcNow.ToString("O")); } catch (Exception ex) { Log.Write($"Could not record the GNOME hint: {ex.Message}"); }
    }

    /// <summary>After the user's click: confirm in a host-drawn dialog, copy, enable for the next login, and say so.</summary>
    public static async Task InstallAsync(Gtk.Window? parent)
    {
        if (!Directory.Exists(Source)) { await Dialogs.InfoAsync(parent, "This Umbra build does not include the GNOME extension."); return; }
        if (!await Dialogs.ConfirmAsync(parent, "Install the Umbra GNOME Shell extension?",
                $"It is copied to {Target} and turned on for your user. GNOME starts it the next time you log in.", "Install", "Cancel")) return;
        try
        {
            if (Directory.Exists(Target)) Directory.Delete(Target, recursive: true);
            Directory.CreateDirectory(Target);
            foreach (var file in Directory.GetFiles(Source)) File.Copy(file, Path.Combine(Target, Path.GetFileName(file)));
            // gsettings talks to dconf: off the UI thread, each call bounded, so a hung dconf cannot freeze GTK.
            var result = await Task.Run(EnableAsync);
            Dismiss();
            Log.Write($"GNOME extension installed in {Target} ({result})");
            await Dialogs.InfoAsync(parent, result switch
            {
                EnableResult.Enabled => "Installed. Log out and back in, and Umbra pins your widgets through the extension.",
                EnableResult.UserExtensionsOff =>
                    $"Installed in {Target}, but user extensions are turned off on this computer. Turn on extensions in the Extensions app, then log out and back in.",
                _ => $"Installed in {Target}. Turn it on in the Extensions app after you log out and back in.",
            });
        }
        catch (Exception ex)
        {
            Log.Write($"GNOME extension install failed: {ex.Message}");
            await Dialogs.InfoAsync(parent, $"The extension could not be installed: {ex.Message}");
        }
    }

    enum EnableResult { Enabled, UserExtensionsOff, Failed }

    /// <summary>
    /// Adds the extension to enabled-extensions (and takes it off disabled-extensions). With user extensions turned off
    /// (<c>disable-user-extensions</c>) it still is listed, but the user is told it will not run until they turn them on.
    /// </summary>
    static async Task<EnableResult> EnableAsync()
    {
        if (await GsettingsAsync("get", "enabled-extensions") is not { } enabled) return EnableResult.Failed;
        if (GnomeShell.EnabledWith(enabled) is { } list && await GsettingsAsync("set", "enabled-extensions", list) is null) return EnableResult.Failed;
        if (await GsettingsAsync("get", "disabled-extensions") is { } disabled && GnomeShell.DisabledWithout(disabled) is { } rest)
            await GsettingsAsync("set", "disabled-extensions", rest);
        if (GnomeShell.ParseStrings(await GsettingsAsync("get", "enabled-extensions") ?? "")?.Contains(GnomeShell.Uuid) != true) return EnableResult.Failed;
        return (await GsettingsAsync("get", "disable-user-extensions"))?.Trim() == "true" ? EnableResult.UserExtensionsOff : EnableResult.Enabled;
    }

    /// <summary>One <c>gsettings</c> call on <c>org.gnome.shell</c>, at most 5 s (then killed); its output, or null on failure.</summary>
    static async Task<string?> GsettingsAsync(string verb, string key, string? value = null)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("gsettings") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in value is null ? new[] { verb, "org.gnome.shell", key } : new[] { verb, "org.gnome.shell", key, value }) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return null;
            using var cts = new CancellationTokenSource(5000);
            try
            {
                var output = p.StandardOutput.ReadToEndAsync(cts.Token);
                _ = p.StandardError.ReadToEndAsync(cts.Token);
                await p.WaitForExitAsync(cts.Token);
                return p.ExitCode == 0 ? await output : null;
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                Log.Write($"gsettings {verb} {key} timed out");
                return null;
            }
        }
        catch (Exception ex) { Log.Verbose($"gsettings failed: {ex.Message}"); return null; }
    }
}
