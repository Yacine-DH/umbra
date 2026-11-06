// Script Runner: runs the user's own scripts (added in Settings) via umbra.run. Data in umbra.store ("results", "ui").
// Scripts that keep running (a tunnel, a server) are started and stopped here and owned by the host; this page only shows them.
const $ = (id) => document.getElementById(id);
const listEl = $("list"), logEl = $("log"), bigEl = $("big"), histEl = $("history"), copyBtn = $("copy");

const MAX_KEEP = 6000;   // characters of each stream saved for the newest run
const HISTORY = 10;      // runs remembered per script
let commands = [];
let results = {};            // id -> [newest run, ...older]; a run is { at, exitCode, ms, stdout, stderr, kind, note, truncated }
let selected = null;
const running = new Map();   // id -> start time of a run (that finishes) started from this page
const services = new Map();  // id -> { running, since, exitCode, restarting, restarts, text } for scripts that keep running
let live = null;             // { id, text } live output of the selected script while it runs
let loadError = "";
let first = true;
let autoTimer = null;

const opts = () => umbra.options;
const canStop = typeof umbra.stop === "function";
const canWatch = typeof umbra.commandOutput === "function";
const esc = (s) => String(s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);

function fmtMs(ms) {
  if (ms < 1000) return `${Math.round(ms)} ms`;
  if (ms < 60000) return `${(ms / 1000).toFixed(1)} s`;
  if (ms < 3600000) return `${Math.floor(ms / 60000)} m ${Math.round((ms % 60000) / 1000)} s`;
  return `${Math.floor(ms / 3600000)} h ${Math.floor((ms % 3600000) / 60000)} m`;
}
function ago(ts) {
  const s = Math.max(0, (Date.now() - ts) / 1000);
  if (s < 10) return "just now";
  if (s < 60) return `${Math.floor(s)} s ago`;
  if (s < 3600) return `${Math.floor(s / 60)} min ago`;
  if (s < 86400) return `${Math.floor(s / 3600)} h ago`;
  return `${Math.floor(s / 86400)} d ago`;
}
const clock = (ts) => new Date(ts).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });

/** Keep the first n lines (or the last n for live output); report how many were cut. */
function trimLines(text, n, fromEnd = false) {
  const lines = String(text || "").replace(/\r\n/g, "\n").replace(/\n+$/, "").split("\n");
  if (lines.length === 1 && lines[0] === "") return { text: "", cut: 0 };
  if (lines.length <= n) return { text: lines.join("\n"), cut: 0 };
  return { text: (fromEnd ? lines.slice(-n) : lines.slice(0, n)).join("\n"), cut: lines.length - n };
}
const lastLine = (s) => (String(s || "").split(/\r?\n/).reverse().find((l) => l.trim()) || "").trim();

const nameOf = (c) => c.title || c.id;
const keeps = (c) => !!(c && c.keepsRunning);
const lastRun = (id) => (results[id] || [])[0];
/** The script was ended by a Stop (here, in Settings or the tray), not on its own: the host's last note says "stopped". */
const stoppedByUser = (s) => /^\[Umbra\] stopped/m.test((String(s?.text || "").trim().split("\n").reverse().find((l) => l.startsWith("[Umbra] ")) || ""));

// ---------- data ----------

/** The user's scripts (and any commands in widget.json). Umbra before umbra.commands() only had widget.json. */
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
    commands.sort((a, b) => Number(!!b.user) - Number(!!a.user));
    loadError = "";
  } catch (err) {
    commands = [];
    loadError = `Could not load the scripts (${err.message || err})`;
  }
  await loadServices();
}

/** State of the scripts that keep running (since when, last exit), from the host. */
async function loadServices() {
  const before = new Map(services);
  services.clear();
  if (!canWatch) return;
  await Promise.all(commands.filter(keeps).map(async (c) => {
    try { services.set(c.id, await umbra.commandOutput(c.id)); } catch { /* keep going without it */ }
  }));
  for (const c of commands.filter(keeps)) noticeServiceChange(c, before.get(c.id), services.get(c.id));
}

/** Results were one object per script before 1.2.0; now the newest few runs. */
function migrate(r) {
  const out = {};
  if (!r || typeof r !== "object" || Array.isArray(r)) return out;
  for (const [id, v] of Object.entries(r)) {
    const runs = Array.isArray(v) ? v : v && typeof v === "object" ? [v] : [];
    out[id] = runs.filter((x) => x && typeof x === "object" && typeof x.at === "number").slice(0, HISTORY);
  }
  return out;
}

