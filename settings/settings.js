// Umbra Settings panel. All state lives in the host; this page renders it and posts changes back.
const bridge = window.umbraBridge; // shared/bridge.js: WebView2 or WebKitGTK
const post = (msg) => bridge && bridge.post(msg);
const $ = (s, root = document) => root.querySelector(s);
const $$ = (s, root = document) => [...root.querySelectorAll(s)];

// Line icons (24x24, stroke). Rendered into every [data-icon] element.
const ICONS = {
  home: '<path d="M4 11.5 12 4.5l8 7"/><path d="M6.5 9.5V19.5h11V9.5"/><path d="M10 19.5v-5h4v5"/>',
  sliders: '<path d="M4 6h10M18 6h2M4 12h4M12 12h8M4 18h12M20 18h0"/><circle cx="16" cy="6" r="2"/><circle cx="10" cy="12" r="2"/><circle cx="18" cy="18" r="2"/>',
  grid: '<rect x="4" y="4" width="7" height="7" rx="2"/><rect x="13" y="4" width="7" height="7" rx="2"/><rect x="4" y="13" width="7" height="7" rx="2"/><rect x="13" y="13" width="7" height="7" rx="2"/>',
  compass: '<circle cx="12" cy="12" r="8.5"/><path d="m15.5 8.5-2 5-5 2 2-5z"/>',
  info: '<circle cx="12" cy="12" r="8.5"/><path d="M12 11v5M12 8h.01"/>',
  github: '<path d="M9 19c-4 1.3-4-2-6-2.5M15 21v-3.5c0-1 .1-1.4-.5-2 2.8-.3 5.5-1.4 5.5-6a4.6 4.6 0 0 0-1.3-3.2 4.2 4.2 0 0 0-.1-3.2s-1-.3-3.4 1.3a11.6 11.6 0 0 0-6.2 0C6.6 2.8 5.6 3.1 5.6 3.1a4.2 4.2 0 0 0-.1 3.2A4.6 4.6 0 0 0 4.2 9.5c0 4.6 2.7 5.7 5.5 6-.6.6-.6 1.2-.5 2V21"/>',
  mouse: '<rect x="6" y="3" width="12" height="18" rx="6"/><path d="M12 7v4"/>',
  plus: '<path d="M12 5v14M5 12h14"/>',
  refresh: '<path d="M20 11a8 8 0 0 0-14.7-4.3L4 8M4 4v4h4M4 13a8 8 0 0 0 14.7 4.3L20 16M20 20v-4h-4"/>',
  search: '<circle cx="11" cy="11" r="6.5"/><path d="m20 20-3.8-3.8"/>',
  bug: '<rect x="8" y="6" width="8" height="14" rx="4"/><path d="M12 20v-8M4 13h4M16 13h4M5 7l3 2M19 7l-3 2M5 19l3-2M19 19l-3-2M9.5 4.5 11 6M14.5 4.5 13 6"/>',
  target: '<circle cx="12" cy="12" r="8"/><circle cx="12" cy="12" r="3"/>',
  trash: '<path d="M4 7h16M10 11v6M14 11v6M6 7l1 12a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2l1-12M9 7V4h6v3"/>',
  lock: '<rect x="5" y="11" width="14" height="9" rx="2"/><path d="M8 11V8a4 4 0 0 1 8 0v3"/>',
  unlock: '<rect x="5" y="11" width="14" height="9" rx="2"/><path d="M8 11V8a4 4 0 0 1 7.75-1.4"/>',
  check: '<path d="m5 12.5 4.5 4.5L19 7.5"/>',
  alert: '<path d="M12 4 3 19.5h18zM12 10v4.5M12 17.2h.01"/>',
  keyboard: '<rect x="3" y="6.5" width="18" height="11" rx="2.5"/><path d="M7 10.5h.01M11 10.5h.01M15 10.5h.01M17.5 10.5h.01M8 14h8"/>',
  palette: '<path d="M12 4a8 8 0 1 0 0 16c1.2 0 1.8-.8 1.8-1.7 0-1.3-1-1.5-1-2.6 0-.9.7-1.7 1.7-1.7H16a4 4 0 0 0 4-4C20 6.7 16.4 4 12 4z"/><circle cx="8" cy="11" r="1"/><circle cx="11" cy="7.6" r="1"/><circle cx="15.2" cy="8.4" r="1"/>',
  download: '<path d="M12 4v11M7 10l5 5 5-5M5 20h14"/>',
  layers: '<path d="m12 4 8.5 4.5L12 13 3.5 8.5z"/><path d="m3.5 12.5 8.5 4.5 8.5-4.5M3.5 16.5 12 21l8.5-4.5"/>',
  chevron: '<path d="m6 9 6 6 6-6"/>',
  copy: '<rect x="8" y="8" width="11" height="12" rx="2"/><path d="M16 8V6a2 2 0 0 0-2-2H7a2 2 0 0 0-2 2v9a2 2 0 0 0 2 2h1"/>',
};
function icons(root = document) {
  for (const el of $$("[data-icon]", root)) {
    const name = el.dataset.icon;
    if (!ICONS[name] || el.firstChild) continue;
    el.setAttribute("aria-hidden", "true");
    el.innerHTML = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">${ICONS[name]}</svg>`;
  }
}

let state = null;      // { version, startup, repo, settings, widgets[] }
let catalog = null;    // [{ id, name, ..., installed, updateAvailable }]
let catalogError = null;
let themes = null;     // [{ id, name, tokens, installed, builtin, updateAvailable }]
let themesError = null;
const busy = new Map(); // id -> "install" | "remove"
let catalogLoading = false;
let browseFilter = "all"; // all | installed | updates
let updatingAll = null;   // { ids, index, total } while "Update all" runs

// ---------- navigation ----------
function show(page) {
  $$(".nav button").forEach((b) => {
    const on = b.dataset.page === page;
    b.classList.toggle("active", on);
    on ? b.setAttribute("aria-current", "page") : b.removeAttribute("aria-current");
  });
  $$(".page").forEach((p) => p.classList.toggle("active", p.dataset.page === page));
  $("#content").scrollTop = 0;
  if (page === "browse" && !catalog && !catalogLoading) loadCatalog();
  if (page === "themes" && !themes) loadThemes();
  if (page === "privacy") post({ type: "privacy" });
  post({ type: "page", page }); // the host remembers the last page for the next open
}
$$(".nav button[data-page]").forEach((b) => b.addEventListener("click", () => show(b.dataset.page)));

// ---------- toast ----------
let toastTimer;
// With an action ({ label, run }) the toast stays longer and shows a button (Undo).
function toast(text, error = false, action = null) {
  const t = $("#toast");
  t.textContent = text;
  t.classList.toggle("error", error);
  t.classList.toggle("has-action", !!action);
  if (action) {
    const b = document.createElement("button");
    b.type = "button";
    b.className = "toast-action";
    b.textContent = action.label;
    b.addEventListener("click", () => { t.classList.remove("show"); action.run(); });
    t.append(b);
  }
  t.classList.add("show");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.classList.remove("show"), action ? 8000 : error ? 4200 : 1600);
}

// ---------- saving (debounced, so typing in a field doesn't save every keystroke) ----------
let saveTimer = null;
function save(immediate = false) {
  clearTimeout(saveTimer);
  saveTimer = null;
  const send = () => { saveTimer = null; post({ type: "save", settings: state.settings }); toast("Saved"); };
  immediate ? send() : (saveTimer = setTimeout(send, 450));
}
// A debounced save still waiting for its timer (sent right away before the window closes).
function flushSave() { if (saveTimer) save(true); }

// ---------- links ----------
function openLink(key) {
  const repo = (state && state.repo) || "https://github.com/AmineChr54/umbra";
  const urls = { repo, issues: `${repo}/issues`, contribute: `${repo}/blob/main/CONTRIBUTING.md` };
  post({ type: "openUrl", url: urls[key] || key });
}
$("#reportIssue").addEventListener("click", () => post({ type: "reportIssue" }));
$("#copyDiagnostics").addEventListener("click", () => post({ type: "copyDiagnostics" }));
document.addEventListener("click", (e) => {
  const b = e.target.closest("[data-url]");
  if (b) openLink(b.dataset.url);
});

// ---------- General ----------
function renderGeneral() {
  const g = state.settings.general;
  document.documentElement.dataset.motion = g.animations === false || g.animationSpeed === "off" ? "off" : "on";
  window.prefsRender && prefsRender();
  $("#startup").checked = state.startup;
  $("#startup").disabled = !!state.portable;
  $("#startupSub").textContent = state.portable ? "Not available in portable mode." : "Your widgets appear on the desktop when you sign in.";
  $("#checkUpdates").checked = g.checkForUpdates !== false;
  $("#hideFullscreen").checked = !!g.hideOnFullscreen;
  syncInterval(g.refreshSeconds);
  labelRows();
}
// Segmented control = radio group: the selected button is the one tab stop, arrows move and select.
function syncInterval(seconds) {
  const buttons = $$("#interval button");
  const on = buttons.find((b) => Number(b.dataset.v) === seconds);
  for (const b of buttons) {
    b.setAttribute("role", "radio");
    b.setAttribute("aria-checked", b === on ? "true" : "false");
    b.tabIndex = b === on || (!on && b === buttons[0]) ? 0 : -1;
    b.classList.toggle("on", b === on);
  }
}
$("#startup").addEventListener("change", (e) => {
  post({ type: "setStartup", value: e.target.checked });
  toast(e.target.checked ? "Umbra will start with Windows" : "Umbra won't start with Windows");
});
$("#checkUpdates").addEventListener("change", (e) => { state.settings.general.checkForUpdates = e.target.checked; save(true); });
$("#hideFullscreen").addEventListener("change", (e) => { state.settings.general.hideOnFullscreen = e.target.checked; save(true); });
$$("#interval button").forEach((b) =>
  b.addEventListener("click", () => {
    state.settings.general.refreshSeconds = Number(b.dataset.v);
    renderGeneral();
    save(true);
  })
);

// Names for controls whose text lives in the row beside them, so a screen reader says "Start with Windows, switch, on"
// instead of an unnamed check box. Idempotent: runs after every render that can add rows.
let rowIds = 0;
function labelRows(root = document) {
  for (const row of $$(".row", root)) {
    const title = $(".row-title", row);
    if (!title) continue;
    const sub = $(".row-sub", row);
    for (const el of [title, sub]) if (el && !el.id) el.id = `row-t${++rowIds}`;
    for (const input of $$(".switch input", row)) {
      input.setAttribute("role", "switch");
      if (!input.hasAttribute("aria-label")) input.setAttribute("aria-labelledby", title.id);
      if (sub) input.setAttribute("aria-describedby", sub.id);
    }
    for (const group of $$(".segmented", row)) group.setAttribute("aria-labelledby", title.id);
  }
}

// Arrow keys move through a list of buttons (sidebar, segmented control). `pick` also selects the one reached.
function arrowNav(container, pick) {
  container.addEventListener("keydown", (e) => {
    const items = $$("button", container).filter((b) => !b.disabled && !b.hidden);
    const i = items.indexOf(document.activeElement);
    if (i < 0 || e.ctrlKey || e.altKey || e.metaKey) return;
    const next = { ArrowDown: i + 1, ArrowRight: i + 1, ArrowUp: i - 1, ArrowLeft: i - 1, Home: 0, End: items.length - 1 }[e.key];
    if (next === undefined) return;
    e.preventDefault();
    const target = items[(next + items.length) % items.length];
    target.focus();
    if (pick) target.click();
  });
}
arrowNav($("#nav"), false);
arrowNav($("#interval"), true);

// ---------- Updates (About) ----------
function renderUpdate() {
  const u = state.update || {};
  const a = u.available;
  $("#updateText").textContent = typeof u.error === "string" && u.error.startsWith("The update to") ? u.error
    : a && u.progress != null ? `Downloading Umbra v${a.version}: ${u.progress} %`
    : a && u.ready ? `Umbra v${a.version} is ready. Restart to update.`
    : a && u.error ? u.error
    : a ? `Umbra v${a.version} is available.`
    : u.error ? "Couldn't check for updates right now."
    : u.lastChecked ? "You're up to date."
    : u.enabled === false ? "Update checks are turned off in General." : "Checking for updates...";
  $("#update").classList.toggle("has-update", !!a);
  $("#updateInstall").hidden = !a;
  $("#updateInstall").lastChild.textContent = u.portable ? "Download" : u.ready ? "Restart to update" : "Update now";
  $("#aboutPortable").hidden = !state.portable;
  $("#aboutPortable").textContent = state.portable ? `Portable mode - data in ${state.dataDir}` : "";
  // Disabled from the click until Umbra quits (or the install fails), also while the download reports progress.
  const busy = !!(u.installing || u.progress != null);
  $("#updateInstall").disabled = busy;
  $("#updateCheck").disabled = busy;
  if (u.installing && a) $("#updateText").textContent = u.ready ? "Restarting Umbra to update..." : "Downloading the update. Umbra will restart when it's done...";
  const bs = u.button || "none";
  $("#navUpdate").hidden = bs === "none";
  // A disabled #updateInstall ignores click(), so the nav button must be disabled for the same window.
  $("#navUpdate").disabled = bs === "downloading" || !!u.installing;
  $("#navUpdateText").textContent = bs === "downloading" ? (u.progress != null ? `Downloading... ${u.progress} %` : "Downloading...") : bs === "ready" ? (u.installing ? "Restarting..." : "Restart to update") : u.portable ? "Download update" : "Update now";
  // Says what is being updated (widgets have their own Update all in Browse), to what, and what the click will do.
  $("#navUpdateSub").textContent = a ? `Umbra v${state.version} → v${a.version}` : "";
  $("#navUpdate").title = u.installing ? ""
    : bs === "ready" ? "Restarts Umbra to install the new version"
    : bs !== "available" ? ""
    : u.portable ? "Opens the release page to download the new version"
    : "Downloads the new version, then restarts Umbra to install it";
  $$(".nav button[data-page=about]").forEach((b) => b.classList.toggle("dot", !!a));
  renderWhatsNew();
}

