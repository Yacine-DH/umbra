# Linux release checklist (template)

Appended to `dist/release-checklist-<ver>.md` by `SKILL.md` when the release ships Linux assets (epic #172, issue #200). It
is derived from the acceptance criteria of the child issues #173-#199; each row names its source issue. It is a checklist,
not a plan: tick a box only after you did it on the named configuration, never from memory or from a PR description.

If a feature did not ship in this release, write `n/a (#issue, reason)` instead of ticking. Anything that fails: file an issue
labelled `platform: linux` (title with the configuration), put its number in the cell as `F #123` and in "Failures" at the end.
A failure in a **release blocker** (sandbox parity, focus gating, command approvals, control socket) stops the release.

## Per build (Linux, automated: tick after running)

- [ ] `dotnet build Umbra.Linux.slnf -c Release -warnaserror` is 0 warnings (#176)
- [ ] `dotnet test Umbra.Linux.slnf -c Release` passes (#174, #176)
- [ ] `node scripts/catalog.mjs --check` and `node scripts/check-versions.mjs` pass; once the metainfo exists (#196), its `<release>` must match `Directory.Build.props`, which `check-versions.mjs` enforces (#197)
- [ ] The Linux CI job is green, including the web-extension build (#176, #180)
- [ ] `linux/test/smoke.sh` is green under `xvfb-run` + openbox and under a headless `mutter` (or `kwin_wayland --virtual`) session, captures inspected (#181, #198)
- [ ] The hostile-widget suite (`linux/test/hostile-widget/`) blocks every vector in CI, and breaking the sandbox makes CI fail (#180, #198)
- [ ] `appstreamcli validate` and `desktop-file-validate` pass (#178, #196)
- [ ] `gnome-extensions pack` produces a valid zip (#187)
- [ ] `linux/test/update.sh` passes (notify-only update check against a fake feed; About capture inspected) (#197)
- [ ] `powershell -File build.ps1 -Linux` (Docker) wrote `dist/umbra-<ver>-linux-x64.tar.gz` and `-arm64.tar.gz`, each with a `.sha256` that matches (#197)
- [ ] The arm64 tarball's binaries are aarch64: `tar -xOzf dist/umbra-<ver>-linux-arm64.tar.gz umbra-<ver>-linux-arm64/umbra/umbra | file -` (#197)
- [ ] Assets next to `UmbraSetup-<ver>.exe` in `gh release view v<ver> --json assets -q ".assets[].name"`: `umbra-<ver>-linux-x64.tar.gz`, `umbra-<ver>-linux-arm64.tar.gz` and their `.sha256`, the `.flatpak` bundle once #196's Flatpak half lands (#196, #197)
- [ ] After publishing, an older Linux install (or `umbractl check-update` on one) reports the new version and the tray item opens the release page (#197)

## Configurations (fill in what you actually used)

| Column | Distro and version | Desktop version | Session | Hardware or VM | Monitors and scale |
|---|---|---|---|---|---|
| A: Ubuntu 24.04, GNOME 46, Wayland | | | Wayland | | |
| B: Ubuntu 25.x, GNOME 48+, Wayland | | | Wayland | | |
| C: KDE Plasma 6, Wayland | | | Wayland | | |
| D: X11 session (note GNOME/KDE/other) | | | X11 | | |

Optional, install + start only, from the epic's minimum distributions: Fedora 41, Arch. Expected backend per column (`umbractl status`
reports it, #182): A and B `GnomeExtension` (with the extension enabled), C `LayerShell`, D `X11`.

## Test matrix

Mark each cell `[x]` pass, `F #n` fail (issue filed), or `n/a (reason)`. Use a throwaway profile or a fresh user for install rows. On your own desktop, inspect only through `umbractl capture`; never take desktop or full-screen screenshots.

| Check | A | B | C | D | What to verify (source) |
|---|---|---|---|---|---|
| Install, tarball | [ ] | [ ] | [ ] | [ ] | Fresh VM: `tar x && ./install.sh`; Umbra in the app grid, starts, widgets pinned, `umbractl status` works (#196) |
| Install, Flatpak | [ ] | [ ] | [ ] | [ ] | `flatpak install umbra.flatpak`; same as above plus notifications and media (#196) |
| Widgets pinned on the wallpaper | [ ] | [ ] | [ ] | [ ] | Transparent background, below normal windows, above desktop icons, icons still clickable (#179, #182, #186, #187) |
| Not a normal window | [ ] | [ ] | [ ] | [ ] | Not in alt-tab, dock/taskbar, task manager or overview; stays on every workspace (#182, #186, #187) |
| Show Desktop / Peek | [ ] | [ ] | [ ] | [ ] | Super+D (GNOME), Peek at Desktop (KDE) leave widgets visible; Super overview and workspace switching OK (#182, #186, #187) |
| Drag, resize, snap, edit pill | [ ] | [ ] | [ ] | [ ] | Press-and-drag saves the new position; resize and Ctrl+scroll zoom only in edit mode on an unlocked widget; snap guides; pill Done; group tab strip (#183, #186) |
| Typing only after a click (focus gating) | [ ] | [ ] | [ ] | [ ] | Type in a text editor while widgets run: nothing lands in a widget; click quick-notes or todo, typing works; click back into the editor, typing goes there (#183, #188) |
| 2 monitors, unplug/replug | [ ] | [ ] | [ ] | [ ] | Widgets on each monitor; unplug clamps them back on screen; replug; move a widget across monitors (#182, #186, #187) |
| Fractional scaling 125 % | [ ] | [ ] | [ ] | [ ] | Positions, sizes and text sharp, drag still accurate (#186, #187) |
| Fractional scaling 150 % | [ ] | [ ] | [ ] | [ ] | Same (#186, #187) |
| Sleep/resume | [ ] | [ ] | [ ] | [ ] | `systemctl suspend` then resume: widgets refresh, log line present (#193) |
| Lock/unlock | [ ] | [ ] | [ ] | [ ] | Widgets still pinned and gated after unlock; no stuck focus (#187, #193) |
| Time zone and clock | [ ] | [ ] | [ ] | [ ] | Change the time zone: clock widgets update without a restart (#193) |
| Accent colour | [ ] | [ ] | [ ] | [ ] | GNOME 47+ accent change updates themed widgets live; elsewhere the default accent is used (#193) |
| Tray | [ ] | [ ] | [ ] | [ ] | Icon and menu (Settings, Edit widgets, Hide widgets, Pause, Quit) on GNOME with AppIndicator and KDE; with no watcher Umbra starts and logs the tray as unavailable (#191) |
| Notifications | [ ] | [ ] | [ ] | [ ] | Widget with `permissions.notifications` notifies; without it is refused; an action button reaches the widget (#189) |
| Media | [ ] | [ ] | [ ] | [ ] | now-playing shows Rhythmbox, Firefox and Spotify; controls only after a click; refused without `media: ["control"]`; `https://` art (Spotify) shows no art, by design (#190) |
| Hotkeys | [ ] | [ ] | [ ] | [ ] | KDE and GNOME 48+: bound shortcuts trigger the actions. GNOME 46: Settings shows "Not supported on this desktop", no errors in the log loop. X11: `XGrabKey` works. `umbractl hotkeys` reports the real state (#192) |
| Commands and approval prompt | [ ] | [ ] | [ ] | [ ] | claude-waker runs after approval from the host prompt; an unapproved or changed command asks again; a command past its timeout is killed with its children; `umbractl`/MCP cannot approve (#194) |
| Hostile-widget suite | [ ] | [ ] | [ ] | [ ] | Run `linux/test/hostile-widget/`: every vector blocked (list below) and the declared host works; log shows `sandbox blocked` (#180) |
| Crash recovery | [ ] | [ ] | [ ] | [ ] | `kill -SEGV` a widget's WebKitWebProcess: reloads; the 4th crash within 10 min shows "This widget stopped working"; Reload works (#185) |
| Uninstall leaves no autostart entry | [ ] | [ ] | [ ] | [ ] | After `uninstall.sh` / `flatpak uninstall`: no `~/.config/autostart/io.github.aminechr54.Umbra.desktop`, nothing left running; Settings "Start on login" off removes it (#178, #196, #200) |

Hostile-widget vectors, each against an undeclared host (#180): fetch, XHR, `<img>`, CSS `url()`, `@import`, `<link rel=stylesheet>`,
`<script src>`, `<iframe>`, WebSocket, EventSource, `navigator.sendBeacon`, `<form>` submit, `window.open`, `location =`,
`<audio>/<video>`, `@font-face`, Worker/`importScripts`, `<link rel=prefetch>`. Per column, record: all blocked [ ]; declared host works [ ].

## Per-feature human checks (not tied to a column; do once on the primary configuration)

### Desktop pinning, GNOME and KDE specifics
- [ ] GNOME without the extension: the XWayland fallback works and the Settings hint appears exactly once; the hint's button installs the extension from the tarball zip; the Flatpak links to extensions.gnome.org instead (#187)
- [ ] GNOME with the extension: another app cannot get its windows treated as Umbra widgets (token check); desktop icons still clickable (#187)
- [ ] KDE: wallpaper placement, Peek at Desktop, windows above widgets, widgets absent from the task manager (#186)
- [ ] Position persists across restarts (#182)
- [ ] Wayland session where `gtk_layer_is_supported()` is false falls back to X11/XWayland, and `umbractl status` says which backend (#182, #186)

### Sandbox and navigation (release blocker, maintainer review) (#180)
- [ ] All catalog widgets still load their shared JS and CSS (the `https://umbra.local/` rewrite)
- [ ] Clicking a link opens the default browser and the widget stays on its page
- [ ] `umbra.log` shows each blocked vector; the declared host works

### Settings (#184, #191, #192, #197)
- [ ] Every Settings page renders; `umbractl capture settings:<page>` for each looks right
- [ ] Browse: install a widget, update one that adds permissions (native confirm box, not HTML), remove, then Undo
- [ ] Windows-only controls are hidden or relabelled ("Start with Windows" reads "Start on login", no Windows accent or installer-update controls); Windows-only widgets are marked "Windows only" in Browse and not offered (#194, #195)
- [ ] Update check only notifies: "Update available" with a button that opens the release page; nothing is downloaded or run; Flatpak skips the check (#197)

### Widgets (#195)
- [ ] `umbractl capture` of every catalog widget looks like Windows: alignment, tabular numbers, no tofu or missing emoji
- [ ] Widgets with Windows assumptions (script-runner, study-timelapse) behave as decided in #194/#195

### Control, CLI and MCP (#175, #181)
- [ ] `umbractl status`, `list`, `install <catalog id>`, `remove`, `set`, `reload`, `capture` work against the running app
- [ ] `umbractl mcp` lists tools and `capture` returns an image; Flatpak form: `flatpak run --command=umbractl io.github.aminechr54.Umbra mcp`
- [ ] Commands that are not implemented fail with a clear message, not a crash
- [ ] Only the current user can connect to the control socket (second user account, if available)

### Data and paths (#178)
- [ ] Data in `$XDG_DATA_HOME/umbra` (`~/.local/share/umbra` by default) with the Windows layout; WebKit cache in `$XDG_CACHE_HOME/umbra`; `UMBRA_DATA_DIR` and `portable.txt` still win
- [ ] A second `umbra` launch opens Settings and does not start a second instance (#179)

### Fresh-user path (#199)
- [ ] A person who has never seen Umbra follows only the README on a fresh Ubuntu VM and gets Umbra running without other help

## Collected from merged PRs

One row per "human check" line found in the bodies of merged PRs that close an issue #173-#200, with its `#PR`.

| Done | Check | Source |
|---|---|---|
| [ ] | (collected lines go here) | #PR |

## Failures

| Column and row | Issue (label `platform: linux`) | Blocker? |
|---|---|---|
| | | |

## Ship (Linux additions to the Windows "Ship" list)

- [ ] Every blocker above closed, and every other failure has an open issue
- [ ] Release text lists the Linux assets, the supported desktops (KDE Plasma 6, GNOME 46+) and the known differences (hotkeys on GNOME 46, tray on stock GNOME, `file://`-only media art, update check notifies only) (#190, #191, #192, #197)
- [ ] The matrix above is filled and its link or copy is attached to the release PR (#200 acceptance)