async function loadState() {
  results = migrate(await umbra.store.get("results", {}));
  const ui = await umbra.store.get("ui", {});
  selected = ui && typeof ui.selected === "string" ? ui.selected : null;
}
const saveResults = () => umbra.store.set("results", results).catch((e) => console.error("Script Runner: could not save", e));
const saveUi = () => umbra.store.set("ui", { selected }).catch(() => {});

function remember(id, entry) {
  // Only the newest run keeps its output; older ones keep the facts, so the store stays small.
  const older = (results[id] || []).slice(0, HISTORY - 1).map(({ stdout, stderr, ...rest }) => rest);
  results[id] = [entry, ...older];
  saveResults();
}

// ---------- notifications ----------

/** A toast when a script fails (or finishes, by the option). Needs "Allow notifications" for this widget in Settings. */
function notify(c, text, failed) {
  const mode = String(opts().notify || "fail");
  if (mode === "never" || (mode === "fail" && !failed) || typeof umbra.notify !== "function") return;
  umbra.notify({ title: nameOf(c), body: text, tag: `script-${c.id}` }).catch(() => { /* switched off, or rate limited */ });
}

/** A script that keeps running went down on its own (not a Stop): say so. */
function noticeServiceChange(c, before, now) {
  if (!before || !now || !before.running || now.running || stoppedByUser(now)) return;
  const exit = now.exitCode != null ? ` (exit ${now.exitCode})` : "";
  notify(c, now.restarting ? `Stopped${exit}; Umbra restarts it shortly.` : `Stopped${exit}.`, true);
}

// ---------- running ----------

/** Turn a rejection from umbra.run into something to show. */
function describeError(err) {
  const msg = String((err && err.message) || err || "Unknown error");
  if (/approv/i.test(msg)) return { kind: "warn", note: "Not approved yet. Click Run and approve the prompt, or approve it in Settings > My widgets." };
  return { kind: "error", note: msg };
}

async function runScript(id) {
  const c = commands.find((x) => x.id === id);
  if (!c) return;
  if (keeps(c)) return startService(c);
  if (running.has(id)) return;
  running.set(id, Date.now());
  selected = id; saveUi();
  live = null;
  render();
  poll();
  const t0 = Date.now();
  let entry;
  try {
    const r = await umbra.run(id);
    let kind = r.exitCode === 0 ? "ok" : "fail";
    let note = "";
    if (r.stopped) { kind = "stopped"; note = "Stopped."; }
    else if (r.timedOut) { kind = "timeout"; note = "Timed out and was stopped. Raise its time limit in Settings > My widgets > Script Runner (max 120 s), or make it a script that keeps running."; }
    else if (r.truncated) { note = "Output was larger than 1 MB and was cut."; }
    entry = { at: Date.now(), exitCode: r.exitCode, ms: r.durationMs ?? Date.now() - t0, stdout: String(r.stdout || "").slice(0, MAX_KEEP), stderr: String(r.stderr || "").slice(0, MAX_KEEP), kind, note, truncated: !!r.truncated };
  } catch (err) {
    const d = describeError(err);
    entry = { at: Date.now(), exitCode: null, ms: Date.now() - t0, stdout: "", stderr: "", kind: d.kind, note: d.note };
  }
  running.delete(id);
  if (live && live.id === id) live = null;
  remember(id, entry);
  if (entry.kind === "ok") notify(c, `Finished in ${fmtMs(entry.ms)}.${lastLine(entry.stdout) ? ` ${lastLine(entry.stdout)}` : ""}`.slice(0, 200), false);
  else if (entry.kind !== "stopped") notify(c, entry.exitCode != null ? `Failed with exit code ${entry.exitCode}.` : entry.note.slice(0, 200), true);
  render();
}

async function startService(c) {
  selected = c.id; saveUi();
  try {
    await umbra.run(c.id);
  } catch (err) {
    const d = describeError(err);
    remember(c.id, { at: Date.now(), exitCode: null, ms: 0, stdout: "", stderr: "", kind: d.kind, note: d.note });
  }
  await loadServices(); // the host also sends commandsChanged; this makes the click feel immediate
  render();
}

