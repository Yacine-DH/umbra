# Building a widget for Umbra

Umbra widgets are small web pages (HTML, CSS, JS) that Umbra pins to the desktop. Anything in this repo's `widgets/` folder shows up in Umbra's **Browse** page once it's merged, so publishing a widget is just a pull request.

## 1. Create the folder

```
widgets/
  my-widget/            folder name = widget id: lowercase letters, digits, dashes
    widget.json         manifest (required)
    index.html          entry page (required)
    preview.png         screenshot shown in Browse (recommended, 2x, black background)
    ...                 any .js .css .svg .png .woff2 etc. (5 MB max)
```

## 2. Write `widget.json`

```json
{
  "id": "my-widget",
  "name": "My Widget",
  "description": "One sentence that tells people why they want it.",
  "author": "your-github-name",
  "version": "1.0.0",
  "entry": "index.html",
  "preview": "preview.png",
  "width": 300,
  "height": 160,
  "permissions": { "hosts": ["api.example.com"] },
  "options": {
    "city": { "label": "City", "type": "text", "default": "Berlin", "hint": "Shown under the field" },
    "units": { "label": "Units", "type": "select", "default": "c",
               "choices": [ { "value": "c", "label": "Celsius" }, { "value": "f", "label": "Fahrenheit" } ] },
    "compact": { "label": "Compact mode", "type": "toggle", "default": false }
  }
}
```

- `width` and `height` are logical pixels. Umbra scales them for the user's display, so a 300x160 widget is crisp on a 4K screen.
- `permissions.hosts` lists every HTTPS hostname your widget may contact. **Everything else is blocked**, including `fetch`, images and scripts, so list exactly what you need. Use `[]` if the widget works offline.
- `options` become a settings form in Umbra's **My widgets** page, in the order you write them. Option types are `text`, `number`, `select` and `toggle`.
- Bump `version` whenever you change the widget. Users then see an **Update** button.
- `platforms` is optional (`["windows"]`, `["linux"]`; leave it out for both). Set it when the widget starts programs that exist on one OS only. Browse then shows "Windows only" and offers no install on the other.

## 3. Use the SDK

```html
<link rel="stylesheet" href="https://umbra.local/shared/umbra.css">
<script src="https://umbra.local/shared/umbra.js"></script>
<script>
  umbra.every(async () => {                 // runs now, then every N seconds (user setting)
    const o = umbra.options;                // { city, units, compact }
    const data = await umbra.fetchJson(`https://api.example.com/now?city=${encodeURIComponent(o.city)}`);
    document.body.textContent = data.temp;
  });
  umbra.onSettings(() => { /* options changed, re-render */ });
</script>
```

| API | What it does |
|---|---|
| `umbra.ready` | Promise that resolves once settings have arrived |
| `umbra.options` | This widget's option values |
| `umbra.settings` | `{ refreshSeconds, animations, locked, scale, editMode, options }` (`locked` is the opposite of `editMode`, one global lock; users can zoom a widget 50-300 %; lay out at your manifest size and Umbra zooms the page) |
| `umbra.every(fn)` | Call `fn` now and on the user's refresh interval |
| `umbra.onSettings(fn)` | Called when the user changes anything in Settings |
| `umbra.fetchJson(url)` | Fetch JSON through Umbra, with no CORS problems (host must be in `permissions.hosts`) |
| `umbra.store.get(key, fallback)` / `.set(key, value)` | Save the widget's own data (any JSON, 1 MB), kept across restarts |
| `umbra.openUrl(url)` | Open a link in the user's browser |
| `umbra.tween(from, to, ms, fn)` | Ease-out number animation (skipped when motion is off) |
| `umbra.fmt(n, decimals)` | `7744.06` to `"7,744.06"` |

`umbra.css` gives you the design tokens, such as `--text`, `--text-2`, `--accent`, `--up` and `--down`, plus the Segoe UI Variable fonts. Dragging is handled for you. Mark interactive elements with `data-no-drag` if they need clicks to stay clicks.

## 4. Design guidelines

- **True black background** (`#000`). Umbra is built for OLED, and black pixels are off there.
- Keep text at 11px or larger, and use `font-variant-numeric: tabular-nums` (the `.num` class) for numbers that change.
- Respect `html[data-motion="off"]`. `umbra.css` already disables CSS animations there.
- Make no network calls faster than `umbra.every` schedules them.

