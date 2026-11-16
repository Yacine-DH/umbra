# Linux widget audit

Static audit of every catalog widget under `widgets/` (27 at the time of writing) plus the shared SDK files, for the Linux
port ([#172](https://github.com/AmineChr54/umbra/issues/172), audit issue [#195](https://github.com/AmineChr54/umbra/issues/195)).

**Method and limits.** This was done by reading code, manifests and CSS only. Nothing was rendered on Linux. Every row marked
"needs capture" still needs `umbractl capture` on a real Ubuntu/KDE/GNOME session before #195 can be closed (see
[What is still open](#what-is-still-open)).

## Summary

| Result | Count | Widgets |
|---|---|---|
| Works on Linux as is (pure web, no host-only APIs) | 23 | buddy, calendar-glance, clock, countdowns, crypto, daily-quote, event-radar, freecell, fx-rates, github-activity, habits, internships, minesweeper, pomodoro, prayer-times, quick-notes, solitaire, sp500, spider, todo, tracker, transit-departures, weather |
| Works, depends on another Linux issue | 2 | now-playing (#190 MPRIS), claude-waker (#194 commands, #193 resume) |
| Windows-only, planned `platforms: ["windows"]` | 2 | study-timelapse, script-runner (as shipped) |

23 portable + 2 dependent + 2 Windows-only = 27 widgets. Seven widgets got a fix and a version bump in this change
(calendar-glance, clock, countdowns, event-radar, now-playing, prayer-times, tracker), plus the shared font stack.

## Per widget

`Planned platforms` is the value the widget should carry once the `platforms` manifest field from
[#194](https://github.com/AmineChr54/umbra/issues/194) exists. That field is **not implemented by this audit**; it is only
recorded here. `all` means "no restriction".

| Widget | Linux status | Planned platforms | Issue found | Action |
|---|---|---|---|---|
| buddy | OK | all | none (HTTPS relay only) | none |
| calendar-glance | OK, fixed | all | Option label "Follow Windows" | Renamed to "Follow system" (1.2.2). Needs `umbra.hour12` / `umbra.locale` from the Linux host (see Host dependencies) |
| claude-waker | OK, depends | all | Runs the declared command `claude -p Hello --model ${model}`; wake on resume needs the Linux resume signal | none in the widget. Needs #194 (command runner; the scrubbed environment must still find `claude`, usually `~/.local/bin`) and #193 (resume detection). Human check on Linux |
| clock | OK, fixed | all | Option label "Follow Windows" | Renamed to "Follow system" (1.1.3). Same host dependency as calendar-glance |
| countdowns | OK, fixed, needs capture | all | `<input type="date">` / `type="time">`: WebKitGTK has no native pickers and falls back to a plain text field (to be confirmed on Linux). Colour emoji set (24 emoji) needs an emoji font | Added `placeholder="YYYY-MM-DD"` / `"HH:MM"` so the text fallback is usable (1.0.3); JS already validates `YYYY-MM-DD` strings. Emoji: install `fonts-noto-color-emoji` (packaging, #196) |
| crypto | OK | all | none; uses `color-mix()` (needs WebKitGTK 2.40+, Ubuntu 24.04 ships 2.44+) | none |
| daily-quote | OK | all | none | none |
| event-radar | OK, fixed, needs capture | all | Same date-input fallback as countdowns; status emoji | Placeholders added (1.1.1) |
| freecell | OK, needs capture | all | Card suits U+2660/2665/2666/2663 are coloured text. If a system font maps them to colour emoji, red/black is lost | none; check the capture |
| fx-rates | OK | all | none | none |
| github-activity | OK | all | none | none |
| habits | OK, needs capture | all | 12 emoji icons need an emoji font | none; packaging dependency on a colour emoji font |
| internships | OK | all | none | none |
| minesweeper | OK | all | none | none |
| now-playing | OK, depends | all | Description said "any Windows media app"; data comes from the media session | Description reworded (1.0.2). Needs #190 (MPRIS) for `umbra.media`; on Linux the "app" label will be the MPRIS identity, not a Windows AUMID |
| pomodoro | OK | all | none | none |
| prayer-times | OK, fixed | all | Option label "Follow Windows (24-hour if unknown)" | Renamed to "Follow system (24-hour if unknown)" (1.0.1) |
| quick-notes | OK | all | none | none |
| script-runner | **Windows-only as shipped** | `["windows"]` | Two of three shipped commands are PowerShell (`Get-CimInstance Win32_OperatingSystem`, `Get-PSDrive`); the third needs `python` on PATH (Linux usually has `python3` only). README paths are `C:\...` and `%APPDATA%\Umbra\...`. Monospace font is `Consolas, "Cascadia Mono", monospace`, which falls back to the generic monospace correctly, so no CSS problem | Not changed here. A Linux variant needs different declared commands, and the manifest has no per-OS command list until #194. Decide in #194/#195: either mark it `["windows"]`, or ship Linux example commands (`uname -sr`, `uptime -p`, `df -h`) behind a Linux variant. README should get a Linux section once decided |
| solitaire | OK, needs capture | all | Suit glyphs as above; U+21BB (refresh arrow) is not in every default font, so it may come from a fallback font | none; check the capture |
| sp500 | OK | all | none; minus sign U+2212 is in Inter, Cantarell, Noto Sans and DejaVu | none |
| spider | OK, needs capture | all | Suit glyphs as above | none |
| study-timelapse | **Windows-only** | `["windows"]` | Commands are `tasklist`, `schtasks /Run`, `taskkill /IM Capture.exe`; it drives the Windows-only "Study Time-Lapse" tools (`Capture.exe`, `Render.exe`); the default `capturePath` is `C:\StudyTimeLapse\dist\StudyTimeLapse` and the setup text builds `schtasks /Create` lines | Not changed. Hide it from the Linux catalog with `platforms: ["windows"]` once #194 lands; do not port |
| todo | OK | all | none | none |
| tracker | OK, fixed, needs capture | all | Same date-input fallback as countdowns | Placeholders added (1.0.1) |
| transit-departures | OK | all | none (public BVG REST API) | none |
| weather | OK | all | none | none |

## Shared files

| File | Finding | Action |
|---|---|---|
| `shared/umbra.css` | `--font` / `--font-display` ended in `system-ui, sans-serif` after the Segoe names. On Linux that resolves to the desktop UI font, so widgets would render, but with different metrics per distro | Stacks now continue `"Inter", "Cantarell", "Noto Sans", "Ubuntu", "Roboto", "DejaVu Sans"` before `system-ui`. Segoe stays first, so Windows is unchanged. No font file is bundled (see below) |
| `shared/umbra.css` | `.num` uses `font-variant-numeric: tabular-nums` | Inter and Noto Sans have tabular figures; check Cantarell, Ubuntu and DejaVu Sans in the captures |
| `shared/umbra.css` | `forced-colors` rules are written for Windows contrast themes | Harmless on Linux (the media query does not match) |
| `shared/crashed.html` | Uses `var(--font)` through `umbra.css` | Covered by the new stack |
| `shared/umbra.js` | Hard-coded `https://umbra.local` is rewritten by the host (#180). `umbra.hour12` / `umbra.locale` come from the host, documented as the Windows region | Linux host must send both (see Host dependencies) |

## Path and API scan

Searched all widgets for `\` paths, drive letters, `%APPDATA%`, `navigator.platform` / `userAgent`, `.exe`, `cmd`, `powershell`,
`tasklist`, `schtasks`, `Program Files`, clipboard, `Notification`, `AudioContext`, `showPicker`, `alert/confirm/prompt`.

- Windows paths or executables appear only in `study-timelapse` and `script-runner` (rows above).
- No widget reads `navigator.platform` or `userAgent`, uses the Clipboard API, `Notification`, audio or dialogs.
- All persistence is `umbra.store` (host-managed), so no widget builds a file path itself.
- Layout features that need a modern engine: `color-mix()` (crypto, sp500, countdowns, event-radar, tracker, solitaire, spider,
  freecell), `:has()` (claude-waker, countdowns). Both need WebKitGTK 2.40+. The minimum runtime in #172 (Ubuntu 24.04, Fedora 41,
  Arch) is newer, so this is fine; do not lower it.
- Scrollbars are styled with `::-webkit-scrollbar`, which WebKitGTK supports.

## Host dependencies (outside widget code)

These are not widget bugs, but widgets rely on them behaving the same on Linux:

1. **Locale and 12/24-hour clock.** `umbra.locale` and `umbra.hour12` (documented as the Windows region) must be filled from the
   Linux session (`LANG` / `LC_TIME`, or the portal) or "Follow system" in clock, calendar-glance and prayer-times will show the
   `en-US` / 24-hour default. Done in #359: `LinuxSession.TimeFormat` (`DesktopLocale` in Core) uses GNOME's `clock-format`
   through the settings portal (followed live), else the `LC_TIME` locale (KDE's `plasma-localerc` when not exported).
2. **Interface font choices.** `src/Umbra.Core/Preferences.cs` (`Fonts`) and `settings/prefs.js` offer Segoe UI, Arial, Verdana,
   Tahoma, Georgia and Consolas, and write `--font:<name>,system-ui,sans-serif`. Arial, Verdana, Tahoma and Consolas do not exist on
   most Linux systems, so choosing one silently falls back to `system-ui`. Follow-up: offer Linux fonts on Linux. Not done here
   because it is C# and settings UI, outside a widget audit.
3. **Commands environment.** `claude-waker` calls `claude` without a path. The Linux command runner must keep `~/.local/bin` (or the
   user's `PATH`) in the scrubbed environment.
4. **Emoji font.** countdowns, habits and event-radar need a colour emoji font (Noto Color Emoji). Declare it as a packaging
   dependency in #196.
5. **`umbra.media`.** now-playing is only as good as the MPRIS mapping in #190 (title, artist, album art, app name, controls).

## What is still open

#195's acceptance criteria are **not met** by this audit; they need a Linux machine:

- `umbractl capture` of every catalog widget on Ubuntu (and ideally KDE and GNOME), attached to the PR, compared with the
  Windows captures: alignment, number widths, no missing glyphs ("tofu"), emoji and card suits.
- Confirm that `type="date"` / `type="time"` really falls back to a text field in WebKitGTK 6 and that the placeholders look right.
- **Inter is not bundled.** The issue proposes shipping Inter (OFL, variable woff2) under `shared/fonts/` with an `@font-face`. That
  needs a font file from outside the repo, so it was left out of this change. Until then the stack lists Inter by name, which only
  helps when the user has it installed; otherwise Cantarell, Noto Sans, Ubuntu, Roboto or DejaVu Sans is used. Bundling Inter would make
  all distros render identically and is the recommended follow-up.
- The `platforms` field itself and filtering the catalog by platform belong to #194.
- Decide script-runner: Windows-only or a Linux variant (needs #194 first).
