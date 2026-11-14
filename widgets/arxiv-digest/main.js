// arXiv Digest: today's count for an arXiv category and the papers that matter to you, ranked by your search (query.js)
// and a small learner (learn.js). Parsing is in feed.js.
//
// Sources: the RSS feed (rss.arxiv.org), and the HTML "new" listing (arxiv.org/list/<cat>/new) only when the feed is
// empty (weekends, holidays) or too large and nothing newer is cached. arXiv announces once a day, so the widget checks
// at most hourly, whatever the refresh setting. Once per single category, the "pastweek" page fills in the last five
// daily totals, so the sparkline has a shape on the first day.
//
// Saved in umbra.store (per copy):
//   cache      { cats, date, counts, items, source, fetchedAt, htmlAt } the last announcement (abstracts trimmed)
//   history    { "<cats>": [{ d: "YYYY-MM-DD", n }], _backfilled: [cats] }   new + cross-list counts, last 30
//              announcements per category
//   saved      [paper + { date, savedAt }]               the Saved tab, newest first (max 200)
//   dismissed  { id: time }  hidden papers;  opened { id: time }  papers opened (each teaches the learner once)
//   model      the learner's weights (learn.js)
// dismissed and opened forget entries after 60 days.

const F = ArxivFeed, Q = ArxivQuery, L = ArxivLearn;
const HOUR = 3600000, DAY = 86400000;
const RETRY_MS = 5 * 60000;       // after a failed check, wait this long
const HTML_EVERY = 12 * HOUR;     // the listing page is fetched at most this often when the feed is empty
const FORGET_MS = 60 * DAY;
const MAX_SAVED = 200;

// Used when the Search setting is empty: what this widget was made for (wave-function collapse and quantum gravity),
// and a starting point for AI categories.
const SUGGESTED = {
  physics: "wave function collapse^3, wavefunction collapse^3, objective collapse^3, spontaneous collapse^3, collapse model^2, " +
    "GRW^2, continuous spontaneous localization^2, CSL, Diosi Penrose^3, Schrodinger Newton^3, gravitational decoherence^3, " +
    "quantum gravity^3, gravity mediated entanglement^3, gravitationally induced entanglement^3, quantum nature of gravity^3, " +
    "semiclassical gravity^2, measurement problem^2, decoherence, cat:gr-qc, -spherical collapse",
  ai: "interpretability^3, mechanistic^2, reasoning^2, agent^2, alignment^2, large language model^2, LLM^2, world model^2, " +
    "scaling law^2, reinforcement learning, benchmark, quantum machine learning^3, physics informed^2",
};
const NAMES = {
  "quant-ph": "Quantum Physics", "gr-qc": "General Relativity and Quantum Cosmology", "hep-th": "High Energy Physics - Theory",
  "cs.AI": "Artificial Intelligence", "cs.LG": "Machine Learning", "cs.CL": "Computation and Language",
};
const KIND = { new: "New", cross: "Cross-list", replace: "Updated" };

const $ = (id) => document.getElementById(id);
const listEl = $("list"), footL = $("foot-l"), footR = $("foot-r");

let cache = null, history = {}, saved = [], dismissed = {}, opened = {}, model = null;
let view = "today";
let error = "";             // "", "offline" or "format"
let loading = true, busy = false, lastTry = 0, first = true;
let lastCats = "";
let undo = null;            // { p, timer } after hiding a paper
let resetArmed = 0;
const expanded = new Set();

const opts = () => umbra.options;
function catsOpt() {
  const o = opts();
  return F.cleanCats(o.category === "custom" ? o.customCategory : o.category) || "quant-ph";
}
const suggestedFor = (c) => /(^|\+)(cs\.|stat\.ML)/.test(c) ? SUGGESTED.ai
  : /quant-ph|gr-qc|hep-|astro-ph|physics\.|cond-mat|math-ph/.test(c) ? SUGGESTED.physics : "";
const usingSuggested = () => !String(opts().query || "").trim();
const queryText = () => String(opts().query || "").trim() || suggestedFor(catsOpt());
const learnOn = () => opts().learn !== false;
const topN = () => [3, 5, 10].includes(+opts().topN) ? +opts().topN : 5;

// ---------- storage ----------

