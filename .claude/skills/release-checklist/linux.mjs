// Writes the Linux section of the release checklist (issue #200) from linux.md into dist/release-checklist-<ver>.md.
//
//   node .claude/skills/release-checklist/linux.mjs                 append to (or replace the Linux section of) dist/release-checklist-<ver>.md
//   node .claude/skills/release-checklist/linux.mjs --since v1.17.0 collect PRs merged after that tag (or an ISO date, or "all")
//   node .claude/skills/release-checklist/linux.mjs --stdout        print the section instead of writing the file
//   node .claude/skills/release-checklist/linux.mjs --prs prs.json  read PRs from a file instead of gh (offline; same JSON as gh)
//
// Fills in: the version (Directory.Build.props), the automated checks (linux/test/ci.sh if it exists, else every
// linux/test/*.sh with its header line), the Flatpak status (#196 open = blocked), and the "Human check" lines of every merged
// Linux PR (label "platform: linux", "Linux" in the title, or closing an issue #172-#200) since the last release that shipped a
// Linux tarball (all Linux PRs when none did). A merged PR without its own Human check section (a rebase or a stack) contributes
// the Human check sections of the closed PRs it mentions (#N), credited "#orig via #merged". Nothing is ever ticked.
import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, "..", "..", "..");
const repo = "AmineChr54/umbra";
const args = process.argv.slice(2);
const opt = (name) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : undefined; };
const run = (cmd, a) => execFileSync(cmd, a, { cwd: root, encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] });
const tryRun = (cmd, a) => { try { return run(cmd, a); } catch { return null; } };
const gh = (a) => JSON.parse(run("gh", [...a, "-R", repo]));

const ver = readFileSync(join(root, "Directory.Build.props"), "utf8").match(/<Version>([^<]+)<\/Version>/)[1];
const fixture = opt("--prs") ? JSON.parse(readFileSync(opt("--prs"), "utf8")) : null;

// Since when: an explicit --since, else the newest release (not this version) with a Linux tarball, else everything.
function since() {
  const s = opt("--since");
  if (s === "all") return { label: "all merged Linux PRs", date: null };
  if (s && /^\d{4}-\d{2}-\d{2}/.test(s)) return { label: `PRs merged since ${s}`, date: s };
  if (s) return { label: `PRs merged since ${s}`, date: run("git", ["log", "-1", "--format=%cI", s]).trim() };
  if (fixture) return { label: "all PRs in the --prs file", date: null };
  for (const r of gh(["release", "list", "--limit", "40", "--json", "tagName,publishedAt"])) {
    if (r.tagName === `v${ver}`) continue;
    const assets = gh(["release", "view", r.tagName, "--json", "assets"]).assets.map((a) => a.name);
    if (assets.some((n) => /linux-x64\.tar\.gz$/.test(n)))
      return { label: `PRs merged since ${r.tagName}, the last release with Linux tarballs`, date: r.publishedAt };
  }
  return { label: "all merged Linux PRs (no earlier release shipped Linux tarballs)", date: null };
}