async function stopScript(id) {
  if (!canStop) return;
  try { await umbra.stop(id); } catch { /* it ended meanwhile */ }
}

// ---------- live output ----------

/** While the selected script runs, read what it printed so far (once a second, only while the widget is visible). */
async function poll() {
  const c = commands.find((x) => x.id === selected);
  if (!c || !canWatch || document.hidden) return;
  if (!(running.has(c.id) || (keeps(c) && services.get(c.id)?.running))) return;
  try {
    const o = await umbra.commandOutput(c.id);
    if (keeps(c)) services.set(c.id, o);
    live = { id: c.id, text: o.text || "" };
    renderOutput();
  } catch { /* try again next second */ }
}

// ---------- automatic runs ----------

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
  const c = commands.find((x) => x.id === id);
  if (!c || keeps(c)) return; // a script that keeps running has its own "Start automatically" switch in Settings
  const mins = Math.max(1, Number(opts().everyMinutes) || 15);
  autoTimer = setInterval(() => { if (canAuto(id)) runScript(id); }, mins * 60000);
}

/**
 * Background runs can't open the approval prompt. Your own scripts are approved when you save them in Settings, so they
 * run unless the last try needed approval; a widget.json command only repeats after it ran with the button.
 */
function canAuto(id) {
  const last = lastRun(id), c = commands.find((x) => x.id === id);
  if (c && keeps(c) && services.get(id)?.running) return false;
  if (c && c.user) return !last || last.kind !== "warn";
  return !!last && last.kind !== "warn" && last.kind !== "error";
}

// ---------- rendering ----------

function subtitle(c) {
  if (keeps(c)) {
    const s = services.get(c.id), r = lastRun(c.id);
    if (s && s.running) return { text: `Running · ${fmtMs(Date.now() - s.since)}${s.restarts ? ` · restarted ${s.restarts}×` : ""}`, cls: "live" };
    if (s && s.restarting) return { text: `Stopped${s.exitCode != null ? ` (exit ${s.exitCode})` : ""} · restarting`, cls: "warn" };
    if (r && r.kind === "warn" && Date.now() - r.at < 60000) return { text: "Needs your approval", cls: "warn" };
    if (r && r.kind === "error" && Date.now() - r.at < 60000) return { text: "Could not start", cls: "bad" };
    if (s && s.exitCode != null) return stoppedByUser(s) ? { text: "Stopped", cls: "" } : { text: `Stopped · exit ${s.exitCode}`, cls: s.exitCode === 0 ? "" : "bad" };
    return { text: c.description || "Keeps running · not started", cls: "" };
  }
  if (running.has(c.id)) return { text: `Running ${fmtMs(Date.now() - running.get(c.id))}`, cls: "" };
  const r = lastRun(c.id);
  if (!r) return { text: c.description || "Never run", cls: "" };
  const when = ago(r.at);
  switch (r.kind) {
    case "ok": return { text: `Exit 0 · ${fmtMs(r.ms)} · ${when}`, cls: "" };
    case "fail": return { text: `Exit ${r.exitCode} · ${fmtMs(r.ms)} · ${when}`, cls: "bad" };
    case "timeout": return { text: `Timed out after ${fmtMs(r.ms)} · ${when}`, cls: "bad" };
    case "stopped": return { text: `Stopped after ${fmtMs(r.ms)} · ${when}`, cls: "" };
    case "warn": return { text: "Needs your approval", cls: "warn" };
    default: return { text: `Could not run · ${when}`, cls: "bad" };
  }
}

function dotClass(c) {
  if (keeps(c)) {
    const s = services.get(c.id);
    if (!s) return "";
    return s.running ? "live" : s.restarting ? "warn" : s.exitCode != null && s.exitCode !== 0 && !stoppedByUser(s) ? "fail" : "";
  }
  if (running.has(c.id)) return "run";
  const r = lastRun(c.id);
  if (!r) return "";
  return r.kind === "ok" && r.truncated ? "warn" : r.kind === "stopped" ? "" : r.kind;
}

