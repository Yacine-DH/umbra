<!-- Template for the Linux section of dist/release-checklist-<ver>.md. Don't copy it by hand: `node .claude/skills/release-checklist/linux.mjs`
fills the {{...}} fields (version, automated checks found in linux/test/, Flatpak status, human checks from merged Linux PRs) and appends it. -->
<!-- linux-section:start -->
## Linux (epic #172)

Derived from the acceptance criteria of #173-#200; each row names its source issue. Tick a box only after you did it on the named
configuration, never from memory or a PR description. A feature that did not ship: `n/a (#issue, reason)`. A failure: file an issue
labelled `platform: linux` (title with the configuration), write `F #123` in the cell and add it to "Failures". A failure in a
**release blocker** (sandbox parity, focus gating, command approvals, control socket) stops the release.

### Linux automated checks (run first, tick after running)

{{automated}}

### Linux packaging

- [ ] On this PC: `powershell -File build.ps1 -Linux` (Docker) wrote `dist/umbra-{{ver}}-linux-x64.tar.gz` and `-arm64.tar.gz`, each with a matching `.sha256` (#197). On a Linux machine instead: `linux/package.sh x64` (needs the .NET 10 SDK, gcc, `libwebkitgtk-6.0-dev`), or `linux/container-package.sh . dist x64` for a clean copy
- [ ] The arm64 tarball's binaries are aarch64: `tar -xOzf dist/umbra-{{ver}}-linux-arm64.tar.gz umbra-{{ver}}-linux-arm64/umbra/umbra | file -` (#197)
- [ ] The tarball holds `install.sh`, `io.github.aminechr54.Umbra.desktop`, `umbra/icons/` and `umbra/gnome-extension/umbra@aminechr54.github.io/`; `desktop-file-validate` passes on the entry (#178, #187, #196)
- [ ] Flatpak bundle: {{flatpak}}

### Configurations (fill in what you actually used)

| Column | Distro and version | Desktop version | Session | Hardware or VM | Monitors and scale |
|---|---|---|---|---|---|
| A: Ubuntu 24.04, GNOME 46, Wayland | | | Wayland | | |
| B: Ubuntu 25.x, GNOME 48+, Wayland | | | Wayland | | |
| C: KDE Plasma 6, Wayland | | | Wayland | | |
| D: X11 session (note GNOME/KDE/other) | | | X11 | | |

Expected pin backend per column (`umbractl status` shows `pinBackend`, #182): A and B `gnome-extension` with the extension enabled,
otherwise `x11` (XWayland fallback); C `layer-shell`; D `x11`. Use a fresh user or VM for the install rows. On your own desktop inspect
only through `umbractl capture`; never take desktop or full-screen screenshots.

### Test matrix

Mark each cell `[x]` pass, `F #n` fail (issue filed), or `n/a (reason)`.

| Check | A | B | C | D | What to verify (source) |
|---|---|---|---|---|---|
| Tarball install | [ ] | [ ] | [ ] | [ ] | Fresh user: `tar xzf umbra-{{ver}}-linux-x64.tar.gz && ./install.sh`: `umbra`/`umbractl` in `~/.local/bin`, Umbra in the app menu with its icon, starts, `umbractl status` answers; running it again updates in place and keeps `~/.local/share/umbra` (#196) |
| GNOME extension opt-in | [ ] | [ ] | n/a | n/a | Plain `./install.sh` copies no extension; `./install.sh --gnome-extension` copies it but never enables it; after `gnome-extensions enable umbra@aminechr54.github.io` and a new login, `pinBackend` is `gnome-extension` (#187, #196) |
| Open a .umbrawidget | [ ] | [ ] | [ ] | [ ] | After `install.sh`, Files/Dolphin show the file as "Umbra widget"; double-click opens the host-drawn review (declared access listed), Cancel installs nothing, Install installs; also works with Umbra not running (#365) |
| Tarball uninstall | [ ] | [ ] | [ ] | [ ] | `./install.sh --uninstall`: Umbra stops, app, launchers, menu entry, icons and `~/.config/autostart/io.github.aminechr54.Umbra.desktop` are gone, the extension files are removed, data stays; Settings "Start on login" off also removes the autostart file (#178, #196) |
| Pinned on the wallpaper | [ ] | [ ] | [ ] | [ ] | Transparent background, below every normal window, above the desktop icons (icons still clickable), on every workspace, not in Alt+Tab, dock/taskbar or overview; Super+D / Peek at Desktop leave widgets visible; position kept across restarts. Backends: X11 hints (D, fallback), layer-shell (C), GNOME extension (A, B) (#182, #186, #187) |
| Edit mode | [ ] | [ ] | [ ] | [ ] | Drag saves the new position; resize and Ctrl+wheel zoom only in edit mode on an unlocked widget; arrow nudge; snap guides and the Snap toggle; the pill's Done ends edit mode; group tab strip (#183, #186) |
| Focus gating | [ ] | [ ] | [ ] | [ ] | Type in a text editor while widgets run: nothing lands in a widget. Click quick-notes or todo: typing goes there; click back into the editor: typing goes there. Hostile page: install `linux/test/edit-widget` (it asks for focus, drag and resize on its own) and type elsewhere for a minute: it never takes the keyboard or moves (#183, #186, #187, release blocker) |
| Monitors, scaling, sleep | [ ] | [ ] | [ ] | [ ] | 2 monitors: widgets on each, move one across; unplug clamps widgets back on screen, replug; fractional scaling 125 % and 150 %: sharp text, right sizes, drag accurate; laptop undock/redock; `systemctl suspend` then resume: widgets refresh (log line), still pinned; lock/unlock pauses and resumes, no stuck focus (#182, #186, #187, #193) |
| Tray | [ ] | [ ] | [ ] | [ ] | KDE: icon and menu (Settings, Edit widgets, Hide widgets, Pause, Quit). GNOME with the AppIndicator extension: same. No watcher (stock GNOME without AppIndicator): Umbra starts, logs the tray as unavailable, launching `umbra` again opens Settings (#191) |
| Notifications | [ ] | [ ] | [ ] | [ ] | A widget with `permissions.notifications` asks only after a click, then notifies; refused while its switch is off; a button reaches the widget; the body click opens its url (#189) |
| Media (MPRIS) | [ ] | [ ] | [ ] | [ ] | now-playing shows Rhythmbox, Firefox and Spotify; controls only after a click; refused without `media: ["control"]`; `https://` art (Spotify) shows no art, by design (#190) |
| Hotkeys | [ ] | [ ] | [ ] | [ ] | B, C (GlobalShortcuts portal): the desktop's dialog names Umbra, the bound shortcut runs its action with another app focused, a cancelled dialog shows "Not confirmed", restored after restart without a new dialog. D (X11): works with no dialog, a taken combination says "already in use". A (GNOME 46): Settings shows "Not supported on this desktop", no repeating log errors. `umbractl hotkeys` reports the real state (#192) |
| Settings | [ ] | [ ] | [ ] | [ ] | Every page renders (`umbractl capture settings:<page>`); Browse install, update with new access (native confirm box), remove, Undo; Windows-only controls hidden or reworded ("Start on login"), Windows-only widgets marked and not offered; a second `umbra` launch opens Settings (#184, #194, #195) |
| System Monitor | [ ] | [ ] | [ ] | [ ] | CPU, RAM, network, disk activity and the mounted drives (`/`, `/home`, a USB stick by mount point); GPU percent on amdgpu or the NVIDIA driver, "n/a" on Intel/nouveau (#364) |
| Crash recovery | [ ] | [ ] | [ ] | [ ] | `kill -SEGV` a widget's WebKitWebProcess: it reloads; the 4th crash within 10 minutes shows "This widget stopped working"; Reload brings it back (#185) |
| Commands and approval | [ ] | [ ] | [ ] | [ ] | A command widget asks with the host-drawn prompt only after a click; runs after Allow; a changed command asks again; a command past its timeout dies with its children; `umbractl approvals` / MCP cannot approve (#194, release blocker) |
| Update notice (notify only) | [ ] | [ ] | [ ] | [ ] | An older install sees this release: Settings > About and the tray say "Update available" and open the release page; nothing is downloaded or run (#197) |
| Sandbox | [ ] | [ ] | [ ] | [ ] | `linux/test/sandbox.sh` green on that machine; then install `linux/test/hostile-widget` by hand in the real session: every vector below blocked, the declared hosts work, `umbra.log` shows `sandbox blocked`, a link opens the default browser while the widget stays on its page (#180, release blocker) |
| Flatpak | {{flatpakcell}} | {{flatpakcell}} | {{flatpakcell}} | {{flatpakcell}} | `flatpak install umbra.flatpak`: starts, pinned, notifications and media through portals, commands refused, update check skipped, `flatpak run --command=umbractl io.github.aminechr54.Umbra status`; `flatpak uninstall` leaves no autostart entry (#196) |

Hostile-widget vectors, each against an undeclared host (#180): fetch, XHR, `<img>`, CSS `url()`, `@import`, `<link rel=stylesheet>`,
`<script src>`, `<iframe>`, WebSocket, EventSource, `navigator.sendBeacon`, `<form>` submit, `window.open`, `location =`,
`<audio>/<video>`, `@font-face`, Worker/`importScripts`, `<link rel=prefetch>`.

### Once, on the primary configuration

- [ ] GNOME without the extension: XWayland fallback, the Settings hint appears once and its Install button works; with it, another app can't get its windows treated as widgets (#187)
- [ ] Only the current user can connect to the control socket (second account); `umbractl status`/`list`/`install`/`remove`/`capture` and `umbractl mcp` work (#175, #181, release blocker)
- [ ] Data in `~/.local/share/umbra` (XDG), cache in `~/.cache/umbra`; `UMBRA_DATA_DIR` and `portable.txt` still win (#178)
- [ ] `umbractl capture` of every catalog widget looks like Windows (alignment, tabular numbers, no tofu); Windows-only widgets behave as decided (#195)
- [ ] Someone new follows only the README on a fresh Ubuntu VM and gets Umbra running (#199)

### Collected from merged Linux PRs

{{collected}}

### Failures

| Column and row | Issue (label `platform: linux`) | Blocker? |
|---|---|---|
| | | |

### Ship (Linux additions)

- [ ] Every blocker above closed, every other failure has an open issue
- [ ] The release has `umbra-{{ver}}-linux-x64.tar.gz`, `-arm64.tar.gz` and their `.sha256` next to the installer (`gh release view v{{ver}} --json assets -q ".assets[].name"`)
- [ ] Release text lists the Linux assets, the supported desktops (KDE Plasma 6, GNOME 46+) and the known differences (hotkeys on GNOME 46, tray on stock GNOME, `file://`-only media art, notify-only updates)
- [ ] For the first Linux release: this matrix is filled and attached to the release PR (#200 acceptance)
<!-- linux-section:end -->
