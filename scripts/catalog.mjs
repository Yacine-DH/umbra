#!/usr/bin/env node
// Validates every widgets/<id>/ folder and (re)generates widgets/index.json,
// the catalog that Umbra's Browse page reads.
//
//   node scripts/catalog.mjs          validate + write widgets/index.json
//   node scripts/catalog.mjs --check  validate + fail if index.json is out of date (CI on pull requests)

import { readFileSync, writeFileSync, readdirSync, statSync, existsSync } from "node:fs";
import { join, relative, extname, sep } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(fileURLToPath(import.meta.url), "..", "..");
const widgetsDir = join(root, "widgets");
const indexPath = join(widgetsDir, "index.json");
const check = process.argv.includes("--check");

const ID = /^[a-z0-9][a-z0-9-]{1,39}$/;
const SEMVER = /^\d+\.\d+\.\d+$/;
const HOST = /^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,}$/i;
const ALLOWED = new Set([".json", ".html", ".css", ".js", ".mjs", ".svg", ".png", ".jpg", ".jpeg", ".webp", ".gif", ".woff", ".woff2", ".ttf", ".otf", ".txt", ".md"]);
const MAX_BYTES = 5 * 1024 * 1024;
const MAX_CHANGELOG = 500;
const OPTION_TYPES = new Set(["text", "number", "select", "toggle"]);
// Highest widget.json format the app understands (WidgetManifest.SupportedManifestVersion).
const MANIFEST_VERSION = 1;
// Same list and order as WidgetManifest.Categories in the app.
const CATEGORIES = ["time", "productivity", "info", "markets", "system", "media", "developer", "social", "games"];
const KNOWN_KEYS = new Set(["$schema", "id", "name", "description", "author", "version", "manifestVersion", "minUmbraVersion", "changelog", "category", "entry", "preview", "icon", "width", "height", "themeable", "private", "defaultPosition", "platforms", "permissions", "options"]);

const errors = [];
const fail = (id, msg) => errors.push(`widgets/${id}: ${msg}`);

function walk(dir) {
  return readdirSync(dir).flatMap((name) => {
    const p = join(dir, name);
    return statSync(p).isDirectory() ? walk(p) : [p];
  });
}