// Release notes are untrusted text from GitHub: build DOM nodes with textContent only, never innerHTML. Only https
// links become clickable, and the host opens them in the browser.
const NOTE_LINK = /\[([^\]\n]{1,200})\]\((https:\/\/[^\s)]{1,500})\)|(https:\/\/[^\s<>()\[\]]{1,500})/g;
function noteInline(parent, text) {
  text = text.replace(/\*\*|__|`/g, "");
  let last = 0, m;
  NOTE_LINK.lastIndex = 0;
  while ((m = NOTE_LINK.exec(text))) {
    if (m.index > last) parent.append(text.slice(last, m.index));
    const url = m[2] || m[3].replace(/[.,;:!?]+$/, "");
    const a = document.createElement("a");
    a.textContent = m[1] || url;
    a.dataset.href = url;
    a.tabIndex = 0;
    a.setAttribute("role", "link");
    parent.append(a);
    last = m.index + (m[2] ? m[0].length : url.length);
    NOTE_LINK.lastIndex = last;
  }
  if (last < text.length) parent.append(text.slice(last));
}
function renderNotes(host, notes) {
  host.replaceChildren();
  let list = null;
  for (const raw of String(notes || "").replace(/<[^>]*>/g, "").split(/\r?\n/)) {
    const line = raw.trim();
    const bullet = /^[-*+]\s+(.*)$/.exec(line);
    if (bullet) {
      if (!list) host.append((list = document.createElement("ul")));
      noteInline(list.appendChild(document.createElement("li")), bullet[1]);
      continue;
    }
    list = null;
    if (!line) continue;
    const head = /^#{1,6}\s+(.*)$/.exec(line);
    const el = document.createElement(head ? "h4" : "p");
    noteInline(el, head ? head[1] : line);
    host.append(el);
  }
}
function renderWhatsNew() {
  const w = state.update && state.update.whatsNew;
  $("#whatsNew").hidden = !w;
  if (!w) return;
  $("#whatsNewTitle").textContent = `What's new in Umbra ${w.version}`;
  const body = $("#whatsNewBody");
  if (body.dataset.src !== w.notes) { body.dataset.src = w.notes; renderNotes(body, w.notes); } // not on every state push: keeps the scroll position
  $("#whatsNewBody").hidden = !w.notes;
  $("#whatsNewFull").dataset.href = w.url;
}
$("#whatsNew").addEventListener("click", (e) => {
  const t = e.target.closest("[data-href]");
  if (t) post({ type: "openUrl", url: t.dataset.href });
});
$("#whatsNew").addEventListener("keydown", (e) => {
  if (e.key === "Enter" && e.target.dataset && e.target.dataset.href) post({ type: "openUrl", url: e.target.dataset.href });
});
$("#updateCheck").addEventListener("click", () => { $("#updateCheck").disabled = true; $("#updateText").textContent = "Checking..."; post({ type: "checkUpdate" }); });
$("#navUpdate").addEventListener("click", () => $("#updateInstall").click());
$("#updateInstall").addEventListener("click", () => {
  if (state.update && state.update.portable) { post({ type: "openUrl", url: state.update.available.url }); return; }
  $("#updateInstall").disabled = true;
  $("#updateText").textContent = state.update && state.update.ready ? "Restarting Umbra..." : "Downloading the update. Umbra will restart when it's done...";
  post({ type: "installUpdate" });
});

// ---------- Hotkeys ----------
const MODKEYS = new Set(["Control", "Alt", "Shift", "Meta", "AltGraph"]);
let recording = null;
function comboFromEvent(e) {
  const k = e.key;
  const main = k.length === 1 ? (k === " " ? "Space" : k.toUpperCase())
    : /^Fd{1,2}$/.test(k) ? k : { ArrowLeft: "Left", ArrowRight: "Right", ArrowUp: "Up", ArrowDown: "Down", " ": "Space" }[k] || k;
  return [e.ctrlKey && "Ctrl", e.altKey && "Alt", e.shiftKey && "Shift", e.metaKey && "Win", main].filter(Boolean).join("+");
}
function renderHotkeys() {
  const list = $("#hotkeyList");
  // The list is rebuilt after each change; put keyboard focus back on the same shortcut.
  const refocus = list.contains(document.activeElement) ? document.activeElement.dataset.action : null;
  list.replaceChildren();
  for (const h of state.hotkeys || []) {
    const row = document.createElement("div");
    row.className = "row";
    const text = document.createElement("div");
    text.className = "row-text";
    const title = document.createElement("div");
    title.className = "row-title";
    title.textContent = h.label;
    text.append(title);
    if (h.error) {
      const sub = document.createElement("div");
      sub.className = "row-sub";
      sub.style.color = "#ff6b6b";
      sub.textContent = "Not active: " + h.error;
      text.append(sub);
    }
    const btn = document.createElement("button");
    btn.className = "btn";
    btn.dataset.action = h.action;
    btn.textContent = recording === h.action ? "Press keys..." : h.keys || "Off";
    btn.setAttribute("aria-label", recording === h.action ? `${h.label}: press the new shortcut. Escape cancels, Backspace turns it off.`
      : `${h.label}: ${h.keys || "off"}. Press Enter to change.`);
    btn.addEventListener("click", () => { recording = h.action; renderHotkeys(); btn.focus(); });
    btn.addEventListener("blur", () => { if (recording === h.action) { recording = null; renderHotkeys(); } });
    btn.addEventListener("keydown", (e) => {
      if (recording !== h.action) return;
      e.preventDefault();
      if (MODKEYS.has(e.key)) return;
      recording = null;
      if (e.key === "Escape") return renderHotkeys();
      post({ type: "setHotkey", action: h.action, keys: e.key === "Backspace" && !(e.ctrlKey || e.altKey || e.metaKey) ? "" : comboFromEvent(e) });
    });
    row.append(text, btn);
    list.append(row);
    if (refocus === h.action) btn.focus();
  }
}
$("#hotkeyReset").addEventListener("click", () => post({ type: "resetHotkeys" }));

// ---------- My widgets ----------
function field(wid, key, spec, value) {
  const wrap = document.createElement("div");
  wrap.className = "field";
  const id = `f-${wid}-${key}`;
  const label = Object.assign(document.createElement("label"), { htmlFor: id, textContent: spec.label || key });
  wrap.append(label);

  const set = (v) => { state.settings.widgets[wid].options[key] = v; save(); };
  let input;
  if (spec.type === "select") {
    input = Object.assign(document.createElement("select"), { className: "select", id });
    for (const c of spec.choices || []) input.append(new Option(c.label, c.value, false, c.value === value));
    input.addEventListener("change", () => set(input.value));
  } else if (spec.type === "toggle") {
    input = document.createElement("label");
    input.className = "switch";
    input.innerHTML = `<input type="checkbox" id="${id}"><span></span>`;
    $("input", input).setAttribute("role", "switch");
    $("input", input).checked = !!value;
    $("input", input).addEventListener("change", (e) => set(e.target.checked));
  } else {
    input = Object.assign(document.createElement("input"), {
      className: "input", id, type: spec.type === "number" ? "number" : "text", value: value ?? "", spellcheck: false,
    });
    for (const k of ["min", "max", "step"]) if (spec.type === "number" && spec[k] !== undefined) input[k] = spec[k];
    input.addEventListener("input", () => set(spec.type === "number" ? Number(input.value) : input.value));
  }
  wrap.append(input);
  if (spec.hint) wrap.append(Object.assign(document.createElement("div"), { className: "hint-text", textContent: spec.hint }));
  return wrap;
}

// A collapsed section for options with the same `group` (or `advanced: true`). <details> gives keyboard
// folding for free; which sections are open is remembered per widget copy in this browser profile.
function optionSection(wid, name, els, store = name) {
  const key = `${wid}\n${store}`;
  let open = [];
  try { open = JSON.parse(localStorage.getItem("openOptionSections") || "[]"); } catch { /* ignore */ }
  const box = Object.assign(document.createElement("details"), { className: "optsection", open: open.includes(key) });
  const summary = Object.assign(document.createElement("summary"), { textContent: name });
  summary.prepend(Object.assign(document.createElement("span"), { className: "ico" }));
  summary.firstChild.dataset.icon = "chevron";
  const body = Object.assign(document.createElement("div"), { className: "optsection-body" });
  body.append(...els);
  box.append(summary, body);
  box.addEventListener("toggle", () => {
    try {
      const set = new Set(JSON.parse(localStorage.getItem("openOptionSections") || "[]"));
      if (box.open) set.add(key); else set.delete(key);
      localStorage.setItem("openOptionSections", JSON.stringify([...set]));
    } catch { /* storage unavailable */ }
  });
  return box;
}

function thumb(el, w) {
  if (w.preview) el.style.backgroundImage = `url("${w.preview}")`;
  else { el.classList.add("fallback"); el.textContent = (w.name || "?").trim()[0]; }
}

const openCards = () => { try { return new Set(JSON.parse(localStorage.getItem("openCards") || "[]")); } catch { return new Set(); } };

// Lists are sorted (#236): Active first, then Not enabled, each by name; grouped widgets stay together under their group header.
// `refocus` = a widget copy whose switch had focus: it gets it back after the rebuild.
let myFilter = "";
function renderInstalled(refocus) {
  const list = $("#installed");
  const scroller = $(".content");
  const top = scroller ? scroller.scrollTop : 0;
  list.textContent = "";
  $("#count").textContent = state.widgets.length || "";
  $("#installMore").hidden = $("#groupStrip").hidden = !state.widgets.length;
  $("#mySearchBox").hidden = state.widgets.length < 6; // a filter only earns its place on a long list
  if ($("#mySearchBox").hidden && myFilter) { myFilter = ""; $("#mySearch").value = ""; }
  if (!state.widgets.length) {
    list.innerHTML = '<div class="empty">No widgets yet.<br><br><button class="btn primary" id="goBrowse">Browse widgets</button></div>';
    $("#goBrowse").addEventListener("click", () => show("browse"));
    return;
  }
  const cards = new Map();
  for (const w of state.widgets) {
    const ws = state.settings.widgets[w.id];
    const card = $("#tpl-installed").content.firstElementChild.cloneNode(true);
    thumb($(".thumb", card), w);
    $(".wname", card).textContent = w.name; // "Name (2)" for extra copies
    const badge = $(".badge", card);
    badge.textContent = w.source === "builtin" ? "Built-in" : w.private ? "Private" : "Community";
    badge.classList.add(w.private && w.source !== "builtin" ? "private" : w.source);
    $(".wmeta", card).textContent = [w.author && `by ${w.author}`, `v${w.version}`].filter(Boolean).join("  ·  ");
    $(".wdesc", card).textContent = w.description;

    // Look the settings up on each change: the host may have sent a newer state (a zoom on the desktop)
    // while this list wasn't rebuilt, and writing to the old `ws` would be lost.
    const cur = () => state.settings.widgets[w.id];
    card.setAttribute("aria-label", w.name);
    const enabled = $(".enabled", card);
    enabled.setAttribute("role", "switch");
    enabled.setAttribute("aria-label", `${w.name} enabled`);
    enabled.checked = ws.enabled;
    card.classList.toggle("off", !ws.enabled);
    enabled.addEventListener("change", () => {
      cur().enabled = enabled.checked;
      save(true);
      renderInstalled(w.id); // moves the card between Active and Not enabled right away
    });

    // Group badge (click to change or remove) replaces the old per-card dropdown (#235).
    {
      const here = (state.groups || []).find((g) => g.members.includes(w.id));
      const gb = $(".gbadge", card);
      gb.textContent = here ? here.name : "+ Group";
      gb.classList.toggle("none", !here);
      gb.setAttribute("aria-label", here ? `${w.name} is in group ${here.name}. Change group` : `Add ${w.name} to a group`);
      gb.addEventListener("click", () => openGroupMenu(gb, w, here));
      const sel = $(".sel", card);
      sel.hidden = !selectMode;
      sel.checked = selected.has(w.id);
      sel.setAttribute("aria-label", `Select ${w.name}`);
      sel.addEventListener("change", () => { sel.checked ? selected.add(w.id) : selected.delete(w.id); renderGroups(); });
      const main = $(".wcard-main", card);
      main.draggable = true;
      main.addEventListener("dragstart", (e) => { e.dataTransfer.setData("text/plain", w.id); e.dataTransfer.effectAllowed = "move"; card.classList.add("dragging"); });
      main.addEventListener("dragend", () => card.classList.remove("dragging"));
      if (here) card.classList.add("ingroup");
    }

    // Fold/unfold the options: folded until opened, so a long list stays readable. Remembered per widget in this browser profile.
    const fold = $(".fold", card);
    const setFolded = (folded, remember) => {
      card.classList.toggle("folded", folded);
      fold.setAttribute("aria-expanded", String(!folded));
      fold.setAttribute("aria-label", `${folded ? "Show" : "Hide"} ${w.name} options`);
      if (!remember) return;
      try {
        const set = openCards();
        if (folded) set.delete(w.id); else set.add(w.id);
        localStorage.setItem("openCards", JSON.stringify([...set]));
      } catch { /* storage unavailable */ }
    };
    setFolded(!openCards().has(w.id), false);
    fold.addEventListener("click", () => setFolded(!card.classList.contains("folded"), true));
    // The whole row opens the card, except the controls on it. A disabled card has no options to show.
    $(".wcard-main", card).addEventListener("click", (e) => {
      if (e.target.closest("button, label, input, select, a")) return;
      if (selectMode) $(".sel", card).click();
      else if (!card.classList.contains("off")) fold.click();
    });

    renderScripts($(".cmds", card), w); // the user's own first: they are why the widget is there
    renderCommands($(".cmds", card), w);
    // Approve and Revoke live in the folded part of the card, so the row says they are there, and when one needs the user.
    {
      const cmds = [...(w.commands || []), ...(w.scripts || [])];
      const text = cmds.some((c) => c.status === "changed") ? "Command changed"
        : cmds.some((c) => c.status !== "approved") ? "Needs approval"
        : cmds.length ? "Runs programs"
        : w.camera ? "Uses camera" : "";
      if (text) {
        const access = Object.assign(document.createElement("span"), { className: "badge access", textContent: text });
        access.classList.toggle("attention", cmds.some((c) => c.status !== "approved"));
        badge.after(access);
      }
    }
    window.prefsCard && prefsCard(card, w);

    const fields = $(".fields", card);
    const order = w.optionOrder && w.optionOrder.length ? w.optionOrder : Object.keys(w.options || {});
    const sections = new Map(); // `group` (or "Advanced") -> its fields, in order of first appearance
    for (const key of order) {
      const spec = w.options[key];
      if (!spec) continue;
      const el = field(w.id, key, spec, ws.options[key]);
      const name = typeof spec.group === "string" && spec.group.trim() ? spec.group.trim() : spec.advanced === true ? "Advanced" : null;
      if (!name) { fields.append(el); continue; }
      if (!sections.has(name)) sections.set(name, []);
      sections.get(name).push(el);
    }
    for (const [name, els] of sections) fields.append(optionSection(w.id, name, els));

    // Size: 50-300 %.
    const scale = $(".scale", card);
    const scaleVal = $(".size-val", card);
    const showScale = (pct) => {
      scaleVal.textContent = `${Math.round(pct)}%`;
      scale.setAttribute("aria-valuetext", `${Math.round(pct)}%`);
      scale.style.setProperty("--p", `${((pct - 50) / 250) * 100}%`);
    };
    // The slider sets a proportional size, so it replaces a free width/height dragged by hand.
    const setScale = (pct) => { cur().scale = pct / 100; cur().useDefaultScale = false; cur().width = cur().height = null; scale.value = pct; showScale(pct); save(true); };
    scale.setAttribute("aria-label", `${w.name} size`);
    scaleVal.setAttribute("aria-label", `Reset ${w.name} size to the default`);
    scale.value = Math.round((ws.scale || 1) * 100);
    showScale((ws.scale || 1) * 100);
    scale.addEventListener("input", () => showScale(Number(scale.value)));
    scale.addEventListener("change", () => setScale(Number(scale.value)));
    // Let go of focus after a mouse drag so later updates from the desktop can re-render this list.
    scale.addEventListener("pointerup", () => setTimeout(() => scale.blur()));
    scaleVal.addEventListener("click", () => { // back to the default size (General)
      const d = Math.round((state.settings.general.defaultScale || 1) * 100);
      setScale(d); cur().useDefaultScale = true; scaleVal.blur();
    });

    const mon = $(".monitor", card);
    const mons = state.monitors || [];
    mon.closest(".field").hidden = mons.length < 2;
    for (const m of mons) mon.append(new Option(m.label, m.name));
    if (w.monitor && mons.some((m) => m.name === w.monitor)) mon.value = w.monitor;
    else mon.selectedIndex = Math.max(0, mons.findIndex((m) => /primary/.test(m.label)));
    mon.addEventListener("change", () => { post({ type: "setMonitor", id: w.id, monitor: mon.value }); toast("Moved to " + mon.selectedOptions[0].textContent); });

    $(".reset", card).setAttribute("aria-label", `Reset position of ${w.name}`);
    $(".reset", card).addEventListener("click", () => { post({ type: "resetPosition", id: w.id }); toast("Position reset"); });
    // Copies: the first copy can add more (each with its own options, position and data), extra copies can be removed.
    const extra = w.instance > 1;
    const copy = $(".copy", card);
    const removeCopy = $(".removecopy", card);
    copy.setAttribute("aria-label", `Add another copy of ${w.name}`);
    removeCopy.setAttribute("aria-label", `Remove ${w.name}`);
    if (extra) copy.remove();
    else copy.addEventListener("click", () => { post({ type: "addInstance", id: w.widget }); toast(`Added another ${w.name}`); });
    if (!extra) removeCopy.remove();
    else removeCopy.addEventListener("click", () => { post({ type: "removeInstance", id: w.id }); toast(`${w.name} removed`); });
    card.dataset.instance = w.id;
    const remove = $(".remove", card);
    remove.setAttribute("aria-label", `Remove ${w.name}`);
    if (w.source === "builtin" || extra) remove.remove();
    else remove.addEventListener("click", () => job(w.widget, "remove", remove));

    // A disabled card is a one-liner: name, switch and Remove.
    if (!ws.enabled) {
      const sw = $(".switch", card);
      for (const b of $$(".remove, .removecopy", card)) sw.before(b);
    }
    icons(card);
    labelRows(card);
    cards.set(w.id, card);
  }
  const q = myFilter.trim().toLowerCase();
  const nameOf = (id) => (state.widgets.find((x) => x.id === id) || { name: id }).name;
  const match = (id) => !q || nameOf(id).toLowerCase().includes(q);
  const isOn = (id) => !!(state.settings.widgets[id] || {}).enabled;
  const byName = (x, y) => nameOf(x).localeCompare(nameOf(y), undefined, { sensitivity: "base" });
  const section = (title, count) => {
    const el = Object.assign(document.createElement("div"), { className: "sechead" });
    el.append(Object.assign(document.createElement("span"), { textContent: title }), Object.assign(document.createElement("span"), { className: "gcount", textContent: String(count) }));
    return el;
  };
  const active = [...cards.keys()].filter((id) => isOn(id) && match(id));
  const off = [...cards.keys()].filter((id) => !isOn(id) && match(id)).sort(byName);
  const placed = new Set();
  const body = [];
  // Active: groups first (header + enabled members in tab order), then the rest alphabetically.
  for (const g of state.groups || []) {
    const members = g.members.filter((id) => cards.has(id) && isOn(id) && match(id));
    if (q && !members.length) continue; // while filtering, only groups with a hit
    body.push(groupHeader(g));
    const folded = foldedGroups().has(g.id);
    for (const id of members) {
      placed.add(id);
      const card = cards.get(id);
      card.hidden = folded;
      body.push(card);
    }
  }
  for (const id of active.filter((id) => !placed.has(id)).sort(byName)) body.push(cards.get(id));
  const activeCount = active.length;
  if (activeCount || (state.groups || []).length) list.append(section("Active", activeCount));
  list.append(...body);
  if (off.length) {
    list.append(section("Not enabled", off.length));
    for (const id of off) list.append(cards.get(id));
  }
  if (!activeCount && !off.length) {
    list.append(Object.assign(document.createElement("div"), { className: "empty", textContent: `No widget matches "${myFilter.trim()}".` }));
  }
  if (scroller) { scroller.style.scrollBehavior = "auto"; scroller.scrollTop = top; scroller.style.scrollBehavior = ""; }
  if (refocus) cards.get(refocus)?.querySelector(".enabled")?.focus({ preventScroll: true });
  applyFocus();
}

// "Options..." in a widget's right-click menu: the host asks for this copy's card. State may arrive after the
// request (Settings was just opened), so the request lasts a moment and renderInstalled applies it again.
let focusId = null;
let focusUntil = 0;
let focusScrolled = false;
function focusWidget(id) {
  show("widgets");
  focusId = id;
  focusUntil = Date.now() + 2500;
  focusScrolled = false;
  applyFocus();
}
function applyFocus() {
  if (!focusId || Date.now() > focusUntil) { focusId = null; return; }
  const card = $$("#installed .wcard").find((c) => c.dataset.instance === focusId);
  if (!card) return;
  card.classList.add("focus");
  if (!focusScrolled) {
    focusScrolled = true;
    if (card.classList.contains("folded")) $(".fold", card).click(); // they asked for this widget's options
    card.scrollIntoView({ block: "center" });
  }
  setTimeout(() => card.classList.remove("focus"), Math.max(0, focusUntil - Date.now()));
}

// Local programs a widget may run. The host re-checks everything: this only shows the exact command line and
// sends back ids (and the hash it showed, so a command that changed meanwhile is not approved blind).
function renderCommands(root, w) {
  if (w.camera) {
    // Granted only by the host's prompt after a click on the widget; this row can only take it away.
    const row = Object.assign(document.createElement("div"), { className: "cmd" });
    const desc = Object.assign(document.createElement("div"), { className: "cmd-desc" });
    desc.append(Object.assign(document.createElement("span"), {
      className: `cmd-status ${w.cameraApproved ? "approved" : "pending"}`,
      textContent: w.cameraApproved ? "Approved" : "Asked on first use",
    }), "Records webcam timelapses (red dot while recording)");
    row.append(Object.assign(document.createElement("div"), { className: "cmd-line", textContent: "Camera" }), desc);
    if (w.cameraApproved) {
      const btn = Object.assign(document.createElement("button"), { className: "btn small danger", textContent: "Revoke" });
      btn.setAttribute("aria-label", `Revoke camera access for ${w.name}`);
      btn.addEventListener("click", () => post({ type: "revokeCamera", widget: w.widget }));
      row.append(btn);
    }
    root.append(row);
  }
  const cmds = w.commands || [];
  if (!cmds.length) return;
  root.append(Object.assign(document.createElement("div"), { className: "cmds-title", textContent: "Runs programs on this PC" }));
  for (const c of cmds) {
    const row = document.createElement("div");
    row.className = "cmd";
    const line = Object.assign(document.createElement("div"), { className: "cmd-line", textContent: c.line });
    const desc = Object.assign(document.createElement("div"), { className: "cmd-desc" });
    const status = Object.assign(document.createElement("span"), {
      className: `cmd-status ${c.status}`,
      textContent: c.status === "approved" ? "Approved" : c.status === "changed" ? "Changed, needs approval" : "Not approved",
    });
    desc.append(status, c.description || "");
    const btn = document.createElement("button");
    btn.className = "btn small" + (c.status === "approved" ? " danger" : " primary");
    btn.textContent = c.status === "approved" ? "Revoke" : "Approve";
    btn.setAttribute("aria-label", `${btn.textContent} ${c.line}`);
    btn.addEventListener("click", () =>
      post({ type: c.status === "approved" ? "revokeCommand" : "approveCommand", widget: w.widget, command: c.id, hash: c.hash }));
    row.append(line, desc, btn);
    root.append(row);
  }
}

// ---------- The user's own scripts (permissions.userScripts, e.g. Script Runner) ----------
// The host checks everything (UserScript.Normalize) and builds the command; this form only collects what the user
// typed. Saving here is the user's approval of exactly that command, so the line the host runs is shown on each row.
const scriptDrafts = new Map(); // widget copy id -> the script being added or edited (kept while the state re-renders)
let scriptFocus = null;         // widget copy whose editor should take focus after the next render
const SCRIPT_KINDS = [["powershell", "PowerShell"], ["python", "Python"], ["bash", "Bash"], ["program", "Program"]];
const mk = (tag, className, text) => Object.assign(document.createElement(tag), className ? { className } : {}, text != null ? { textContent: text } : {});
// Starting points for a new script. Picking one only fills the form: it becomes the user's own script when they save it.
const SCRIPT_EXAMPLES = [
  { title: "System info", description: "Windows version, uptime and free memory", kind: "powershell", mode: "code", timeoutSeconds: 30,
    code: '$o = Get-CimInstance Win32_OperatingSystem\n"$($o.Caption) $($o.Version)"\n"Uptime: $([int]((Get-Date) - $o.LastBootUpTime).TotalHours) h"\n"Free memory: $([math]::Round($o.FreePhysicalMemory / 1MB, 1)) GB"' },
  { title: "Free disk space", description: "Free space on each drive", kind: "powershell", mode: "code", timeoutSeconds: 30,
    code: 'Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Used -ne $null } | ForEach-Object {\n    "{0}: {1:N1} GB free of {2:N1} GB" -f $_.Name, ($_.Free / 1GB), (($_.Used + $_.Free) / 1GB)\n}' },
  { title: "Python version", description: "Needs Python on PATH", kind: "python", mode: "code", timeoutSeconds: 20,
    code: "import sys, platform\nprint('Python', platform.python_version())\nprint(sys.executable)" },
  { title: "Ping every 5 seconds", description: "Keeps running until you stop it", kind: "powershell", mode: "code", keepsRunning: true,
    code: 'while ($true) {\n    $r = Test-Connection 1.1.1.1 -Count 1 -ErrorAction SilentlyContinue\n    if ($r) { "$(Get-Date -Format T)  $($r.ResponseTime) ms" } else { "$(Get-Date -Format T)  no answer" }\n    Start-Sleep -Seconds 5\n}' },
];
const clockTime = (ms) => new Date(ms).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });

