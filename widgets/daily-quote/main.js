// Daily quote: a quote or word from the built-in lists (quotes.js, words.js), in a shuffled order that is
// different for every install and doesn't repeat until the whole list was shown. Favorites, the order seed and
// the position per list live in umbra.store ("state", "favs").
const $ = id => document.getElementById(id);
const TOPICS = ["discipline", "calm", "courage", "focus", "growth", "kindness", "resilience", "wisdom"];
const LEVELS = ["everyday", "advanced", "rare"];
const ROTATIONS = ["quote", ...LEVELS, "any"];
const ANY = LEVELS.flatMap(l => WORDS[l]);

let state = { seed: 0, pos: {}, period: null };
let favs = [];
let pinned = null;  // an entry picked in the favorites panel, shown until Next or the next period

// ---- helpers --------------------------------------------------------------------------------------

/** FNV-1a, 32-bit. */
function hash(s) {
  let h = 0x811c9dc5;
  for (let i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 0x01000193); }
  return h >>> 0;
}
const keyOf = (kind, e) => kind + ":" + hash(e[0].toLowerCase()).toString(36);

/** mulberry32: a small seeded PRNG, so the order is the same after a restart. */
function rng(seed) {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = Math.imul(a ^ (a >>> 15), a | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

// Days since a fixed local epoch, shifted so the day rolls over at dayStart.
function dayNumber(now, startHour) {
  const shifted = new Date(now.getTime() - startHour * 36e5);
  return Math.floor(Date.UTC(shifted.getFullYear(), shifted.getMonth(), shifted.getDate()) / 864e5);
}
const dayStart = () => Math.max(0, Math.min(23, Math.floor(+umbra.options.dayStart || 0)));
const cap = s => s ? s[0].toUpperCase() + s.slice(1) : "";

function rotation() {
  const o = umbra.options;
  if (o.mode !== "word") return "quote";
  return LEVELS.includes(o.level) ? o.level : "any";
}
const listFor = rot => rot === "quote" ? QUOTES : rot === "any" ? ANY : WORDS[rot];

// ---- order ----------------------------------------------------------------------------------------

const permCache = new Map();
/** The shuffled order of one pass (cycle) through a list; each pass gets a new order. */
function perm(rot, cycle) {
  const id = `${rot}/${cycle}/${state.seed}`;
  let p = permCache.get(id);
  if (p) return p;
  if (permCache.size > 12) permCache.clear();
  const n = listFor(rot).length, r = rng(hash(id));
  p = Array.from({ length: n }, (_, i) => i);
  for (let i = n - 1; i > 0; i--) { const j = Math.floor(r() * (i + 1)); [p[i], p[j]] = [p[j], p[i]]; }
  permCache.set(id, p);
  return p;
}
function entryAt(rot, pos) {
  const list = listFor(rot), n = list.length, c = Math.floor(pos / n);
  return list[perm(rot, c)[pos - c * n]];
}

/** Topics the user left on; null when every switch is off (then all are shown, with a note). */
function topicsOn() {
  const on = TOPICS.filter(t => umbra.options["topic" + cap(t)] !== false);
  return on.length ? on : null;
}
function allowed(rot, e) {
  if (rot !== "quote") return true;
  const on = topicsOn();
  return !on || on.includes(e[2]);
}
/** The nearest position from pos in direction dir whose entry passes the topic switches, or null. */
function settle(rot, pos, dir) {
  const n = listFor(rot).length;
  for (let i = 0; i <= n && pos >= 0; i++, pos += dir) if (allowed(rot, entryAt(rot, pos))) return pos;
  return null;
}

// ---- state ----------------------------------------------------------------------------------------

const validFav = f => f && typeof f.k === "string" && (f.kind === "quote" || f.kind === "word")
  && Array.isArray(f.e) && f.e.length >= 2 && f.e.every(s => typeof s === "string");

async function load() {
  const s = await umbra.store.get("state", null);
  state = s && typeof s === "object" ? s : {};
  if (!Number.isInteger(state.seed)) state.seed = Math.floor(Math.random() * 2 ** 31);
  if (!state.pos || typeof state.pos !== "object") state.pos = {};
  for (const r of ROTATIONS) state.pos[r] = Math.max(0, Math.floor(+state.pos[r] || 0));
  const f = await umbra.store.get("favs", []);
  favs = Array.isArray(f) ? f.filter(validFav) : [];
  permCache.clear();
}
const save = () => umbra.store.set("state", state).catch(e => console.warn("state not saved", e));
const saveFavs = () => umbra.store.set("favs", favs).catch(e => console.warn("favorites not saved", e));

/** Moves every list one step when a new period starts (a new day, or a new start of Umbra). Returns true when it did. */
function checkPeriod(boot) {
  const start = umbra.options.change === "start";
  const next = start ? "start" : "d" + dayNumber(new Date(), dayStart());
  const prev = state.period;
  if (start ? !boot : prev === next) return false;
  state.period = next;
  // The first run shows the first entry; switching between the two Change modes doesn't skip one either.
  if (prev != null && (start || prev !== "start")) for (const r of ROTATIONS) state.pos[r]++;
  pinned = null;
  return true;
}

function current() {
  if (pinned) return pinned;
  const rot = rotation(), pos = state.pos[rot];
  const p = settle(rot, pos, 1);
  if (p === null) return null;
  if (p !== pos) { state.pos[rot] = p; save(); }
  return { kind: rot === "quote" ? "quote" : "word", e: entryAt(rot, p) };
}
function canGoBack() {
  if (pinned) return true;
  const rot = rotation();
  return state.pos[rot] > 0 && settle(rot, state.pos[rot] - 1, -1) !== null;
}
function step(dir) {
  if (pinned) { pinned = null; if (dir < 0) return render(); }
  const rot = rotation(), p = settle(rot, state.pos[rot] + dir, dir);
  if (p === null) return;
  state.pos[rot] = p;
  save();
  render();
}

// ---- rendering ------------------------------------------------------------------------------------

function greeting(now, name) {
  const h = now.getHours();
  const g = h >= 5 && h < 12 ? "Good morning" : h >= 12 && h < 17 ? "Good afternoon" : h >= 17 && h < 22 ? "Good evening" : "Good night";
  const n = String(name ?? "").trim().slice(0, 40);
  return n ? `${g}, ${n}` : g;
}

const NOT_PEOPLE = /proverb|unknown|anonymous/i;
function authorUrl(name) {
  const title = Object.prototype.hasOwnProperty.call(WIKI, name) ? WIKI[name] : name;
  if (title === null || NOT_PEOPLE.test(name)) return null;
  return title === name
    ? "https://en.wikipedia.org/wiki/Special:Search?go=Go&search=" + encodeURIComponent(name)
    : "https://en.wikipedia.org/wiki/" + encodeURIComponent(title.replace(/ /g, "_"));
}
const wiktionaryUrl = w => "https://en.wiktionary.org/wiki/" + encodeURIComponent(w.toLowerCase().replace(/ /g, "_"));

// A single click opens the link a moment later, so a double-click (next entry) never opens it too.
let clickTimer = 0;
function link(text, url, title) {
  if (!url) return document.createTextNode(text);
  const a = document.createElement("a");
  a.className = "link";
  a.href = url;
  a.title = title;
  a.textContent = text;
  a.setAttribute("data-no-drag", "");
  a.addEventListener("click", ev => {
    ev.preventDefault();
    clearTimeout(clickTimer);
    // Never while arranging widgets (a locked widget gets the click even in edit mode).
    if (ev.detail < 2 && !umbra.settings.editMode)
      clickTimer = setTimeout(() => { if (!umbra.settings.editMode) umbra.openUrl(url); }, 250);
  });
  return a;
}

function render() {
  const o = umbra.options, now = new Date(), cur = current();
  if (!cur) return;
  const { kind, e } = cur, fav = favs.some(f => f.k === keyOf(kind, e));
  $("app").classList.toggle("is-fav", fav);
  const note = kind === "quote" && !pinned && !topicsOn() ? "All topics are off, showing all" : "";
  $("greet").textContent = [o.greeting === false ? "" : greeting(now, o.name), note].filter(Boolean).join(" · ");

  const main = $("main"), sub = $("sub");
  if (kind === "quote") {
    main.className = "main";
    main.textContent = `“${e[0]}”`;
    $("meta").textContent = "";
    sub.replaceChildren("— ", link(e[1], authorUrl(e[1]), "Open on Wikipedia"));
    $("ex").textContent = "";
  } else {
    main.className = "main word";
    main.replaceChildren(link(e[0], wiktionaryUrl(e[0]), "Open on Wiktionary"));
    $("meta").textContent = o.pronunciation === false || !e[2] ? e[1] : `${e[1]} · ${e[2]}`;
    sub.textContent = e[3] ?? "";
    $("ex").textContent = o.example === false || !e[4] ? "" : `“${e[4]}”`;
  }

  fit();

  $("back").disabled = !canGoBack();
  const fb = $("fav");
  fb.textContent = fav ? "♥" : "♡";
  fb.classList.toggle("on", fav);
  fb.title = fav ? "Remove from favorites" : "Add to favorites";
  $("list").textContent = `♥ ${favs.length}`;
}

/** Shrinks the text (18 px down to 12 px) until it fits the window, so a long quote or a small widget never clips. */
const MAX_FONT = 18, MIN_FONT = 12;
function fit() {
  const body = $("body"), inner = $("inner"), cs = getComputedStyle(body);
  // The content box: clientHeight includes the padding the hover strip adds, so leave it out.
  const room = body.clientHeight - parseFloat(cs.paddingTop) - parseFloat(cs.paddingBottom);
  let size = MAX_FONT;
  body.style.fontSize = size + "px";
  while (size > MIN_FONT && inner.offsetHeight > room) body.style.fontSize = --size + "px";
}

function renderPanel() {
  const items = favs.map(f => {
    const li = document.createElement("li"), t = document.createElement("div"), small = document.createElement("small");
    t.className = "t";
    if (f.kind === "quote") { t.textContent = `“${f.e[0]}”`; small.textContent = `— ${f.e[1]}`; }
    else { t.textContent = f.e[0]; small.textContent = `${f.e[1]}${f.e[3] ? " · " + f.e[3] : ""}`; }
    t.append(small);
    const x = document.createElement("button");
    x.className = "x";
    x.textContent = "×";
    x.title = "Remove from favorites";
    x.setAttribute("data-no-drag", "");
    x.addEventListener("click", ev => { ev.stopPropagation(); removeFav(f.k); });
    li.append(t, x);
    li.setAttribute("data-no-drag", "");
    li.addEventListener("click", () => { pinned = { kind: f.kind, e: f.e }; openPanel(false); render(); });
    return li;
  });
  $("favs").replaceChildren(...items);
  $("empty").hidden = favs.length > 0;
}

// ---- actions --------------------------------------------------------------------------------------

function toggleFav() {
  const cur = current();
  if (!cur) return;
  const k = keyOf(cur.kind, cur.e), i = favs.findIndex(f => f.k === k);
  if (i >= 0) favs.splice(i, 1);
  else favs.unshift({ k, kind: cur.kind, e: cur.e.slice(), at: Date.now() });
  saveFavs();
  render();
  renderPanel();
}
function removeFav(k) {
  favs = favs.filter(f => f.k !== k);
  saveFavs();
  render();
  renderPanel();
}
function openPanel(open) {
  $("panel").hidden = !open;
  if (open) renderPanel();
}

async function copyText(text) {
  try { await navigator.clipboard.writeText(text); return true; } catch { /* the window may not have focus: fall back */ }
  try {
    umbra.requestFocus();
    const ta = document.createElement("textarea");
    ta.value = text;
    ta.setAttribute("readonly", "");
    ta.style.cssText = "position:fixed;left:-9999px;opacity:0";
    document.body.append(ta);
    ta.select();
    const ok = document.execCommand("copy");
    ta.remove();
    return ok;
  } catch { return false; }
}
async function copyCurrent() {
  const cur = current();
  if (!cur) return;
  const { kind, e } = cur;
  const ok = await copyText(kind === "quote" ? `“${e[0]}” — ${e[1]}` : `${e[0]} (${e[1]}): ${e[3]}`);
  const b = $("copy");
  b.textContent = ok ? "✓" : "!";
  b.classList.add("flash");
  setTimeout(() => { b.textContent = "⧉"; b.classList.remove("flash"); }, 1200);
}

// ---- start --------------------------------------------------------------------------------------

(async () => {
  await umbra.ready;
  await load();
  if (checkPeriod(true)) save();
  render();
  umbra.rendered();

  $("next").addEventListener("click", () => step(1));
  $("back").addEventListener("click", () => step(-1));
  $("fav").addEventListener("click", toggleFav);
  $("copy").addEventListener("click", copyCurrent);
  $("list").addEventListener("click", () => openPanel(true));
  $("close").addEventListener("click", () => openPanel(false));
  $("main").addEventListener("dblclick", () => {
    clearTimeout(clickTimer);
    getSelection()?.removeAllRanges();
    if (!umbra.settings.editMode) step(1);
  });
  document.addEventListener("keydown", ev => { if (ev.key === "Escape") openPanel(false); });
  new ResizeObserver(fit).observe($("body"));

  umbra.onSettings(() => { if (checkPeriod(false)) save(); render(); });
  umbra.store.onChange(async () => { await load(); render(); renderPanel(); });
  setInterval(() => { if (checkPeriod(false)) save(); render(); }, 60000);
})();
