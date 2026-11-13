// Countdowns: a grid of events (dot grid, bar or ring) plus this year / month / week.
// Data lives in umbra.store ("events"). Date math is in countdown.js.
const C = window.Countdown;
const $ = (id) => document.getElementById(id);
const gridEl = $("grid"), sheet = $("sheet"), emojiMenu = $("emoji-menu");
const fTitle = $("f-title"), fDate = $("f-date"), fTime = $("f-time"), fStart = $("f-start");

const COLORS = {
  indigo: "#8b93ff", sky: "#4cc2ff", green: "#2fd58b", amber: "#ffb35c", coral: "#ff7a59", pink: "#ff6fae",
};
const COLOR_KEYS = Object.keys(COLORS);
const STYLES = ["dots", "bar", "ring"];
const PERIODS = { year: "This year", month: "This month", week: "This week" };
const EMOJI = ["🎂", "🎉", "🎁", "✈️", "🏖️", "🎄", "🎓", "💍", "❤️", "👶", "🏠", "💼",
  "📚", "🏃", "⚽", "🎵", "🎮", "🚀", "⭐", "🌙", "☀️", "🍀", "📅", "⏳"];

const ICON = {
  repeat: '<svg viewBox="0 0 12 12"><path d="M9.5 4.5A3.8 3.8 0 0 0 2.6 4M2.5 7.5A3.8 3.8 0 0 0 9.4 8"/><path d="M9.7 1.8v2.8H6.9M2.3 10.2V7.4h2.8"/></svg>',
  smile: '<svg viewBox="0 0 18 18"><circle cx="9" cy="9" r="6.6"/><path d="M6.4 10.6c.7.9 1.6 1.4 2.6 1.4s1.9-.5 2.6-1.4"/><path d="M6.8 7.2h.01M11.2 7.2h.01" stroke-width="2"/></svg>',
  hourglass: '<svg viewBox="0 0 18 18"><path d="M5 2.5h8M5 15.5h8M6 2.5c0 3.5 3 4.5 3 6.5s-3 3-3 6.5M12 2.5c0 3.5-3 4.5-3 6.5s3 3 3 6.5"/></svg>',
  plus: '<svg viewBox="0 0 16 16"><path d="M8 3v10M3 8h10"/></svg>',
};

let events = [];
let first = true;
let draft = null; // the event being added or edited in the sheet
let deleteArmed = null;