function editScript(w, draft) {
  scriptDrafts.set(w.id, { widget: w.widget, kind: "powershell", mode: "file", path: "", code: "", args: "", description: "", title: "", timeoutSeconds: 60,
    keepsRunning: false, startIn: "", autoStart: false, restart: false, runAsAdmin: false, ...draft });
  scriptFocus = w.id;
  try { localStorage.setItem("openCards", JSON.stringify([...openCards().add(w.id)])); } catch { /* storage unavailable */ }
  renderInstalled();
}

function renderScripts(root, w) {
  if (!w.userScripts) return;
  const draft = scriptDrafts.get(w.id);
  const scripts = w.scripts || [];
  const head = mk("div", "scripts-head");
  head.append(mk("div", "cmds-title", "Your scripts"));
  if (!draft) {
    const add = mk("button", "btn small");
    add.innerHTML = '<span class="ico" data-icon="plus"></span>Add script';
    add.setAttribute("aria-label", `Add a script to ${w.name}`);
    add.addEventListener("click", () => editScript(w, {}));
    head.append(add);
  }
  root.append(head);
  if (!scripts.length && !draft)
    root.append(mk("div", "cmd-desc", "Add a PowerShell, Python or Bash script, or any program. It shows up in the widget with a Run button."));
  for (const s of scripts) {
    if (draft && draft.id === s.id) { root.append(scriptEditor(w, draft)); continue; }
    const row = mk("div", "cmd script");
    const name = mk("div", "script-name", s.title);
    if (s.keepsRunning) {
      // A tunnel or server: say so up front, and whether it is up right now.
      name.append(mk("span", "script-kind", "Keeps running"));
      const state = s.running ? `Running since ${clockTime(s.since)}` : s.restarting ? "Restarting" : s.lastExit != null ? `Stopped (exit ${s.lastExit})` : "Not running";
      name.append(mk("span", `script-state${s.running ? " on" : s.restarting ? " wait" : ""}`, state));
    }
    const line = mk("div", "cmd-line", s.line || "(can't run: open it to fix)");
    if (s.runAsAdmin) name.append(mk("span", "script-kind", "As administrator"));
    const desc = mk("div", "cmd-desc");
    desc.append(mk("span", `cmd-status ${s.status === "invalid" ? "changed" : s.status}`,
      s.status === "approved" ? "Approved" : s.status === "invalid" ? "Needs fixing" : s.status === "changed" ? "Changed, needs approval" : "Not approved"), s.description || "");
    const actions = mk("div", "script-actions");
    if (s.status === "pending" || s.status === "changed") {
      const ok = mk("button", "btn small primary", "Approve");
      ok.setAttribute("aria-label", `Approve ${s.title}: ${s.line}`);
      ok.addEventListener("click", () => post({ type: "approveCommand", widget: w.widget, command: s.id, hash: s.hash }));
      actions.append(ok);
    } else if (s.keepsRunning && s.status === "approved") {
      const on = s.running || s.restarting;
      const go = mk("button", `btn small${on ? " danger" : " primary"}`, on ? "Stop" : "Start");
      go.setAttribute("aria-label", `${on ? "Stop" : "Start"} ${s.title}`);
      go.addEventListener("click", () => { post({ type: on ? "stopScript" : "startScript", widget: w.widget, script: s.id }); go.blur(); });
      actions.append(go);
    }
    const edit = mk("button", "btn small", "Edit");
    edit.setAttribute("aria-label", `Edit ${s.title}`);
    edit.addEventListener("click", () => editScript(w, s));
    const del = mk("button", "btn small danger icon");
    del.innerHTML = '<span class="ico" data-icon="trash"></span>';
    del.title = "Delete";
    del.setAttribute("aria-label", `Delete ${s.title}`);
    del.addEventListener("click", () => post({ type: "deleteScript", widget: w.widget, script: s.id }));
    actions.append(edit, del);
    row.append(name, line, desc, actions);
    root.append(row);
  }
  if (draft && !draft.id) root.append(scriptEditor(w, draft));
}