async function load() {
  const [c, h, s, d, o, m] = await Promise.all(["cache", "history", "saved", "dismissed", "opened", "model"].map((k) => umbra.store.get(k, null)));
  cache = c && Array.isArray(c.items) && c.counts ? c : null;
  history = h && typeof h === "object" ? h : {};
  saved = Array.isArray(s) ? s : [];
  dismissed = d && typeof d === "object" ? d : {};
  opened = o && typeof o === "object" ? o : {};
  model = m && m.w ? m : null;
  const old = Date.now() - FORGET_MS;
  for (const map of [dismissed, opened]) for (const id of Object.keys(map)) if (!(map[id] > old)) delete map[id];
}
function save(key, value) {
  return umbra.store.set(key, value).catch((e) => console.warn(`arxiv-digest: could not save ${key}`, e));
}

/** The listing as stored: abstracts trimmed so the widget's data stays well under the 1 MB store limit. */
function compact(l, cats, source) {
  let cut = 1100;
  const make = () => l.items.slice(0, 900).map((p) => ({
    id: p.id, title: p.title, authors: p.authors.slice(0, 12), more: Math.max(0, p.authors.length - 12),
    abstract: p.abstract.length > cut ? p.abstract.slice(0, cut).replace(/\s+\S*$/, "") + " …" : p.abstract,
    cats: p.cats.slice(0, 6), type: p.type, jref: p.jref,
  }));
  let items = make();
  while (JSON.stringify(items).length > 700000 && cut > 300) { cut = Math.floor(cut * 0.7); items = make(); }
  return { cats, date: l.date, counts: l.counts, items, source, fetchedAt: Date.now(), htmlAt: 0 };
}

function remember(c, date, n) {
  if (!date) return;
  const h = (history[c] || []).filter((x) => x.d !== date);
  h.push({ d: date, n });
  h.sort((a, b) => (a.d < b.d ? -1 : 1));
  history[c] = h.slice(-30);
}

/** The date of the latest weekday announcement arXiv should have made by now (US Eastern), "YYYY-MM-DD". */
function expectedDate(now) {
  const p = Object.fromEntries(new Intl.DateTimeFormat("en-CA", { timeZone: "America/New_York", year: "numeric", month: "2-digit", day: "2-digit" })
    .formatToParts(new Date(now)).map((x) => [x.type, x.value]));
  const d = new Date(Date.UTC(+p.year, +p.month - 1, +p.day));
  while (d.getUTCDay() === 0 || d.getUTCDay() === 6) d.setUTCDate(d.getUTCDate() - 1);
  return d.toISOString().slice(0, 10);
}

// ---------- fetching ----------

/** Checks arXiv when it's time to. Resolves true when it checked (whatever the outcome), false when it didn't need to. */
async function refresh(force) {
  if (busy) return false;
  const c = catsOpt();
  const mine = !!cache && cache.cats === c;
  const now = Date.now();
  if (!force && mine && now - cache.fetchedAt < HOUR) {
    // arXiv changes once a day and the last hourly check succeeded, so what's on screen is current. Say so: the SDK
    // would otherwise dim it as stale after 3 refresh intervals. A Date only moves "last data"; failures still count.
    if (!error) umbra.status?.ok(new Date());
    return false;
  }
  if (!force && mine && now - lastTry < RETRY_MS) return false;
  busy = true; lastTry = now;
  try {
    let l = null, tooBig = false;
    try {
      l = F.parseRss(await umbra.fetchText(`https://rss.arxiv.org/rss/${c}`));
    } catch (e) {
      if (/too large/i.test(String(e?.message || e))) tooBig = true;
      else throw e;
    }
    let source = "rss";
    if (!l || !l.items.length) {
      // Weekend or holiday (or a feed over 2 MB): keep the cached announcement unless a newer one should exist.
      const behind = !mine || (cache.date < expectedDate(now) && now - (cache.htmlAt || 0) > HTML_EVERY);
      if (!tooBig && !behind) { cache.fetchedAt = now; error = ""; await save("cache", cache); return true; }
      const parts = [];
      for (const one of c.split("+")) parts.push(F.parseHtml(await umbra.fetchText(`https://arxiv.org/list/${one}/new?skip=0&show=500`)));
      l = parts.length === 1 ? parts[0] : F.merge(parts);
      source = "html";
      if (!l.items.length) { const e = new Error("the listing page had no papers"); e.format = true; throw e; }
      if (mine && l.date && l.date <= cache.date) { cache.fetchedAt = now; cache.htmlAt = now; error = ""; await save("cache", cache); return true; }
    }
    cache = compact(l, c, source);
    if (source === "html") cache.htmlAt = now;
    remember(c, cache.date, cache.counts.new + cache.counts.cross);
    error = "";
    await save("cache", cache);
    await save("history", history);
  } catch (e) {
    console.warn("arxiv-digest: check failed", e);
    error = e && e.format ? "format" : "offline";
    if (e && e.format) umbra.status?.failed(e);
  } finally {
    busy = false;
  }
  return true;
}

