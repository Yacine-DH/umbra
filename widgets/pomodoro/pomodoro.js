// Pomodoro focus session (Windows Focus style). Rules live in session.js (FocusSession); this file is the UI.
// umbra.store keys: "session" (the running session, see session.js; survives restarts), "setup" ({ minutes, breaks }),
// "log" ({ "YYYY-MM-DD": focus minutes }, unchanged from 1.x). The 1.x keys "state" and "config" are left alone and ignored.
// Session events go through emit(); other features (the timelapse, #240) subscribe with Pomodoro.on(fn).
const S = window.FocusSession;
const q = (id) => document.getElementById(id);
const app = q("app"), arc = q("arc");
const R = 54, CIRC = 2 * Math.PI * R, MIN = 60000;
const LIVE_MS = 60000; // a phase change older than this (caught up after a restart) gets no toast

const T = window.PomodoroTimelapse;
let session = null, setup = { minutes: 60, breaks: false }, log = {}, timer = null, menuOpen = false;
// Timelapse: settings in store key "timelapse"; tlState is the host's latest state, tlError a plain-words failure.
// The camera only starts from a click (Start focus session, or Start on the status pill), never automatically. The host has no
// pause, so capture runs through pauses and breaks and stops when the session ends; it also pauses while the widget is hidden.
let tl = T.clean(null), tlState = null, tlError = "", folderLabel = "Videos", tlBusy = false;

const dayKey = (d = new Date()) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
const fmtMin = (m) => (m >= 60 ? `${Math.floor(m / 60)}h ${String(m % 60).padStart(2, "0")}m` : `${m}m`);
const fmtClock = (ms) => {
  const s = Math.ceil(ms / 1000), h = Math.floor(s / 3600), m = Math.floor(s / 60) % 60, ss = String(s % 60).padStart(2, "0");
  return h ? `${h}:${String(m).padStart(2, "0")}:${ss}` : `${String(m).padStart(2, "0")}:${ss}`;
};
const plural = (n, w) => `${n} ${w}${n === 1 ? "" : "s"}`;
const auto = () => umbra.options.autoStart !== false;
const setText = (el, t) => { if (el.textContent !== t) el.textContent = t; };
const store = (k, v) => umbra.store.set(k, v).catch(() => {});

// ---- events: the one place session changes fan out (log, toasts, future hooks) ----
const listeners = [];
window.Pomodoro = { on: (fn) => listeners.push(fn), get session() { return session; } };

function emit(events) {
  for (const e of events) {
    if (e.type === "phaseEnd" && e.focusMs > 0) credit(e.focusMs, e.at);
    if (e.type === "done" || e.type === "end") stopTl();
    for (const fn of listeners) { try { fn(e, session); } catch (err) { console.error(err); } }
  }
  const last = events.filter((e) => e.type === "phaseStart" || e.type === "done").at(-1);
  if (last && Date.now() - last.at < LIVE_MS) notify(last);
}

function apply(r) {
  session = r.s;
  emit(r.events); // listeners and the toast still see the finished session
  if (session.done) { session = null; umbra.store.remove("session").catch(() => {}); } // done is never kept: back to the setup view
  else store("session", session);
  render();
}

function credit(ms, at) {
  const m = Math.round(ms / MIN);
  if (m <= 0) return;
  const k = dayKey(new Date(at));
  log[k] = (log[k] || 0) + m;
  const keep = Object.keys(log).sort().slice(-60);
  log = Object.fromEntries(keep.map((d) => [d, log[d]]));
  store("log", log);
}

function phaseTitle(s, i = s.idx) {
  const { n, of } = S.focusIndex({ ...s, idx: i });
  return s.phases[i].kind === "focus" ? `Focus period ${n} of ${of}` : `${s.phases[i].min} min break`;
}

function notify(e) {
  if (umbra.options.notify === false || !session) return;
  let title, body;
  if (e.type === "done") { title = "Focus session complete"; body = `You focused for ${fmtMin(Math.round((session.focused || 0) / MIN))}.`; }
  else {
    const p = session.phases[e.idx], nxt = session.phases[e.idx + 1];
    title = p.kind === "break" ? "Time for a break" : phaseTitle(session, e.idx);
    body = p.kind === "break" ? `${p.min} minutes. Then ${phaseTitle(session, e.idx + 1).toLowerCase()}.`
      : `${p.min} minutes of focus.${nxt ? ` Up next: ${nxt.min} min break.` : " Last one."}`;
    if (!e.auto) body += " Start it from the widget.";
  }
  umbra.notify?.({ title, body, tag: "pomodoro-phase" })?.catch?.(() => {});
}

// ---- actions ----
const now = () => Date.now();
function startSession() {
  setup.minutes = S.clampTotal(setup.minutes);
  store("setup", setup);
  apply(S.start(S.create(setup.minutes, setup.breaks), now()));
  tlError = ""; if (!tlActive()) tlState = null;
  if (tl.on) startTl(); // inside the Start click, which the host requires
}

