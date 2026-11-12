# Umbra: guide for AI agents and contributors

Umbra pins small web widgets (HTML/CSS/JS in WebView2) to the Windows desktop. The C# host knows nothing
about any specific widget; widgets are folders with a `widget.json`. Windows 10 1809+ / 11 only today; a Linux version
(KDE Plasma 6, GNOME 46+) is in progress, see "Linux port" below.

## Commands

```powershell
dotnet build Umbra.slnx -warnaserror      # must stay at 0 warnings
dotnet test Umbra.slnx                    # unit tests (xUnit), no app or network needed
dotnet build Umbra.Linux.slnf -warnaserror   # Core + umbractl + Core tests: the part that builds on Linux (CI runs it on Ubuntu)
dotnet test Umbra.Linux.slnf
node --test tests/relay/*.test.mjs        # relay (Buddy + Hearth) Worker logic, no Cloudflare needed
node scripts/catalog.mjs                  # validate widgets/ + themes/ and regenerate both index.json files (commit them)
powershell -File build.ps1                # publish both exes + installer -> dist/UmbraSetup-<ver>.exe
powershell -File scripts/ci-local.ps1 -Pr 123 -Status   # CI: every ci.yml + widgets.yml job on this PC (Docker + host, portable smoke), posts the required local-ci check
node scripts/release.mjs minor            # start a release (patch|minor|major|X.Y.Z): bump, checks, release PR
dotnet run --project src/Umbra            # run from source (exit the installed Umbra first: one instance per user)
```

