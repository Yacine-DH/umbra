// Starts an Umbra release in one command. Merging the PR it opens publishes the release (release.yml).
//
//   node scripts/release.mjs minor            patch | minor | major | 1.12.0
//   node scripts/release.mjs minor --dry-run  show the version and the draft notes, change nothing
//   node scripts/release.mjs minor --no-test  skip the local build and tests (CI still runs them on the PR)
//
// Steps: check the tree is clean and on an up-to-date main, bump Directory.Build.props and installer/Umbra.iss
// (keeping their line endings and BOM), run check-versions, the catalog check, build and tests, draft notes from the
// PRs merged since the last tag, commit "Release vX.Y.Z" on release/vX.Y.Z, push and open the PR with the notes as its body.
// Edit the notes in the PR if you like: when it is merged, release.yml builds the installer, creates the tag and the
// GitHub release, and uses the PR body (above the end-of-notes marker) as the release notes.
import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync, mkdirSync } from "node:fs";
import { join } from "node:path";
import { pathToFileURL } from "node:url";

const root = join(import.meta.dirname, "..");
export const REPO = "AmineChr54/umbra";
export const NOTES_END = "<!-- end of release notes -->";
export const UNSIGNED = "The installer is unsigned, so Windows SmartScreen will warn until code signing is set up (#9).";

// ---------- pure helpers (tests/release/release.test.mjs) ----------

/** The next version: "patch" | "minor" | "major" | an explicit X.Y.Z that is higher than the current one. */
export function nextVersion(current, bump) {
  const parse = (v) => {
    const m = /^(\d+)\.(\d+)\.(\d+)$/.exec(String(v).trim().replace(/^v/, ""));
    if (!m) throw new Error(`"${v}" is not a version like 1.2.3`);
    return m.slice(1).map(Number);
  };
  const [a, b, c] = parse(current);
  let next;
  if (bump === "patch") next = [a, b, c + 1];
  else if (bump === "minor") next = [a, b + 1, 0];
  else if (bump === "major") next = [a + 1, 0, 0];
  else next = parse(bump);
  const cmp = next[0] - a || next[1] - b || next[2] - c;
  if (cmp <= 0) throw new Error(`${next.join(".")} is not higher than the current ${current}`);
  return next.join(".");
}