// ---- timelapse ----
const tlActive = () => !!tlState && (tlState.state === "recording" || tlState.state === "paused");
async function startTl() {
  if (tlBusy || tlActive() || !session) return;
  tlBusy = true; tlError = ""; tlState = null; render();
  try {
    tlState = await umbra.timelapse.start(T.request(tl, session.total));
    if (tlState && tlState.error) tlError = T.friendly(tlState.error);
  } catch (err) { tlError = T.friendly(err && err.message); }
  tlBusy = false; render();
}
function stopTl() {
  if (!tlActive()) return;
  umbra.timelapse.stop().then((s) => { tlState = s; render(); }).catch(() => {});
}
function onTlState(s) {
  tlState = s;
  if (s && s.state === "idle" && s.error) tlError = T.friendly(s.error);
  render();
}
// The pill sits in the run view, and moves to the setup view so a session that just ended still shows its video being made.
function renderTl() {
  const box = q("tl-run"), act = q("tl-act"), v = T.status(tlState, { busy: tlBusy, error: tlError, folder: folderLabel });
  // In setup: what the session that just ended produced, including a host failure, but not a stale failure to start.
  const show = session ? tl.on || v.kind !== "idle" : v.kind !== "idle" && !tlBusy && (v.kind !== "err" || !!(tlState && tlState.error));
  box.hidden = !show;
  if (!show) return;
  const home = session ? q("run") : q("setup");
  if (box.parentElement !== home) home.insertBefore(box, session ? null : q("start"));
  if (box.className !== `tl-run ${v.kind}`) box.className = `tl-run ${v.kind}`;
  setText(q("tl-stat"), v.text);
  box.title = v.title || "";
  act.hidden = !(v.action && session);
  setText(act, v.action || "");
}
function renderTlSetup() {
  setText(q("tl-interval"), `${tl.interval} s`);
  q("tl-prev").disabled = tl.interval === T.INTERVALS[0];
  q("tl-next").disabled = tl.interval === T.INTERVALS.at(-1);
  q("tl-on").checked = tl.on;
  q("tl-fields").hidden = !tl.on;
  q("tl-folder").textContent = folderLabel;
  setText(q("tl-sum"), tl.on ? T.summary(tl, setup.minutes) : "Off");
}
const setTl = (patch) => { tl = T.clean({ ...tl, ...patch }); store("timelapse", tl); render(); };
function toggle() {
  if (!session) return;
  apply(session.running ? S.pause(session, now()) : S.start(session, now()));
}
function tick() {
  if (session && session.running) {
    const r = S.advance(session, now(), auto());
    if (r.events.length) { apply(r); return; }
  }
  render();
}

// ---- render ----
function renderTicks() {
  const g = q("ticks");
  let out = "";
  for (let i = 0; i < 12; i++) {
    const a = (i / 12) * 2 * Math.PI, r1 = 45.5, r2 = 48;
    const p = (r) => `${(60 + r * Math.cos(a)).toFixed(2)}`, t = (r) => `${(60 + r * Math.sin(a)).toFixed(2)}`;
    out += `<line x1="${p(r1)}" y1="${t(r1)}" x2="${p(r2)}" y2="${t(r2)}"/>`;
  }
  g.innerHTML = out;
  arc.style.strokeDasharray = String(CIRC);
}

function renderSetup() {
  const inp = q("minutes");
  if (document.activeElement !== inp) inp.value = setup.minutes;
  q("minus").disabled = setup.minutes <= S.MIN_TOTAL;
  q("plus").disabled = setup.minutes >= S.MAX_TOTAL;
  q("skip-breaks").checked = !setup.breaks;
  const k = S.breakCount(setup.minutes, true);
  q("breaks-hint").textContent = !setup.breaks ? "You'll have no breaks"
    : k ? `You'll have ${plural(k, "break")}` : "Too short for a break";
}

function renderRun() {
  const s = session, t = now();
  const p = s.phases[s.idx], left = S.remaining(s, t), total = p.min * MIN;
  const paused = !s.running && left < total;
  const waiting = !s.running && left === total;
  app.classList.toggle("brk", p.kind === "break");
  app.classList.toggle("paused", paused);
  setText(q("phase"), phaseTitle(s));
  setText(q("time"), fmtClock(left));
  q("time").classList.toggle("long", left > 59 * MIN + 59000); // h:mm:ss needs a smaller size to clear the ticks
  setText(q("sub"), paused ? "Paused" : waiting ? (p.kind === "break" ? "Break ready" : "Ready") : p.kind === "break" ? "Break" : "Focus");
  const off = String(CIRC * (1 - left / total));
  if (arc.style.strokeDashoffset !== off) arc.style.strokeDashoffset = off;
  const nxt = S.nextPhase(s);
  setText(q("next"), nxt
    ? (nxt.kind === "break" ? `Up next: ${nxt.min} min break` : `Up next: ${phaseTitle(s, s.idx + 1).toLowerCase()}`)
    : "Last focus period");
  const verb = s.running ? "Pause" : paused ? "Resume" : p.kind === "break" ? "Start break" : "Start";
  const toggleBtn = q("toggle");
  toggleBtn.classList.toggle("stopped", !s.running);
  if (toggleBtn.title !== verb) { toggleBtn.title = verb; toggleBtn.setAttribute("aria-label", verb); }
  q("reset").classList.toggle("off", s.running);
  setText(q("m-skip"), p.kind === "break" ? "Skip break" : nxt ? "Skip to break" : "Finish now");
  q("menu").hidden = !menuOpen;
  q("more").setAttribute("aria-expanded", String(menuOpen));
}

