// Script Runner: runs the commands declared in widget.json via umbra.run. Data in umbra.store ("results", "ui").
const $ = (id) => document.getElementById(id);
const listEl = $("list"), logEl = $("log"), bigEl = $("big");

const MAX_KEEP = 6000; // characters of each stream saved in the store
let commands = [];
let results = {};            // id -> { at, exitCode, ms, stdout, stderr, kind, note }
let selected = null;
const running = new Map();   // id -> start time
let loadError = "";
let first = true;
let autoTimer = null;

const opts = () => umbra.options;
const esc = (s) => String(s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);

function fmtMs(ms) { return ms < 1000 ? `${Math.round(ms)} ms` : ms < 60000 ? `${(ms / 1000).toFixed(1)} s` : `${Math.floor(ms / 60000)} m ${Math.round((ms % 60000) / 1000)} s`; }
function ago(ts) {
  const s = Math.max(0, (Date.now() - ts) / 1000);
  if (s < 10) return "just now";
  if (s < 60) return `${Math.floor(s)} s ago`;
  if (s < 3600) return `${Math.floor(s / 60)} min ago`;
  if (s < 86400) return `${Math.floor(s / 3600)} h ago`;
  return `${Math.floor(s / 86400)} d ago`;
}

/** Keep the first n lines; report how many were cut. */
function trimLines(text, n) {
  const lines = String(text || "").replace(/\r\n/g, "\n").replace(/\n+$/, "").split("\n");
  if (lines.length === 1 && lines[0] === "") return { text: "", cut: 0 };
  return lines.length > n ? { text: lines.slice(0, n).join("\n"), cut: lines.length - n } : { text: lines.join("\n"), cut: 0 };
}
const lastLine = (s) => (String(s || "").split(/\r?\n/).reverse().find((l) => l.trim()) || "").trim();

// ---------- data ----------

/** The scripts added in Settings plus the examples in widget.json. Umbra before umbra.commands() only had widget.json. */
async function loadCommands() {
  try {
    if (typeof umbra.commands === "function") {
      commands = (await umbra.commands()).filter((c) => c && c.id);
    } else {
      const res = await fetch("widget.json", { cache: "no-store" });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const m = await res.json();
      commands = ((m.permissions || {}).commands || []).filter((c) => c && c.id);
    }
    // Your own scripts first: they are why you added the widget; the shipped examples follow.
    commands.sort((a, b) => Number(!!b.user) - Number(!!a.user));
    loadError = "";
  } catch (err) {
    commands = [];
    loadError = `Could not load the scripts (${err.message || err})`;
  }
}

async function loadState() {
  const r = await umbra.store.get("results", {});
  results = r && typeof r === "object" && !Array.isArray(r) ? r : {};
  const ui = await umbra.store.get("ui", {});
  selected = ui && typeof ui.selected === "string" ? ui.selected : null;
}
const saveResults = () => umbra.store.set("results", results).catch((e) => console.error("Script Runner: could not save", e));
const saveUi = () => umbra.store.set("ui", { selected }).catch(() => {});

// ---------- running ----------

/** Turn a rejection from umbra.run into something to show. */
function describeError(err) {
  const msg = String((err && err.message) || err || "Unknown error");
  if (/approv/i.test(msg)) return { kind: "warn", note: "Not approved yet. Click Run and approve the prompt, or approve it in Settings > My widgets." };
  return { kind: "error", note: msg };
}

async function runScript(id) {
  if (running.has(id)) return;
  running.set(id, Date.now());
  selected = id; saveUi();
  render();
  const t0 = Date.now();
  let entry;
  try {
    const r = await umbra.run(id);
    let kind = r.exitCode === 0 ? "ok" : "fail";
    let note = "";
    if (r.timedOut) { kind = "timeout"; note = "Timed out and was stopped. Raise its time limit in Settings > My widgets > Script Runner (max 120 s) if it needs longer."; }
    else if (r.truncated) { note = "Output was larger than 1 MB and was cut."; }
    entry = { at: Date.now(), exitCode: r.exitCode, ms: r.durationMs ?? Date.now() - t0, stdout: String(r.stdout || "").slice(0, MAX_KEEP), stderr: String(r.stderr || "").slice(0, MAX_KEEP), kind, note, truncated: !!r.truncated };
  } catch (err) {
    const d = describeError(err);
    entry = { at: Date.now(), exitCode: null, ms: Date.now() - t0, stdout: "", stderr: "", kind: d.kind, note: d.note };
  }
  running.delete(id);
  results[id] = entry;
  saveResults();
  render();
}

const nameOf = (c) => c.title || c.id;

/** The option names a script by its name or its id. */
function autoId() {
  const want = String(opts().autoScript || "").trim().toLowerCase();
  if (!want) return "";
  const c = commands.find((x) => x.id === want) || commands.find((x) => nameOf(x).toLowerCase() === want);
  return c ? c.id : "";
}

function setupAuto() {
  clearInterval(autoTimer); autoTimer = null;
  const id = autoId();
  if (!id) return;
  const mins = Math.max(1, Number(opts().everyMinutes) || 15);
  autoTimer = setInterval(() => { if (canAuto(id)) runScript(id); }, mins * 60000);
}

/**
 * Background runs can't open the approval prompt. Your own scripts are approved when you save them in Settings, so they
 * run unless the last try needed approval; an example only repeats after it ran with the button.
 */
function canAuto(id) {
  const last = results[id], c = commands.find((x) => x.id === id);
  if (c && c.user) return !last || last.kind !== "warn";
  return !!last && last.kind !== "warn" && last.kind !== "error";
}

// ---------- rendering ----------

