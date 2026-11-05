---
name: release
description: Ship a new Umbra version with scripts/release.mjs (bump, checks, release PR with draft notes); merging the PR makes release.yml build the installer, tag vX.Y.Z and publish the release. Use for "release", "publish a new version", "cut vX.Y.Z".
---

# Release Umbra

Never bump versions, tag or create the release by hand: the script and the workflow do it.

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
5. **Wait for CI**, then merge the PR (squash).
6. **Watch** `gh run watch $(gh run list -w release.yml -L 1 --json databaseId -q ".[0].databaseId")`. On the merge to `main`,
   `release.yml` sees a version without a tag, runs the tests, builds and (if the secrets exist) signs the installer, creates
   the tag `vX.Y.Z` on the merge commit and the release `Umbra X.Y.Z` with the PR body as notes, and attaches the installer
   and the portable zip. A version that already has a tag is skipped.
7. **Verify:** `gh release view vX.Y.Z --json assets -q ".assets[].name"` lists `UmbraSetup-X.Y.Z.exe` (about 90 MB) and
   `Umbra-X.Y.Z-portable.zip`. The winget manifests are an artifact of the run (`scripts/winget.ps1`, CONTRIBUTING.md).

Rebuild an existing release: Actions > Release > Run workflow with the tag, or push the tag by hand.

Fallback if CI is down: `powershell -File build.ps1` locally, then
`gh release create vX.Y.Z dist/UmbraSetup-X.Y.Z.exe dist/Umbra-X.Y.Z-portable.zip --title "Umbra X.Y.Z" --notes-file <notes>`.
