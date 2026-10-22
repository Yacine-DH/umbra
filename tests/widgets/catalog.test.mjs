// scripts/catalog.mjs: the optional "platforms" field and the Linux-aware permissions.commands rules.
// The script derives its repo root from its own location, so each test copies it into a throwaway repo tree and runs it there.
import test from "node:test";
import assert from "node:assert/strict";
import { cpSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const script = join(fileURLToPath(import.meta.url), "..", "..", "..", "scripts", "catalog.mjs");

/** Builds a repo with one widget (the manifest fields given are merged over a valid base), runs the catalog script, returns {status, out, index}. */
function run(manifest, { extra = [] } = {}) {
  const repo = mkdtempSync(join(tmpdir(), "umbra-catalog-"));
  try {
    mkdirSync(join(repo, "scripts"));
    cpSync(script, join(repo, "scripts", "catalog.mjs"));
    const dir = join(repo, "widgets", "demo");
    mkdirSync(dir, { recursive: true });
    writeFileSync(join(dir, "index.html"), "<html></html>");
    const base = { id: "demo", name: "Demo", description: "d", author: "a", version: "1.0.0" };
    writeFileSync(join(dir, "widget.json"), JSON.stringify({ ...base, ...manifest }));
    const r = spawnSync(process.execPath, [join(repo, "scripts", "catalog.mjs"), ...extra], { encoding: "utf8" });
    let index = null;
    try { index = JSON.parse(readFileSync(join(repo, "widgets", "index.json"), "utf8")); } catch { /* not written on failure */ }
    return { status: r.status, out: `${r.stdout}${r.stderr}`, index };
  } finally {
    rmSync(repo, { recursive: true, force: true });
  }
}

const cmd = (run0, params) => ({ permissions: { hosts: [], commands: [{ id: "x", run: run0, ...(params ? { params } : {}) }] } });

test("a widget without platforms has no platforms entry in the index", () => {
  const r = run({});
  assert.equal(r.status, 0, r.out);
  assert.equal("platforms" in r.index.widgets[0], false);
});

test("platforms is copied into the index as declared", () => {
  for (const platforms of [["windows"], ["linux"], ["windows", "linux"]]) {
    const r = run({ platforms });
    assert.equal(r.status, 0, r.out);
    assert.deepEqual(r.index.widgets[0].platforms, platforms);
  }
});

test("invalid platforms values fail validation", () => {
  for (const platforms of [[], ["macos"], ["Windows"], ["windows", "windows"], "windows", null, 7, [1], ["windows", "linux", "linux"]]) {
    const r = run({ platforms });
    assert.equal(r.status, 1, `${JSON.stringify(platforms)} should be rejected`);
    assert.match(r.out, /"platforms" must be a non-empty list/);
  }
});

test("the index check notices a stale platforms entry", () => {
  // run() writes the index and removes the repo, so check the script's own --check on a repo whose index lacks the field.
  const repo = mkdtempSync(join(tmpdir(), "umbra-catalog-"));
  try {
    mkdirSync(join(repo, "scripts"));
    cpSync(script, join(repo, "scripts", "catalog.mjs"));
    const dir = join(repo, "widgets", "demo");
    mkdirSync(dir, { recursive: true });
    writeFileSync(join(dir, "index.html"), "<html></html>");
    const manifest = { id: "demo", name: "Demo", description: "d", author: "a", version: "1.0.0" };
    writeFileSync(join(dir, "widget.json"), JSON.stringify(manifest));
    const exe = join(repo, "scripts", "catalog.mjs");
    assert.equal(spawnSync(process.execPath, [exe], { encoding: "utf8" }).status, 0);
    assert.equal(spawnSync(process.execPath, [exe, "--check"], { encoding: "utf8" }).status, 0);
    writeFileSync(join(dir, "widget.json"), JSON.stringify({ ...manifest, platforms: ["linux"] }));
    const stale = spawnSync(process.execPath, [exe, "--check"], { encoding: "utf8" });
    assert.equal(stale.status, 1);
    assert.match(stale.stderr, /out of date/);
  } finally {
    rmSync(repo, { recursive: true, force: true });
  }
});

test("a POSIX absolute path is a valid executable; a UNC-looking one is not", () => {
  assert.equal(run(cmd(["/usr/bin/claude", "-p", "hi"])).status, 0);
  assert.equal(run(cmd(["C:\\Tools\\app.exe"])).status, 0);
  for (const bad of ["//server/share/app", "/usr/bin/", "/usr/../bin/x", "bin/tool", "\\\\server\\share\\app.exe", "/usr/b\\in/x"]) {
    const r = run(cmd([bad]));
    assert.equal(r.status, 1, `${bad} should be rejected`);
  }
});

test("Linux shells, runtimes and launchers count as interpreters, so free-text parameters are refused", () => {
  for (const exe of ["sh", "dash", "zsh", "python3", "python3.12", "perl", "ruby", "env", "sudo", "xargs", "/usr/bin/bash", "/home/u/run.sh", "tool.py", "Bash.EXE"]) {
    const r = run(cmd([exe, "${p}"], { p: {} }));
    assert.equal(r.status, 1, `${exe} with free text should be rejected`);
    assert.match(r.out, /runs arbitrary code/);
    assert.equal(run(cmd([exe, "${p}"], { p: { choices: ["a", "b"] } })).status, 0, `${exe} with fixed choices is fine`);
  }
  assert.equal(run(cmd(["claude", "-p", "${p}"], { p: {} })).status, 0);
});