function render() {
  q("today").textContent = fmtMin(log[dayKey()] || 0);
  q("setup").hidden = !!session;
  q("run").hidden = !session;
  if (session) renderRun(); else { app.classList.remove("brk", "paused"); renderSetup(); renderTlSetup(); }
  renderTl();
  schedule();
}

// One timer per second, only while a phase runs and the PC is awake. A recording keeps it going while the PC is locked or
// the display is off: the camera doesn't pause for those, and only this timer can notice that the session ended and stop it.
function schedule() {
  const want = !!(session && session.running) && (!umbra.settings.paused || tlActive());
  if (want && !timer) timer = setInterval(tick, 1000);
  if (!want && timer) { clearInterval(timer); timer = null; }
}

// ---- inputs ----
const setMinutes = (v) => { setup.minutes = S.clampTotal(v); store("setup", setup); render(); };
q("minus").onclick = () => setMinutes(Math.ceil(setup.minutes / 5) * 5 - 5);
q("plus").onclick = () => setMinutes(Math.floor(setup.minutes / 5) * 5 + 5);
q("minutes").onchange = () => setMinutes(q("minutes").value);
q("minutes").onkeydown = (e) => { if (e.key === "Enter") q("minutes").blur(); };
q("skip-breaks").onchange = () => { setup.breaks = !q("skip-breaks").checked; store("setup", setup); render(); };
q("tl-on").onchange = () => setTl({ on: q("tl-on").checked });
q("tl-prev").onclick = () => setTl({ interval: T.stepInterval(tl.interval, -1) });
q("tl-next").onclick = () => setTl({ interval: T.stepInterval(tl.interval, 1) });
q("tl-folder").onclick = () => umbra.timelapse.chooseFolder()
  .then((r) => { if (r && r.folder) { folderLabel = r.folder; render(); } })
  .catch((err) => { const m = q("setup-msg"); m.textContent = T.friendly(err && err.message); m.hidden = false; });
q("tl-act").onclick = startTl;
q("start").onclick = startSession;
q("toggle").onclick = toggle;
q("more").onclick = (e) => { e.stopPropagation(); menuOpen = !menuOpen; render(); };
document.addEventListener("pointerdown", (e) => { if (menuOpen && !e.target.closest("#menu, #more")) { menuOpen = false; render(); } });
const menuAction = (fn) => () => { menuOpen = false; if (session) apply(fn(session, now())); };
q("reset").onclick = menuAction((s, t) => S.end(s, t)); // same as End session in the menu: keeps the focus time and makes the video
q("m-skip").onclick = menuAction((s, t) => S.skip(s, t, auto()));
q("m-add").onclick = menuAction((s, t) => S.extend(s, 5, t));
q("m-end").onclick = menuAction((s, t) => S.end(s, t));

umbra.onSettings(() => tick());
umbra.onTimeChanged?.(() => tick());
document.addEventListener("visibilitychange", () => tick());
umbra.store.onChange?.(async (k) => {
  // Ignore echoes of our own writes (same value as in memory): only a real outside change re-renders.
  const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
  let changed = false;
  if (!k || k === "log") { const v = await umbra.store.get("log", {}); if (!same(v, log)) { log = v; changed = true; } }
  if (!k || k === "setup") { const v = { ...setup, ...(await umbra.store.get("setup", {})) }; if (!same(v, setup)) { setup = v; changed = true; } }
  if (!k || k === "session") {
    const s = await umbra.store.get("session", null), v = S.valid(s) && !s.done ? s : null;
    if (!same(v, session)) { session = v; changed = true; if (!session) stopTl(); } // removed from outside: the recording ends with it
  }
  if (changed) tick();
});

(async () => {
  await umbra.ready;
  renderTicks();
  log = await umbra.store.get("log", {});
  setup = { ...setup, ...(await umbra.store.get("setup", {})) };
  const s = await umbra.store.get("session", null);
  session = S.valid(s) && !s.done ? s : null; // a stored finished session (older versions) loads as idle
  if (s && !session) umbra.store.remove("session").catch(() => {});
  tl = T.clean(await umbra.store.get("timelapse", null));
  try {
    umbra.timelapse.onChange(onTlState);
    const st = await umbra.timelapse.status();
    folderLabel = st.folder || folderLabel;
    // A recording from before a reload is not ours to resume without a click; only show a live one.
    if (st.state !== "idle") tlState = st;
  } catch { /* no camera permission or an older host: the section still saves settings */ }
  tick(); // catches up on phases that ended while Umbra was closed
  umbra.rendered();
})();