function subtitle(c) {
  if (running.has(c.id)) return { text: `Running ${fmtMs(Date.now() - running.get(c.id))}`, cls: "" };
  const r = results[c.id];
  if (!r) return { text: c.description || "Never run", cls: "" };
  const when = ago(r.at);
  switch (r.kind) {
    case "ok": return { text: `Exit 0 · ${fmtMs(r.ms)} · ${when}`, cls: "" };
    case "fail": return { text: `Exit ${r.exitCode} · ${fmtMs(r.ms)} · ${when}`, cls: "bad" };
    case "timeout": return { text: `Timed out after ${fmtMs(r.ms)} · ${when}`, cls: "bad" };
    case "warn": return { text: "Needs your approval", cls: "warn" };
    default: return { text: `Could not run · ${when}`, cls: "bad" };
  }
}

function dotClass(c) {
  if (running.has(c.id)) return "run";
  const r = results[c.id];
  if (!r) return "";
  return r.kind === "ok" && r.truncated ? "warn" : r.kind;
}

function renderList() {
  if (loadError) { listEl.innerHTML = `<div class="empty">${esc(loadError)}</div>`; return; }
  if (!commands.length) {
    listEl.innerHTML = `<div class="empty">No scripts yet. Add one in Settings: My widgets, Script Runner, Add script.</div>`;
    return;
  }
  listEl.innerHTML = commands.map((c) => {
    const s = subtitle(c), busy = running.has(c.id);
    return `<div class="row${c.id === selected ? " sel" : ""}" data-id="${esc(c.id)}">
      <span class="dot ${dotClass(c)}"></span>
      <div class="info"><div class="name">${esc(nameOf(c))}</div><div class="sub num ${s.cls}">${esc(s.text)}</div></div>
      <button class="run" data-run="${esc(c.id)}" ${busy ? "disabled" : ""}>${busy ? "Running" : "Run"}</button>
    </div>`;
  }).join("");
}

function outMeta() {
  const c = commands.find((x) => x.id === selected);
  if (!c) return "";
  if (running.has(c.id)) return `running ${fmtMs(Date.now() - running.get(c.id))}`;
  const r = results[c.id];
  if (!r) return "";
  return r.exitCode == null ? ago(r.at) : `exit ${r.exitCode} · ${fmtMs(r.ms)} · ${new Date(r.at).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}`;
}

function renderOutput() {
  const c = commands.find((x) => x.id === selected);
  bigEl.hidden = true;
  $("out-meta").textContent = outMeta();
  if (!c) {
    $("out-name").textContent = "Output";
    logEl.innerHTML = commands.length ? '<span class="dim">Press Run on a script to see its output here.</span>' : "";
    return;
  }
  $("out-name").textContent = nameOf(c);
  if (running.has(c.id)) { logEl.innerHTML = '<span class="dim">Waiting for the script to finish...</span>'; return; }
  const r = results[c.id];
  if (!r) { logEl.innerHTML = `<span class="dim">${esc(c.description || "Never run.")}</span>`; return; }
  const n = Math.max(5, Math.min(200, Number(opts().outputLines) || 40));
  const out = trimLines(r.stdout, n), err = trimLines(r.stderr, Math.max(3, Math.floor(n / 4)));
  let html = "";
  if (r.note) html += `<span class="note">${esc(r.note)}</span>\n`;
  if (out.text) html += esc(out.text) + (out.cut ? `\n<span class="dim">... ${out.cut} more line${out.cut > 1 ? "s" : ""}</span>` : "") + "\n";
  if (err.text) html += `<span class="err">${esc(err.text)}</span>` + (err.cut ? `\n<span class="dim">... ${err.cut} more error line${err.cut > 1 ? "s" : ""}</span>` : "") + "\n";
  if (!out.text && !err.text && !r.note) html = '<span class="dim">(no output)</span>';
  logEl.innerHTML = html;
  if (opts().bigLine && r.kind === "ok" && lastLine(r.stdout)) { bigEl.textContent = lastLine(r.stdout); bigEl.hidden = false; }
}

function render() {
  $("title").textContent = String(opts().title || "Scripts");
  $("count").textContent = commands.length ? `${commands.length} script${commands.length > 1 ? "s" : ""}` : "";
  renderList();
  renderOutput();
  if (first) { first = false; requestAnimationFrame(() => umbra.rendered()); }
}

/** Once a second: update only the text of the time labels, so a click in progress is never interrupted. */
function tick() {
  if (document.hidden) return;
  for (const row of listEl.querySelectorAll(".row")) {
    const c = commands.find((x) => x.id === row.dataset.id);
    if (c) row.querySelector(".sub").textContent = subtitle(c).text;
  }
  $("out-meta").textContent = outMeta();
}

listEl.addEventListener("click", (e) => {
  const btn = e.target.closest("[data-run]");
  if (btn) { runScript(btn.dataset.run); return; }
  const row = e.target.closest(".row");
  if (row) { selected = row.dataset.id; saveUi(); render(); }
});

setInterval(tick, 1000);

(async () => {
  await umbra.ready;
  await Promise.all([loadCommands(), loadState()]);
  if (!selected || !commands.some((c) => c.id === selected)) selected = commands.length ? commands[0].id : null;
  render();
  setupAuto();
  const id = autoId();
  if (opts().runOnStart && id && canAuto(id)) runScript(id);
  umbra.onSettings(() => { render(); setupAuto(); });
  // A script added, edited or deleted in Settings shows up at once.
  if (umbra.onCommandsChange) umbra.onCommandsChange(async () => {
    await loadCommands();
    if (!commands.some((c) => c.id === selected)) { selected = commands.length ? commands[0].id : null; saveUi(); }
    render(); setupAuto();
  });
  umbra.store.onChange(async () => { await loadState(); render(); });
})();