/** The button: Run, or Stop while it runs; Start/Stop for a script that keeps running. */
function button(c) {
  if (keeps(c)) {
    const s = services.get(c.id), on = !!(s && (s.running || s.restarting));
    return on ? { label: "Stop", act: "stop", cls: "stop", disabled: !canStop } : { label: "Start", act: "run", cls: "", disabled: false };
  }
  if (running.has(c.id)) return canStop ? { label: "Stop", act: "stop", cls: "stop", disabled: false } : { label: "Running", act: "", cls: "", disabled: true };
  return { label: "Run", act: "run", cls: "", disabled: false };
}

function renderList() {
  if (loadError) { listEl.innerHTML = `<div class="empty">${esc(loadError)}</div>`; return; }
  if (!commands.length) {
    listEl.innerHTML = `<div class="empty">No scripts yet. Add one in Settings: My widgets, Script Runner, Add script. There are examples to start from.</div>`;
    return;
  }
  listEl.innerHTML = commands.map((c) => {
    const s = subtitle(c), b = button(c);
    return `<div class="row${c.id === selected ? " sel" : ""}" data-id="${esc(c.id)}">
      <span class="dot ${dotClass(c)}"></span>
      <div class="info"><div class="name">${esc(nameOf(c))}${keeps(c) ? '<span class="tag">keeps running</span>' : ""}</div><div class="sub num ${s.cls}">${esc(s.text)}</div></div>
      <button class="go ${b.cls}" data-act="${b.act}" data-cmd="${esc(c.id)}" ${b.disabled ? "disabled" : ""}>${b.label}</button>
    </div>`;
  }).join("");
}

function outMeta() {
  const c = commands.find((x) => x.id === selected);
  if (!c) return "";
  if (keeps(c)) {
    const s = services.get(c.id);
    return s && s.running ? `since ${clock(s.since)}` : "";
  }
  if (running.has(c.id)) return `running ${fmtMs(Date.now() - running.get(c.id))}`;
  const r = lastRun(c.id);
  if (!r) return "";
  return r.exitCode == null ? ago(r.at) : `exit ${r.exitCode} · ${fmtMs(r.ms)} · ${clock(r.at)}`;
}

/** What the Copy button copies: the output shown, in full (not cut to the line limit). */
let copyText = "";

function renderOutput() {
  const c = commands.find((x) => x.id === selected);
  bigEl.hidden = true;
  $("out-meta").textContent = outMeta();
  copyText = "";
  if (!c) {
    $("out-name").textContent = "Output";
    logEl.innerHTML = commands.length ? '<span class="dim">Press Run on a script to see its output here.</span>' : "";
    renderHistory(null);
    copyBtn.hidden = true;
    return;
  }
  $("out-name").textContent = nameOf(c);
  const n = Math.max(5, Math.min(200, Number(opts().outputLines) || 40));
  const atBottom = logEl.scrollHeight - logEl.scrollTop - logEl.clientHeight < 24;

  // Live: a run in progress, or a script that keeps running (its newest lines, kept scrolled to the end).
  const svc = keeps(c) ? services.get(c.id) : null;
  const liveText = live && live.id === c.id ? live.text : svc ? svc.text || "" : null;
  if (running.has(c.id) || (svc && liveText)) {
    const t = trimLines(liveText || "", n, true);
    copyText = liveText || "";
    logEl.innerHTML = (t.cut ? `<span class="dim">... ${t.cut} earlier line${t.cut > 1 ? "s" : ""}</span>\n` : "")
      + (t.text ? esc(t.text).replace(/^\[Umbra\] .*$/gm, (l) => `<span class="dim">${l}</span>`) : '<span class="dim">Waiting for output...</span>');
    if (atBottom || first) logEl.scrollTop = logEl.scrollHeight;
    copyBtn.hidden = !copyText;
    renderHistory(keeps(c) ? null : c);
    return;
  }

  const r = lastRun(c.id);
  if (!r || keeps(c)) {
    const msg = r && r.note ? `<span class="note">${esc(r.note)}</span>`
      : `<span class="dim">${esc(c.description || (keeps(c) ? "Press Start. It keeps running until you stop it or turn the widget off." : "Never run."))}</span>`;
    logEl.innerHTML = msg;
    renderHistory(null);
    copyBtn.hidden = true;
    return;
  }
  const out = trimLines(r.stdout, n), err = trimLines(r.stderr, Math.max(3, Math.floor(n / 4)));
  let html = "";
  if (r.note) html += `<span class="note">${esc(r.note)}</span>\n`;
  if (out.text) html += esc(out.text) + (out.cut ? `\n<span class="dim">... ${out.cut} more line${out.cut > 1 ? "s" : ""}</span>` : "") + "\n";
  if (err.text) html += `<span class="err">${esc(err.text)}</span>` + (err.cut ? `\n<span class="dim">... ${err.cut} more error line${err.cut > 1 ? "s" : ""}</span>` : "") + "\n";
  if (!out.text && !err.text && !r.note) html = '<span class="dim">(no output)</span>';
  logEl.innerHTML = html;
  copyText = [r.stdout, r.stderr].filter((s) => s && s.trim()).join("\n");
  copyBtn.hidden = !copyText;
  if (opts().bigLine && r.kind === "ok" && lastLine(r.stdout)) { bigEl.textContent = lastLine(r.stdout); bigEl.hidden = false; }
  renderHistory(c);
}