The installed app is at `%LOCALAPPDATA%\Programs\Umbra`, and user data is at `%APPDATA%\Umbra` (`settings.json`, `umbra.log`,
community widgets in `widgets\`). `UMBRA_DATA_DIR` overrides the data folder (tests use a temp dir).

## Map

| Path | Role |
|---|---|
| `src/Umbra.Core/` | Platform-neutral `net10.0` library (no WinForms/WebView2/WinRT): settings and widget model, security checks, control protocol, commands, themes, backups. `UmbraHost.cs` holds `IUmbraHost` / `IWidgetView` / `ISettingsView` / `IUpdater`, the surface `Control` automates; the Windows `App` implements it, a Linux host will too (#172) |
| `src/Umbra/Program.cs` | Entry: single instance, WebView2 check, starts `App`, serves the control pipe |
| `src/Umbra/App.cs` | Owns settings, widget windows, tray menu, Settings window. `OnUi()` marshals work to the UI thread |
| `src/Umbra/WidgetWindow.cs` | One borderless desktop-pinned window per widget; JS bridge (`fetchJson`, `drag`, `rendered`...); host-drawn right-click menu (`ContextMenuRequested`; `RemovedCopy.cs` backs its Undo); `CaptureAsync` |
| `src/Umbra.Core/CrashRecovery.cs`, `src/Umbra/WebView2Crash.cs`, `shared/crashed.html` | Crash policy (`WebView2Crash` maps `CoreWebView2.ProcessFailed`; on Linux `WidgetWindow.Classify` maps WebKit's `web-process-terminated`): renderer exit reloads (a hung renderer is only logged) (max 3 per 10 min), then the host-owned "This widget stopped working" page (Reload button); browser-process exit recreates the environment and view; Data & privacy shows the count |
| `src/Umbra/Tray.cs` | Tray tooltip text and the "update ready" badge icon; `--restart` flag (the new process waits for the old one's single-instance mutex) |
| `src/Umbra/EditPill.cs` | The host-drawn "Editing widgets · Snap · Done" pill per monitor in edit mode: its own non-activating, Progman-owned window just above the widgets (not a WebView, so never in a capture) |
| `src/Umbra/DesktopPin.cs`, `Native.cs` | Win32: owned by Progman (re-applied when shown and when Explorer restarts), kept at `HWND_BOTTOM`, no activation, DWM corners |
| `src/Umbra/WebHost.cs` | WebView2 environment, virtual hosts `umbra.local` / `community.umbra.local`, **sandbox** |
| `src/Umbra/SettingsWindow.cs` + `settings/` | Settings panel (HTML) and its message protocol; `ShowPageAsync`, `CaptureAsync` |
| `src/Umbra.Core/Community.cs` | Catalog fetch, install/remove, `WriteLocalWidget`, path and type validation |
| `src/Umbra.Core/Themes.cs` | `Theme`: theme.json model, token allow-list and value validation, generated `:root` CSS (applied by `shared/umbra.js`); installed in `%APPDATA%\Umbra\themes\<id>`, catalog in `themes/` |
| `src/Umbra.Core/Protocol.cs` (`PortableMode`), `Log.cs` (`AppPaths`) | Portable mode: `portable.txt` next to `Umbra.exe` puts data (and the WebView2 cache) in `<AppDir>\data`; no autostart, no self-install; pipe/mutex name carries a data-dir hash. `UMBRA_DATA_DIR` still wins |
| `src/Umbra.Core/Updates.cs`, `src/Umbra/UpdateService.cs` | Update check: pure version compare / release parsing / URL validation (`Updates`, Core), and `UpdateService` (Windows) (daily GitHub check, background download + verify, silent installer run with `/UPDATE=1`; `Umbra.iss` then starts `Umbra.exe --updated`; release notes shown once after an update via `General.LastSeenVersion`) |
| `src/Umbra.Updater/`, `src/Umbra.Core/UpdaterArgs.cs` | `Umbra.Updater.exe`: the dark update window (closing, installing with log-derived progress, starting). `UpdateService.InstallAsync` copies it to temp and runs it, which runs the silent installer; without it the installer runs invisibly as before. `UpdaterArgs` (shared source) is its command-line contract |
| `src/Umbra/ToastService.cs`, `src/Umbra.Core/Notifier.cs` | `umbra.notify`: real toasts via `ToastNotificationManager` (installer shortcut AUMID `AmineChr54.Umbra`; portable/source runs register their own under HKCU; balloon fallback), plus the rate limit and text/action/XML helpers |
| `src/Umbra.Core/WidgetStore.cs` | `umbra.store`: per-widget JSON data in `%APPDATA%\Umbra\data\<id>.json` (1 MB cap, deleted on `umbractl remove`; Settings > Remove moves it to the trash) |
| `src/Umbra.Core/WidgetTrash.cs` | Settings > Remove moves the widget folder, data and settings of every copy to `%APPDATA%\Umbra\trash\<id>-<timestamp>` for Undo; purged after 7 days at startup; never part of backups |
| `src/Umbra.Core/UserScripts.cs` | The user's own scripts for widgets with `permissions.userScripts` (Script Runner): `UserScript` (validation, the argv it becomes) and the store in `%APPDATA%\Umbra\scripts.json` (+ code files in `scripts\<id>\`). Edited only in Settings (`saveScript`/`deleteScript`); `CommandService.All` merges them with declared commands; widgets list them with `umbra.commands()`. Scripts that keep running (tunnels, servers) are owned by `CommandService` (start/stop/restart, `OutputTail`, `CommandRunner.Start`); the tray lists them; `ShellLink.cs` reads a picked `.lnk` |
| `src/Umbra.Core/WidgetManifest.cs`, `Settings.cs` | `widget.json` model and discovery; `settings.json` model |
| `src/Umbra.Core/Protocol.cs` | Control protocol (shared source, linked into umbractl). Command list lives here |
| `src/Umbra.Core/Linux/` | Pure Linux platform logic (namespace `Umbra.Linux`, tested on Windows too): `LinuxPaths.cs` (`LinuxAppPaths`: XDG data/cache dirs, `UMBRA_DATA_DIR`, portable, Flatpak detection, env injected), `DesktopEntry.cs` (autostart `.desktop` text: build, parse, Exec quoting), `LinuxAutostart.cs` (`IAutostart`: `.desktop` file or Background portal under Flatpak), `ProcParsers.cs` (`/proc/stat`, `meminfo`, `uptime`, `net/dev`, power_supply `uevent` from text), `LinuxMetrics.cs` (`umbra.system()` JSON, same shape as `WindowsMetrics`, reads through `IProcSource`). `PortableMode.DataDir` and `AppPaths.WebViewDataDir` use these paths off Windows (so `LinuxPaths.cs` is also linked into umbractl). `tests/Umbra.Core.Tests/Linux/LinuxLiveTests.cs` (`[LinuxFact]`) runs the file reader on the live `/proc` and the autostart file through `desktop-file-validate`; the Background portal call (Flatpak) is still only an interface |
| `src/Umbra.Core/Control.cs` | Executes control commands against `IUmbraHost`: the single place that defines what automation can do |
| `src/Umbra/Screens.cs`, `WindowsMedia.cs`, `WindowsMetrics.cs`, `PerfCounters.cs` | Windows halves of pure Core rules: live monitors + fullscreen test (`Placement`), media session (`MediaService`), CPU/memory/network/GPU/disk (`SystemMetrics`; GPU and disk from English-named PDH counters, totals only) |
| `src/Umbra.Cli/` | `umbractl.exe`: CLI (`Program.cs`, argument mapping in `CommandLine.cs`), MCP stdio server (`McpServer.cs`), scaffold (`Scaffold.cs`) |
| `shared/umbra.js`, `umbra.css` | Widget SDK and design tokens, served to every widget |
| `shared/tabicons/` | Bundled tab-strip icons (PNG + `index.json`); the only icons the user picker, `umbractl groups icon` and a widget's `umbra.setTabIcon` suggestion may name (`Groups.CleanSuggestion`) |
| `widgets/<id>/` | Catalog widgets (nothing is built in; `sp500` is installed from Browse); `widgets/index.json` is generated by `node scripts/catalog.mjs`, never hand-edited; PR authors run it and commit the result (CI does not) |
| `site/` | Website on GitHub Pages (Astro + Starlight): landing page, widget catalog, docs in `src/content/docs/docs/`. `npm run build` there (Node >=22.23.4, CI uses 24); `scripts/sync.mjs` pulls catalog, previews, SDK doc and schemas from the repo, so `docs/widget-sdk.md` stays the one SDK source. Deployed by `pages.yml`; not part of the app |
| `schemas/widget.schema.json`, `docs/widget-sdk.md` | Manifest schema and SDK reference (both ship next to umbractl for agents) |
| `installer/Umbra.iss`, `build.ps1` | Inno Setup per-user installer; the version comes from `Directory.Build.props` |
| `tests/Umbra.Core.Tests/` | Unit tests for Core and the CLI (`extern alias cli`); plain `net10.0`, run on Windows and Linux. `[WindowsFact]`/`[WindowsTheory]` skip Windows-only cases elsewhere |
| `src/Umbra.Linux/` | The Linux host (assembly `umbra`, GTK4 + WebKitGTK 6 through GirCore; epic #172), a skeleton so far: `Program.cs` (single instance, control socket, `Gtk.Application`), `App.cs` (`IUmbraHost`; features without a Linux backend log "not available on Linux yet (#N)"), `WidgetWindow.cs` (one undecorated transparent window + `WebKit.WebView` per widget copy, the page message switch), `WebHost.cs` (shared context and session, the `umbra://` scheme handler, `xdg-open`), `UmbraScheme.cs` (pure path rules of the scheme: the Linux counterpart of the virtual-host folder mapping), `UiThread.cs` (GLib main-loop synchronization context), `DesktopPin.cs` (`IDesktopPin`; `X11Pin`: EWMH hints through libX11, also used under XWayland, which `Program.ChooseDisplayBackend` selects on a Wayland session until #186/#187) + `WidgetPlacement.cs` (pure: where a window goes, never off screen), `WidgetWindow.Edit.cs` (edit mode and focus gating: drag, resize, zoom, nudge and keyboard focus only with physical input, read from GTK capture-phase controllers and the X server's pointer state) + `Overlays.cs` (host-drawn GTK windows, never web views: `EditPill`, `SnapGuides`, `GroupTabs`), `Snapshot.cs` (`umbractl capture`: `webkit_web_view_get_snapshot` of that one view, transparent background kept), **sandbox** in `Sandbox.cs` (pure rules: CSP text, policy text, navigation lock, dead-end proxy) + `native/umbra-webext.c` (web-process extension, built by the csproj with gcc into `webext/libumbra-webext.so`) + `WebHost.NewView`/`Lock`/`SessionFor` |
| `linux/test/` | Headless test harness for the Linux host: `headless.sh` (Xvfb + openbox + own D-Bus, throwaway `UMBRA_DATA_DIR`, fake `xdg-open`), `sandbox.sh` + `hostile-widget/` (every way a page can reach an undeclared host; fails on one escaped connection), `catalog.sh` (every catalog widget must load), `pin.sh` (X11 pinning: window type, state, position, stacking, restart), `edit.sh` + `edit-widget/` (edit mode with xdotool as the real mouse and keyboard: a page asking by itself gets nothing; drag, Ctrl+wheel, nudge, typing after a click, the pill), `crash.sh` (kills a widget's web process: three reloads, then the stopped page and its Reload button), `smoke.sh` (the Linux `scripts/smoke.ps1`: umbractl status/list/new/capture/configure/data/MCP/install/remove/quit) |
| `tests/Umbra.Linux.Tests/` | Unit tests for the pure parts of the Linux host (no display needed) |
| `tests/Umbra.Tests/` | Windows host tests (WebView2, WinForms, toasts, power events, command runner with cmd/powershell) |
| `tools/umbra-relay/` | The only server: a Cloudflare Worker + Durable Object behind Buddy (`/pair`, `/beat`, `/unpair`, API frozen) and Hearth (`/hearth/*`, contract in its README). Free plan, all answers status-200 JSON because `fetchJson` drops non-2xx bodies. Never deployed from here; `tests/relay/*.test.mjs` run the Worker against an in-memory fake (`node --test tests/relay/*.test.mjs`) |

## Linux port (epic #172, in progress)

Read epic [#172](https://github.com/AmineChr54/umbra/issues/172) before touching anything Linux: it holds the design and the ordered
child issues (#173-#200). Only part of it exists in the repo; don't document or assume the rest as done.

- **Exists:** `src/Umbra.Core`, `tests/Umbra.Core.Tests`, `src/Umbra.Cli` as plain `net10.0`, `Umbra.Linux.slnf`, the Ubuntu CI job,
  the bridge shim in `shared/bridge.js`, `.claude/skills/linux-port/SKILL.md` (the loop a Linux contributor runs: `/loop 2m /linux-port`),
  and the host skeleton `src/Umbra.Linux` + `tests/Umbra.Linux.Tests` (#179): widgets load from `umbra://` and talk to the host
  (`dotnet run --project src/Umbra.Linux`, with `UMBRA_DATA_DIR=$(mktemp -d)`), and the widget sandbox (#180, see the first
  security invariant). Building it needs `gcc`, `pkg-config` and `libwebkitgtk-6.0-dev`. `umbractl` and `umbractl mcp` work against it, capture included (#181). Widgets are pinned to the desktop through X11 hints (#182; on Wayland through XWayland, `umbractl status` shows `pinBackend`). Edit mode works on X11 (#183: drag with snapping, resize, Ctrl+wheel zoom, arrow nudge, the edit pill, tab strips). There is no Settings window or tray yet.
  After touching the sandbox run `linux/test/sandbox.sh` and `linux/test/catalog.sh`; after any host change `linux/test/smoke.sh`
  (all headless, throwaway profile).
- **Planned, not in the repo yet** (names from the epic; add map rows when the files land):
  `linux/` (launcher, `.desktop`, metainfo, `flatpak/`, `test/`), `linux/gnome-extension/umbra@aminechr54.github.io/` (#187).
  Linux app id: `io.github.aminechr54.Umbra` (the Windows AUMID stays `AmineChr54.Umbra`).
- Every security invariant below applies **unchanged** on Linux and sandbox parity is a release blocker. If something can't be matched,
  say so in the issue; don't quietly weaken it. Changes to the sandbox, commands, approvals, focus gating or the control socket need the maintainer's review.
- **Linux screenshots:** on a real desktop use only `umbractl capture` (that WebView), never a desktop or full-screen screenshot.
  Compositor screenshots only inside a throwaway nested/headless session you started (`xvfb-run`, `kwin_wayland --virtual`,
  `dbus-run-session -- gnome-shell --nested --wayland`), with `UMBRA_DATA_DIR=$(mktemp -d)`. Drag, typing into widgets, multi-monitor and
  fractional scaling are human checks: list them in the PR. No `sudo` or GNOME settings changes outside a nested session.
- Windows CI must stay green on every PR. Label: `platform: linux`. Widgets use only `window.umbra`, never `chrome.webview`.

## Adding a capability

New automation goes through the control protocol so the CLI, MCP and tests stay in sync:
1. Add the command to `Protocol.Commands` and implement it in `Control.Execute`, reusing host methods (add what it needs to
   `IUmbraHost` in `src/Umbra.Core/UmbraHost.cs` and implement it in `App`).
2. Map it in `umbractl` (`CommandLine.TranslateAsync` in `src/Umbra.Cli/CommandLine.cs`, plus the help text in
   `Program.cs`) and add an MCP tool in `McpServer.Tools`.
3. Add tests (`tests/Umbra.Core.Tests` for Control/protocol logic, so they also run on Linux; `tests/Umbra.Tests` for Windows host code), then update the README "Automate Umbra" section and `docs/widget-sdk.md` if widget-facing.

## Security invariants (never weaken without an issue and review)

- A widget page can only reach `umbra.local`, `community.umbra.local`, `data:`, `blob:` and HTTPS hosts in its
  `permissions.hosts`. Everything else gets a 403 from `WebResourceRequested` in `WebHost.InitAsync`.
  On Linux the same rule has three layers (`src/Umbra.Linux`): the web-process extension cancels the request (`send-request`, host list
  sent by the host process, nothing but Umbra's origins until it arrived), every `umbra://` response carries a per-widget
  Content-Security-Policy (covers WebSocket, EventSource, beacons, workers; no frames, forms or plugins), and each widget has its own
  network session whose only route is a dead-end proxy except for its declared hosts (so an engine preconnect or prefetch never leaves
  the machine; off when the user has a proxy). Without the extension file no widget page is opened at all.
- `fetchJson` goes through `WidgetManifest.AllowsUrl` (HTTPS plus a declared host).
- Top-level navigation off the virtual hosts is cancelled and links open in the user's browser.
- Community and agent-written files: relative forward-slash paths only, no `..`, allowed extensions only, 5 MB,
  id equals folder, all staged in temp and validated before `Commit` (`Community.cs`). "Install from file" (`Community.PrepareLocal`,
  Settings > Browse, `umbractl install-local`) applies the same rules to a folder or zip, refuses zip-slip entries and links, and
  needs the user's accept for any declared access even on a fresh install. It has no MCP tool, and the path comes from a host-drawn picker, never the page.
  A flat zip (widget.json at its top) has no folder name, so only the id pattern applies there. Installing over an existing id
  inherits its command approvals (per id + command hash). Local installs are recorded in `local-installs.json` and never get catalog updates.
- Widget windows never activate themselves. They get keyboard focus only right after a physical mouse press on them
  (`WidgetWindow.TakeKeyboardFocus`), so a widget can't pull focus while the user types elsewhere.
- A widget can't resize or zoom itself: the page's `resize` and `zoom` messages are honoured only in edit mode, for an
  unlocked widget, with a physical mouse press (resize) or Ctrl held (zoom) over that window.
- The one exception to "a widget cannot run code" is `permissions.commands` (`WidgetCommands.cs`, `CommandService.cs`,
  `CommandRunner.cs`). A widget runs only a command it declared: exact executable plus argv array, no shell, parameters
  are `${name}` whole-argument placeholders validated and passed as single arguments, never a free command line. Every
  command needs the user's approval, stored per widget id + command hash in `approvals.json` (a changed command is
  asked again). Only a host-drawn prompt (after a physical click on the widget, rate-limited) or the Settings button
  approves; **no control command, umbractl or MCP tool may approve** (`approvals` is read-only). Runs are as the
  user, in the widget's data dir, with a scrubbed environment, a timeout (max 120 s) and a 1 MB output cap, never
  while the widget is disabled. Built-in widgets get no automatic trust. Don't loosen any of this without an issue and review.
- `permissions.userScripts` (`UserScripts.cs`) lets the user write their own commands, never the widget: only the Settings page
  adds, edits, deletes or approves one (saving there is the approval, the row shows the exact command line), the page
  sees ids and names only (`umbra.commands()`), the host builds a fixed argv (no shell, no parameters) that passes
  `CommandSpec.Validate`, and code typed in Settings runs from a file named after its hash, rewritten from `scripts.json` before each run.
  No control command, umbractl or MCP tool may add, change, approve, start or stop a script (tested). A declared command id always wins.
  The one exception to the 120 s timeout (#322): a user script the user marked **Keeps running** in Settings (`CommandSpec.KeepsRunning`,
  never settable from widget.json) has no timeout and keeps only a 64 KB output tail; it runs in a kill-on-close job (dies with Umbra),
  at most one per widget + script and 8 in all, and stops on Stop (widget, Settings, tray), when the widget's last window closes,
  and when the script is edited or deleted. Auto-start and restart only ever start an approved script. Keeps-running and Start in are in the hash.
  A user script the user marked **Run as administrator** in Settings (`CommandSpec.RunAsAdmin`, never settable from widget.json, in the hash)
  runs through ShellExecute `runas`, so Windows' UAC prompt asks on every run; it starts only right after a physical click on the widget
  (never a timer, auto-start or automation), is approved only in Settings (never the widget's prompt), and can't keep running.
  What UAC elevates is System32 Windows PowerShell running the fixed helper in `ElevatedRun.cs` (what runs comes only from its
  -EncodedCommand line): it starts the approved program with the same argv, folder and scrubbed environment, no window, and sends its
  output back over a one-instance pipe Umbra created (token-checked; Umbra only reads). Umbra closing the pipe (Stop, timeout, exit)
  makes the helper kill the program's tree. No job object (an elevated child can't join Umbra's).
- Camera (`permissions.camera`, `Timelapse.cs`, `TimelapseService.cs`): only the host touches the webcam (Windows.Media.Capture),
  the page never gets the stream, a frame, a full path or a write path (it sees the video's file name and the folder's name;
  the output folder comes from the host's folder dialog). Each widget id needs a one-time host-drawn approval
  (`CameraConsent`, `ApprovalDialog.AskCamera`, stored in approvals.json under `@camera`): asked only after a physical LEFT
  click on a visible widget outside edit mode (`ClickGate`: not a right-click, drag or the host menu), rate-limited, never
  after 3 refusals, revocable in Settings, read-only to automation, and dropped when the manifest stops declaring camera
  (a manifest that newly adds it is also listed by `PermissionDiff`). One session at a time, a host-drawn red dot on the widget
  and in the tray, max 3600 stills and 12 h, paused with the camera closed while the widget is hidden, stopped on widget close
  and on exit, stills deleted after render, on cancel and at startup, disk space checked. No network in the capture path.
  **No control command, umbractl or MCP tool may start or approve the camera** (tested). Don't loosen without an issue and review.
- `permissions.saveImages` (`ImageArchive.cs`, `WidgetWindow.HandleImages`): a widget hands the host a PNG data URL and the host writes it
  into a folder the user picked in the host's own folder dialog (opened only after a physical left click; no default folder, so nothing is
  written before the user chose). The page never supplies a path or file name (host-built `<widget> <date> <time>.png`, no overwrite), only
  PNGs up to 1 MB are accepted (magic bytes checked), 30 saves per 10 minutes, disk space checked, and it can't read files back. The folder
  (`image-folders.json`) is forgotten when the widget stops declaring the permission or is removed; updates adding it go through `PermissionDiff`.
  No control command, umbractl or MCP tool may choose the folder or save (tested).
- Updating an installed catalog widget never grants new access silently: `Community.PrepareAsync` diffs the downloaded manifest against the
  installed one (`PermissionDiff`: hosts, system, media, notifications, camera, commands) and `PendingInstall.Require` refuses until the user accepted.
  Settings asks with a host-drawn box (`SettingsWindow.ConfirmNewAccess`), `umbractl install` needs `--accept-permissions`, and the MCP
  server strips that argument. Any new install path (e.g. "Update all") must go through `PrepareAsync`/`InstallAsync` with `acceptPermissions` false by default.
- Every package install path (Settings From file / From folder / From URL, double-click on a `.umbrawidget`, `install-local`, `install-url`)
  goes through `Community.PrepareLocal` (or `PrepareUrlAsync`, which downloads then calls it) and a host-drawn review (`PackageInstall`);
  access it declares is always accepted by the user, never by a tool argument. `install-local`, `install-url` and the `open-package` pipe line
  (the double-click hand-off, not a control command) have no MCP tool; the MCP server strips `acceptPermissions`, and `pack_widget`'s `out`/`overwrite`.
  URL downloads: HTTPS only, no IP literals/localhost/local names (trailing dot included), no credentials or odd ports, at most 5 redirects each re-checked, a connect step that refuses private/loopback/link-local addresses (DNS rebinding included), no cookies or decompression, 5 MB, 60 s; `install-url` refuses to replace an installed or catalog id without `--overwrite` (CLI only).

- `umbra.requestNotifications()` (`NotificationConsent.cs`, `ApprovalDialog.AskNotifications`) shows the host-drawn "Allow notifications?"
  prompt only for a widget declaring `permissions.notifications` whose switch is unset, only after a physical left click on it (`ClickGate`),
  one at a time and rate-limited. Its only effect is the user's per-widget `Notifications` switch; an explicit Off (Settings or
  "Don't ask again") is never asked again. No control command, umbractl or MCP tool shows or answers it.
- `umbra.setTabIcon(name)` is a suggestion only: `WidgetWindow.HandleTabIcon` accepts a name that passes `Groups.IsIconName` and exists in `shared/tabicons` (no paths, URLs, emoji or markup), ignores and logs anything else, rate limits it (`Groups.AllowSuggestion`), and stores it in `WidgetSettings.SuggestedTabIcon`, separate from the user's `TabIcon`, which always wins (`Groups.ChooseIcon`). It never adds a control command, and a widget can't read or change the user's choice.
- Themes are data only: token names come from `Theme.Allowed`, values must match the color/length patterns (mirrored in
  `scripts/catalog.mjs` and `schemas/theme.schema.json`), and catalog tokens are re-validated before Settings draws them.
- The control pipe uses `PipeOptions.CurrentUserOnly`. Never add a network listener.
- Built-in widgets (the generic `<Content>` mechanism, none shipped today) cannot be removed or shadowed by community or agent ids.
- Self-update (`Updates.cs`) runs in the host only; the installer must be HTTPS from `github.com/AmineChr54/umbra/releases/download/` and named `UmbraSetup-<release version>.exe`. Control commands may check for updates but never install one.

## Verifying UI changes

Use `umbractl capture <widget-id|settings:<page>> out.png`, then look at the PNG. It captures only that WebView.
**Never take desktop or full-screen screenshots**: they capture the user's other windows. Drag behaviour needs a human check, so note that in the PR.

## Conventions

- C#: file-scoped namespaces, `sealed` classes, short `///` summaries on types and non-obvious members, no new NuGet
  dependencies without a reason (MCP and JSON-RPC are hand-rolled on purpose).
- JS: no build step or frameworks in `shared/`, `settings/` or built-in widgets.
- Widgets: true black `#000`, Segoe UI Variable via `umbra.css`, tabular numbers, respect `data-motion`.
- Bump `version` in a widget's `widget.json` on every change; the app version is bumped only by `node scripts/release.mjs`, never by hand.
- Never hand-edit `widgets/index.json` or `themes/index.json`: run `node scripts/catalog.mjs` and commit the result in your PR. Nothing regenerates them after merge, and CI (`catalog.mjs --check`) fails the PR if they are stale.

## Workflow

- Work is tracked on the GitHub Projects board [Umbra Roadmap](https://github.com/users/AmineChr54/projects/5) (Backlog, Ready, In progress, In review, Done).
  New issues are added automatically and labelled `status: triage`.
- Labels: `type: bug|feature|docs|widget|chore`, `area: host|widgets|settings|sdk|ai|installer|ci|docs`,
  `priority: p0|p1|p2`, plus `good first issue` and `help wanted`.
- One issue per PR when possible, with `Closes #N` in the body. CI must pass: it runs on the maintainer's PC, not GitHub Actions
  (`scripts/ci-local.ps1 -Pr <N> -Status`: the jobs of `ci.yml` and `widgets.yml` in Docker and on the host). It posts the `local-ci`
  commit status, the check branch protection on `main` requires. The `pr-reviewer` agent runs it, captures what changed, and merges.
- Before starting an issue, skip it if it has an assignee; otherwise comment "I'm taking this" so work isn't duplicated.
- Contributors without write access work from a fork (`gh repo fork AmineChr54/umbra --clone`, then
  `gh pr create --repo AmineChr54/umbra`). They can't set labels, assignees or the board, and never merge; the
  maintainer does that, and runs CI for their PR on the maintainer's PC only after reading the diff (`-TrustFork`: CI runs their code).
- Releases: `node scripts/release.mjs <patch|minor|major>` bumps `Directory.Build.props` and `installer/Umbra.iss`, runs the
  checks and opens the `Release vX.Y.Z` PR with draft notes as its body. After it merges, `build.ps1` runs on the maintainer's PC and
  `gh release create` publishes the installer and portable zip with the PR body as notes (GitHub Actions, and so `release.yml`, is off; see `.claude/skills/release`).

Project agents live in `.claude/agents/` and repeatable procedures in `.claude/skills/`.