/**
 * Fills the sparkline with the last five announcements' totals, once per single category (for several categories the
 * pages would count a paper listed in two of them twice, so those build their history day by day). Dates already
 * recorded from a real check are kept.
 */
async function backfill(c) {
  const done = Array.isArray(history._backfilled) ? history._backfilled : (history._backfilled = []);
  if (c.includes("+") || done.includes(c)) return;
  try {
    const days = F.parsePastWeek(await umbra.fetchText(`https://arxiv.org/list/${c}/pastweek?skip=0&show=25`));
    for (const x of days) if (!(history[c] || []).some((h) => h.d === x.d)) remember(c, x.d, x.n);
    done.push(c);
    await save("history", history);
  } catch (e) {
    console.warn("arxiv-digest: no past-week totals", e);
  }
}

// ---------- learning ----------

let rareFor = null, rareFn = null;
function rare() {
  const items = cache?.items || [];
  if (rareFor !== items) { rareFor = items; rareFn = L.rarity(items); }
  return rareFn;
}
function teach(p, signal, isUndo) {
  if (!learnOn()) return;
  model = L.update(model, p, signal, rare(), Date.now(), isUndo);
  save("model", model);
}

// ---------- actions ----------

const absUrl = (id) => `https://arxiv.org/abs/${id}`;
const isSaved = (id) => saved.some((s) => s.id === id);

function openPaper(p) {
  umbra.openUrl(absUrl(p.id));
  if (!opened[p.id]) {
    opened[p.id] = Date.now();
    save("opened", opened);
    teach(p, 1);
  }
}

function toggleSave(p) {
  if (isSaved(p.id)) {
    saved = saved.filter((s) => s.id !== p.id);
    teach(p, 2, true);
  } else {
    const { id, title, authors, more, abstract, cats, type, jref } = p;
    saved.unshift({ id, title, authors, more, abstract: abstract.length > 700 ? abstract.slice(0, 700).replace(/\s+\S*$/, "") + " …" : abstract,
      cats, type, jref, date: p.date || cache?.date || "", savedAt: Date.now() });
    saved = saved.slice(0, MAX_SAVED);
    teach(p, 2);
  }
  save("saved", saved);
  render();
}

function hide(p) {
  dismissed[p.id] = Date.now();
  save("dismissed", dismissed);
  teach(p, -1);
  if (undo) clearTimeout(undo.timer);
  undo = { p, timer: setTimeout(() => { undo = null; render(); }, 8000) };
  render();
}

function unhide() {
  if (!undo) return;
  const p = undo.p;
  clearTimeout(undo.timer);
  undo = null;
  delete dismissed[p.id];
  save("dismissed", dismissed);
  teach(p, -1, true);
  render();
}

// ---------- rendering ----------

function el(tag, cls, txt) {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (txt !== undefined) e.textContent = txt;
  return e;
}

const SVG = "http://www.w3.org/2000/svg";
function icon(d, filled) {
  const s = document.createElementNS(SVG, "svg");
  s.setAttribute("viewBox", "0 0 16 16");
  const p = document.createElementNS(SVG, "path");
  p.setAttribute("d", d);
  p.setAttribute("fill", filled ? "currentColor" : "none");
  p.setAttribute("stroke", "currentColor");
  p.setAttribute("stroke-width", "1.3");
  p.setAttribute("stroke-linejoin", "round");
  p.setAttribute("stroke-linecap", "round");
  s.append(p);
  return s;
}
const STAR = "M8 1.8l1.85 3.85 4.2.55-3.07 2.92.78 4.17L8 11.25l-3.76 2.04.78-4.17L1.95 6.2l4.2-.55z";
const CROSS = "M4.2 4.2l7.6 7.6M11.8 4.2l-7.6 7.6";