## 5. Test locally

The fastest loop uses `umbractl` (installed with Umbra):

```powershell
umbractl new my-widget                 # scaffold a working widget and show it on the desktop
umbractl create .\widgets\my-widget    # (re)load the folder you are editing in the repo
umbractl capture my-widget preview.png # pixel-exact 2x screenshot, ready to commit as preview.png
umbractl logs                          # load errors and requests blocked by the sandbox
umbractl dev .\widgets\my-widget       # load the folder and reload on every save, printing blocked requests and errors live
umbractl validate .\widgets\my-widget  # the checks an install does, plus lints (exit 1 on errors)
umbractl pack .\widgets\my-widget      # write my-widget.umbrawidget, the shareable package (double-click or Browse > Install from file)
```

New here? Follow the [10-minute tutorial](docs/widgets/tutorial.md), copy from the [cookbook](docs/widgets/cookbook.md) (every recipe points at a real widget), or read [coming from browser extensions](docs/widgets/from-browser-extensions.md). `widget.json` has a `manifestVersion` (default 1) and an optional `minUmbraVersion`; run `node scripts/catalog.mjs` and commit the regenerated index before you open the pull request.

Or copy your folder to `%APPDATA%\Umbra\widgets\my-widget\` and run `umbractl reload`. It appears in **My widgets**. The complete API reference is [docs/widget-sdk.md](docs/widget-sdk.md), and editors validate `widget.json` against [schemas/widget.schema.json](schemas/widget.schema.json) when it starts with the `$schema` line.

To test the Browse catalog end to end, point Umbra at your local repo:

```powershell
$env:UMBRA_CATALOG_BASE = "file:///C:/path/to/umbra/widgets"
node scripts/catalog.mjs          # validates and rebuilds widgets/index.json and themes/index.json
& "$env:LOCALAPPDATA\Programs\Umbra\Umbra.exe"
```

## Building a theme

A theme restyles every widget through the design tokens of `shared/umbra.css`. It is data only, so it is a single file:

```
themes/ember/
  theme.json      required
  preview.png     optional
```

```json
{
  "$schema": "https://raw.githubusercontent.com/AmineChr54/umbra/main/schemas/theme.schema.json",
  "id": "ember",
  "name": "Ember",
  "description": "Warm orange accent on deep brown-black surfaces.",
  "author": "your-github-name",
  "version": "1.0.0",
  "tokens": { "--accent": "#ff8a3d", "--accent-soft": "rgba(255, 138, 61, 0.15)", "--radius-m": "8px" }
}
```

- `id` equals the folder name (2-40 lowercase letters, digits and dashes; `default` is taken). Bump `version` on every change.
- `tokens` may only use the names in the [token table](docs/widget-sdk.md#umbracss-tokens): colors (`--bg`, `--surface-1..3`, `--line`, `--line-strong`, `--text`, `--text-2`, `--text-3`, `--accent`, `--accent-strong`, `--accent-soft`, `--up`, `--down`, `--flat`) as `#rgb`/`#rrggbb`/`#rrggbbaa`, `rgb()`, `rgba()`, `hsl()` or `hsla()`, and radii (`--radius-s|m|l`) as `0-99px` or `rem`. Anything else (CSS, `url()`, scripts, other tokens, subfolders) fails validation. Leave out what you don't change. Keep `--bg` at the default `#000` unless the theme is about the background, and keep text readable (contrast of `--text` and `--text-2` on your surfaces).
- Try it: `umbractl send install-theme '{"id":"ember"}'` installs from the catalog; to test a local file copy the folder to `%APPDATA%\Umbra\themes\<id>\` and run `umbractl themes`, then `umbractl theme <id>` and `umbractl capture settings:themes themes.png`.
- Run `node scripts/catalog.mjs`: it validates your theme and regenerates `themes/index.json`. Open a pull request with your `themes/<id>/` folder and that regenerated index. CI runs `node scripts/catalog.mjs --check` and fails if the index is out of date (nothing is committed to `main` for you).

## Working on the app itself

Pick an issue from the [roadmap board](https://github.com/users/AmineChr54/projects/5) (labels `good first issue` and `help wanted` are a good start), comment that you're on it, and open a PR that says `Closes #N`. Build and test with `dotnet build Umbra.slnx` and `dotnet test Umbra.slnx`; CI runs the same on Windows, plus an Ubuntu job for the platform-neutral part (`Umbra.Linux.slnf`). CI runs on the maintainer's PC, not GitHub Actions: `powershell -File scripts/ci-local.ps1` runs every job (the Linux and Node ones in Docker), and you can run it yourself before opening the PR. [CLAUDE.md](CLAUDE.md) explains the architecture and the security rules, and AI coding agents read it too.