/** Earlier runs of a script that finishes: when, how it ended, how long. */
function renderHistory(c) {
  const runs = c ? (results[c.id] || []).slice(1) : [];
  histEl.hidden = !runs.length;
  if (!runs.length) { histEl.innerHTML = ""; return; }
  histEl.innerHTML = `<div class="hist-title">Earlier runs</div>` + runs.map((r) => {
    const label = r.kind === "ok" ? "exit 0" : r.kind === "fail" ? `exit ${r.exitCode}` : r.kind === "timeout" ? "timed out"
      : r.kind === "stopped" ? "stopped" : r.kind === "warn" ? "needs approval" : "could not run";
    const dot = r.kind === "ok" ? "ok" : r.kind === "stopped" ? "" : r.kind === "warn" ? "warn" : "fail";
    return `<div class="hist num"><span class="dot ${dot}"></span><span class="hist-when">${esc(ago(r.at))}</span><span class="hist-what">${esc(label)}</span><span class="hist-ms">${esc(fmtMs(r.ms || 0))}</span></div>`;
  }).join("");
}

function render() {
  $("title").textContent = String(opts().title || "Scripts");
  $("count").textContent = commands.length ? `${commands.length} script${commands.length > 1 ? "s" : ""}` : "";
  renderList();
  renderOutput();
  if (first) { first = false; requestAnimationFrame(() => umbra.rendered()); }
}

/** Once a second: update only the text of the time labels (so a click in progress is never interrupted), and live output. */
function tick() {
  if (document.hidden) return;
  for (const row of listEl.querySelectorAll(".row")) {
    const c = commands.find((x) => x.id === row.dataset.id);
    if (c) row.querySelector(".sub").textContent = subtitle(c).text;
  }
  $("out-meta").textContent = outMeta();
  poll();
}

listEl.addEventListener("click", (e) => {
  const btn = e.target.closest("[data-act]");
  if (btn) {
    if (btn.dataset.act === "run") runScript(btn.dataset.cmd);
    else if (btn.dataset.act === "stop") stopScript(btn.dataset.cmd);
    return;
  }
  const row = e.target.closest(".row");
  if (row) { selected = row.dataset.id; live = null; saveUi(); render(); poll(); }
});

copyBtn.addEventListener("click", async () => {
  if (!copyText) return;
  let ok = false;
  try { await navigator.clipboard.writeText(copyText); ok = true; } catch {
    // Without the async clipboard: copy through a hidden text box.
    const t = Object.assign(document.createElement("textarea"), { value: copyText });
    t.style.cssText = "position:fixed;opacity:0;pointer-events:none";
    document.body.append(t);
    t.select();
    try { ok = document.execCommand("copy"); } catch { ok = false; }
    t.remove();
  }
  copyBtn.textContent = ok ? "Copied" : "Couldn't copy";
  setTimeout(() => { copyBtn.textContent = "Copy"; }, 1400);
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
  // A script added, edited or deleted in Settings, or one that keeps running started or stopped, shows up at once.
  if (umbra.onCommandsChange) umbra.onCommandsChange(async () => {
    await loadCommands();
    if (!commands.some((c) => c.id === selected)) { selected = commands.length ? commands[0].id : null; saveUi(); }
    if (live && !running.has(live.id) && !services.get(live.id)?.running) live = null;
    render(); setupAuto();
  });
  umbra.store.onChange(async () => { await loadState(); render(); });
})();
