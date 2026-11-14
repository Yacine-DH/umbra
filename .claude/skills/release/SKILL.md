---
name: release
description: Ship a new Umbra version with scripts/release.mjs (bump, checks, release PR with draft notes), local CI, merge, then build.ps1 and gh release create on this PC (GitHub Actions is off). Use for "release", "publish a new version", "cut vX.Y.Z".
---

# Release Umbra

Never bump versions by hand: `release.mjs` does it. The tag and the release come only from step 6 (`gh release create --target` on the
merge commit of the release PR), never from a hand-pushed tag.

1. **Checklist:** run `.claude/skills/release-checklist` first (`dist/release-checklist-<ver>.md`).
2. **Pick the bump** (semver): `patch` for fixes only, `minor` for features, `major` for breaking changes to the widget SDK
   or the protocol. Preview with `node scripts/release.mjs minor --dry-run`: it prints the next version and the draft notes
   and changes nothing.
3. **Start it** on a clean, up-to-date `main`: `node scripts/release.mjs minor` (or `patch`, `major`, `X.Y.Z`). It:
   - bumps `Directory.Build.props` and `installer/Umbra.iss` (keeping line endings and BOM);
   - runs `check-versions.mjs`, `catalog.mjs --check`, the build with `-warnaserror` and the tests (`--no-test` skips the
     last two; CI still runs them);
   - commits `Release vX.Y.Z` on `release/vX.Y.Z` as the user (no co-author trailer), pushes, and opens the PR with
     notes drafted from the PRs merged since the last tag (`type: feature` -> Added, `type: bug` -> Fixed, the rest -> Changed).
4. **Polish the notes** in the PR body, above `<!-- end of release notes -->`: user language, one bold lead per item, the style
   of earlier releases (`gh release view v1.10.0`). Use `gh pr edit <N> --body-file <file>`.
5. **Run CI on this PC** (`powershell -File scripts/ci-local.ps1 -Pr <N> -Status`), then merge the PR
   (`gh pr merge <N> --squash --delete-branch`; the `local-ci` status is the required check).
6. **Build and publish on this PC.** GitHub Actions is off, so `release.yml` doesn't run. On the updated `main`:
   `powershell -File build.ps1 -Linux` (Docker Desktop must be running: `-Linux` runs `scripts/linux-package.ps1`, which builds
   `dist/umbra-X.Y.Z-linux-x64.tar.gz` and `-arm64.tar.gz`, each with a `.sha256`, in Ubuntu 24.04 containers; arm64 is
   cross-built on amd64, and if that image ever fails, `scripts/linux-package.ps1 -Arch arm64 -Qemu` emulates it instead
   (slow, time-limited, retried once). Without Docker, drop `-Linux` and the Linux assets, or build them later on a Linux
   machine with `linux/container-package.sh . dist x64` and `gh release upload`). Check the portable zip runs (unzip it to a temp folder, start `Umbra.exe --background`,
   `umbractl status` shows the version and `portable: true`, then `umbractl quit`), and create the release on the merge commit
   with the notes above `<!-- end of release notes -->`:
   `gh release create vX.Y.Z dist/UmbraSetup-X.Y.Z.exe dist/Umbra-X.Y.Z-portable.zip dist/umbra-X.Y.Z-linux-x64.tar.gz dist/umbra-X.Y.Z-linux-x64.tar.gz.sha256 dist/umbra-X.Y.Z-linux-arm64.tar.gz dist/umbra-X.Y.Z-linux-arm64.tar.gz.sha256 --target <merge sha> --title "Umbra X.Y.Z" --notes-file <notes> --latest`.
   The installer is unsigned (no signing secrets here). Linux copies only notify: once the release has their arch's tarball,
   Settings > About and the tray say "Update available" and open the release page (`LinuxUpdater`); a release without the
   tarballs is never announced to Linux. TODO(#196): the Flatpak bundle joins these assets once the manifest and metainfo exist
   (then `check-versions.mjs` also checks the metainfo `<release>`, and `release.mjs` must add that entry).
7. **Verify:** `gh release view vX.Y.Z --json assets -q ".assets[].name"` lists `UmbraSetup-X.Y.Z.exe` (about 90 MB),
   `Umbra-X.Y.Z-portable.zip` and the two Linux tarballs with their `.sha256` (about 70 MB each). For winget, fill the manifests from the *released* installer:
   `gh release download vX.Y.Z -p UmbraSetup-X.Y.Z.exe -D <temp>`, then `powershell -File scripts/winget.ps1 -Installer <temp>/UmbraSetup-X.Y.Z.exe`
   (writes `dist/winget/X.Y.Z`; check with `winget validate --manifest`).

Rebuild an existing release: check out its tag in a worktree, run `build.ps1` there, and `gh release upload vX.Y.Z <files> --clobber`.
If GitHub Actions is ever turned back on (`gh workflow enable Release`), merging the release PR makes `release.yml` do step 6.