function scriptEditor(w, d) {
  const form = mk("form", "script-edit");
  form.noValidate = true;
  form.setAttribute("aria-label", d.id ? `Edit ${d.title}` : `New script for ${w.name}`);
  const row = (label, control, hint) => {
    const f = mk("div", "field");
    if (!control.hasAttribute("aria-label")) control.setAttribute("aria-label", label);
    f.append(mk("span", "flabel", label), control);
    if (hint) f.append(mk("span", "hint-text", hint));
    return f;
  };
  const input = (key, attrs = {}) => {
    const i = Object.assign(mk("input", "input"), { value: d[key] ?? "" }, attrs);
    i.addEventListener("input", () => { d[key] = i.type === "number" ? Number(i.value) : i.value; });
    return i;
  };

  const title = input("title", { maxLength: 60, placeholder: "Backup photos" });
  const kind = mk("select", "select");
  for (const [v, label] of SCRIPT_KINDS) kind.append(new Option(label, v));
  kind.value = d.kind;
  kind.addEventListener("change", () => { d.kind = kind.value; sync(); });

  /** A segmented radio group; onPick(value) sets the draft. Returns [element, buttons]. */
  const segmented = (choices, onPick) => {
    const box = mk("div", "segmented");
    box.setAttribute("role", "radiogroup");
    const btns = choices.map(([v, label]) => {
      const b = mk("button", "", label);
      b.type = "button";
      b.setAttribute("role", "radio");
      b.dataset.v = String(v);
      b.addEventListener("click", () => { onPick(v); sync(); });
      box.append(b);
      return b;
    });
    return [box, btns];
  };
  const mark = (btns, value) => { for (const b of btns) { const on = b.dataset.v === String(value); b.classList.toggle("on", on); b.setAttribute("aria-checked", String(on)); } };

  // Runs and finishes (a report, a backup) or keeps running (a tunnel, a server): asked up front, since it changes how it runs.
  const [life, lifeBtns] = segmented([[false, "Runs and finishes"], [true, "Keeps running"]], (v) => { d.keepsRunning = v; });
  const lifeRow = row("How it runs", life);

  // Script file or code you write here (Program always runs a file).
  const [mode, modeBtns] = segmented([["file", "Script file"], ["code", "Write code"]], (v) => { d.mode = v; });
  const modeRow = row("Run", mode);

  // Only when adding: start from an example (it fills the form; saving makes it the user's own script).
  let exampleRow = null;
  if (!d.id) {
    const pick = mk("select", "select");
    pick.append(new Option("Start from scratch", ""));
    SCRIPT_EXAMPLES.forEach((x, i) => pick.append(new Option(`${x.title}${x.keepsRunning ? " (keeps running)" : ""}`, String(i))));
    pick.value = d.example ?? "";
    pick.addEventListener("change", () => {
      const x = SCRIPT_EXAMPLES[Number(pick.value)];
      Object.assign(d, { example: pick.value, title: "", description: "", kind: "powershell", mode: "file", path: "", code: "", args: "", timeoutSeconds: 60, keepsRunning: false, autoStart: false, restart: false, runAsAdmin: false },
        pick.value === "" ? {} : x);
      document.activeElement?.blur();
      renderInstalled();
    });
    exampleRow = row("Example", pick);
  }

  const path = input("path", { placeholder: "C:\\Scripts\\backup.ps1", spellcheck: false });
  const browse = mk("button", "btn small", "Browse...");
  browse.type = "button";
  browse.addEventListener("click", () => post({ type: "pickScriptFile", kind: d.kind, ref: w.id }));
  const pathBox = mk("div", "script-path");
  pathBox.append(path, browse);
  const pathRow = row("Script file", pathBox);

  const code = Object.assign(mk("textarea", "input code"), { value: d.code ?? "", rows: 8, spellcheck: false });
  code.setAttribute("aria-label", "Code");
  code.addEventListener("input", () => { d.code = code.value; });
  // Tab indents instead of leaving the box (Shift+Tab or Esc then Tab still moves on).
  code.addEventListener("keydown", (e) => {
    if (e.key !== "Tab" || e.shiftKey || e.ctrlKey || e.altKey) return;
    e.preventDefault();
    code.setRangeText("    ", code.selectionStart, code.selectionEnd, "end");
    d.code = code.value;
  });
  const codeRow = row("Code", code);

  const args = input("args", { placeholder: "--quiet \"D:\\My Photos\"", spellcheck: false });
  const argsRow = row("Arguments", args, 'Optional. Separate them with spaces; put one with spaces in "quotes".');
  // The folder it runs in, like a shortcut's "Start in" (empty: the widget's own data folder).
  const startIn = input("startIn", { placeholder: "Optional, e.g. C:\\Scripts", spellcheck: false });
  const pickDir = mk("button", "btn small", "Browse...");
  pickDir.type = "button";
  pickDir.addEventListener("click", () => post({ type: "pickScriptFolder", path: d.startIn || "", ref: w.id }));
  const dirBox = mk("div", "script-path");
  dirBox.append(startIn, pickDir);
  const dirRow = row("Start in", dirBox);

  const timeout = input("timeoutSeconds", { type: "number", min: 1, max: 120, step: 1 });
  timeout.classList.add("num");
  const timeoutRow = row("Time limit (seconds)", timeout);
  const toggle = (key, label, hint) => {
    const sw = mk("label", "switch");
    sw.innerHTML = '<input type="checkbox"><span></span>';
    const box = $("input", sw);
    box.setAttribute("role", "switch");
    box.setAttribute("aria-label", label);
    box.checked = !!d[key];
    box.addEventListener("change", () => { d[key] = box.checked; sync(); });
    return row(label, sw, hint);
  };
  const autoRow = toggle("autoStart", "Start automatically", "Whenever the widget is on, so also when you sign in.");
  const restartRow = toggle("restart", "Restart if it stops", "After 5 s, then longer waits; it gives up after 5 tries in a row.");
  // Elevation: Windows asks (UAC) on every run, and only a click on Run starts one. Not for scripts that keep running.
  const adminRow = toggle("runAsAdmin", "Run as administrator",
    "Windows asks you each time. Only starts when you click Run on the widget; its output shows in its own window, not in the widget.");
  const desc = input("description", { maxLength: 200, placeholder: "Optional, shown under the name" });

  const save = mk("button", "btn small primary", d.id ? "Save" : "Add script");
  save.type = "submit";
  const cancel = mk("button", "btn small", "Cancel");
  cancel.type = "button";
  cancel.addEventListener("click", () => { scriptDrafts.delete(w.id); document.activeElement?.blur(); renderInstalled(); });
  const actions = mk("div", "script-edit-actions");
  const saveHint = mk("span", "hint-text");
  actions.append(saveHint, cancel, save);

  form.append(...[exampleRow, row("Name", title), row("Type", kind), lifeRow, modeRow, pathRow, codeRow, argsRow, dirRow, timeoutRow, adminRow, autoRow, restartRow, row("Description", desc), actions].filter(Boolean));
  form.addEventListener("submit", (e) => {
    e.preventDefault();
    const script = Object.fromEntries(["id", "title", "description", "kind", "mode", "path", "code", "args", "startIn"].map((k) => [k, String(d[k] ?? "")]));
    post({ type: "saveScript", widget: d.widget, script: { ...script, timeoutSeconds: Math.round(Number(d.timeoutSeconds)) || 60,
      keepsRunning: !!d.keepsRunning, autoStart: !!d.keepsRunning && !!d.autoStart, restart: !!d.keepsRunning && !!d.restart,
      runAsAdmin: !d.keepsRunning && !!d.runAsAdmin } });
    document.activeElement?.blur(); // so the state that follows rebuilds the list
  });
  form.addEventListener("keydown", (e) => { if (e.key === "Escape") { e.stopPropagation(); cancel.click(); } });

  function sync() {
    const program = d.kind === "program", bashCode = d.kind === "bash" && d.mode === "code";
    const isCode = !program && d.mode === "code";
    modeRow.hidden = program;
    mark(modeBtns, program ? "file" : d.mode);
    mark(lifeBtns, !!d.keepsRunning);
    timeoutRow.hidden = !!d.keepsRunning;
    autoRow.hidden = restartRow.hidden = !d.keepsRunning;
    adminRow.hidden = !!d.keepsRunning;
    saveHint.textContent = d.keepsRunning
      ? "Saving approves exactly this command. It runs as you, never from a shell, until you stop it or turn the widget off."
      : d.runAsAdmin
        ? "Saving approves exactly this command. Each run asks Windows for administrator rights first, never from a shell."
        : "Saving approves exactly this command. It runs as you, with a time limit, never from a shell.";
    pathRow.hidden = isCode;
    codeRow.hidden = !isCode;
    code.rows = bashCode ? 2 : 8;
    code.placeholder = { powershell: 'Get-Date\n"Hello from PowerShell"', python: 'import datetime\nprint(datetime.datetime.now())', bash: "date && uptime" }[d.kind] || "";
    $(".flabel", pathRow).textContent = program ? "Program" : "Script file";
    path.placeholder = { powershell: "C:\\Scripts\\backup.ps1", python: "C:\\Scripts\\report.py", bash: "~/bin/sync.sh", program: "C:\\Tools\\app.exe or pwsh" }[d.kind];
    const hint = $(".hint-text", codeRow) || codeRow.appendChild(mk("span", "hint-text"));
    hint.textContent = bashCode ? "One line, run with bash -c. For a longer script, save a .sh file and pick it." : "Saved by Umbra and run as a file.";
  }
  sync();
  if (scriptFocus === w.id) { scriptFocus = null; requestAnimationFrame(() => title.focus()); }
  return form;
}