/** Replaces one version in a file's text with a regex whose first group is the version; BOM and line endings are untouched. */
function replaceVersion(text, re, version, file) {
  if (!re.test(text)) throw new Error(`${file}: version not found`);
  return text.replace(re, (all, old) => all.replace(old, version));
}
export const bumpProps = (text, v) => replaceVersion(text, /<Version>\s*([^<\s]+)\s*<\/Version>/, v, "Directory.Build.props");
export const bumpIss = (text, v) => replaceVersion(text, /#define\s+AppVersion\s+"([^"]+)"/, v, "installer/Umbra.iss");
export const propsVersion = (text) => /<Version>\s*([^<\s]+)\s*<\/Version>/.exec(text)?.[1];

/**
 * Draft notes from merged PRs ([{ number, title, labels: [{ name }] }]): "type: feature" -> Added, "type: bug" -> Fixed,
 * everything else -> Changed. Release PRs and chores only for CI/maintainers are left out. Edit the draft in the PR.
 */
export function draftNotes(prs) {
  const groups = { Added: [], Changed: [], Fixed: [] };
  for (const pr of [...prs].sort((x, y) => x.number - y.number)) {
    const labels = (pr.labels || []).map((l) => l.name);
    if (/^Release v\d/.test(pr.title) || labels.includes("type: chore")) continue;
    const group = labels.includes("type: bug") ? "Fixed" : labels.includes("type: feature") ? "Added" : "Changed";
    groups[group].push(`- **${pr.title.replace(/\s+$/, "")}** (#${pr.number})`);
  }
  const parts = Object.entries(groups).filter(([, items]) => items.length).map(([name, items]) => `## ${name}\n\n${items.join("\n")}`);
  if (!parts.length) parts.push("## Changed\n\n- Small fixes and improvements.");
  return `${parts.join("\n\n")}\n\n${UNSIGNED}\n`;
}

/** The release notes inside a release PR body: everything above the end marker (all of it when there is none). */
export function notesFromBody(body) {
  const text = String(body || "").replace(/\r\n/g, "\n");
  const i = text.indexOf(NOTES_END);
  return (i >= 0 ? text.slice(0, i) : text).trim();
}

// ---------- the command ----------

// No shell anywhere: arguments such as "merged:>2026-10-31..." reach the program as they are.
const run = (cmd, args, opts = {}) => execFileSync(cmd, args, { cwd: root, encoding: "utf8", stdio: opts.show ? "inherit" : ["ignore", "pipe", "pipe"] });
const git = (...args) => run("git", args).trim();
const gh = (...args) => run("gh", args).trim();
const step = (s) => console.log(`\n> ${s}`);

function main(argv) {
  const bump = argv.find((a) => !a.startsWith("--"));
  const dry = argv.includes("--dry-run"), noTest = argv.includes("--no-test");
  if (!bump) { console.error("Usage: node scripts/release.mjs <patch|minor|major|X.Y.Z> [--dry-run] [--no-test]"); process.exit(2); }

  const propsPath = join(root, "Directory.Build.props"), issPath = join(root, "installer", "Umbra.iss");
  const props = readFileSync(propsPath, "utf8"), iss = readFileSync(issPath, "utf8");
  const current = propsVersion(props);
  const version = nextVersion(current, bump);
  const tag = `v${version}`, branch = `release/${tag}`;

  step("Checking the repository");
  git("fetch", "--quiet", "--tags", "origin");
  if (git("tag", "--list", tag)) throw new Error(`${tag} already exists`);
  const lastTag = git("describe", "--tags", "--abbrev=0", "origin/main");
  if (!dry) {
    if (git("rev-parse", "--abbrev-ref", "HEAD") !== "main") throw new Error("Switch to main first (git switch main)");
    if (git("status", "--porcelain", "--untracked-files=no")) throw new Error("Commit or stash your changes first: the release commit holds only the version bump");
    if (git("rev-parse", "HEAD") !== git("rev-parse", "origin/main")) throw new Error("main is not at origin/main: run git pull --ff-only");
  }

  step(`Drafting notes from PRs merged since ${lastTag}`);
  const since = git("log", "-1", "--format=%cI", lastTag);
  const prs = JSON.parse(gh("pr", "list", "--repo", REPO, "--state", "merged", "--base", "main", "--limit", "200",
    "--search", `merged:>${since}`, "--json", "number,title,labels"));
  const notes = draftNotes(prs);
  console.log(`${current} -> ${version}, ${prs.length} merged PR(s)\n\n${notes}`);
  if (dry) return;

  step(`Bumping to ${version}`);
  writeFileSync(propsPath, bumpProps(props, version), "utf8");
  writeFileSync(issPath, bumpIss(iss, version), "utf8");
  run("node", ["scripts/check-versions.mjs"], { show: true });
  run("node", ["scripts/catalog.mjs", "--check"], { show: true });
  if (!noTest) {
    step("Build and tests");
    run("dotnet", ["build", "Umbra.slnx", "-warnaserror", "-v", "q", "-nologo"], { show: true });
    run("dotnet", ["test", "Umbra.slnx", "--no-build"], { show: true });
  }

  step(`Committing on ${branch} and opening the release PR`);
  git("switch", "--quiet", "-c", branch);
  git("commit", "--quiet", "-m", `Release ${tag}`, "--", "Directory.Build.props", "installer/Umbra.iss");
  git("push", "--quiet", "-u", "origin", branch);
  mkdirSync(join(root, "dist"), { recursive: true });
  const bodyFile = join(root, "dist", "release-pr.md");
  writeFileSync(bodyFile, `${notes}\n${NOTES_END}\n\nMerging this publishes **Umbra ${version}**: release.yml builds the installer, tags \`${tag}\` and uses the notes above.\nEdit them here before merging. Checklist: \`.claude/skills/release-checklist\`.\n`);
  const url = gh("pr", "create", "--repo", REPO, "--base", "main", "--head", branch, "--title", `Release ${tag}`,
    "--body-file", bodyFile, "--label", "type: chore", "--label", "area: installer");
  git("switch", "--quiet", "main");
  console.log(`\nRelease PR: ${url}\nEdit the notes in the PR if needed, wait for CI, merge it: release.yml publishes ${tag}.`);
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? "").href) {
  try { main(process.argv.slice(2)); }
  catch (e) { console.error(`\nrelease: ${e.message}`); process.exit(1); }
}