// permissions.commands: the same rules as CommandSpec.Validate in the host (src/Umbra.Core/WidgetCommands.cs).
const CMD_ID = /^[a-z0-9][a-z0-9-]{0,39}$/;
const PLACEHOLDER = /^\$\{([a-z][A-Za-z0-9]{0,31})\}$/;
const BARE_EXE = /^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$/;
const ABS_EXE = /^[A-Za-z]:[\\/][^\0*?"<>|]{1,250}$/;
const POSIX_ABS_EXE = /^\/(?!\/)[^\0*?"<>|\\]{1,250}$/; // no leading "//" (a UNC path on Windows)
const PARAM_NAME = /^[a-z][A-Za-z0-9]{0,31}$/;
// Programs that run whatever their arguments name (shells, script hosts, runtimes, launchers): free-text parameters are refused for them.
const INTERPRETERS = new Set(["cmd", "powershell", "pwsh", "wscript", "cscript", "mshta", "node", "python", "pythonw", "py", "bash", "sh", "wsl", "rundll32", "regsvr32", "msiexec",
  "dash", "ash", "zsh", "fish", "ksh", "csh", "tcsh", "busybox", "nodejs", "deno", "bun", "perl", "ruby", "php", "lua", "osascript",
  "env", "xargs", "sudo", "su", "doas", "pkexec", "nohup", "setsid", "nice", "ionice", "timeout", "systemd-run", "flatpak", "flatpak-spawn",
  "awk", "gawk", "sed", "find", "xterm"]);
const VERSIONED_INTERPRETER = /^(python|pypy|perl|ruby|php|lua|node)[0-9][0-9.]*$/i; // python3, python3.12, perl5.38...
const SCRIPT_SUFFIX = /\.(ps1|bat|cmd|vbs|js|mjs|cjs|sh|bash|py|pl|rb|php|lua)$/i;
const PLATFORMS = ["windows", "linux"];
const CMD_KEYS = new Set(["id", "run", "description", "params", "timeoutSeconds"]);
const hasControl = (s) => /[\u0000-\u0008\u000a-\u001f\u007f]/.test(s);

/** Returns [{id, run (display string), description}] for the index; reports problems through fail(). */
function validateCommands(wid, commands) {
  if (commands === undefined) return [];
  if (!Array.isArray(commands)) { fail(wid, `"permissions.commands" must be an array`); return []; }
  if (commands.length > 16) fail(wid, "at most 16 commands per widget");
  const out = [];
  const seen = new Set();
  for (const c of commands) {
    const name = `command "${c?.id}"`;
    if (typeof c !== "object" || c === null || Array.isArray(c)) { fail(wid, "each command must be an object"); continue; }
    for (const k of Object.keys(c)) if (!CMD_KEYS.has(k)) fail(wid, `${name}: unknown key "${k}"`);
    if (typeof c.id !== "string" || !CMD_ID.test(c.id)) fail(wid, `${name}: id must be 1-40 lowercase letters, digits and dashes`);
    else if (seen.has(c.id)) fail(wid, `duplicate command id "${c.id}"`);
    else seen.add(c.id);
    if (c.description !== undefined && (typeof c.description !== "string" || c.description.length > 200)) fail(wid, `${name}: description must be a string of at most 200 characters`);
    if (c.timeoutSeconds !== undefined && (!Number.isInteger(c.timeoutSeconds) || c.timeoutSeconds < 1 || c.timeoutSeconds > 120)) fail(wid, `${name}: timeoutSeconds must be an integer from 1 to 120`);
    const run = c.run;
    if (!Array.isArray(run) || run.length < 1 || run.length > 32 || run.some((a) => typeof a !== "string" || a.length > 1024 || hasControl(a))) {
      fail(wid, `${name}: "run" must be an array of 1-32 strings (executable first, then arguments)`);
      continue;
    }
    if (!BARE_EXE.test(run[0]) && !ABS_EXE.test(run[0]) && !POSIX_ABS_EXE.test(run[0])) fail(wid, `${name}: executable "${run[0]}" must be a plain program name or an absolute path like C:\\Tools\\app.exe or /usr/bin/app`);
    if (/[\\/]$/.test(run[0]) || run[0].includes("..")) fail(wid, `${name}: executable path is invalid`);
    const params = c.params ?? {};
    const used = new Set();
    for (const a of run.slice(1)) {
      const p = PLACEHOLDER.exec(a);
      if (a.includes("${") && !p) fail(wid, `${name}: "${a}": a parameter must be a whole argument written exactly as \${name}`);
      if (p) { used.add(p[1]); if (!(p[1] in params)) fail(wid, `${name}: \${${p[1]}} is not declared in "params"`); }
    }
    const file = run[0].replace(/\\/g, "/").split("/").pop();
    const exeName = file.replace(/\.(exe|com)$/i, "");
    const interpreter = SCRIPT_SUFFIX.test(file) || INTERPRETERS.has(exeName.toLowerCase()) || VERSIONED_INTERPRETER.test(exeName);
    for (const [pn, spec] of Object.entries(params)) {
      if (interpreter && !(typeof spec === "object" && spec !== null && Array.isArray(spec.choices)))
        fail(wid, `${name}: "${run[0]}" runs arbitrary code, so parameter "${pn}" needs fixed "choices"`);
      if (!PARAM_NAME.test(pn)) fail(wid, `${name}: invalid parameter name "${pn}"`);
      if (!used.has(pn)) fail(wid, `${name}: parameter "${pn}" is declared but never used`);
      if (typeof spec !== "object" || spec === null) { fail(wid, `${name}: parameter "${pn}" must be an object`); continue; }
      for (const k of Object.keys(spec)) if (k !== "maxLength" && k !== "choices") fail(wid, `${name}: parameter "${pn}" may only have maxLength and choices`);
      const max = spec.maxLength ?? 200;
      if (!Number.isInteger(max) || max < 1 || max > 1000) fail(wid, `${name}: parameter "${pn}" maxLength must be 1-1000`);
      if (spec.choices !== undefined && (!Array.isArray(spec.choices) || spec.choices.length < 1 || spec.choices.length > 64
        || spec.choices.some((x) => typeof x !== "string" || !x.length || x.length > max)))
        fail(wid, `${name}: parameter "${pn}" choices must be 1-64 non-empty strings within maxLength`);
    }
    out.push({ id: c.id, run: run.map((a) => (/[\s"]/.test(a) || a === "" ? JSON.stringify(a) : a)).join(" "), description: c.description ?? "" });
  }
  return out;
}

const entries = [];
for (const id of readdirSync(widgetsDir).sort()) {
  const dir = join(widgetsDir, id);
  if (!statSync(dir).isDirectory()) continue;

  const manifestPath = join(dir, "widget.json");
  if (!existsSync(manifestPath)) { fail(id, "missing widget.json"); continue; }

  let m;
  try { m = JSON.parse(readFileSync(manifestPath, "utf8")); }
  catch (e) { fail(id, `widget.json is not valid JSON (${e.message})`); continue; }

  // Same rules as schemas/widget.schema.json (kept dependency-free, so CI needs only Node).
  for (const k of Object.keys(m)) if (!KNOWN_KEYS.has(k)) fail(id, `unknown key "${k}" in widget.json (typo?)`);
  if (!ID.test(id)) fail(id, "folder name must be lowercase letters, digits and dashes (2-40 chars)");
  if (m.id !== id) fail(id, `"id" must equal the folder name ("${id}")`);
  for (const k of ["name", "description", "author", "version"])
    if (typeof m[k] !== "string" || !m[k].trim()) fail(id, `"${k}" is required`);
  if (m.version && !SEMVER.test(m.version)) fail(id, `"version" must look like 1.2.3`);
  if (m.manifestVersion !== undefined && (!Number.isInteger(m.manifestVersion) || m.manifestVersion < 1 || m.manifestVersion > MANIFEST_VERSION))
    fail(id, `"manifestVersion" must be an integer from 1 to ${MANIFEST_VERSION}`);
  if (m.minUmbraVersion !== undefined && (typeof m.minUmbraVersion !== "string" || !SEMVER.test(m.minUmbraVersion)))
    fail(id, `"minUmbraVersion" must look like 1.9.0`);
  if (m.changelog !== undefined && (typeof m.changelog !== "string" || !m.changelog.trim() || m.changelog.length > MAX_CHANGELOG))
    fail(id, `"changelog" must be a non-empty string of at most ${MAX_CHANGELOG} characters (what changed in this version)`);
  if (!CATEGORIES.includes(m.category)) fail(id, `"category" is required: one of ${CATEGORIES.join(", ")}`);
  if (m.private !== undefined && typeof m.private !== "boolean") fail(id, `"private" must be true or false`);
  if (m.private === true) fail(id, `"private" widgets are installed from a file, not published in the catalog (see docs/widget-sdk.md)`);
  const entry = m.entry || "index.html";
  if (!existsSync(join(dir, entry))) fail(id, `entry file "${entry}" not found`);
  if (m.preview && !existsSync(join(dir, m.preview))) fail(id, `preview "${m.preview}" not found`);
  if (m.icon !== undefined && (typeof m.icon !== "string" || !/^[A-Za-z0-9_-]+.png$/.test(m.icon) || !existsSync(join(dir, m.icon)) || statSync(join(dir, m.icon)).size > 65536))
    fail(id, `"icon" must be an existing .png in the widget folder (letters, digits, - or _), at most 64 KB`);
  for (const k of ["width", "height"])
    if (m[k] !== undefined && (!Number.isInteger(m[k]) || m[k] < 80 || m[k] > 1200)) fail(id, `"${k}" must be an integer between 80 and 1200`);

  // Optional OS restriction (absent = both): a non-empty list of unique, lowercase "windows" / "linux". Same rule as WidgetPlatforms.Validate.
  if (m.platforms !== undefined && (!Array.isArray(m.platforms) || !m.platforms.length || new Set(m.platforms).size !== m.platforms.length || m.platforms.some((p) => !PLATFORMS.includes(p))))
    fail(id, `"platforms" must be a non-empty list of unique values from: ${PLATFORMS.join(", ")} (leave it out for both)`);

  const hosts = m.permissions?.hosts ?? [];
  if (!Array.isArray(hosts)) fail(id, `"permissions.hosts" must be an array`);
  else for (const h of hosts) if (typeof h !== "string" || !HOST.test(h)) fail(id, `invalid host "${h}" (hostnames only, no scheme or path)`);

  const system = m.permissions?.system ?? [];
  if (!Array.isArray(system)) fail(id, `"permissions.system" must be an array`);
  else for (const s of system) if (!["cpu", "memory", "network", "gpu", "disk"].includes(s)) fail(id, `unknown system metric "${s}" (cpu, memory, network, gpu, disk)`);

  const media = m.permissions?.media ?? [];
  if (!Array.isArray(media)) fail(id, `"permissions.media" must be an array`);
  else for (const s of media) if (!["read", "control"].includes(s)) fail(id, `unknown media permission "${s}" (read, control)`);
  if (m.permissions?.notifications !== undefined && typeof m.permissions.notifications !== "boolean") fail(id, `"permissions.notifications" must be a boolean`);
  if (m.permissions?.camera !== undefined && typeof m.permissions.camera !== "boolean") fail(id, `"permissions.camera" must be a boolean`);
  if (m.permissions?.saveImages !== undefined && typeof m.permissions.saveImages !== "boolean") fail(id, `"permissions.saveImages" must be a boolean`);
  if (m.permissions?.userScripts !== undefined && typeof m.permissions.userScripts !== "boolean") fail(id, `"permissions.userScripts" must be a boolean`);

  const commands = validateCommands(id, m.permissions?.commands);

  for (const [key, spec] of Object.entries(m.options ?? {})) {
    if (!OPTION_TYPES.has(spec?.type)) fail(id, `option "${key}" has unknown type "${spec?.type}"`);
    if (spec?.type === "select" && !Array.isArray(spec.choices)) fail(id, `select option "${key}" needs "choices"`);
    if (spec?.group !== undefined && (typeof spec.group !== "string" || !spec.group.trim() || spec.group.length > 40))
      fail(id, `option "${key}": "group" must be a non-empty string of at most 40 characters`);
    if (spec?.advanced !== undefined && typeof spec.advanced !== "boolean") fail(id, `option "${key}": "advanced" must be true or false`);
  }

  const files = walk(dir).map((p) => relative(dir, p).split(sep).join("/")).sort();
  let total = 0;
  for (const f of files) {
    if (!ALLOWED.has(extname(f).toLowerCase())) fail(id, `file type not allowed: ${f}`);
    total += statSync(join(dir, f)).size;
  }
  if (total > MAX_BYTES) fail(id, `widget is ${(total / 1048576).toFixed(1)} MB; the limit is 5 MB`);

  entries.push({
    id,
    name: m.name,
    description: m.description,
    author: m.author,
    version: m.version,
    category: m.category,
    // Browse greys out an entry made for a newer Umbra (WidgetManifest.Incompatibility).
    manifestVersion: m.manifestVersion ?? 1,
    ...(m.minUmbraVersion ? { minUmbraVersion: m.minUmbraVersion } : {}),
    ...(typeof m.changelog === "string" && m.changelog.trim() ? { changelog: m.changelog.trim() } : {}),
    preview: m.preview ?? null,
    ...(m.icon ? { icon: m.icon } : {}),
    // Only present for widgets restricted to some OS; Browse shows "Windows only" and offers no install elsewhere.
    ...(Array.isArray(m.platforms) && m.platforms.length ? { platforms: m.platforms } : {}),
    hosts,
    ...(system.length ? { system } : {}),
    ...(media.length ? { media } : {}),
    ...(m.permissions?.notifications ? { notifications: true } : {}),
    ...(m.permissions?.camera ? { camera: true } : {}),
    ...(m.permissions?.saveImages ? { saveImages: true } : {}),
    ...(m.permissions?.userScripts ? { userScripts: true } : {}),
    // Only present for widgets that can start programs; Browse flags them so users see it before installing.
    ...(commands.length ? { commands } : {}),
    files,
  });
}

// ---------- themes: themes/<id>/theme.json (+ optional preview image) -> themes/index.json ----------
// Themes are data only. These rules mirror Theme.cs in the app and schemas/theme.schema.json.
const themesDir = join(root, "themes");
const themesIndexPath = join(themesDir, "index.json");
const THEME_KEYS = new Set(["$schema", "id", "name", "description", "author", "version", "preview", "tokens"]);
const THEME_FILES = new Set([".json", ".png", ".jpg", ".jpeg", ".webp"]);
const COLOR_TOKENS = ["--bg", "--surface-1", "--surface-2", "--surface-3", "--line", "--line-strong", "--text", "--text-2", "--text-3", "--accent", "--accent-strong", "--accent-soft", "--up", "--down", "--flat"];
const LENGTH_TOKENS = ["--radius-s", "--radius-m", "--radius-l"];
const COLOR = /^#(?:[0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$|^(?:rgb|rgba|hsl|hsla)\( *[0-9.]+(?:%|deg)? *[, ] *[0-9.]+%? *[, ] *[0-9.]+%? *(?:[,/] *[0-9.]+%? *)?\)$/;
const LENGTH = /^(?:[0-9]|[1-9][0-9])(?:\.[0-9]{1,3})?(?:px|rem)$/;
const MIN_CONTRAST = 3;
// Channels 0-255 of #hex, rgb() or hsl() (alpha ignored); mirrors Theme.TryRgb in the app.
function rgb(v) {
  if (v.startsWith("#")) {
    let h = v.slice(1);
    if (h.length <= 4) h = [...h.slice(0, 3)].map((c) => c + c).join("");
    return [0, 2, 4].map((i) => parseInt(h.slice(i, i + 2), 16));
  }
  const p = v.slice(v.indexOf("(") + 1, -1).split(/[, /]+/).filter(Boolean);
  const n = (s, u) => parseFloat(s.endsWith(u) && u ? s.slice(0, -u.length) : s);
  if (v.startsWith("rgb")) return p.slice(0, 3).map((s) => Math.min(255, Math.max(0, s.endsWith("%") ? n(s, "%") * 2.55 : n(s, ""))));
  const hue = ((n(p[0], "deg") % 360) + 360) % 360, s = Math.min(100, Math.max(0, n(p[1], "%"))) / 100, l = Math.min(100, Math.max(0, n(p[2], "%"))) / 100;
  const f = (k0) => { const k = (k0 + hue / 30) % 12; return 255 * (l - s * Math.min(l, 1 - l) * Math.max(-1, Math.min(k - 3, 9 - k, 1))); };
  return [f(0), f(8), f(4)];
}
const lum = (c) => { const [r, g, b] = c.map((x) => { x /= 255; return x <= 0.03928 ? x / 12.92 : ((x + 0.055) / 1.055) ** 2.4; }); return 0.2126 * r + 0.7152 * g + 0.0722 * b; };
const contrast = (a, b) => { const [x, y] = [lum(a), lum(b)]; return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05); };
const MAX_THEME_BYTES = 1024 * 1024;
const failTheme = (id, msg) => errors.push(`themes/${id}: ${msg}`);

const themeEntries = [];
for (const id of existsSync(themesDir) ? readdirSync(themesDir).sort() : []) {
  const dir = join(themesDir, id);
  if (!statSync(dir).isDirectory()) continue;
  const file = join(dir, "theme.json");
  if (!existsSync(file)) { failTheme(id, "missing theme.json"); continue; }
  let t;
  try { t = JSON.parse(readFileSync(file, "utf8")); }
  catch (e) { failTheme(id, `theme.json is not valid JSON (${e.message})`); continue; }

  for (const k of Object.keys(t)) if (!THEME_KEYS.has(k)) failTheme(id, `unknown key "${k}" in theme.json`);
  if (!ID.test(id) || id === "default") failTheme(id, 'folder name must be lowercase letters, digits and dashes (2-40 chars) and not "default"');
  if (t.id !== id) failTheme(id, `"id" must equal the folder name ("${id}")`);
  for (const k of ["name", "description", "author", "version"])
    if (typeof t[k] !== "string" || !t[k].trim() || t[k].length > 300) failTheme(id, `"${k}" is required (max 300 chars)`);
  if (t.version && !SEMVER.test(t.version)) failTheme(id, `"version" must look like 1.2.3`);
  if (t.preview !== undefined && (typeof t.preview !== "string" || t.preview.includes("/") || t.preview.includes("\\") || t.preview.includes(":") || !THEME_FILES.has(extname(t.preview).toLowerCase()) || extname(t.preview).toLowerCase() === ".json" || !existsSync(join(dir, t.preview))))
    failTheme(id, `"preview" must be an existing .png, .jpg or .webp file next to theme.json`);

  const tokens = t.tokens;
  if (!tokens || typeof tokens !== "object" || Array.isArray(tokens) || !Object.keys(tokens).length) failTheme(id, `"tokens" must be a non-empty object`);
  else for (const [name, value] of Object.entries(tokens)) {
    if (COLOR_TOKENS.includes(name)) { if (typeof value !== "string" || value.length > 64 || !COLOR.test(value)) failTheme(id, `${name}: "${value}" is not a plain color (#rrggbb, rgb(), hsl())`); }
    else if (LENGTH_TOKENS.includes(name)) { if (typeof value !== "string" || !LENGTH.test(value)) failTheme(id, `${name}: "${value}" is not a length like 12px (0-99px or rem)`); }
    else failTheme(id, `token "${name}" is not allowed. Allowed: ${[...COLOR_TOKENS, ...LENGTH_TOKENS].join(", ")}`);
  }
  // Guard rails (same as Theme.Parse): readable text and distinct up/down; unset tokens count as the defaults.
  if (tokens && typeof tokens === "object" && !Array.isArray(tokens) && !errors.some((e) => e.startsWith(`themes/${id}:`))) {
    const eff = (k, d) => rgb(tokens[k] ?? d);
    if (contrast(eff("--text", "#f4f5f7"), eff("--bg", "#000000")) < MIN_CONTRAST) failTheme(id, `--text is too close to --bg (contrast under ${MIN_CONTRAST}:1)`);
    if (eff("--up", "#2fd58b").join() === eff("--down", "#ff5a5f").join()) failTheme(id, "--up and --down must be different colors");
  }

  const files = walk(dir).map((p) => relative(dir, p).split(sep).join("/")).sort();
  let total = 0;
  for (const f of files) {
    if (f.includes("/") || !THEME_FILES.has(extname(f).toLowerCase())) failTheme(id, `file not allowed: ${f} (only theme.json and a preview image, no subfolders)`);
    total += statSync(join(dir, f)).size;
  }
  if (total > MAX_THEME_BYTES) failTheme(id, `theme is ${(total / 1024).toFixed(0)} KB; the limit is 1 MB`);

  themeEntries.push({ id, name: t.name, description: t.description, author: t.author, version: t.version, preview: t.preview ?? null, tokens: tokens ?? {}, files });
}

if (errors.length) {
  console.error(`Catalog validation failed:\n  - ${errors.join("\n  - ")}`);
  process.exit(1);
}

const json = JSON.stringify({ schema: 1, widgets: entries }, null, 2) + "\n";
const themesJson = JSON.stringify({ schema: 1, themes: themeEntries }, null, 2) + "\n";
const norm = (p) => (existsSync(p) ? readFileSync(p, "utf8").replace(/\r\n/g, "\n") : "");
if (check) {
  if (norm(indexPath) !== json) {
    console.error("widgets/index.json is out of date. Run: node scripts/catalog.mjs");
    process.exit(1);
  }
  if (themeEntries.length && norm(themesIndexPath) !== themesJson) {
    console.error("themes/index.json is out of date. Run: node scripts/catalog.mjs");
    process.exit(1);
  }
  console.log(`OK: ${entries.length} widget(s) and ${themeEntries.length} theme(s) valid, indexes up to date`);
} else {
  writeFileSync(indexPath, json);
  if (themeEntries.length || existsSync(themesIndexPath)) writeFileSync(themesIndexPath, themesJson);
  console.log(`Wrote widgets/index.json with ${entries.length} widget(s) and themes/index.json with ${themeEntries.length} theme(s)`);
}