// Arrange widgets (edit mode) is the one global lock (#234). The host owns it (general.lockAll), so it has its own message.
$("#safeOff").addEventListener("click", () => post({ type: "setSafeMode", value: false }));
$("#editMode").addEventListener("change", (e) => {
  post({ type: "setEditMode", value: e.target.checked });
  showArrange(e.target.checked);
  toast(e.target.checked ? "Widgets unlocked: drag to move, drag the corner or Ctrl+scroll to resize" : "Widgets locked");
});
function showArrange(on) {
  $("#editMode").checked = on;
  $("#arrangeBox").classList.toggle("on", on);
  $("#arrangeState").textContent = on ? "Unlocked: drag, resize, then turn off" : "Locked";
  const ico = $(".arrange-ico");
  if (ico.dataset.icon !== (on ? "unlock" : "lock")) { ico.dataset.icon = on ? "unlock" : "lock"; ico.textContent = ""; icons(ico.parentElement); }
  renderHome();
}

// ---------- Layouts ----------
// ---------- Groups (#235) ----------
let selectMode = false;
const selected = new Set();
const foldedGroups = () => { try { return new Set(JSON.parse(localStorage.getItem("foldedGroups") || "[]")); } catch { return new Set(); } };
const setGroupFolded = (id, folded) => {
  try {
    const set = foldedGroups();
    if (folded) set.add(id); else set.delete(id);
    localStorage.setItem("foldedGroups", JSON.stringify([...set]));
  } catch { /* storage unavailable */ }
};
const widgetLabel = (inst) => (state.widgets.find((x) => x.id === inst) || { name: inst }).name;

// Dropping a card on a group chip or header adds it to that group.
function dropTarget(el, g) {
  el.addEventListener("dragover", (e) => { e.preventDefault(); e.dataTransfer.dropEffect = "move"; el.classList.add("over"); });
  el.addEventListener("dragleave", () => el.classList.remove("over"));
  el.addEventListener("drop", (e) => {
    e.preventDefault();
    el.classList.remove("over");
    const id = e.dataTransfer.getData("text/plain");
    if (id && state.widgets.some((w) => w.id === id)) { post({ type: "group", op: "join", group: g.id, id }); toast(`Added ${widgetLabel(id)} to ${g.name}`); }
  });
}

// The strip at the top of My widgets: one chip per group (a drop target) plus New group and multi-select.
function renderGroups() {
  const chips = $("#groupChips");
  chips.replaceChildren();
  for (const g of state.groups || []) {
    const chip = Object.assign(document.createElement("button"), { className: "gchip", type: "button" });
    chip.append(g.name, Object.assign(document.createElement("span"), { className: "gcount", textContent: String(g.members.length) }));
    chip.title = `Go to ${g.name}`;
    chip.addEventListener("click", () => $$("#installed .ghead").find((h) => h.dataset.group === g.id)?.scrollIntoView({ block: "center" }));
    dropTarget(chip, g);
    chips.append(chip);
  }
  $("#selectBtn").setAttribute("aria-pressed", String(selectMode));
  $("#selectBtn").textContent = selectMode ? "Done selecting" : "Select widgets";
  const hasGroups = (state.groups || []).length > 0;
  $("#selectBtn").hidden = !hasGroups && !selectMode; // it only adds to a group, so it waits for the first one
  $("#groupHint").textContent = hasGroups ? "Add widgets with a card's badge, by dragging a card onto a group, or with Select widgets."
    : "Stack widgets in one spot and switch between them with tabs.";
  for (const id of [...selected]) if (!state.widgets.some((w) => w.id === id)) selected.delete(id);
  $("#bulkBar").hidden = !selectMode;
  $("#bulkCount").textContent = `${selected.size} selected`;
  const pick = $("#bulkGroup");
  const keep = pick.value;
  pick.replaceChildren(...(state.groups || []).map((g) => new Option(g.name, g.id)));
  if ((state.groups || []).some((g) => g.id === keep)) pick.value = keep;
  $("#bulkAdd").disabled = !selected.size || !(state.groups || []).length;
  $("#newGroupBtn").disabled = (state.groups || []).length >= 20;
  icons($("#groupStrip"));
}

$("#mySearch").addEventListener("input", (e) => {
  myFilter = e.target.value;
  const keep = e.target;
  renderInstalled();
  keep.focus();
});
$("#goBrowseMore").addEventListener("click", () => show("browse"));

$("#newGroupBtn").addEventListener("click", () => {
  const form = $("#newGroupForm");
  form.hidden = !form.hidden;
  $("#newGroupBtn").setAttribute("aria-expanded", String(!form.hidden));
  if (!form.hidden) $("#newGroupName").focus();
});
$("#newGroupCancel").addEventListener("click", () => {
  $("#newGroupForm").hidden = true;
  $("#newGroupBtn").setAttribute("aria-expanded", "false");
  $("#newGroupBtn").focus();
});
$("#newGroupForm").addEventListener("submit", (e) => {
  e.preventDefault();
  const name = $("#newGroupName").value.trim();
  if (!name) { $("#newGroupName").focus(); return; }
  post({ type: "group", op: "create", name }); // empty first: widgets are added afterwards
  $("#newGroupName").value = "";
  $("#newGroupForm").hidden = true;
  $("#newGroupBtn").setAttribute("aria-expanded", "false");
  toast(`Group ${name} created. Add widgets with a card's badge or by dragging.`);
});
$("#selectBtn").addEventListener("click", () => {
  selectMode = !selectMode;
  if (!selectMode) selected.clear();
  renderInstalled();
  renderGroups();
});
$("#bulkAdd").addEventListener("click", () => {
  const group = $("#bulkGroup").value;
  if (!group || !selected.size) return;
  for (const id of selected) post({ type: "group", op: "join", group, id });
  toast(`Added ${selected.size} widget${selected.size === 1 ? "" : "s"} to ${((state.groups || []).find((g) => g.id === group) || {}).name}`);
  selected.clear();
});

// Badge menu: move a widget to another group, remove it from its group, or start a new one.
function openGroupMenu(anchor, w, here) {
  const items = (state.groups || []).map((g) => ({ text: g.name, run: () => post({ type: "group", op: "join", group: g.id, id: w.id }), on: !!here && here.id === g.id }));
  if (here) items.push({ text: "Remove from group", run: () => post({ type: "group", op: "leave", id: w.id }) });
  items.push({ text: "New group...", run: () => {
    $("#newGroupForm").hidden = false;
    $("#newGroupBtn").setAttribute("aria-expanded", "true");
    $("#newGroupName").focus();
    $("#newGroupName").scrollIntoView({ block: "center" });
  } });
  popupMenu(anchor, items);
}

// A small keyboard-accessible menu under (or, with `up`, above) a button: arrows move, Enter picks, Esc and outside clicks close.
function popupMenu(anchor, items, { up = false } = {}) {
  document.querySelector(".gmenu")?.remove();
  const menu = document.createElement("div");
  menu.className = "gmenu" + (up ? " up" : "");
  menu.setAttribute("role", "menu");
  if (up) menu.style.left = anchor.offsetLeft + "px";
  anchor.setAttribute("aria-expanded", "true");
  const close = (refocus) => {
    menu.remove();
    document.removeEventListener("mousedown", away, true);
    anchor.setAttribute("aria-expanded", "false");
    if (refocus) anchor.focus();
  };
  const away = (e) => { if (!menu.contains(e.target) && !anchor.contains(e.target)) close(false); };
  for (const { text, run, on } of items) {
    const b = Object.assign(document.createElement("button"), { type: "button", textContent: text });
    b.setAttribute("role", "menuitem");
    if (on) b.classList.add("on");
    b.addEventListener("click", () => { close(false); run(); });
    menu.append(b);
  }
  menu.addEventListener("keydown", (e) => {
    const list = $$("button", menu);
    const i = list.indexOf(document.activeElement);
    if (e.key === "Escape") { e.preventDefault(); e.stopPropagation(); close(true); }
    else if (e.key === "ArrowDown") { e.preventDefault(); list[(i + 1) % list.length].focus(); }
    else if (e.key === "ArrowUp") { e.preventDefault(); list[(i - 1 + list.length) % list.length].focus(); }
  });
  anchor.after(menu);
  document.addEventListener("mousedown", away, true);
  menu.querySelector("button")?.focus();
}

// A group's header in the list: fold, rename, tabs (activate, reorder, icon, remove), cycle and delete.
function groupHeader(g) {
  const folded = foldedGroups().has(g.id);
  const head = document.createElement("section");
  head.className = "ghead" + (folded ? " folded" : "");
  head.dataset.group = g.id;
  head.setAttribute("aria-label", `Group ${g.name}`);
  const top = document.createElement("div");
  top.className = "ghead-top";
  const fold = Object.assign(document.createElement("button"), { className: "fold", type: "button" });
  fold.innerHTML = '<span class="ico" data-icon="chevron"></span>';
  fold.setAttribute("aria-expanded", String(!folded));
  fold.setAttribute("aria-label", `${folded ? "Show" : "Hide"} widgets of ${g.name}`);
  fold.addEventListener("click", () => { setGroupFolded(g.id, !folded); renderInstalled(); });
  const name = document.createElement("input");
  name.className = "input name";
  name.value = g.name;
  name.maxLength = 40;
  name.spellcheck = false;
  name.setAttribute("aria-label", "Group name");
  name.addEventListener("change", () => post({ type: "group", op: "configure", group: g.id, name: name.value }));
  name.addEventListener("keydown", (e) => { if (e.key === "Enter") name.blur(); });
  const count = Object.assign(document.createElement("span"), { className: "gcount", textContent: `${g.members.length} widget${g.members.length === 1 ? "" : "s"}` });
  const cycle = document.createElement("input");
  cycle.className = "input";
  cycle.type = "number";
  cycle.min = 0;
  cycle.max = 3600;
  cycle.value = g.cycleSeconds || "";
  cycle.placeholder = "Cycle (s)";
  cycle.title = "Switch to the next tab every this many seconds (5-3600); empty = off";
  cycle.setAttribute("aria-label", "Cycle every seconds");
  cycle.style.width = "92px";
  cycle.addEventListener("change", () => post({ type: "group", op: "configure", group: g.id, cycleSeconds: Math.max(0, Math.round(Number(cycle.value) || 0)) }));
  const del = document.createElement("button");
  del.className = "btn small danger";
  del.type = "button";
  del.title = "Delete group (the widgets stay)";
  del.setAttribute("aria-label", `Delete group ${g.name}`);
  del.innerHTML = '<span class="ico" data-icon="trash"></span>';
  del.addEventListener("click", () => post({ type: "group", op: "delete", group: g.id }));
  top.append(fold, name, count, cycle, del);
  head.append(top);

  if (!g.members.length) {
    head.append(Object.assign(document.createElement("div"), { className: "ghead-empty", textContent: "Empty. Drop a widget card here, or use a card's badge." }));
  } else if (!folded) {
    const tabs = document.createElement("div");
    tabs.className = "tabchips";
    g.members.forEach((inst, i) => {
      const chip = document.createElement("span");
      chip.className = "tabchip" + (inst === g.active ? " on" : "");
      const pick = Object.assign(document.createElement("button"), { className: "tabpick", textContent: widgetLabel(inst) });
      pick.title = "Show this tab";
      pick.addEventListener("click", () => post({ type: "group", op: "activate", group: g.id, id: inst }));
      const ic = Object.assign(document.createElement("button"), { className: "tabicon", title: "Choose tab icon" });
      ic.setAttribute("aria-label", `Choose tab icon for ${widgetLabel(inst)}`);
      const curIcon = (g.icons || {})[inst] || (g.suggestedIcons || {})[inst]; // the user's pick wins over the widget's suggestion
      if (curIcon) ic.append(Object.assign(document.createElement("img"), { src: ICON_BASE + curIcon + ".png", alt: "" }));
      else ic.textContent = "◇";
      ic.addEventListener("click", () => openIconPicker(inst, ic));
      chip.append(ic, pick);
      if (i > 0) {
        const left = Object.assign(document.createElement("button"), { textContent: "‹", title: "Move earlier" });
        left.setAttribute("aria-label", `Move ${widgetLabel(inst)} earlier`);
        left.addEventListener("click", () => post({ type: "group", op: "reorder", group: g.id, id: inst, index: i - 1 }));
        chip.append(left);
      }
      const out = Object.assign(document.createElement("button"), { textContent: "×", title: "Remove from group" });
      out.setAttribute("aria-label", `Remove ${widgetLabel(inst)} from group`);
      out.addEventListener("click", () => post({ type: "group", op: "leave", id: inst }));
      chip.append(out);
      tabs.append(chip);
    });
    head.append(tabs);
  }
  dropTarget(head, g);
  icons(head);
  return head;
}