## Working on the Linux port

Umbra runs on Windows only today. A Linux version for KDE Plasma 6 and GNOME 46+ is in progress, tracked in epic [#172](https://github.com/AmineChr54/umbra/issues/172). **Read the epic first**: it holds the design (repo layout, dependencies, the Windows to Linux mapping) and the ordered child issues (#173 to #200). Each child issue says what it depends on. The Linux host (`src/Umbra.Linux`) and the `linux/` folder do not exist yet; the first host issue is #179.

**Status of what you can build today.** The platform-neutral part (`src/Umbra.Core`, `src/Umbra.Cli`, `tests/Umbra.Core.Tests`) builds and tests on Linux and on Windows:

```bash
dotnet build Umbra.Linux.slnf -warnaserror   # 0 warnings
dotnet test Umbra.Linux.slnf
```

That needs only the .NET 10 SDK. The Linux host is not part of the solution filter until it exists.

**Dev setup on Ubuntu (GNOME), from the epic.** You need this once the host lands (#179); it is not needed for the commands above. Package names are the epic's and are not yet exercised in this repo:

```bash
# runtime + dev libs
sudo apt install libgtk-4-1 libgtk-4-dev libwebkitgtk-6.0-4 libwebkitgtk-6.0-dev \
                 libgtk4-layer-shell0 libgtk4-layer-shell-dev build-essential nodejs gh
# test tools (nested/headless sessions, X11 path)
sudo apt install xvfb openbox xdotool kwin-wayland mutter weston imagemagick dbus-x11
# .NET 10 SDK: the dotnet backports PPA, or https://dot.net/v1/dotnet-install.sh --channel 10.0
```

If `libgtk4-layer-shell*` is missing from your Ubuntu release, build it from https://github.com/wmww/gtk4-layer-shell (meson). The epic also names the runtime packages for Fedora 41 (`gtk4 webkitgtk6.0 gtk4-layer-shell`) and Arch (`gtk4 webkitgtk-6.0 gtk4-layer-shell`). Node 22 runs the catalog scripts.

**How to test on Linux (rules from the epic):**

- **On your real desktop, inspect only through `umbractl capture <widget|settings:page> out.png`.** It captures just that WebView. **Never take desktop or full-screen screenshots** of a personal desktop: they capture your other windows (same rule as in CLAUDE.md).
- **Compositor screenshots are fine only in a throwaway session you started yourself**, where nothing personal is on screen. The epic lists these ways to start one (not yet exercised in this repo):
  - X11: `xvfb-run -s "-screen 0 1920x1080x24" openbox ...`
  - KDE: `kwin_wayland --virtual --width 1920 --height 1080 --socket wayland-umbra`, or nested in a window
  - GNOME: `dbus-run-session -- gnome-shell --nested --wayland`, or `mutter --headless --wayland --virtual-monitor 1920x1080`
- Use a throwaway profile: `UMBRA_DATA_DIR=$(mktemp -d)`. Only one instance runs per user and data dir. Never touch the data folder of an Umbra you actually use, and only kill processes you started.
- No `sudo`, system-wide installs or changes to GNOME settings and extensions outside a nested session as part of a test.
- Drag, typing into widgets, multi-monitor and fractional scaling need a human check on a real desktop: list them in the PR under a "Human check" heading, saying what you tried and on which distribution, desktop and session type.

**Working rules for Linux PRs** (from the epic):

- Comment "I'm taking this" on the child issue before you start, work from your fork, and open one PR per issue with `Closes #N`.
- Windows must keep working: the Windows CI jobs must stay green, and `dotnet build -warnaserror` stays at 0 warnings.
- Every security invariant in CLAUDE.md applies unchanged on Linux, and Linux sandbox parity is a release blocker. A PR that touches the sandbox, commands, approvals, focus gating or the control socket needs the maintainer's review. If something can't be matched on Linux, open a discussion in the issue; don't quietly weaken it.
- New NuGet packages need a reason in the PR; the epic allows only `GirCore.Gtk-4.0` and `GirCore.WebKit-6.0`.
- Update the CLAUDE.md map when you add files or projects. The label for Linux issues is `platform: linux`.
- Widgets must talk to the host only through `window.umbra`, never through `window.chrome.webview` or any other bridge ([widget SDK](docs/widget-sdk.md)).

**Running it with Claude Code.** On the Linux machine, with your fork cloned and the prerequisites installed, `/loop 2m /linux-port` runs the loop in [`.claude/skills/linux-port/SKILL.md`](.claude/skills/linux-port/SKILL.md): it picks the next ready issue, hands it to agents in worktrees, routes review and CI feedback, and collects the human checks.

## 6. Open a pull request

Add your `widgets/<id>/` folder and the regenerated catalog index. Run `node scripts/catalog.mjs` and commit the resulting `widgets/index.json` (and `themes/index.json`) with your widget. Never edit the index by hand. CI runs `node scripts/catalog.mjs --check` on the PR and fails if the index is out of date, because nothing regenerates or commits it after the merge. If another widget PR lands first and your index conflicts, rebase on `main` and run `node scripts/catalog.mjs` again. Validation checks the id, the required fields, the semantic version, the hostnames, the option types, the file types and the 5 MB limit.

Reviewers will check that the widget only uses the hosts it declares and looks at home next to the others.

## Releasing: signing and winget (maintainers)

Releases are built on the maintainer's PC (`build.ps1`, then `gh release create`; see `.claude/skills/release`) while GitHub Actions is off. When it's on, `release.yml` builds `UmbraSetup-<version>.exe`, signs it with `signtool` **only if** the repo secrets `SIGN_CERT_BASE64` (a code-signing `.pfx`, base64) and `SIGN_CERT_PASSWORD` exist, and otherwise publishes it unsigned. It also fills the manifests in [`winget/`](winget) with the version and SHA256 (`scripts/winget.ps1`) and attaches them to the workflow run as `winget-manifests-<version>`.

One-time setup (needs a person, not CI):

1. **Certificate.** Get a code-signing certificate (an OV/EV certificate from a CA, or Azure Trusted Signing / SignPath if you qualify) and export it as a `.pfx`. Encode it with `[Convert]::ToBase64String([IO.File]::ReadAllBytes("cert.pfx"))` and add the repo secrets `SIGN_CERT_BASE64` and `SIGN_CERT_PASSWORD`. The next tagged release is signed; SmartScreen reputation still builds up over time.
2. **First winget submission.** After a release, download the `winget-manifests-<version>` artifact (or run `powershell -File scripts/winget.ps1 -Installer dist/UmbraSetup-<version>.exe`), then either run `wingetcreate submit <folder>` or fork [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) and copy the three files to `manifests/a/AmineChr54/Umbra/<version>/`. Validate with `winget validate <folder>` and `winget install --manifest <folder>` in a clean VM, then open the pull request. Moderators must approve the first one.
3. **Later releases.** Repeat step 2 for each version (or add a `wingetcreate update AmineChr54.Umbra` / [winget-releaser](https://github.com/vedantmgoyal9/winget-releaser) job once the package exists, using a PAT secret).
