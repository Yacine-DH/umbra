// scripts/release.mjs: version math, file bumps that keep BOM and line endings, draft notes, notes taken from a PR body.
//   node --test tests/release/*.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { nextVersion, bumpProps, bumpIss, bumpMetainfo, propsVersion, draftNotes, notesFromBody, NOTES_END, UNSIGNED } from "../../scripts/release.mjs";

const root = join(import.meta.dirname, "..", "..");

test("next version from patch, minor, major or an explicit one", () => {
  assert.equal(nextVersion("1.10.0", "patch"), "1.10.1");
  assert.equal(nextVersion("1.10.3", "minor"), "1.11.0");
  assert.equal(nextVersion("1.10.3", "major"), "2.0.0");
  assert.equal(nextVersion("1.10.0", "1.12.0"), "1.12.0");
  assert.equal(nextVersion("1.10.0", "v1.10.1"), "1.10.1");
  assert.throws(() => nextVersion("1.10.0", "1.10.0"), /not higher/);
  assert.throws(() => nextVersion("1.10.0", "1.9.9"), /not higher/);
  assert.throws(() => nextVersion("1.10.0", "banana"), /not a version/);
});

test("bumping keeps the BOM, line endings and everything else", () => {
  const props = "﻿<Project>\r\n  <PropertyGroup>\r\n    <Version>1.10.0</Version>\r\n    <Product>Umbra</Product>\r\n  </PropertyGroup>\r\n</Project>\r\n";
  const bumped = bumpProps(props, "1.11.0");
  assert.equal(bumped, props.replace("1.10.0", "1.11.0"));
  assert.equal(propsVersion(bumped), "1.11.0");
  const iss = "; Umbra\n#ifndef AppVersion\n  #define AppVersion \"1.10.0\"\n#endif\n#define Other \"1.10.0\"\n";
  assert.equal(bumpIss(iss, "1.11.0"), iss.replace('AppVersion "1.10.0"', 'AppVersion "1.11.0"'));
  assert.throws(() => bumpProps("<Project/>", "1.0.1"), /version not found/);
});

test("the real files have a version the bump can find", () => {
  const props = readFileSync(join(root, "Directory.Build.props"), "utf8");
  const v = propsVersion(props);
  assert.match(v, /^\d+\.\d+\.\d+$/);
  assert.match(bumpIss(readFileSync(join(root, "installer", "Umbra.iss"), "utf8"), "99.0.0"), /AppVersion "99\.0\.0"/);
});

test("draft notes group merged PRs by type and leave out releases and chores", () => {
  const notes = draftNotes([
    { number: 317, title: "Script Runner: your own scripts in Settings", labels: [{ name: "type: feature" }] },
    { number: 315, title: "Hearth 1.4.1: keep Offline readable", labels: [{ name: "type: widget" }] },
    { number: 311, title: "Fix clipped text in dialogs", labels: [{ name: "type: bug" }] },
    { number: 312, title: "Release v1.10.0", labels: [] },
    { number: 320, title: "CI: cache NuGet", labels: [{ name: "type: chore" }] },
  ]);
  assert.match(notes, /^## Added\n\n- \*\*Script Runner: your own scripts in Settings\*\* \(#317\)/);
  assert.match(notes, /## Changed\n\n- \*\*Hearth 1\.4\.1: keep Offline readable\*\* \(#315\)/);
  assert.match(notes, /## Fixed\n\n- \*\*Fix clipped text in dialogs\*\* \(#311\)/);
  assert.doesNotMatch(notes, /Release v1\.10\.0|cache NuGet/);
  assert.ok(notes.trimEnd().endsWith(UNSIGNED));
  assert.match(draftNotes([]), /Small fixes and improvements/);
});

test("the release notes are the PR body above the end marker", () => {
  const body = `## Added\r\n\r\n- Zoom\r\n\r\n${NOTES_END}\r\n\r\nMerging this publishes Umbra 1.11.0.`;
  assert.equal(notesFromBody(body), "## Added\n\n- Zoom");
  assert.equal(notesFromBody("## Fixed\n\n- A bug\n"), "## Fixed\n\n- A bug");
  assert.equal(notesFromBody(null), "");
});

test("the metainfo gets a <release> entry on top, once, in its own indentation and line endings", () => {
  const xml = "<component>\r\n  <releases>\r\n    <release version=\"1.17.0\" date=\"2026-11-12\">\r\n      <url type=\"details\">https://github.com/AmineChr54/umbra/releases/tag/v1.17.0</url>\r\n    </release>\r\n  </releases>\r\n</component>\r\n";
  const out = bumpMetainfo(xml, "1.18.0", "2026-11-15");
  assert.equal(out, xml.replace("    <release version=\"1.17.0\"",
    "    <release version=\"1.18.0\" date=\"2026-11-15\">\r\n      <url type=\"details\">https://github.com/AmineChr54/umbra/releases/tag/v1.18.0</url>\r\n    </release>\r\n    <release version=\"1.17.0\""));
  assert.equal(bumpMetainfo(out, "1.18.0", "2026-11-16"), out, "already listed: unchanged");
  assert.throws(() => bumpMetainfo("<releases/>", "1.0.1", "2026-01-01"), /no <release>/);
  const real = readFileSync(join(root, "linux", "io.github.aminechr54.Umbra.metainfo.xml"), "utf8");
  assert.match(bumpMetainfo(real, "99.0.0", "2030-01-01"), /<release version="99\.0\.0" date="2030-01-01">/);
});
