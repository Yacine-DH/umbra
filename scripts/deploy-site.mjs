#!/usr/bin/env node
// Deploys the website (site/) to GitHub Pages from this PC, since GitHub Actions is off (pages.yml can't run).
// Builds the committed HEAD exactly like pages.yml did (Node 24, npm ci, npm run build) inside Docker, from a
// `git archive` export, so the working tree and its node_modules are never touched. Then force-pushes site/dist as the
// single commit of the `gh-pages` branch, which Pages serves ("Deploy from a branch": gh-pages, /).
//
//   node scripts/deploy-site.mjs            build and deploy HEAD (must be main, in sync with origin/main)
//   node scripts/deploy-site.mjs --dry-run  build only, print where dist is
//
// Needs Docker Desktop running. If Actions is ever back (`gh workflow enable Website`), switch Pages back to
// "GitHub Actions" in the repo settings and stop using this.
import { execFileSync } from "node:child_process";
import { mkdtempSync, rmSync, writeFileSync, existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const dry = process.argv.includes("--dry-run");
const run = (cmd, args, opts = {}) => execFileSync(cmd, args, { stdio: "inherit", ...opts });
const out = (cmd, args, opts = {}) => execFileSync(cmd, args, { encoding: "utf8", ...opts }).trim();
const fail = (msg) => { console.error(msg); process.exit(1); };

const root = out("git", ["rev-parse", "--show-toplevel"]);
const sha = out("git", ["rev-parse", "HEAD"], { cwd: root });
if (!dry) {
  const branch = out("git", ["rev-parse", "--abbrev-ref", "HEAD"], { cwd: root });
  if (branch !== "main") fail(`Deploy from main (on ${branch}).`);
  run("git", ["fetch", "-q", "origin", "main"], { cwd: root });
  if (out("git", ["rev-parse", "origin/main"], { cwd: root }) !== sha) fail("HEAD is not origin/main: pull or push first.");
}
try { out("docker", ["info", "--format", "{{.ServerVersion}}"]); } catch { fail("Docker is not running: start Docker Desktop."); }

const work = mkdtempSync(join(tmpdir(), "umbra-site-"));
console.log(`> Exporting ${sha.slice(0, 7)} to ${work}`);
const tar = join(work, "src.tar");
run("git", ["archive", "--format=tar", "-o", tar, "HEAD"], { cwd: root });
const src = join(work, "repo");
run("docker", ["run", "--rm", "-v", `${work}:/w`, "node:24", "sh", "-c",
  "mkdir -p /w/repo && tar -xf /w/src.tar -C /w/repo && cd /w/repo/site && npm ci --no-audit --no-fund && npm run build && chmod -R a+rwX /w/repo/site/dist"]);
const dist = join(src, "site", "dist");
if (!existsSync(join(dist, "index.html"))) fail("Build produced no site/dist/index.html.");
// Pages runs Jekyll on branch deploys unless told not to, and Jekyll drops Astro's _astro/ folder.
writeFileSync(join(dist, ".nojekyll"), "");
if (dry) { console.log(`Built (dry run): ${dist}`); process.exit(0); }

const remote = out("git", ["remote", "get-url", "origin"], { cwd: root });
const g = (...a) => run("git", ["-C", dist, ...a]);
g("init", "-q", "-b", "gh-pages");
g("add", "-A");
g("-c", "user.name=" + out("git", ["config", "user.name"], { cwd: root }), "-c", "user.email=" + out("git", ["config", "user.email"], { cwd: root }),
  "commit", "-q", "-m", `Deploy website from ${sha.slice(0, 7)}`);
g("push", "-q", "--force", remote, "gh-pages:gh-pages");
console.log(`Deployed ${sha.slice(0, 7)} to gh-pages. Pages publishes it in a minute: https://aminechr54.github.io/umbra/`);
try { rmSync(work, { recursive: true, force: true }); } catch { /* Docker may still hold files; temp is cleaned later */ }