function dayLabel(d) {
  if (!d) return "";
  const t = new Date(d + "T12:00:00Z");
  return t.toLocaleDateString(umbra.locale || undefined, { weekday: "short", day: "numeric", month: "short", timeZone: "UTC" });
}

const fmt = (n) => (umbra.fmt ? umbra.fmt(n, 0) : String(n));

function authorLine(p) {
  const a = p.authors || [];
  const n = a.length + (p.more || 0);
  return n === 0 ? "" : n === 1 ? a[0] : n === 2 ? `${a[0]} and ${a[1]}` : `${a[0]} et al. (${n} authors)`;
}

/** The abstract with the query's terms marked, built from text nodes (never HTML from the feed). */
function snippet(text, q) {
  const p = el("p", "snip");
  p.dataset.noDrag = "";
  let at = 0;
  for (const [a, b] of Q.highlights(text, q)) {
    if (a > at) p.append(document.createTextNode(text.slice(at, a)));
    p.append(el("mark", "", text.slice(a, b)));
    at = b;
  }
  if (at < text.length) p.append(document.createTextNode(text.slice(at)));
  return p;
}

function card(p, o) {
  const art = el("article", `card t-${p.type}`);
  const c1 = el("div", "c1");
  if (o.rank) c1.append(el("span", "rank num", String(o.rank)));
  c1.append(el("span", "aid num", p.id), el("span", "kind type", KIND[p.type] || "New"));
  if (o.pick) c1.append(el("span", "kind pick", "For you"));
  if (o.savedDate) c1.append(el("span", "aid", dayLabel(o.savedDate)));
  c1.append(el("span", "sp"));

  const on = isSaved(p.id);
  const star = el("button", "ico" + (on ? " on" : ""));
  star.type = "button"; star.dataset.noDrag = "";
  star.title = on ? "Remove from Saved" : "Save for later";
  star.setAttribute("aria-label", star.title);
  star.append(icon(STAR, on));
  star.addEventListener("click", () => toggleSave(p));
  c1.append(star);
  if (o.hide) {
    const x = el("button", "ico");
    x.type = "button"; x.dataset.noDrag = "";
    x.title = "Not interested (hide)";
    x.setAttribute("aria-label", x.title);
    x.append(icon(CROSS, false));
    x.addEventListener("click", () => hide(p));
    c1.append(x);
  }
  art.append(c1);

  const t = el("button", "ttl", p.title);
  t.type = "button"; t.dataset.noDrag = "";
  t.title = [authorLine(p), (p.cats || []).join(", "), "Open on arXiv"].filter(Boolean).join("\n");
  t.addEventListener("click", () => openPaper(p));
  art.append(t);

  if (opts().showSnippet !== false && p.abstract) {
    const s = snippet(p.abstract, o.q);
    if (expanded.has(p.id)) s.classList.add("open");
    s.title = expanded.has(p.id) ? "Click to shorten" : "Click to read the whole abstract";
    s.addEventListener("click", () => {
      if (expanded.has(p.id)) expanded.delete(p.id); else expanded.add(p.id);
      s.classList.toggle("open");
      s.title = s.classList.contains("open") ? "Click to shorten" : "Click to read the whole abstract";
    });
    art.append(s);
  }

  const reasons = (o.hits || []).slice(0, 3).map((h) => h.label);
  if (reasons.length || (o.why && o.why.length)) {
    const w = el("div", "why");
    w.append(document.createTextNode(reasons.join(" · ")));
    if (o.why && o.why.length) {
      if (reasons.length) w.append(document.createTextNode(" · "));
      w.append(el("span", "l", "learned: " + o.why.join(", ")));
    }
    w.title = (o.hits || []).map((h) => `${h.label} (${h.where})`).join("\n") + (o.why?.length ? `\nLearned from your clicks: ${o.why.join(", ")}` : "");
    art.append(w);
  }
  return art;
}

function showState(title, body, link) {
  const box = el("div", "state");
  box.append(el("strong", "", title), document.createTextNode(body));
  if (link) {
    box.append(document.createElement("br"));
    const a = el("a", "", link.text);
    a.dataset.noDrag = "";
    a.addEventListener("click", link.fn);
    box.append(a);
  }
  listEl.replaceChildren(box);
}

