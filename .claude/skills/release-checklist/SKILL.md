---
name: release-checklist
description: Build a filled release checklist for Umbra (per build, per release, install tests, human checks gathered from merged PRs, ship steps) and save it to dist/release-checklist-<ver>.md. Use before any installer/zip build, "release checklist", "prepare a release", alongside the release skill.
---

# Release checklist

Complements `.claude/skills/release` (which does the bump, tag and workflow watch). Run this first, work through it, then ship.

## Generate the filled copy

1. Version: `<Version>` in `Directory.Build.props` (the target release). Last tag: `git describe --tags --abbrev=0`.
2. Tag date: `git log -1 --format=%cI <lasttag>`.
3. Merged PRs since then: `gh pr list --state merged --search "merged:>=<date>" --limit 100 --json number,title,body`.
4. From each body, collect every line matching `needs human check` (case-insensitive, also "human check"), prefixed with `#<number>`.
5. Write `dist/release-checklist-<ver>.md` (create `dist/`): the template below with `<ver>` filled in and the collected lines added as extra rows in the human table. Tick boxes (`- [ ]`) stay empty for the user; tick the automated ones yourself only after running them.

## Linux section (epic #172, issue #200)

Once the Windows part is written (step 5), add the Linux section with the script; don't copy `linux.md` by hand:

```bash
node .claude/skills/release-checklist/linux.mjs            # appends to dist/release-checklist-<ver>.md (re-running replaces it)
node .claude/skills/release-checklist/linux.mjs --since all  # first Linux release: every Linux PR ever merged
```

It fills [`linux.md`](linux.md) (edit the rows there, the script only fills the `{{...}}` fields) with:
- **Automated checks:** `linux/test/ci.sh` if it exists on main, else every `linux/test/*.sh` suite with its header line, all run
  under a short private `TMPDIR`; the Linux build/tests, catalog/version checks and `desktop-file-validate`.
- **Packaging:** `build.ps1 -Linux` on this PC, or `linux/package.sh` / `linux/container-package.sh` on a Linux machine, plus the arm64 and contents checks.
- **The human test matrix** (Ubuntu 24.04 GNOME 46 Wayland, Ubuntu 25.x GNOME 48+, KDE Plasma 6 Wayland, X11 session) with rows for
  install/uninstall, pinning, edit mode, focus gating, monitors/scaling/suspend, tray, notifications, media, hotkeys, Settings, crash
  recovery, commands, update notice, sandbox and Flatpak (`blocked #196` while #196 is open).
- **Collected from merged Linux PRs:** PRs labelled `platform: linux`, with "Linux" in the title or naming an issue #172-#200, merged
  since the last release that shipped a Linux tarball (`--since <tag|date|all>` overrides). Each bullet of a `## Human check` section
  becomes a row with its `#PR`; a merged rebase/stack PR without its own section takes the sections of the closed PRs it mentions (`#orig via #merged`).

Run it on any OS with `gh` signed in (no Linux needed to generate it). Matrix cells, the configuration table and every box stay empty
for the person who ran them; never tick a Linux check from a PR description. The first Linux release is done only when the matrix is
filled and every failure is an issue labelled `platform: linux` (#200).

## Template

### Per build (every installer or portable zip)
- [ ] `dotnet build Umbra.slnx -warnaserror` is 0 warnings
- [ ] `dotnet test Umbra.slnx` passes
- [ ] `node scripts/catalog.mjs --check` passes
- [ ] `node scripts/check-versions.mjs` passes
- [ ] `powershell -File build.ps1` succeeds
- [ ] `dist/UmbraSetup-<ver>.exe` exists (about 90 MB)
- [ ] `dist/Umbra-<ver>-portable.zip` exists (about 105 MB)

### Per release
- [ ] Semver chosen (patch fixes, minor features, major SDK/protocol breaks)
- [ ] Versions consistent: `Directory.Build.props`, `installer/Umbra.iss`, `winget/*.yaml` (`check-versions.mjs`)
- [ ] Changed widgets/themes have a bumped `version` (`node scripts/check-bump.mjs origin/main`)
- [ ] README and docs current (docs-writer agent)
- [ ] CI green on main; `gh issue list -l "priority: p0"` is empty
- [ ] Notes previewed with `node scripts/release.mjs <bump> --dry-run`, polished in the release PR body (Added / Changed / Fixed)

### Install tests
- [ ] Silent install over the previous version (`/VERYSILENT`) keeps `%APPDATA%\Umbra` (settings, widgets, approvals)
- [ ] Self-update path: older install offers and installs this version
- [ ] Portable zip: runs with `portable.txt`, writes only to `data\`, runs alongside the installed copy (portable zip, once feat/portable-mode lands)
- [ ] `umbractl status` reaches the running app

### Human checks
| Done | Check | Source |
|---|---|---|
| [ ] | Win+D keeps widgets visible | standing |
| [ ] | Dragging widgets | standing |
| [ ] | Edit mode resize and zoom | standing |
| [ ] | Multi-monitor and DPI (#5) | standing |
| [ ] | Notifications (`umbra.notify`) | standing |
| [ ] | Media widget / `umbra.media` | standing |
| [ ] | New or changed Settings pages (capture + eyeball) | standing |
| [ ] | (one row per collected "needs human check" line, with `#PR`) | PR bodies |

### Ship
- [ ] `node scripts/release.mjs <bump>` opened the `Release vX.Y.Z` PR; CI green; PR merged (no manual tag)
- [ ] `build.ps1 -Linux` + `gh release create` on this PC (release skill step 6); assets listed by `gh release view vX.Y.Z --json assets -q ".assets[].name"` (installer, portable zip, `umbra-<ver>-linux-x64.tar.gz` and `-arm64.tar.gz` with their `.sha256`)
- [ ] Release notes on GitHub match the PR body
- [ ] winget submission (`scripts/winget.ps1`)
- [ ] Note in the release text: installer is unsigned, SmartScreen will warn until #9 is done
