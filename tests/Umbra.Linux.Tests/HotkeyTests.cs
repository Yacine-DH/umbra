namespace Umbra.Linux.Tests;

/// <summary>Global hotkeys on Linux (#192): backend choice, the X11 grab and portal trigger mapping, and the reported state.</summary>
public sealed class HotkeyTests
{
    static Func<string, string?> Env(params (string Key, string Value)[] vars) => k => vars.FirstOrDefault(v => v.Key == k).Value;

    [Fact]
    public void Wayland_uses_the_portal_when_it_exists_and_never_grabs_through_xwayland()
    {
        var wayland = Env(("WAYLAND_DISPLAY", "wayland-0"), ("DISPLAY", ":0"));
        Assert.Equal(HotkeyBackend.Portal, HotkeyRules.Choose(wayland, 1));
        Assert.Equal(HotkeyBackend.None, HotkeyRules.Choose(wayland, 0)); // GNOME 46
    }

    [Fact]
    public void A_plain_x11_session_grabs_on_the_x_server()
    {
        Assert.Equal(HotkeyBackend.X11, HotkeyRules.Choose(Env(("DISPLAY", ":0")), 0));
        Assert.Equal(HotkeyBackend.X11, HotkeyRules.Choose(Env(("DISPLAY", ":0")), 1));
        Assert.Equal(HotkeyBackend.None, HotkeyRules.Choose(Env(), 0));
    }

    [Theory]
    [InlineData("Ctrl+Alt+E", HotkeyRules.ControlMask | HotkeyRules.Mod1Mask, "e")]
    [InlineData("win+shift+5", HotkeyRules.Mod4Mask | HotkeyRules.ShiftMask, "5")]
    [InlineData("Ctrl+F12", HotkeyRules.ControlMask, "F12")]
    [InlineData("Alt+PageUp", HotkeyRules.Mod1Mask, "Prior")]
    [InlineData("Ctrl+Alt+Space", HotkeyRules.ControlMask | HotkeyRules.Mod1Mask, "space")]
    [InlineData("Ctrl+Backspace", HotkeyRules.ControlMask, "BackSpace")]
    public void Combinations_become_x11_grabs(string keys, uint mask, string keysym) =>
        Assert.Equal((mask, keysym), HotkeyRules.ToX11(keys));

    [Theory]
    [InlineData("E")]
    [InlineData("Shift+E")]
    [InlineData("Ctrl+Nope")]
    [InlineData("")]
    public void Invalid_combinations_are_never_grabbed(string keys)
    {
        Assert.Null(HotkeyRules.ToX11(keys));
        Assert.Null(HotkeyRules.ToPortalTrigger(keys));
    }

    [Fact]
    public void Caps_lock_and_num_lock_do_not_change_the_match()
    {
        var mask = HotkeyRules.ControlMask | HotkeyRules.Mod1Mask;
        foreach (var v in HotkeyRules.LockVariants) Assert.Equal(mask, HotkeyRules.CleanState(mask | v));
        Assert.Equal(4, HotkeyRules.LockVariants.Distinct().Count());
    }

    [Theory]
    [InlineData("Ctrl+Alt+E", "CTRL+ALT+e")]
    [InlineData("Win+Shift+F3", "SHIFT+LOGO+F3")]
    [InlineData("alt+ctrl+down", "CTRL+ALT+Down")]
    public void Combinations_become_portal_triggers(string keys, string trigger) =>
        Assert.Equal(trigger, HotkeyRules.ToPortalTrigger(keys));

    [Fact]
    public void Portal_shortcuts_use_the_windows_descriptions_and_skip_conflicts()
    {
        var map = new Dictionary<string, string> { ["open-settings"] = "Ctrl+Alt+S", ["edit-mode"] = "Ctrl+Alt+E", ["peek"] = "Ctrl+Alt+E" };
        var list = HotkeyRules.PortalShortcuts(map);
        Assert.Equal(["edit-mode", "open-settings"], list.Select(s => s.Id));
        Assert.Equal(Hotkey.Actions.First(a => a.Id == "edit-mode").Label, list[0].Description);
        Assert.Equal("CTRL+ALT+s", list[1].Trigger);
    }

    [Fact]
    public void Unsupported_desktops_report_every_set_hotkey_as_failed_with_the_reason()
    {
        var arr = HotkeyRules.Describe(new Dictionary<string, string> { ["edit-mode"] = "Ctrl+Alt+E" }, HotkeyBackend.None, new Dictionary<string, HotkeyRules.ActionState>());
        Assert.Equal(Hotkey.Actions.Length, arr.Count);
        var edit = arr.First(h => (string?)h!["action"] == "edit-mode")!;
        Assert.Equal("failed", (string?)edit["status"]);
        Assert.Contains("not supported on this desktop", (string?)edit["error"]);
        Assert.Equal("off", (string?)arr.First(h => (string?)h!["action"] == "peek")!["status"]);
    }

    [Fact]
    public void Backend_states_are_reported_as_they_are()
    {
        var map = new Dictionary<string, string> { ["edit-mode"] = "ctrl+alt+e", ["peek"] = "Ctrl+Alt+P" };
        var states = new Dictionary<string, HotkeyRules.ActionState>
        {
            ["edit-mode"] = new("active", Note: "Desktop shortcut: Ctrl+Alt+E"),
            ["peek"] = new("pending", Note: "Confirm it"),
        };
        var arr = HotkeyRules.Describe(map, HotkeyBackend.Portal, states);
        var edit = arr.First(h => (string?)h!["action"] == "edit-mode")!;
        Assert.Equal(("Ctrl+Alt+E", "active", "Desktop shortcut: Ctrl+Alt+E"), ((string?)edit["keys"], (string?)edit["status"], (string?)edit["note"]));
        Assert.Null(edit["error"]);
        Assert.Equal("pending", (string?)arr.First(h => (string?)h!["action"] == "peek")!["status"]);
        // Set, but the backend never registered it: not silently "active".
        var missing = HotkeyRules.Describe(new Dictionary<string, string> { ["peek"] = "Ctrl+Alt+P" }, HotkeyBackend.X11, new Dictionary<string, HotkeyRules.ActionState>());
        Assert.Equal("failed", (string?)missing.First(h => (string?)h!["action"] == "peek")!["status"]);
    }

    [Fact]
    public void Request_paths_follow_the_portal_naming()
    {
        Assert.Equal("/org/freedesktop/portal/desktop/request/1_42/u7_1", HotkeyRules.RequestPath(":1.42", "u7_1"));
    }
}