function spark(points) {
  const s = $("spark");
  s.replaceChildren();
  if (points.length < 2) { s.removeAttribute("title"); return; }
  const W = 96, H = 34, pad = 3;
  const ns = points.map((p) => p.n);
  const lo = Math.min(...ns), hi = Math.max(...ns);
  const x = (i) => (i / (points.length - 1)) * W;
  const y = (n) => hi === lo ? H / 2 : pad + (1 - (n - lo) / (hi - lo)) * (H - 2 * pad);
  const pts = points.map((p, i) => `${x(i).toFixed(1)},${y(p.n).toFixed(1)}`);
  const area = document.createElementNS(SVG, "path");
  area.setAttribute("class", "area");
  area.setAttribute("d", `M0,${H} L${pts.join(" L")} L${W},${H} Z`);
  const line = document.createElementNS(SVG, "polyline");
  line.setAttribute("class", "line");
  line.setAttribute("points", pts.join(" "));
  const dot = document.createElementNS(SVG, "circle");
  dot.setAttribute("class", "now");
  dot.setAttribute("cx", x(points.length - 1).toFixed(1));
  dot.setAttribute("cy", y(ns[ns.length - 1]).toFixed(1));
  dot.setAttribute("r", "2.2");
  s.append(area, line, dot);
  const t = document.createElementNS(SVG, "title");
  t.textContent = `Last ${points.length} announcements: ${fmt(lo)} to ${fmt(hi)} papers a day`;
  s.append(t);
}

function header() {
  const c = catsOpt();
  const src = $("src");
  src.replaceChildren(document.createTextNode("arXiv · "), el("b", "", c.replace(/\+/g, " + ")));
  src.title = c.split("+").map((x) => NAMES[x] ? `${x}: ${NAMES[x]}` : x).join("\n");
  const mine = cache && cache.cats === c;
  if (!mine) {
    $("date").textContent = "";
    $("big").textContent = "–";
    $("break").replaceChildren(document.createTextNode(loading ? "Checking today's announcement" : " "));
    spark([]);
    return;
  }
  $("date").textContent = dayLabel(cache.date);
  $("date").title = `Announced ${cache.date}. arXiv announces Sunday to Thursday evenings (US Eastern).`;
  const k = cache.counts;
  $("big").textContent = fmt(k.new + k.cross);
  $("big").title = `${fmt(k.new)} new submissions and ${fmt(k.cross)} cross-lists from other categories`;
  const b = $("break");
  b.replaceChildren(el("b", "", fmt(k.new)), document.createTextNode(" new · "), el("b", "", fmt(k.cross)),
    document.createTextNode(" cross-lists · "), el("b", "", fmt(k.replace)), document.createTextNode(" updated"));
  spark(history[c] || []);
}