const opts = () => umbra.options;
const motion = () => document.documentElement.dataset.motion !== "off";
const esc = (s) => String(s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
const uid = () => Date.now().toString(36) + Math.random().toString(36).slice(2, 7);
const weekStart = () => (opts().weekStart === "sunday" ? 0 : 1);
const colorOf = (ev) => COLORS[ev.color] || COLORS.indigo;
const firstGrapheme = (s) => {
  const t = String(s || "").trim();
  if (!t) return "";
  const seg = typeof Intl.Segmenter === "function" ? [...new Intl.Segmenter().segment(t)][0]?.segment : t.slice(0, 2);
  return seg || "";
};

// ---------- data ----------

const valid = (ev) => ev && typeof ev.title === "string" && (PERIODS[ev.period] || C.isKey(ev.date));

async function load() {
  const stored = await umbra.store.get("events", null);
  if (stored === null) {
    // First run: start with something alive rather than an empty card.
    events = [{ id: uid(), title: PERIODS.year, period: "year", style: "ring", color: "indigo", created: C.today() }];
    save();
  } else {
    events = Array.isArray(stored) ? stored.filter(valid) : [];
  }
}

async function save() {
  try { await umbra.store.set("events", events); }
  catch (err) { console.error("Countdowns: could not save", err); }
}

// ---------- formatting ----------

// Formats follow the Windows region (umbra.locale / umbra.hour12), which the host sends after load, so build them on use.
const dtf = (opts) => {
  const cache = new Map();
  return {
    format: (d) => {
      const key = umbra.locale + "|" + umbra.hour12;
      if (!cache.has(key)) cache.set(key, new Intl.DateTimeFormat(umbra.locale, { ...opts, ...(opts.hour ? { hour12: umbra.hour12 } : {}) }));
      return cache.get(key).format(d);
    },
  };
};
const fmtDay = dtf({ day: "numeric", month: "short" });
const fmtMonthYear = dtf({ month: "short", year: "numeric" });
const fmtLong = dtf({ weekday: "short", day: "numeric", month: "long", year: "numeric" });
const fmtTime = dtf({ hour: "numeric", minute: "2-digit" });
const fmtWeekday = dtf({ weekday: "short" });

/** Short date under a tile: "11 Nov", "Nov 2027", or the time when it's today or tomorrow. */
function shortWhen(ev, s) {
  if (C.isTime(ev.time) && s.dayDiff >= 0 && s.dayDiff <= 1) return fmtTime.format(s.target);
  const d = C.parse(s.end);
  return s.end.slice(0, 4) === s.today.slice(0, 4) || s.dayDiff < 300 && s.dayDiff >= 0 ? fmtDay.format(d) : fmtMonthYear.format(d);
}

function longWhen(ev, s) {
  const d = C.parse(s.end);
  return fmtLong.format(d) + (C.isTime(ev.time) ? `, ${fmtTime.format(s.target)}` : "");
}

/** Main line, small line, text inside a ring, and the tooltip for one tile. */
function describe(ev, s) {
  const pct = C.percent(s.pct);
  if (ev.period) {
    const left = C.periodLeft(s);
    const day = ev.period === "week" ? fmtWeekday.format(C.parse(s.today)) : `Day ${s.elapsed + 1}`;
    return ev.style === "ring"
      ? { main: left, sub: day, ring: `${pct}%`, tip: `${ev.title}: ${pct}% gone, ${left}` }
      : { main: `${pct}%`, sub: left, ring: `${pct}%`, tip: `${ev.title}: ${pct}% gone, ${left}` };
  }
  const main = C.remaining(ev, s);
  const yearly = ev.repeat === "yearly" ? " (every year)" : "";
  return { main, sub: shortWhen(ev, s), ring: `${pct}%`, tip: `${ev.title}: ${longWhen(ev, s)}${yearly}. ${main}` };
}

// ---------- rendering ----------

function viz(ev, s, d) {
  const pct = s.pct;
  if (ev.style === "ring") {
    const r = 22, len = 2 * Math.PI * r;
    return `<div class="viz ring"><svg viewBox="0 0 52 52" aria-hidden="true">
        <circle class="track" cx="26" cy="26" r="${r}"/>
        <circle class="arc" cx="26" cy="26" r="${r}" style="--len:${len.toFixed(2)};--off:${(len * (1 - pct)).toFixed(2)}"/>
      </svg><span class="ring-label num">${esc(d.ring)}</span></div>`;
  }
  if (ev.style === "bar") {
    const knob = ev.icon ? `<span class="knob">${esc(ev.icon)}</span>` : `<span class="knob dot"></span>`;
    return `<div class="viz bar" style="--p:${pct.toFixed(4)}"><div class="track"><span class="fill"></span></div>${knob}</div>`;
  }
  return `<div class="viz dots" data-days="${s.days}" data-elapsed="${s.elapsed}"><svg aria-hidden="true"></svg></div>`;
}

function tile(ev, s) {
  const d = describe(ev, s);
  const past = !ev.period && s.dayDiff < 0;
  return `
    <div class="tile${past ? " past" : ""}" role="button" tabindex="0" data-id="${ev.id}" data-no-drag
         style="--c:${past ? "#6b707b" : colorOf(ev)}" title="${esc(d.tip)}">
      <div class="t-head">
        ${ev.icon ? `<span class="t-icon">${esc(ev.icon)}</span>` : ""}
        <span class="t-name">${esc(ev.title)}</span>
        ${ev.repeat === "yearly" && !ev.period ? `<span class="t-rep" aria-label="Every year">${ICON.repeat}</span>` : ""}
      </div>
      ${viz(ev, s, d)}
      <div class="t-foot">
        <span class="t-main num">${esc(d.main)}</span>
        <span class="t-sub num">${esc(d.sub)}</span>
      </div>
    </div>`;
}

function sorted(list) {
  if (opts().sort === "added") return list;
  // Upcoming first (soonest on top), then past one-off events, most recent first.
  return [...list].sort((a, b) => {
    const pa = a.s.msLeft < 0 && a.s.dayDiff < 0, pb = b.s.msLeft < 0 && b.s.dayDiff < 0;
    if (pa !== pb) return pa ? 1 : -1;
    return pa ? b.s.target - a.s.target : a.s.target - b.s.target;
  });
}

function render() {
  $("title").textContent = opts().title || "Countdowns";
  const now = new Date();
  const ws = weekStart();
  let items = events.map((ev) => ({ ev, s: C.status(ev, now, ws) }));
  if (opts().showPast === false) items = items.filter(({ ev, s }) => ev.period || s.dayDiff >= 0);

  if (!events.length) {
    gridEl.innerHTML = `<div class="empty"><div class="icon">${ICON.hourglass}</div><strong>Nothing to count down to yet</strong>
      <span>Birthdays, trips, deadlines: add one and watch the days fill in.</span>
      <button class="btn primary" data-act="add" data-no-drag>${ICON.plus}Add a countdown</button></div>`;
  } else if (!items.length) {
    gridEl.innerHTML = `<div class="empty"><div class="icon">${ICON.hourglass}</div><strong>Nothing coming up</strong>
      <span>Past events are hidden. Turn on "Keep past one-off events" in My widgets to see them.</span></div>`;
  } else {
    gridEl.innerHTML = sorted(items).map(({ ev, s }) => tile(ev, s)).join("");
  }
  paintDots();

  if (first) {
    first = false;
    if (motion()) {
      gridEl.classList.add("intro");
      setTimeout(() => gridEl.classList.remove("intro"), 1400);
    }
    umbra.rendered();
  }
}

/** Dot grids depend on the tile's size, so they're drawn after layout (and again on resize). */
function paintDots() {
  for (const el of gridEl.querySelectorAll(".viz.dots")) {
    const w = el.clientWidth, h = el.clientHeight;
    if (!w || !h) continue;
    const days = Number(el.dataset.days), elapsed = Number(el.dataset.elapsed);
    const g = C.dotGrid(days, w, h);
    const filled = elapsed >= days ? g.n : Math.floor(elapsed / g.size);
    const current = elapsed >= days ? -1 : filled;
    const cw = w / g.cols, ch = g.cell;
    const r = Math.max(1.2, Math.min(cw, ch) * 0.36);
    const top = (h - g.rows * ch) / 2;
    let dots = "";
    for (let i = 0; i < g.n; i++) {
      const x = (i % g.cols + 0.5) * cw, y = top + (Math.floor(i / g.cols) + 0.5) * ch;
      const cls = i < filled ? "on" : i === current ? "now" : "off";
      dots += `<circle class="${cls}" cx="${x.toFixed(2)}" cy="${y.toFixed(2)}" r="${r.toFixed(2)}" style="--i:${i}"/>`;
    }
    const svg = el.firstElementChild;
    svg.setAttribute("viewBox", `0 0 ${w} ${h}`);
    svg.style.setProperty("--n", g.n);
    svg.innerHTML = dots;
    el.title = g.unit === "day" ? `${elapsed} of ${days} days` : `One dot per ${g.unit}: ${elapsed} of ${days} days`;
  }
}

// ---------- sheet (add / edit) ----------

function openSheet(ev) {
  closeEmojiMenu();
  const kind = ev && ev.period ? ev.period : "date";
  draft = ev
    ? { id: ev.id, kind, title: ev.title, icon: ev.icon || "", date: ev.date || "", time: ev.time || "",
        start: ev.start || ev.created || "", yearly: ev.repeat === "yearly", style: ev.style || "dots", color: ev.color || "indigo", created: ev.created }
    : { id: null, kind: "date", title: "", icon: "", date: "", time: "", start: C.today(), yearly: false,
        style: "dots", color: COLOR_KEYS[events.length % COLOR_KEYS.length] };
  fTitle.value = draft.title;
  fDate.value = draft.date;
  fTime.value = draft.time;
  fStart.value = draft.start;
  $("sheet-title").textContent = ev ? "Edit countdown" : "New countdown";
  $("delete").hidden = !ev;
  disarmDelete();
  for (const f of [fTitle, fDate]) f.classList.remove("invalid");
  sheet.hidden = false;
  $("sheet-body").scrollTop = 0;
  paintSheet();
  if (!ev) {
    // The + was just pressed, so the widget may take the keyboard.
    umbra.requestFocus();
    fTitle.focus();
  }
}

function closeSheet() {
  if (sheet.hidden) return;
  closeEmojiMenu();
  sheet.hidden = true;
  draft = null;
  document.activeElement?.blur?.();
}

function paintSheet() {
  sheet.style.setProperty("--c", COLORS[draft.color] || COLORS.indigo);
  sheet.classList.toggle("is-period", draft.kind !== "date");
  sheet.classList.toggle("is-yearly", draft.yearly);
  for (const b of $("kind").children) b.setAttribute("aria-checked", String(b.dataset.kind === draft.kind));
  for (const b of $("style").children) b.setAttribute("aria-checked", String(b.dataset.style === draft.style));
  $("f-yearly").setAttribute("aria-checked", String(draft.yearly));
  $("yearly-hint").textContent = draft.yearly ? "Rolls over after the day" : "Birthdays, anniversaries";
  $("colors").innerHTML = COLOR_KEYS.map((k) =>
    `<button type="button" class="sw" role="radio" data-color="${k}" aria-checked="${k === draft.color}" aria-label="${k}" style="--sw:${COLORS[k]}"></button>`).join("");
  const emoji = $("emoji");
  emoji.innerHTML = draft.icon ? esc(draft.icon) : ICON.smile;
  emoji.classList.toggle("empty", !draft.icon);
  fTitle.placeholder = draft.kind === "date" ? "Title, e.g. Sam's birthday" : PERIODS[draft.kind];
  $("period-hint").textContent = {
    year: "Fills up as the year goes by and starts over on 1 January.",
    month: "Fills up as the month goes by and starts over on the 1st.",
    week: `Fills up as the week goes by and starts over every ${weekStart() ? "Monday" : "Sunday"}. Change the first day in My widgets.`,
  }[draft.kind] || "";
}

function readFields() {
  draft.title = fTitle.value;
  draft.date = fDate.value;
  draft.time = fTime.value;
  draft.start = fStart.value;
}

function invalid(field) {
  field.classList.add("invalid");
  umbra.requestFocus();
  field.focus();
}

function commit() {
  readFields();
  const isPeriod = draft.kind !== "date";
  const title = draft.title.trim() || (isPeriod ? PERIODS[draft.kind] : "");
  if (!title) return invalid(fTitle);
  if (!isPeriod && !C.isKey(draft.date)) return invalid(fDate);

  const ev = { id: draft.id || uid(), title, style: draft.style, color: draft.color, created: draft.created || C.today() };
  if (draft.icon) ev.icon = draft.icon;
  if (isPeriod) ev.period = draft.kind;
  else {
    ev.date = draft.date;
    if (C.isTime(draft.time)) ev.time = draft.time;
    ev.repeat = draft.yearly ? "yearly" : "none";
    if (!draft.yearly && C.isKey(draft.start)) ev.start = draft.start;
  }
  events = draft.id ? events.map((x) => (x.id === draft.id ? ev : x)) : [...events, ev];
  closeSheet();
  render();
  save();
  gridEl.querySelector(`[data-id="${ev.id}"]`)?.scrollIntoView({ block: "nearest", behavior: motion() ? "smooth" : "auto" });
}

function disarmDelete() {
  clearTimeout(deleteArmed);
  deleteArmed = null;
  $("delete").textContent = "Delete";
  $("delete").classList.remove("armed");
}

function remove() {
  // Two clicks, so a stray click never loses an event.
  if (!deleteArmed) {
    $("delete").textContent = "Really delete?";
    $("delete").classList.add("armed");
    deleteArmed = setTimeout(disarmDelete, 3000);
    return;
  }
  disarmDelete();
  const id = draft.id;
  events = events.filter((x) => x.id !== id);
  closeSheet();
  render();
  save();
}

sheet.addEventListener("submit", (e) => { e.preventDefault(); commit(); });
$("cancel").addEventListener("click", closeSheet);
$("close").addEventListener("click", closeSheet);
$("delete").addEventListener("click", remove);
$("add").addEventListener("click", () => openSheet(null));

$("kind").addEventListener("click", (e) => {
  const b = e.target.closest("[data-kind]");
  if (!b || !draft) return;
  readFields();
  const was = draft.kind, kind = b.dataset.kind;
  // Keep a typed title; swap the default "This year" style titles along with the kind.
  if (!draft.title.trim() || draft.title === PERIODS[was]) fTitle.value = draft.title = PERIODS[kind] || "";
  if (kind !== "date" && (draft.style === "dots" || was === "date") && !draft.id) draft.style = "ring";
  draft.kind = kind;
  paintSheet();
});

$("style").addEventListener("click", (e) => {
  const b = e.target.closest("[data-style]");
  if (b && draft) { draft.style = b.dataset.style; paintSheet(); }
});

$("colors").addEventListener("click", (e) => {
  const b = e.target.closest("[data-color]");
  if (b && draft) { draft.color = b.dataset.color; paintSheet(); }
});

$("f-yearly").addEventListener("click", () => {
  if (!draft) return;
  readFields();
  draft.yearly = !draft.yearly;
  paintSheet();
});

for (const f of [fTitle, fDate]) f.addEventListener("input", () => f.classList.remove("invalid"));

// The window never activates, so the date picker popup is kept out of the way: type the date instead.
for (const f of [fDate, fTime, fStart]) {
  f.addEventListener("keydown", (e) => {
    if (e.key === "F4" || (e.altKey && (e.key === "ArrowDown" || e.key === "ArrowUp"))) e.preventDefault();
  });
  f.addEventListener("click", (e) => e.preventDefault()); // no picker on click
}

sheet.addEventListener("keydown", (e) => {
  if (e.key !== "Escape") return;
  e.preventDefault();
  if (!emojiMenu.hidden) closeEmojiMenu();
  else closeSheet();
});

// ---------- emoji menu ----------
// Win + . opens the emoji panel only on Windows; elsewhere the hint would point at nothing.
const ON_WINDOWS = /win/i.test(navigator.userAgentData?.platform || navigator.platform || "");

function openEmojiMenu() {
  emojiMenu.innerHTML = `
    <div class="emoji-grid">${EMOJI.map((e) => `<button type="button" data-emoji="${e}">${e}</button>`).join("")}</div>
    <div class="emoji-foot">
      <input class="field small" id="emoji-other" maxlength="16" placeholder="${ON_WINDOWS ? "Other (Win + .)" : "Other emoji"}" spellcheck="false" aria-label="Any emoji">
      <button type="button" class="btn small" data-emoji="">No icon</button>
    </div>`;
  emojiMenu.hidden = false;
  placeMenu();
  const other = $("emoji-other");
  other.addEventListener("input", () => {
    const g = firstGrapheme(other.value);
    if (g) { draft.icon = g; paintSheet(); }
  });
  other.addEventListener("keydown", (e) => { if (e.key === "Enter") { e.preventDefault(); closeEmojiMenu(); } });
}

/** Below the icon button (it sits at the top of the sheet), kept inside the widget. */
function placeMenu() {
  const host = sheet.getBoundingClientRect();
  const a = $("emoji").getBoundingClientRect();
  const w = emojiMenu.offsetWidth, h = emojiMenu.offsetHeight;
  const left = Math.min(host.width - w - 10, Math.max(10, a.left - host.left));
  const below = a.bottom - host.top + 6;
  const top = below + h <= host.height - 10 ? below : Math.max(10, a.top - host.top - h - 6);
  emojiMenu.style.left = `${left}px`;
  emojiMenu.style.top = `${top}px`;
}

function closeEmojiMenu() {
  emojiMenu.hidden = true;
  emojiMenu.innerHTML = "";
}

$("emoji").addEventListener("click", () => (emojiMenu.hidden ? openEmojiMenu() : closeEmojiMenu()));

emojiMenu.addEventListener("click", (e) => {
  const b = e.target.closest("[data-emoji]");
  if (!b || !draft) return;
  draft.icon = b.dataset.emoji;
  paintSheet();
  closeEmojiMenu();
});

document.addEventListener("pointerdown", (e) => {
  if (!emojiMenu.hidden && !emojiMenu.contains(e.target) && !e.target.closest("#emoji")) closeEmojiMenu();
});

// ---------- grid ----------

gridEl.addEventListener("click", (e) => {
  if (e.target.closest('[data-act="add"]')) return openSheet(null);
  const t = e.target.closest(".tile");
  const ev = t && events.find((x) => x.id === t.dataset.id);
  if (ev) openSheet(ev);
});

gridEl.addEventListener("keydown", (e) => {
  const t = e.target.closest(".tile");
  if (t && (e.key === "Enter" || e.key === " ")) { e.preventDefault(); t.click(); }
});

// ---------- start ----------

/** Recompute on the minute, so "5h 12m" and day changes stay right. */
function tick() {
  setTimeout(() => { if (!document.hidden) render(); tick(); }, 60000 - (Date.now() % 60000) + 50);
}

umbra.ready.then(async () => {
  if (!umbra.store) {
    // Older Umbra (1.1) has no umbra.store, so there is nowhere to keep events.
    $("title").textContent = opts().title || "Countdowns";
    gridEl.innerHTML = `<div class="note">Countdowns needs Umbra 1.2 or newer to save your events.<br>
      <a data-no-drag id="get-update">Get the latest Umbra</a></div>`;
    $("get-update").addEventListener("click", () => umbra.openUrl("https://github.com/AmineChr54/umbra/releases/latest"));
    $("add").hidden = true;
    umbra.rendered();
    return;
  }
  await load();
  render();
  tick();
  document.addEventListener("visibilitychange", () => { if (!document.hidden) render(); });
  let pending = 0;
  new ResizeObserver(() => { cancelAnimationFrame(pending); pending = requestAnimationFrame(paintDots); }).observe(gridEl);
});

umbra.onSettings(() => { if (!first) render(); });
umbra.onTimeChanged?.(() => { if (!first) render(); }); // time zone or clock changed: remaining times and "today" move
umbra.store?.onChange(async () => {
  await load();
  if (draft?.id && !events.some((x) => x.id === draft.id)) closeSheet();
  render();
});