// ---------- Tab icon picker ----------
const ICON_BASE = "https://umbra.local/shared/tabicons/";
let iconIndex = null;

async function openIconPicker(inst, anchor) {
  document.querySelector(".iconpop")?.remove();
  if (!iconIndex) {
    try { iconIndex = await (await fetch(ICON_BASE + "index.json")).json(); }
    catch { toast("Could not load the icon list", true); return; }
  }
  const pop = document.createElement("div");
  pop.className = "iconpop";
  const search = Object.assign(document.createElement("input"), { className: "input", type: "search", placeholder: "Search icons" });
  search.setAttribute("aria-label", "Search icons");
  const grid = document.createElement("div");
  grid.className = "icongrid";
  const close = () => { pop.remove(); document.removeEventListener("mousedown", away, true); };
  const away = (e) => { if (!pop.contains(e.target) && e.target !== anchor) close(); };
  const choose = (name) => { post({ type: "group", op: "icon", id: inst, icon: name }); close(); };
  const fill = () => {
    const q = search.value.trim().toLowerCase();
    grid.replaceChildren();
    const none = Object.assign(document.createElement("button"), { className: "iconcell wide", textContent: "Default" });
    none.addEventListener("click", () => choose(""));
    grid.append(none);
    for (const it of iconIndex) {
      if (q && !it.name.includes(q) && !it.tags.some((t) => t.includes(q))) continue;
      const b = document.createElement("button");
      b.className = "iconcell";
      b.title = it.name;
      b.setAttribute("aria-label", it.name);
      b.append(Object.assign(document.createElement("img"), { src: ICON_BASE + it.name + ".png", alt: "", loading: "lazy" }));
      b.addEventListener("click", () => choose(it.name));
      grid.append(b);
    }
  };
  search.addEventListener("input", fill);
  pop.addEventListener("keydown", (e) => { if (e.key === "Escape") close(); });
  fill();
  pop.append(search, grid);
  document.body.append(pop);
  const r = anchor.getBoundingClientRect();
  pop.style.left = Math.max(8, Math.min(r.left, innerWidth - pop.offsetWidth - 8)) + "px";
  pop.style.top = Math.max(8, Math.min(r.bottom + 6, innerHeight - pop.offsetHeight - 8)) + "px";
  document.addEventListener("mousedown", away, true);
  search.focus();
}

function renderLayouts() {
  $("#autoLayouts").checked = !!state.settings.general.autoSwitchLayouts;
  const list = $("#layoutList");
  list.replaceChildren();
  for (const l of state.layouts || []) {
    const row = document.createElement("div");
    row.className = "row";
    const text = document.createElement("div");
    text.className = "row-text";
    const name = document.createElement("input");
    name.className = "input name";
    name.value = l.name;
    name.maxLength = 40;
    name.spellcheck = false;
    name.setAttribute("aria-label", "Layout name");
    const rename = () => {
      const to = name.value.trim();
      if (!to || to === l.name) { name.value = l.name; return; }
      post({ type: "renameLayout", name: l.name, to });
    };
    name.addEventListener("change", rename);
    name.addEventListener("keydown", (e) => {
      if (e.key === "Enter") name.blur();
      if (e.key === "Escape") { name.value = l.name; name.blur(); }
    });
    const sub = document.createElement("div");
    sub.className = "row-sub";
    const pin = !l.pinned ? "not pinned to a monitor setup"
      : l.matchesNow ? "pinned to the monitors connected now"
      : `pinned to a setup with ${l.pinnedMonitors} monitor${l.pinnedMonitors === 1 ? "" : "s"}`;
    sub.textContent = `${l.enabled} of ${l.widgets} widgets on \u00b7 ${pin}`;
    text.append(name, sub);

    const apply = Object.assign(document.createElement("button"), { className: "btn small", textContent: "Apply" });
    apply.addEventListener("click", () => post({ type: "applyLayout", name: l.name })); // the host answers with a notice or a commandError
    const pinBtn = Object.assign(document.createElement("button"), {
      className: "btn small ghost",
      textContent: l.matchesNow ? "Unpin" : "Pin to these monitors",
      title: l.matchesNow ? "Stop applying this layout automatically" : "Apply this layout automatically when exactly the monitors connected now are in use",
    });
    pinBtn.addEventListener("click", () => post({ type: "pinLayout", name: l.name, pinned: !l.matchesNow }));
    const del = document.createElement("button");
    del.className = "btn small danger";
    del.title = "Delete layout";
    del.innerHTML = '<span class="ico" data-icon="trash"></span>';
    del.addEventListener("click", () => post({ type: "deleteLayout", name: l.name })); // the host answers with layoutDeleted or a commandError
    row.append(text, apply, pinBtn, del);
    list.append(row);
  }
  icons(list);
}
$("#autoLayouts").addEventListener("change", (e) => {
  state.settings.general.autoSwitchLayouts = e.target.checked;
  save(true);
});
$("#layoutSave").addEventListener("click", () => {
  const name = $("#layoutName").value.trim();
  if (!name) { $("#layoutName").focus(); return; }
  post({ type: "saveLayout", name });
  $("#layoutName").value = "";
  toast(`Saved layout ${name}`);
});
$("#layoutName").addEventListener("keydown", (e) => { if (e.key === "Enter") $("#layoutSave").click(); });

// ---------- Browse ----------
function loadCatalog() {
  catalog = null;
  catalogError = null;
  catalogLoading = true;
  renderCatalog();
  post({ type: "catalog" });
}
$("#reloadCatalog").addEventListener("click", loadCatalog);
$("#search").addEventListener("input", renderCatalog);
$$("#browseFilter button").forEach((b) => b.addEventListener("click", () => { browseFilter = b.dataset.filter; renderCatalog(); }));
$("#updateAll").addEventListener("click", () => {
  const ids = (catalog || []).filter((w) => w.updateAvailable).map((w) => w.id);
  if (!ids.length || updatingAll) return;
  updatingAll = { ids, index: 0, total: ids.length };
  for (const id of ids) busy.set(id, "install");
  renderCatalog();
  post({ type: "updateAll" });
});

// Filter chips, the Browse badge in the sidebar and the Update all button, all derived from the catalog the host last sent
// (the host re-sends it after any install/remove, wherever it was started from, so the counts stay right).
function renderBrowseBar() {
  const n = (catalog || []).filter((w) => w.updateAvailable).length;
  $("#updatesCount").textContent = n ? ` (${n})` : "";
  $("#browseCount").textContent = n || "";
  $$("#browseFilter button").forEach((b) => b.classList.toggle("on", b.dataset.filter === browseFilter));
  const all = $("#updateAll");
  all.hidden = !n && !updatingAll;
  all.disabled = !!updatingAll;
  all.lastChild.textContent = updatingAll ? "Updating..." : n === 1 ? "Update 1 widget" : `Update all (${n})`;
  $("#updateStatus").textContent = updatingAll && updatingAll.index ? `Updating ${updatingAll.index} of ${updatingAll.total}...` : "";
}

const expandedCards = new Set(); // catalog ids whose description and commands are shown in full

function catalogThumb(card, w) {
  const th = $(".cthumb", card);
  $(".initial", card).textContent = (w.name || "?").trim()[0];
  if (!w.previewUrl) return;
  const img = new Image();
  img.onload = () => { th.style.backgroundImage = `url("${w.previewUrl}")`; th.classList.add("has-img"); };
  img.src = w.previewUrl;
}

// What a catalog widget can reach and use, one chip each. Commands are drawn by renderCatalog; Home never offers those.
function permissionChips(chips, w) {
  const chip = (text) => {
    const c = document.createElement("span");
    c.className = "chip";
    c.innerHTML = '<span class="ico" data-icon="lock"></span>';
    c.append(text);
    chips.append(c);
  };
  const hosts = w.hosts || [];
  if (!hosts.length) chip("No network access");
  hosts.forEach(chip);
  for (const k of w.media || []) chip(k === "control" ? "Controls media playback" : "Reads now playing");
  if (w.notifications) chip("Can show notifications");
  if (w.camera) chip("Uses your camera (timelapses)");
  if (w.userScripts) chip("Runs scripts you add (Settings)");
  for (const s of w.system || []) chip(`Reads ${s} usage`);
}

function renderCatalog() {
  const grid = $("#catalog");
  grid.textContent = "";
  renderBrowseBar();
  if (catalogError) {
    grid.innerHTML = `<div class="empty" style="grid-column:1/-1">Couldn't reach the community catalog.<br><small>${escapeHtml(catalogError)}</small></div>`;
    return;
  }
  if (!catalog) {
    for (let i = 0; i < 3; i++) grid.append(Object.assign(document.createElement("div"), { className: "skeleton" }));
    return;
  }
  const q = $("#search").value.trim().toLowerCase();
  const inFilter = (w) => browseFilter === "updates" ? w.updateAvailable : browseFilter === "installed" ? w.installed : true;
  const items = catalog.filter((w) => inFilter(w) && (!q || [w.name, w.description, w.author, w.id].join(" ").toLowerCase().includes(q)));
  if (!items.length) {
    const none = q ? "No widgets match your search."
      : browseFilter === "updates" ? "Everything is up to date."
      : browseFilter === "installed" ? "No community widgets installed yet."
      : "The catalog is empty. Be the first to submit one!";
    grid.innerHTML = `<div class="empty" style="grid-column:1/-1">${none}</div>`;
    return;
  }
  for (const w of items) {
    const card = $("#tpl-catalog").content.firstElementChild.cloneNode(true);
    catalogThumb(card, w);
    $(".cname", card).textContent = w.name;
    $(".cver", card).textContent = `v${w.version}`;
    $(".cauthor", card).textContent = w.author ? `by ${w.author}` : "";
    $(".cdesc", card).textContent = w.description || "";
    permissionChips($(".chips", card), w);
    // A widget that can start programs on this PC is flagged with the exact commands (you approve each one on first use).
    // Collapsed cards show one summary chip; "Read more" lists every command in full.
    const chips = $(".chips", card);
    const cmds = w.commands || [];
    const open = expandedCards.has(w.id);
    // Only a lot of command text is folded away; one short command stays visible as it is.
    const longCmds = cmds.length > 1 || cmds.some((c) => (c.run || "").length > 60);
    if (longCmds && !open) {
      const c = document.createElement("span");
      c.className = "chip warn";
      c.innerHTML = '<span class="ico" data-icon="alert"></span>';
      c.append(cmds.length === 1 ? "Runs 1 program (asks first)" : `Runs ${cmds.length} programs (asks first)`);
      chips.append(c);
    } else {
      for (const cmd of cmds) {
        const c = document.createElement("span");
        c.className = "chip warn";
        c.title = `${cmd.description || ""}\nAsks permission before it runs.`.trim();
        c.innerHTML = '<span class="ico" data-icon="alert"></span>';
        c.append(`Runs: ${cmd.run}`);
        chips.append(c);
      }
    }
    const desc = $(".cdesc", card);
    desc.classList.toggle("open", open);
    const more = $(".cmore", card);
    more.setAttribute("aria-expanded", String(open));
    more.textContent = open ? "Show less" : "Read more…";
    more.addEventListener("click", () => {
      if (expandedCards.has(w.id)) expandedCards.delete(w.id); else expandedCards.add(w.id);
      renderCatalog();
    });
    // Shown only for a lot of text: commands that were folded away, an open card, or a description cut off by the line clamp (checked once the card is in the page).
    more.hidden = !(longCmds || open);
    const checkClamp = () => { if (!open && !longCmds) more.hidden = desc.scrollHeight <= desc.clientHeight + 1; };

    card.setAttribute("aria-label", w.name);
    const btn = $(".action", card);
    if (busy.has(w.id)) {
      btn.disabled = true;
      btn.innerHTML = '<span class="spin"></span>Working';
    } else if (w.builtin) {
      btn.classList.add("done");
      btn.innerHTML = '<span class="ico" data-icon="check"></span>Built in';
    } else if (w.updateAvailable) {
      btn.classList.add("primary");
      btn.innerHTML = `<span class="ico" data-icon="download"></span>Update to v${escapeHtml(w.version)}`;
      btn.addEventListener("click", () => job(w.id, "install", btn));
    } else if (w.installed) {
      btn.classList.add("danger");
      btn.innerHTML = '<span class="ico" data-icon="trash"></span>Remove';
      btn.addEventListener("click", () => job(w.id, "remove", btn));
    } else if (w.incompatible) {
      // Made for a newer Umbra (manifestVersion / minUmbraVersion): greyed out with the reason.
      btn.disabled = true;
      btn.textContent = "Needs a newer Umbra";
      btn.title = w.incompatible;
    } else {
      btn.classList.add("primary");
      btn.innerHTML = '<span class="ico" data-icon="download"></span>Install';
      btn.addEventListener("click", () => job(w.id, "install", btn));
    }
    btn.setAttribute("aria-label", `${btn.textContent.trim()} ${w.name}`);
    icons(card);
    grid.append(card);
    checkClamp();
  }
}