function render() {
  header();
  const c = catsOpt();
  const mine = cache && cache.cats === c;
  $("tab-today").classList.toggle("on", view === "today");
  $("tab-saved").classList.toggle("on", view === "saved");
  $("n-saved").textContent = saved.length ? String(saved.length) : "";
  footR.hidden = true; footR.className = "foot-r"; footR.onclick = null;
  footL.className = "foot-l num";
  footL.textContent = "";

  if (view === "saved") return renderSaved();

  if (!mine) {
    $("n-today").textContent = "";
    if (loading || busy) {
      listEl.replaceChildren(...[0, 1, 2, 3].map(() => { const s = el("div", "skel"); s.append(el("i"), el("i"), el("i")); return s; }));
    } else if (error === "format") {
      showState("arXiv changed its format", "The feed no longer looks the way this widget expects. It will try again later.",
        { text: "Open the listing", fn: () => umbra.openUrl(`https://arxiv.org/list/${c.split("+")[0]}/new`) });
    } else {
      showState("Can't reach arXiv", "Check your connection. Today's papers load as soon as it's back.",
        { text: "Try again", fn: () => reload(true) });
    }
    return;
  }

  const q = Q.parse(queryText());
  const rf = rare();
  // Papers you already saved or opened taught the learner; they get no learned boost themselves, or they'd only ever
  // recommend each other. They still rank by your search.
  const NONE = { boost: 0, why: [] };
  const learned = (p) => (isSaved(p.id) || opened[p.id] ? NONE : L.boost(model, p, rf));
  const ranked = Q.rank(cache.items, q, learnOn() ? learned : null, (p) => p.id in dismissed);
  $("n-today").textContent = String(ranked.length);
  const n = topN();

  if (!ranked.length) {
    const total = cache.counts.new + cache.counts.cross;
    showState("No matches today", `${fmt(total)} papers were announced, none matched your search. Try broader terms in this widget's settings.`,
      { text: "Open the full listing", fn: () => umbra.openUrl(`https://arxiv.org/list/${c.split("+")[0]}/new`) });
  } else {
    listEl.replaceChildren(...ranked.slice(0, n).map((r, i) =>
      card(r.p, { rank: i + 1, q, hits: r.hits, why: r.why, pick: r.pick, hide: true })));
  }

  if (undo) {
    footL.textContent = "Hidden. Fewer papers like it from now on.";
    footR.hidden = false; footR.textContent = "Undo"; footR.onclick = unhide;
  } else if (error && error !== "format") {
    footL.classList.add("warn");
    footL.textContent = `Offline. Showing ${dayLabel(cache.date)}`;
  } else {
    const more = ranked.length - Math.min(n, ranked.length);
    const bits = [];
    if (more > 0) bits.push(`+${more} more match`);
    if (usingSuggested()) bits.push("suggested topics");
    if (learnOn() && model && model.n >= L.MIN_SIGNALS) bits.push(`learned from ${model.n} clicks`);
    else if (learnOn()) bits.push(`learning: ${model?.n || 0} of ${L.MIN_SIGNALS} clicks`);
    footL.textContent = bits.join(" · ");
    footL.title = usingSuggested() ? `Suggested search for ${c}:\n${queryText()}\nType your own in this widget's settings.` : "";
  }
}

function renderSaved() {
  const q = Q.parse(queryText());
  if (!saved.length) {
    showState("Nothing saved yet", "Click the star on a paper to keep it here. Saving also teaches the widget what you like.");
  } else {
    listEl.replaceChildren(...saved.map((p) => card(p, { q, savedDate: p.date })));
  }
  const n = model?.n || 0;
  footL.textContent = !learnOn() ? "Learning is off (settings)" : n ? `Learned from ${n} click${n === 1 ? "" : "s"}` : "Nothing learned yet";
  if (learnOn() && n) {
    footR.hidden = false;
    footR.textContent = resetArmed > Date.now() ? "Click again to reset" : "Reset learning";
    if (resetArmed > Date.now()) footR.classList.add("danger");
    footR.onclick = async () => {
      if (resetArmed > Date.now()) {
        resetArmed = 0;
        model = null;
        await save("model", null);
      } else {
        resetArmed = Date.now() + 4000;
        setTimeout(render, 4100);
      }
      render();
    };
  }
}

for (const t of document.querySelectorAll(".tab")) {
  t.addEventListener("click", () => { view = t.dataset.view; resetArmed = 0; listEl.scrollTop = 0; render(); });
}

async function reload(force) {
  if (!cache || cache.cats !== catsOpt()) { loading = true; render(); }
  const checked = await refresh(force === true);
  if (cache && cache.cats === catsOpt() && !error) await backfill(cache.cats);
  // umbra.every calls this every refresh interval; redraw only when something could have changed, so hover, scroll
  // and an open abstract aren't disturbed by a tick that did nothing.
  if (!checked && !loading && !first) return;
  loading = false;
  render();
  if (first) { first = false; umbra.rendered(); }
}

umbra.ready.then(async () => {
  if (!umbra.store || !umbra.fetchText) {
    listEl.replaceChildren();
    showState("Update Umbra", "arXiv Digest needs a newer Umbra to read arXiv and remember your papers.",
      { text: "Get the latest Umbra", fn: () => umbra.openUrl("https://github.com/AmineChr54/umbra/releases/latest") });
    umbra.rendered();
    return;
  }
  await load();
  lastCats = catsOpt();
  if (cache && cache.cats === lastCats) {
    loading = false;
    umbra.status?.ok(new Date(cache.fetchedAt));
    render();
  }
  umbra.every(() => reload(false));
  umbra.onSettings(() => {
    if (first) return;
    const c = catsOpt();
    if (c !== lastCats) { lastCats = c; expanded.clear(); reload(true); }
    else render();
  });
});
