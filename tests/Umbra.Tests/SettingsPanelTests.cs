using System.Text.RegularExpressions;
using Xunit;

namespace Umbra.Tests;

public class SettingsPanelTests
{
    static string Repo()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "Umbra.slnx"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        return root!;
    }

    /// <summary>Ctrl+1..9 in settings.js picks the Nth sidebar button, so the sidebar must list the pages in the host's order.</summary>
    [Fact]
    public void Sidebar_order_matches_pages_so_ctrl_number_shortcuts_line_up()
    {
        var html = File.ReadAllText(Path.Combine(Repo(), "settings", "index.html"));
        var nav = Regex.Match(html, "<nav class=\"nav\".*?</nav>", RegexOptions.Singleline).Value;
        var pages = Regex.Matches(nav, "data-page=\"(\\w+)\"").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(SettingsWindow.Pages, pages);
        Assert.True(pages.Length <= 9, "Ctrl+1..9 is all the shortcut covers");
    }

    /// <summary>Settings opens on Home, the first page in the sidebar and the one the markup starts on.</summary>
    [Fact]
    public void Home_is_the_landing_page()
    {
        Assert.Equal("home", SettingsWindow.Pages[0]);
        var html = Read("index.html");
        Assert.Contains("<button data-page=\"home\" class=\"active\"", html);
        Assert.Contains("<section class=\"page active\" data-page=\"home\">", html);
        Assert.Single(Regex.Matches(html, "class=\"page active\""));
    }

    static string Page(string name) =>
        Regex.Match(Read("index.html"), $"<section class=\"page[^\"]*\" data-page=\"{name}\">.*?</section>", RegexOptions.Singleline).Value;

    /// <summary>General shows only the everyday settings; the rest wait behind a fold-out, here or on Appearance.</summary>
    [Fact]
    public void General_keeps_a_short_list_and_folds_the_rest()
    {
        var general = Page("general");
        var fold = general.IndexOf("<details", StringComparison.Ordinal);
        Assert.True(fold > 0);
        Assert.True(Regex.Matches(general[..fold], "class=\"row\"").Count <= 5, "keep General short (Zoom plus four): new settings go under Advanced or Customize");
        Assert.Contains("id=\"advancedRows\"", general[fold..]);
        Assert.Contains("id=\"appearanceRows\"", Page("themes"));
        Assert.DoesNotContain(" open", Regex.Match(general, "<details[^>]*>").Value);

        var prefs = Read("prefs.js");
        foreach (var look in new[] { "Scale all widgets", "Accent color", "Animations" }) Assert.Contains($"look.append(row(\"{look}\"", prefs);
        foreach (var adv in new[] { "Proxy", "Developer mode", "Slow down on Energy Saver", "Peek duration" }) Assert.Contains($"adv.append(row(\"{adv}\"", prefs);
    }

    /// <summary>Home only suggests widgets that can't run programs or use the camera, and installs through the normal job.</summary>
    [Fact]
    public void Home_suggestions_stay_low_risk()
    {
        var js = Read("settings.js");
        Assert.Contains("!w.camera && !w.userScripts && !(w.commands || []).length", js);
        var card = Regex.Match(js, @"function homePickCard\(w\) \{.*?\n\}", RegexOptions.Singleline).Value;
        Assert.Contains("permissionChips(", card);
        Assert.Contains("job(w.id, \"install\", btn)", card);
    }

    /// <summary>A folded card opens by itself when the right-click menu's Options asks for it.</summary>
    [Fact]
    public void Widget_cards_start_folded_and_open_on_request()
    {
        var js = Read("settings.js");
        Assert.Contains("setFolded(!openCards().has(w.id), false);", js);
        var focus = Regex.Match(js, @"function applyFocus\(\) \{.*?\n\}", RegexOptions.Singleline).Value;
        Assert.Contains(".fold", focus);
    }

    /// <summary>Approve and Revoke sit in the folded part of a card, so the row has to say when a command needs the user.</summary>
    [Fact]
    public void Folded_cards_show_command_status_on_the_row()
    {
        var js = Read("settings.js");
        foreach (var label in new[] { "\"Command changed\"", "\"Needs approval\"", "\"Runs programs\"", "\"Uses camera\"" }) Assert.Contains(label, js);
        Assert.Contains("access.classList.toggle(\"attention\"", js);
    }

    /// <summary>The protocol's command list names the same pages as the host.</summary>
    [Fact]
    public void Capture_help_lists_the_host_pages()
    {
        var protocol = File.ReadAllText(Path.Combine(Repo(), "src", "Umbra.Core", "Protocol.cs"));
        Assert.Contains($"settings:<{string.Join('|', SettingsWindow.Pages)}>", protocol);
    }

    /// <summary>The welcome tour is an accessible modal that rings real controls; the page behind stays inert.</summary>
    [Fact]
    public void Welcome_tour_is_a_modal_spotlight_over_real_controls()
    {
        var html = File.ReadAllText(Path.Combine(Repo(), "settings", "index.html"));
        Assert.Contains("id=\"welcome\"", html);
        Assert.Contains("role=\"dialog\" aria-modal=\"true\"", html);
        Assert.Contains("id=\"tourRing\"", html);
        var js = File.ReadAllText(Path.Combine(Repo(), "settings", "settings.js"));
        var steps = Regex.Match(js, @"const WELCOME = \[.*?\n\];", RegexOptions.Singleline).Value;
        var count = Regex.Matches(steps, @"\btitle:").Count;
        Assert.InRange(count, 8, 14);
        Assert.Equal(count, Regex.Matches(steps, @"\bpage: ""(home|browse|widgets|themes|layouts|hotkeys|general|privacy|about)""").Count);
        // Every #id a step rings exists in the page, so a renamed control can't quietly turn a step into a blank slide.
        foreach (Match m in Regex.Matches(steps, @"#([A-Za-z][\w-]*)")) Assert.Contains($"id=\"{m.Groups[1].Value}\"", html);
        Assert.Contains("document.querySelector(\".app\").inert = true", js);
    }

    /// <summary>"How to add widgets": one (i) button on Browse and one on My widgets, covering the catalog and your own widgets.</summary>
    [Fact]
    public void How_to_add_widgets_popover_is_on_browse_and_my_widgets()
    {
        var html = File.ReadAllText(Path.Combine(Repo(), "settings", "index.html"));
        Assert.Equal(2, Regex.Matches(html, @"<button[^>]*\bdata-help\b").Count);
        var pop = Regex.Match(html, "<div class=\"helppop\".*?\n  </div>", RegexOptions.Singleline).Value;
        foreach (var s in new[] { "From the catalog", "Install", "Arrange", "Install from", ".umbrawidget" }) Assert.Contains(s, pop);
        Assert.DoesNotContain("class=\"howto\"", html);
    }

    static string Read(string name) => File.ReadAllText(Path.Combine(Repo(), "settings", name));

    /// <summary>#281: a widget card has exactly one notifications control (the Default/On/Off select), no extra checkbox.</summary>
    [Fact]
    public void Widget_card_has_one_notifications_control()
    {
        var card = Regex.Match(Read("index.html"), "<template id=\"tpl-installed\">.*?</template>", RegexOptions.Singleline).Value;
        Assert.Single(Regex.Matches(card, "pw-notif\"")); // the select; its wrapper is pw-notif-l
        Assert.DoesNotContain("Allow notifications", card);
        Assert.DoesNotContain("class=\"notif\"", card);
        Assert.DoesNotContain("<input type=\"checkbox\" class=\"notif\"", card);
    }

    /// <summary>#282: host options share the .field row pattern, booleans are switches, Advanced folds the rest.</summary>
    [Fact]
    public void Card_host_options_use_one_row_pattern_and_switches()
    {
        var card = Regex.Match(Read("index.html"), "<template id=\"tpl-installed\">.*?</template>", RegexOptions.Singleline).Value;
        var host = Regex.Match(card, "<div class=\"hostopts\">.*?<div class=\"wcard-actions\">", RegexOptions.Singleline).Value;
        Assert.DoesNotContain("class=\"check", host);
        Assert.Contains("class=\"switch\"><input type=\"checkbox\" class=\"pw-hidden\"", host);
        var visible = host[..host.IndexOf("hostadv-src", StringComparison.Ordinal)];
        foreach (var keep in new[] { "Size", "Monitor", "Start hidden" }) Assert.Contains(keep, visible);
        var advanced = host[host.IndexOf("hostadv-src", StringComparison.Ordinal)..];
        foreach (var fold in new[] { "Refresh", "Opacity", "Notifications", "Show only between", "Power" }) Assert.Contains(fold, advanced);
        Assert.Contains("\"Advanced\"", Read("prefs.js"));
    }

    /// <summary>#283: the two size settings have distinct names that say what they do to existing widgets.</summary>
    [Fact]
    public void General_size_controls_have_distinct_names()
    {
        var prefs = Read("prefs.js");
        Assert.Contains("row(\"Scale all widgets\"", prefs);
        Assert.Contains("row(\"Size of new widgets\"", prefs);
        Assert.Contains("keep their size", prefs);
        Assert.DoesNotContain("row(\"Default size\"", prefs);
    }

    /// <summary>#284 and #285: one Install from menu, a quiet Refresh icon, Update all hidden (also against .btn's display) at zero.</summary>
    [Fact]
    public void Browse_groups_install_entry_points_and_hides_empty_update_all()
    {
        var html = Read("index.html");
        var hero = Regex.Match(html, "<div class=\"hero-actions\">.*?</div>", RegexOptions.Singleline).Value;
        Assert.Equal(3, Regex.Matches(hero, "<button").Count); // Submit, Install from, Refresh (How to add widgets is a link in the text)
        Assert.Contains("id=\"installMenu\"", hero);
        Assert.Contains("aria-label=\"Refresh the catalog\"", hero);
        Assert.DoesNotContain("id=\"installFile\"", html);
        var js = Read("settings.js");
        foreach (var kind in new[] { "kind: \"file\"", "kind: \"folder\"", "type: \"installUrl\"" }) Assert.Contains(kind, js);
        Assert.Contains("all.hidden = !n && !updatingAll;", js);
        Assert.Contains(".btn[hidden]", Read("settings.css"));
    }

    /// <summary>#286: Data and privacy shows display names (the host sends one) and disables Delete data at 0 B.</summary>
    [Fact]
    public void Privacy_rows_use_display_names_and_disable_empty_delete()
    {
        var js = Read("settings.js");
        Assert.Contains("w.name || w.widget", js);
        Assert.Contains("del.disabled = !w.data.bytes;", js);
        Assert.Contains("[\"name\"]", File.ReadAllText(Path.Combine(Repo(), "src", "Umbra.Core", "Backup.cs")));
    }

    /// <summary>The Esc shortcut in the page asks the host to close the window.</summary>
    [Fact]
    public void Page_posts_close_and_host_handles_it()
    {
        var root = Repo();
        Assert.Contains("type: \"close\"", File.ReadAllText(Path.Combine(root, "settings", "settings.js")));
        Assert.Contains("case \"close\":", File.ReadAllText(Path.Combine(root, "src", "Umbra", "SettingsWindow.cs")));
    }
}