// ---------- Themes ----------
// The host sends the active theme as one generated :root rule (validated tokens only); apply it to this page too.
function applyTheme(css) {
  let el = document.getElementById("umbra-theme");
  if (!css) el && el.remove();
  else {
    if (!el) { el = document.createElement("style"); el.id = "umbra-theme"; document.head.append(el); }
    if (el.textContent !== css) el.textContent = css;
  }
  // umbra.css says color-scheme: dark; a light theme flips it so scrollbars, selects and date fields match.
  // Same test as applyScheme in shared/umbra.js: keep the two in step.
  const m = /rgba?\((\d+), (\d+), (\d+)/.exec(getComputedStyle(document.documentElement).backgroundColor);
  document.documentElement.style.colorScheme = m && 0.2126 * m[1] + 0.7152 * m[2] + 0.0722 * m[3] > 140 ? "light" : "";
}

// The accent from Appearance (a picked color, or the Windows accent) is one more :root rule, applied after the theme.
function applyAccent(css) {
  let el = document.getElementById("umbra-accent");
  if (!css) { el && el.remove(); return; }
  if (!el) { el = document.createElement("style"); el.id = "umbra-accent"; document.head.append(el); }
  if (el.textContent !== css) el.textContent = css;
}

function loadThemes() {
  themes = null;
  themesError = null;
  renderThemes();
  post({ type: "themes" });
}
$("#reloadThemes").addEventListener("click", loadThemes);

function renderThemes() {
  const grid = $("#themeGrid");
  grid.textContent = "";
  if (!themes) {
    for (let i = 0; i < 3; i++) grid.append(Object.assign(document.createElement("div"), { className: "skeleton" }));
    return;
  }
  const active = (state && state.settings.general.theme) || "default";
  if (themesError) grid.append(Object.assign(document.createElement("div"), {
    className: "empty", textContent: `Couldn't reach the theme catalog (${themesError}). Installed themes still work.`,
  }));
  for (const t of themes) {
    const card = $("#tpl-theme").content.firstElementChild.cloneNode(true);
    const tk = t.tokens || {};
    // Swatch colors come from validated tokens (host side); the fallbacks are the default look.
    const sw = $(".swatch", card);
    for (const [prop, token] of [["--t-bg", "--bg"], ["--t-surface", "--surface-2"], ["--t-line", "--line-strong"], ["--t-text", "--text"], ["--t-text2", "--text-3"], ["--t-accent", "--accent"], ["--t-radius", "--radius-m"]])
      if (tk[token]) sw.style.setProperty(prop, tk[token]);
    $(".cname", card).textContent = t.name;
    $(".cver", card).textContent = t.builtin ? "" : `v${t.version}`;
    $(".cauthor", card).textContent = t.author ? `by ${t.author}` : "";
    $(".cdesc", card).textContent = t.description || "";
    card.classList.toggle("active", t.id === active);

    card.setAttribute("aria-label", t.name);
    const btn = $(".action", card);
    const remove = $(".remove", card);
    remove.setAttribute("aria-label", `Remove theme ${t.name}`);
    if (!t.installed || t.builtin || t.id === "") remove.remove();
    else remove.addEventListener("click", () => job(t.id, "removeTheme", remove));
    if (busy.has(t.id)) {
      btn.disabled = true;
      btn.innerHTML = '<span class="spin"></span>Working';
    } else if (!t.installed) {
      btn.classList.add("primary");
      btn.innerHTML = '<span class="ico" data-icon="download"></span>Install';
      btn.addEventListener("click", () => job(t.id, "installTheme", btn));
    } else if (t.updateAvailable) {
      btn.classList.add("primary");
      btn.innerHTML = `<span class="ico" data-icon="download"></span>Update to v${escapeHtml(t.version)}`;
      btn.addEventListener("click", () => job(t.id, "installTheme", btn));
    } else if (t.id === active) {
      btn.classList.add("done");
      btn.innerHTML = '<span class="ico" data-icon="check"></span>In use';
    } else {
      btn.classList.add("primary");
      btn.textContent = "Use theme";
      btn.addEventListener("click", () => { post({ type: "setTheme", id: t.id }); toast(`${t.name} applied`); });
    }
    btn.setAttribute("aria-label", `${btn.textContent.trim()} ${t.name}`);
    icons(card);
    grid.append(card);
  }
}

function job(id, type, btn) {
  busy.set(id, type);
  if (btn) { btn.disabled = true; btn.setAttribute("aria-busy", "true"); btn.innerHTML = '<span class="spin"></span>Working'; }
  post({ type, id });
}

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
}

// ---------- messages from the host ----------
bridge && bridge.listen((m) => {
  switch (m.type) {
    case "state":
      state = m;
      $("#ver").textContent = `Version ${m.version}`;
      $("#aboutVer").textContent = `Version ${m.version}`;
      applyTheme(m.themeCss);
      applyAccent(m.accentCss);
      renderGeneral();
      if (!catalog && !catalogLoading && !catalogError && m.settings.general.checkForUpdates !== false) loadCatalog();
      if (!recording) renderHotkeys();
      if (themes) renderThemes();
      renderUpdate();
      showArrange(!!m.editMode);
      $("#safeBanner").hidden = !m.safeMode;
      // Same for the layout names: don't rebuild while one is being renamed.
      // Only a layout name being edited blocks the redraw; a clicked button (Apply, Pin, Delete) must see the new state.
      { const a = document.activeElement; if (!(a && a.tagName === "INPUT" && $("#layoutList").contains(a))) renderLayouts(); }
      // Don't rebuild the widget list while the user is typing in it.
      if (!document.activeElement || !$("#installed").contains(document.activeElement)) renderInstalled();
      renderGroups();
      renderHome();
      break;
    case "privacy":
      renderPrivacy(m.widgets);
      break;
    case "notice":
      toast(m.text);
      break;
    case "zoom":
      $("#zoomReset").textContent = `${m.percent}%`;
      $("#zoomOut").disabled = !!m.min;
      $("#zoomIn").disabled = !!m.max;
      break;
    case "scriptSaved": // the state that follows shows the saved script; close its editor on every copy's card
      for (const [k, d] of scriptDrafts) if (d.widget === m.widget) scriptDrafts.delete(k);
      break;
    case "scriptFile": { // the host's file picker answered for this copy's editor
      const d = scriptDrafts.get(m.ref);
      if (d && typeof m.path === "string") {
        d.path = m.path;
        // A shortcut (.lnk) comes back as what it opens: its kind, arguments and Start in folder.
        if (typeof m.kind === "string" && SCRIPT_KINDS.some(([k]) => k === m.kind)) { d.kind = m.kind; d.mode = "file"; }
        if (typeof m.args === "string") d.args = m.args;
        if (typeof m.startIn === "string" && m.startIn) d.startIn = m.startIn;
        // A picked script usually expects to start in its own folder, like a double-click does.
        else if (!d.startIn && /^[A-Za-z]:\\/.test(m.path) && m.path.includes("\\", 3)) d.startIn = m.path.slice(0, m.path.lastIndexOf("\\"));
        document.activeElement?.blur();
        renderInstalled();
      }
      break;
    }
    case "scriptFolder": { // the host's folder picker answered for an editor's Start in
      const d = scriptDrafts.get(m.ref);
      if (d && typeof m.path === "string") { d.startIn = m.path; document.activeElement?.blur(); renderInstalled(); }
      break;
    }
    case "layoutDeleted":
      toast(`Deleted layout ${m.name}`, false, { label: "Undo", run: () => post({ type: "undoDeleteLayout" }) });
      break;
    case "commandError":
      toast(m.error, true);
      break;
    case "updateError":
      toast(`Update failed: ${m.error}`, true);
      renderUpdate();
      break;
    case "catalog":
      catalogLoading = false;
      catalogError = m.error;
      catalog = m.error && !m.widgets.length ? null : m.widgets;
      renderCatalog();
      renderHome();
      break;
    case "themes":
      themesError = m.error;
      themes = m.error && !m.themes.length ? null : m.themes;
      renderThemes();
      break;
    case "updateProgress":
      if (updatingAll) { updatingAll.index = m.index; renderBrowseBar(); }
      break;
    case "updateAllDone": {
      for (const id of updatingAll ? updatingAll.ids : []) busy.delete(id);
      updatingAll = null;
      const r = m.result || { updated: [], failed: [] };
      if (m.error) toast(`Update all failed: ${m.error}`, true);
      else if (r.failed.length) toast(`Updated ${r.updated.length}, failed: ${r.failed.map((f) => `${f.id} (${f.error})`).join("; ")}`, true);
      else toast(r.updated.length ? `Updated ${r.updated.length} widget${r.updated.length === 1 ? "" : "s"}` : "Everything is up to date");
      renderCatalog();
      break;
    }
    case "jobDone": {
      const kind = busy.get(m.id);
      busy.delete(m.id);
      const isTheme = kind === "installTheme" || kind === "removeTheme";
      const name = ((isTheme ? themes : catalog) || []).find((w) => w.id === m.id)?.name || m.id;
      if (m.error) toast(`${name}: ${m.error}`, true);
      else if (kind === "remove" && m.undo) toast(`${name} removed`, false, { label: "Undo", run: () => post({ type: "undoRemove", id: m.undo }) });
      else toast(kind === "remove" || kind === "removeTheme" ? `${name} removed` : `${name} installed`);
      break;
    }
    case "undoDone":
      m.error ? toast(m.error, true) : toast(`${m.name || "Widget"} restored`);
      break;
  }
});

// About logo = the sidebar logo
$(".about-logo").replaceWith(Object.assign($(".logo").cloneNode(true), { classList: "about-logo" }));
function renderPrivacy(list) {
  const root = $("#privacyList");
  root.textContent = "";
  const fmt = (b) => (b < 1024 ? `${b} B` : `${(b / 1024).toFixed(1)} KB`);
  for (const w of list) {
    const row = document.createElement("div");
    row.className = "row";
    const text = document.createElement("div");
    text.className = "row-text";
    const label = w.name || w.widget; // display name, like everywhere else; the id stays as a muted suffix (#286)
    const title = Object.assign(document.createElement("div"), { className: "row-title", textContent: label });
    title.append(Object.assign(document.createElement("span"), { className: "muted-id", textContent: ` ${w.widget}` }));
    const sub = (t) => Object.assign(document.createElement("div"), { className: "row-sub", textContent: t });
    text.append(
      title,
      sub(w.hosts.length ? `Can reach: ${w.hosts.join(", ")}` : "Can reach no internet hosts"),
      ...(w.camera?.declared ? [sub(`Camera: timelapses (${w.camera.approved ? "approved" : "not approved yet"})`)] : []),
      sub(w.commands.length ? "Commands: " + w.commands.map((c) => `${c.line} (${c.status})`).join("; ") : "Runs no commands"),
      sub(`Saved data: ${fmt(w.data.bytes)}` + (w.blocked.count ? ` \u00b7 ${w.blocked.count} blocked request(s), latest ${w.blocked.recent[w.blocked.recent.length - 1]}` : "")));
    if (w.incompatible) text.append(sub(w.incompatible));
    if (w.crashes?.stopped) text.append(sub("Stopped working: its page kept crashing, so Umbra stopped reloading it. Use Reload on the widget."));
    else if (w.crashes?.count) text.append(sub(`Crashed ${w.crashes.count} time(s) in the recent log (see umbra.log); Umbra reloaded it.`));
    const del = document.createElement("button");
    del.className = "btn small danger";
    del.textContent = "Delete data";
    del.setAttribute("aria-label", `Delete saved data of ${label}`);
    del.disabled = !w.data.bytes;
    del.title = w.data.bytes ? "" : "Nothing saved";
    del.addEventListener("click", () => post({ type: "deleteData", widget: w.widget }));
    row.append(text, del);
    root.append(row);
  }
  if (!list.length) root.append(Object.assign(document.createElement("div"), { className: "row", textContent: "No widgets installed." }));
}
$("#exportBackup").addEventListener("click", () => post({ type: "exportBackup" }));
$("#importBackup").addEventListener("click", () => post({ type: "importBackup" }));
// The host shows the picker and the access prompt; the page never passes a path.
$("#installMenu").addEventListener("click", (e) => {
  const btn = e.currentTarget;
  if (btn.getAttribute("aria-expanded") === "true") { document.querySelector(".gmenu")?.remove(); btn.setAttribute("aria-expanded", "false"); return; }
  popupMenu(btn, [
    { text: "File (.zip or .umbrawidget)…", run: () => post({ type: "installLocal", kind: "file" }) },
    { text: "Folder…", run: () => post({ type: "installLocal", kind: "folder" }) },
    { text: "URL…", run: () => post({ type: "installUrl" }) },
  ], { up: true });
});
$("#openDataFolder").addEventListener("click", () => post({ type: "openDataFolder" }));
$("#openLogBtn").addEventListener("click", () => post({ type: "openLog" }));
$("#resetAll").addEventListener("click", () => post({ type: "resetAll" }));

// ---------- Home ----------
// Hand-picked first widgets. Home only offers ones that can't run programs or use the camera, shows what each can
// reach, and installs through the same job as Browse. The catalog arrives on its own (not at all when update checks are off).
const HOME_PICKS = ["clock", "weather", "quick-notes", "now-playing", "todo"];
const HOME_MINE_MAX = 12;
let homeMineKey = null, homePicksKey = null; // rebuilt only on a change: the host pushes state often, and a rebuild drops keyboard focus