// An issue of the Linux epic (#172-#200) named in the title ("(#192)") or by "Closes/Fixes/Part of #N" in the body.
const epicIssues = (pr) =>
  [...`${pr.title} ${pr.body ?? ""}`.matchAll(/(?:\(|\b(?:closes|fixes|resolves|part of)\s+)#(\d+)/gi)].map((m) => Number(m[1]));
const isLinux = (pr) =>
  (pr.labels ?? []).some((l) => l.name === "platform: linux") ||
  /\blinux\b/i.test(pr.title) ||
  epicIssues(pr).some((n) => n >= 172 && n <= 200);

// The bullets of a "## Human check(s)" section (nested lines joined to their bullet), plus lines elsewhere that name a
// human check ("Human check: ...", "... is a human check"). The PR template's "Needs a human check on real hardware" line
// and pointers ("see #372 for the human checks") are skipped.
function humanChecks(body) {
  const out = [];
  const lines = (body ?? "").replace(/\r/g, "").split("\n");
  let inSection = false;
  for (const line of lines) {
    if (/^#{1,6}\s/.test(line)) { inSection = /^#{1,6}\s*human checks?\b/i.test(line); continue; }
    if (inSection) {
      if (/^([-*]|\d+\.)\s+/.test(line)) out.push(line.replace(/^([-*]|\d+\.)\s+(\[[ xX]\]\s*)?/, ""));
      else if (/^\s+\S/.test(line) && out.length) out[out.length - 1] += " " + line.trim().replace(/^([-*]|\d+\.)\s+/, "");
      else if (line.trim() && !out.length) out.push(line.trim());
    } else if (/(^\W*(needs? )?(a )?human check\b|\b(is|are|needs?) (a )?human checks?\b)/i.test(line) &&
               !/human check on real hardware|\bsee #\d+/i.test(line)) {
      out.push(line.trim().replace(/^([-*]|\d+\.)\s+(\[[ xX]\]\s*)?/, ""));
    }
  }
  return out.map((t) => t.trim()).filter(Boolean);
}
const hasSection = (body) => /^#{1,6}\s*human checks?\b/im.test(body ?? "");

const prCache = new Map();
function closedPr(n) {
  if (prCache.has(n)) return prCache.get(n);
  let pr = null;
  if (fixture) pr = fixture.find((p) => p.number === n) ?? null;
  else {
    const raw = tryRun("gh", ["pr", "view", String(n), "-R", repo, "--json", "number,state,body,title"]);
    pr = raw ? JSON.parse(raw) : null;
  }
  if (pr && pr.state !== "CLOSED") pr = null; // merged ones are listed on their own when in range
  prCache.set(n, pr);
  return pr;
}

function collect(range) {
  const fields = "number,title,body,labels,mergedAt";
  const prs = fixture
    ? fixture.filter((p) => p.state === undefined || p.state === "MERGED")
    : gh(["pr", "list", "--state", "merged", "--limit", "400", "--json", fields]);
  const linux = prs
    .filter(isLinux)
    .filter((p) => !range.date || new Date(p.mergedAt) > new Date(range.date))
    .sort((a, b) => a.number - b.number);
  const rows = [], seen = new Set(), used = new Set();
  const add = (text, source) => {
    const key = text.toLowerCase().replace(/\s+/g, " ");
    if (!seen.has(key)) { seen.add(key); rows.push({ text, source }); }
  };
  for (const pr of linux) {
    for (const t of humanChecks(pr.body)) add(t, `#${pr.number}`);
    if (hasSection(pr.body)) continue;
    const refs = [...new Set([...(pr.body ?? "").matchAll(/#(\d+)/g)].map((m) => Number(m[1])))];
    for (const n of refs) {
      if (n === pr.number || used.has(n)) continue;
      const orig = closedPr(n);
      if (!orig || !hasSection(orig.body)) continue;
      used.add(n);
      for (const t of humanChecks(orig.body)) add(t, `#${n} via #${pr.number}`);
    }
  }
  return { linux, rows };
}

function automated() {
  const tests = join(root, "linux", "test");
  const out = [
    "Run on a Linux checkout of the release commit, never against your own running Umbra. Use a short private TMPDIR: the",
    "headless sessions put their profile and the control socket under it (a socket path must stay under 108 bytes), and a",
    "private one keeps umbractl away from an installed Umbra:",
    "",
    "```bash",
    "export TMPDIR=$(mktemp -d /tmp/u.XXXXXX)",
    "dotnet build Umbra.Linux.slnf -c Release -warnaserror && dotnet test Umbra.Linux.slnf -c Release",
    "```",
    "",
    "- [ ] `dotnet build Umbra.Linux.slnf -warnaserror` is 0 warnings and `dotnet test Umbra.Linux.slnf` passes (#176)",
    "- [ ] `node scripts/catalog.mjs --check` and `node scripts/check-versions.mjs` pass",
    "- [ ] `desktop-file-validate linux/io.github.aminechr54.Umbra.desktop` passes (#178)",
  ];
  if (existsSync(join(tests, "ci.sh"))) {
    out.push("- [ ] `linux/test/ci.sh` is green (runs every linux/test suite in its headless session)");
    return out.join("\n");
  }
  const extra = { "gnome.sh": ["gnome.sh", "gnome.sh fallback"], "sandbox.sh": ["sandbox.sh"] };
  const scripts = existsSync(tests) ? readdirSync(tests).filter((f) => f.endsWith(".sh") && f !== "headless.sh").sort() : [];
  out.push("- [ ] Each headless suite is green (no `linux/test/ci.sh` on main yet, so one by one):");
  for (const f of scripts) {
    const header = readFileSync(join(tests, f), "utf8").split("\n").slice(1)
      .find((l) => /^#\s+\S/.test(l))?.replace(/^#\s+/, "").replace(/\s+/g, " ") ?? "";
    const what = header.length > 140 ? header.slice(0, header.lastIndexOf(" ", 137)) + " ..." : header;
    for (const cmd of extra[f] ?? [f]) out.push(`  - [ ] \`linux/test/${cmd}\`: ${what.replace(/\|/g, "\\|")}`);
  }
  out.push("- [ ] Captures the suites print (crash, update, gnome, media) inspected");
  return out.join("\n");
}

function flatpak() {
  let state = "OPEN";
  if (!fixture) state = tryRun("gh", ["issue", "view", "196", "-R", repo, "--json", "state", "-q", ".state"])?.trim() ?? "OPEN";
  if (state === "OPEN") return { line: "**blocked until #196** (manifest and metainfo not in the repo yet); write `n/a (#196)`", cell: "blocked #196" };
  return { line: "`flatpak-builder` bundle built, `appstreamcli validate` passes, the metainfo `<release>` matches (#196)", cell: "[ ]" };
}

const range = since();
const { linux, rows } = collect(range);
const cell = (s) => s.replace(/\|/g, "\\|").replace(/\s+/g, " ").trim();
const collected = [
  `Gathered from ${range.label}: ${linux.length} PR(s) (${linux.map((p) => `#${p.number}`).join(", ") || "none"}). Each "Human check"`,
  "line, once; check it on the configurations it names and note which in the Check column.",
  "",
  "| Done | Check | Source |",
  "|---|---|---|",
  ...(rows.length ? rows.map((r) => `| [ ] | ${cell(r.text)} | ${r.source} |`) : ["| | (no human checks in these PRs) | |"]),
].join("\n");
const fp = flatpak();
const template = readFileSync(join(here, "linux.md"), "utf8").replace(/\r/g, "");
const section = template
  .slice(template.indexOf("<!-- linux-section:start -->"))
  .replaceAll("{{ver}}", ver)
  .replace("{{automated}}", automated())
  .replace("{{collected}}", collected)
  .replace("{{flatpak}}", fp.line)
  .replaceAll("{{flatpakcell}}", fp.cell);

if (args.includes("--stdout")) { process.stdout.write(section); process.exit(0); }
const file = opt("--out") ?? join(root, "dist", `release-checklist-${ver}.md`);
mkdirSync(dirname(file), { recursive: true });
let text = existsSync(file) ? readFileSync(file, "utf8") : `# Umbra ${ver} release checklist\n`;
const start = text.indexOf("<!-- linux-section:start -->"), end = text.indexOf("<!-- linux-section:end -->");
if (start >= 0 && end > start) text = text.slice(0, start) + text.slice(end + "<!-- linux-section:end -->\n".length);
writeFileSync(file, text.replace(/\s*$/, "\n\n") + section);
console.log(`${file}: Linux section, ${rows.length} human check(s) from ${linux.length} PR(s), ${range.label}`);