function homeSubtitle(total, on) {
  if (!total) return "Small widgets that live on your wallpaper, quietly behind your windows. Pick one to get started.";
  if (state.safeMode) return "Safe mode is on, so community widgets aren't running right now.";
  if (!on) return total === 1 ? "Your widget is turned off. Turn it on in My widgets." : `Your ${total} widgets are turned off. Turn them on in My widgets.`;
  if (on < total) return `${on} on your desktop, ${total - on} turned off.`;
  return on === 1 ? "1 widget on your desktop." : `${on} widgets on your desktop.`;
}

function homePickCard(w) {
  const card = $("#tpl-catalog").content.firstElementChild.cloneNode(true);
  catalogThumb(card, w);
  $(".cname", card).textContent = w.name;
  $(".cauthor", card).textContent = w.author ? `by ${w.author}` : "";
  $(".cdesc", card).textContent = w.description || "";
  permissionChips($(".chips", card), w);
  card.setAttribute("aria-label", w.name);
  const btn = $(".action", card);
  btn.setAttribute("aria-label", `Install ${w.name}`);
  if (busy.has(w.id)) {
    btn.disabled = true;
    btn.innerHTML = '<span class="spin"></span>Working';
  } else {
    btn.classList.add("primary");
    btn.innerHTML = '<span class="ico" data-icon="download"></span>Install';
    btn.addEventListener("click", () => job(w.id, "install", btn));
  }
  icons(card);
  return card;
}

function renderHome() {
  if (!state) return;
  const widgets = state.widgets;
  const on = widgets.filter((w) => (state.settings.widgets[w.id] || {}).enabled);
  $("#homeTitle").textContent = widgets.length ? "Welcome back" : "Welcome to Umbra";
  $("#homeSub").textContent = homeSubtitle(widgets.length, on.length);

  const arranging = $("#editMode").checked;
  $("#homeArrange").disabled = !widgets.length;
  $("#homeArrange").classList.toggle("on", arranging);
  $("#homeArrangeTitle").textContent = arranging ? "Lock widgets" : "Arrange your desktop";
  $("#homeArrangeSub").textContent = !widgets.length ? "Add a widget first"
    : arranging ? "Widgets are unlocked. Click here when you're done." : "Drag and resize widgets, then lock them again";

  const mineKey = JSON.stringify(on.map((w) => [w.id, w.name, w.preview]));
  if (mineKey !== homeMineKey) {
    homeMineKey = mineKey;
    $("#homeMineList").replaceChildren(...on.slice(0, HOME_MINE_MAX).map((w) => {
      const b = Object.assign(document.createElement("button"), { className: "home-chip", type: "button", title: `${w.name} options` });
      const pic = Object.assign(document.createElement("span"), { className: "thumb" });
      thumb(pic, w);
      b.append(pic, w.name);
      b.addEventListener("click", () => focusWidget(w.id));
      return b;
    }));
  }
  $("#homeMine").hidden = !on.length || !!state.safeMode; // in safe mode they aren't on the desktop

  // Once a desktop has a few widgets Home stops suggesting more; Browse is one click away.
  const loading = !widgets.length && catalogLoading;
  const picks = !catalog || widgets.length >= 3 ? [] : HOME_PICKS
    .map((id) => catalog.find((w) => w.id === id))
    .filter((w) => w && !w.installed && !w.incompatible && !w.camera && !w.userScripts && !(w.commands || []).length)
    .filter((w) => !widgets.some((x) => x.widget === w.id)) // the state after an install arrives before the updated catalog
    .slice(0, 3);
  const picksKey = loading ? "loading" : JSON.stringify(picks.map((w) => [w.id, w.version, w.description, w.hosts, w.media, w.notifications, w.system, busy.has(w.id)]));
  if (picksKey !== homePicksKey) {
    homePicksKey = picksKey;
    $("#homePickGrid").replaceChildren(...(loading
      ? [0, 1, 2].map(() => Object.assign(document.createElement("div"), { className: "skeleton" }))
      : picks.map(homePickCard)));
  }
  $("#homePicks").hidden = !loading && !picks.length;
}
$("#homeAdd").addEventListener("click", () => show("browse"));
$("#homeSeeAll").addEventListener("click", () => show("browse"));
$("#homeLook").addEventListener("click", () => show("themes"));
$("#homeAll").addEventListener("click", () => show("widgets"));
$("#homeArrange").addEventListener("click", () => $("#editMode").click()); // same switch, same message and toast as My widgets
$("#homeTour").addEventListener("click", () => welcome(1));

// The fold-outs on General and Appearance reopen the way they were left (this browser profile).
for (const box of $$("details[data-fold]")) {
  try { box.open = JSON.parse(localStorage.getItem("openOptionSections") || "[]").includes(box.dataset.fold); } catch { /* ignore */ }
  box.addEventListener("toggle", () => {
    try {
      const set = new Set(JSON.parse(localStorage.getItem("openOptionSections") || "[]"));
      if (box.open) set.add(box.dataset.fold); else set.delete(box.dataset.fold);
      localStorage.setItem("openOptionSections", JSON.stringify([...set]));
    } catch { /* storage unavailable */ }
  });
}

// ---------- keyboard ----------
// Esc closes the window, Ctrl+F or / jumps to the Browse search, Ctrl+1..9 switch pages. A hotkey being recorded
// handles (and cancels) its own keys first; the arrow keys of the sidebar and segmented control are in arrowNav().
const typing = (el) => !!el && (el.isContentEditable || el.matches("textarea, select, input:not([type=checkbox], [type=radio], [type=range], [type=color])"));
function focusSearch() {
  show("browse");
  $("#search").focus();
  $("#search").select();
}
document.addEventListener("keydown", (e) => {
  if (e.defaultPrevented || recording) return;
  const noMods = !e.ctrlKey && !e.altKey && !e.metaKey;
  if (e.key === "Escape" && noMods && !e.shiftKey) {
    const search = $("#search");
    if (document.activeElement === search && search.value) { search.value = ""; renderCatalog(); return; }
    e.preventDefault();
    // Commit a field being edited and any save still waiting on its timer, then let the host close the window.
    if (document.activeElement && document.activeElement !== document.body) document.activeElement.blur();
    flushSave();
    post({ type: "close" });
  } else if (e.ctrlKey && !e.altKey && !e.metaKey && !e.shiftKey && e.key.toLowerCase() === "f") {
    e.preventDefault();
    focusSearch();
  } else if (e.key === "/" && noMods && !typing(document.activeElement)) {
    e.preventDefault();
    focusSearch();
  } else if (e.ctrlKey && !e.altKey && !e.metaKey && !e.shiftKey && /^[1-9]$/.test(e.key)) {
    const b = $$(".nav button[data-page]")[Number(e.key) - 1];
    if (b) { e.preventDefault(); show(b.dataset.page); }
  }
});

// ---------- zoom: like a browser, 5 % steps owned by the host (SettingsPlacement.Zoom) ----------
const zoom = (step) => post({ type: "zoom", step });
$("#zoomOut").addEventListener("click", () => zoom(-1));
$("#zoomIn").addEventListener("click", () => zoom(1));
$("#zoomReset").addEventListener("click", () => zoom(0));
document.addEventListener("keydown", (e) => {
  if (e.defaultPrevented || recording || !e.ctrlKey || e.altKey || e.metaKey) return;
  const step = e.key === "=" || e.key === "+" ? 1 : e.key === "-" || e.key === "_" ? -1 : e.key === "0" && !e.shiftKey ? 0 : null;
  if (step === null) return;
  e.preventDefault();
  zoom(step);
});
// A mouse notch is one step; a touchpad's small deltas add up to one.
let wheelZoom = 0;
document.addEventListener("wheel", (e) => {
  if (!e.ctrlKey) return;
  e.preventDefault();
  if (Math.abs(e.deltaY) >= 50 || e.deltaMode !== 0) { wheelZoom = 0; zoom(e.deltaY < 0 ? 1 : -1); return; }
  wheelZoom += e.deltaY;
  if (Math.abs(wheelZoom) >= 100) { zoom(wheelZoom < 0 ? 1 : -1); wheelZoom = 0; }
}, { passive: false });

// ---------- welcome dialog (#241): described only, nothing here touches the user's widgets ----------
const WELCOME = [
  { title: "Welcome to Umbra",
    text: "Small widgets that live on your wallpaper, behind your windows. Pick the ones you like: clocks, weather, notes, games and more.",
    art: `<svg viewBox="0 0 240 132"><rect class="a-box" x="14" y="16" width="64" height="100" rx="8"/><rect class="a-hot a-pulse" x="22" y="26" width="48" height="14" rx="5"/><rect class="a-dim" x="22" y="48" width="48" height="10" rx="5"/><rect class="a-dim" x="22" y="64" width="48" height="10" rx="5"/><rect class="a-box" x="92" y="16" width="134" height="100" rx="8"/><rect class="a-hot" x="102" y="26" width="52" height="38" rx="7"/><rect class="a-box" x="162" y="26" width="54" height="38" rx="7"/><rect class="a-dim" x="102" y="74" width="114" height="8" rx="4"/><rect class="a-dim" x="102" y="90" width="80" height="8" rx="4"/><circle class="a-acc a-pulse" cx="204" cy="95" r="7"/></svg>` },
  { title: "Put them where you want",
    text: "Widgets stay locked so you can't bump them. Turn on Arrange to drag and resize them, then press Done.",
    art: `<svg viewBox="0 0 240 132"><rect class="a-box" x="20" y="14" width="84" height="56" rx="8"/><g class="a-slide"><rect class="a-hot" x="72" y="30" width="84" height="56" rx="8"/></g><rect class="a-box" x="64" y="102" width="112" height="22" rx="11"/><rect class="a-acc" x="126" y="106" width="46" height="14" rx="7"/></svg>` },
  { title: "Umbra stays in the tray",
    text: "Close this window and your widgets keep running. Click the tray icon to come back, or right-click it for Arrange and Layouts.",
    art: `<svg viewBox="0 0 240 132"><rect class="a-box" x="30" y="10" width="130" height="80" rx="8"/><rect class="a-hot" x="38" y="18" width="34" height="14" rx="5"/><rect class="a-dim" x="78" y="18" width="34" height="14" rx="5"/><rect class="a-dim" x="118" y="18" width="34" height="14" rx="5"/><rect class="a-dim" x="38" y="42" width="114" height="8" rx="4"/><rect class="a-dim" x="38" y="58" width="80" height="8" rx="4"/><rect class="a-box" x="0" y="110" width="240" height="22"/><circle class="a-acc a-pulse" cx="214" cy="121" r="6"/><circle class="a-dim" cx="196" cy="121" r="5"/></svg>` },
];
let welcomeAt = 0, welcomeFrom = null;
/** Shows welcome slide n (1-based); called by the host on first run and for captures. */
function welcome(n) {
  const w = $("#welcome");
  const open = w.hidden;
  if (open) {
    welcomeFrom = document.activeElement;
    const g = state && state.settings && state.settings.general;
    document.documentElement.dataset.motion = g && (g.animations === false || g.animationSpeed === "off") ? "off" : "on";
  }
  welcomeAt = Math.max(0, Math.min(WELCOME.length - 1, (n || 1) - 1));
  const s = WELCOME[welcomeAt];
  $("#welcomeArt").innerHTML = s.art;
  $("#welcomeStep").textContent = `${welcomeAt + 1} of ${WELCOME.length}`;
  $("#welcomeTitle").textContent = s.title;
  $("#welcomeText").textContent = s.text;
  $$("#welcomeDots i").forEach((d, i) => d.classList.toggle("on", i === welcomeAt));
  $("#welcomeBack").hidden = welcomeAt === 0;
  $("#welcomeNext").textContent = welcomeAt === WELCOME.length - 1 ? "Get started" : "Next";
  $("#welcomeSkip").hidden = welcomeAt === WELCOME.length - 1;
  w.hidden = false;
  if (open) { document.querySelector(".app").inert = true; $("#welcomeNext").focus(); } // the page behind is out of reach for keys, clicks and screen readers
}
function welcomeClose() {
  $("#welcome").hidden = true;
  document.querySelector(".app").inert = false;
  if (welcomeFrom && welcomeFrom.focus) welcomeFrom.focus();
  welcomeFrom = null;
}
$("#welcomeNext").addEventListener("click", () => (welcomeAt === WELCOME.length - 1 ? welcomeClose() : welcome(welcomeAt + 2)));
$("#welcomeBack").addEventListener("click", () => welcome(welcomeAt));
$("#welcomeSkip").addEventListener("click", welcomeClose);
$("#replayTour").addEventListener("click", () => welcome(1));
// On the document in the capture phase: a click on the backdrop moves focus to the body, and Esc must still close only the dialog (not Settings).
document.addEventListener("keydown", (e) => {
  if ($("#welcome").hidden) return;
  if (e.key === "Escape") { e.preventDefault(); e.stopPropagation(); welcomeClose(); return; }
  if (e.key === "Tab") { // keep focus inside the dialog
    const f = $$("#welcome button").filter((b) => !b.hidden);
    const first = f[0], last = f[f.length - 1];
    if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
    else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
  }
}, true);

icons();
labelRows();
post({ type: "load" });
