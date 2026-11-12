// Hearth core: everything about talking to the relay that does not need a DOM. Plain functions plus one
// dependency-injected client, so tests drive it with a fake fetch, a fake clock and a fake broadcast channel.
// Browser: window.HearthCore. Node: module.exports (tests/widgets/hearth.test.mjs).
//
// The relay contract is tools/umbra-relay/README.md (issue #208): every call is a GET answered with status 200,
// failures are { error } or { paired: false, ... }, `rev` comes only from `sync`, pairing reuses /pair.
//
// What the views (#210 Overview, #212 Board, #214 Pet) build on:
//   const client = HearthCore.createClient({ fetchJson, store, channel, now, setTimeout, clearTimeout, instance });
//   await client.start();                 client.subscribe((view) => paint(view));   client.onEvent((e) => ...);
//   client.getView()                      plain snapshot: phase, names, sync (the merged relay state), invite, offline...
//   client.call("tap")                    any authenticated /hearth/<route>, then a prompt re-sync
//   client.setBoardHover(true|false)      the board is being looked at: poll fast (at most ~2 min)
//   client.setPaused(bool) / setRefreshSeconds(n) / refresh()    wire these to umbra.settings and umbra.every
//
// Copies of the widget have separate umbra.store data (the store is per copy), so the copies of one PC talk over a
// BroadcastChannel instead: one of them is elected to poll, the others get its answer, and an unpaired copy asks a
// paired one for the pairing, which has to be allowed by the user in the paired copy (see "link" below).
(function (root) {
  "use strict";

  const HOST = "umbra-buddy-relay.umbra-server.workers.dev";
  const HEARTH_ROUTES = Object.freeze(["sync", "stroke", "undo", "clear", "unclear", "tap", "care", "set", "unpair", "reinvite"]);
  const MAX_URL = 6000; // the relay answers { error: "too long" } above this
  const CODE_TTL_MS = 10 * 60 * 1000;
  /** umbra.store keys (per copy). `pair` is the only place the key lives. */
  const KEYS = Object.freeze({ pair: "pair", invite: "invite", snap: "snap" });
  const POLL = Object.freeze({
    idleMs: 60000,        // sync every 60 s while nothing happens (the user's refresh interval, never below minIdleMs)
    minIdleMs: 30000,
    maxIdleMs: 1800000,
    activeMs: 1500,       // sync every ~1.5 s while the board is being looked at
    activeCapMs: 120000,  // ...for at most 2 minutes per session
    peerWindowMs: 30000,  // a partner stroke keeps the board "active" for 30 s
    maxBackoffMs: 60000,
    settleMs: 1200,       // after start or resume, wait for the other copies' hellos before polling
    heartbeatMs: 5000,    // copies announce themselves to each other
    peerTtlMs: 15000,
    snapEveryMs: 30000,
    linkTtlMs: 60000,
  });

  // ---------- pairing codes ----------

  const CODE_RE = /^[A-HJ-KM-NP-Z2-9]{8}$/; // the relay's alphabet: no I, L, O, 0, 1

  /** What the user typed -> upper case letters and digits only ("abcd-2345 " -> "ABCD2345"). */
  const normalizeCode = (input) => String(input == null ? "" : input).toUpperCase().replace(/[^A-Z0-9]/g, "");

  /** { ok: true, code } or { ok: false, error } with a sentence you can show as it is. */
  function checkCode(input) {
    const code = normalizeCode(input);
    if (!code) return { ok: false, error: "Type the 8-character code from your partner." };
    if (code.length !== 8) return { ok: false, error: "A code has 8 letters and numbers, like ABCD-2345." };
    if (!CODE_RE.test(code)) return { ok: false, error: "That code has a character we never use (I, L, O, 0 or 1). Check it again." };
    return { ok: true, code };
  }

  /** "ABCD2345" -> "ABCD-2345" */
  const formatCode = (code) => { const c = normalizeCode(code); return c.length === 8 ? c.slice(0, 4) + "-" + c.slice(4) : c; };

  /** Time left on a code: { remainingMs, expired, text: "9:42" }. */
  function countdown(expiresAt, now) {
    const remainingMs = Math.max(0, Math.round(Number(expiresAt) - Number(now)) || 0);
    const s = Math.ceil(remainingMs / 1000);
    return { remainingMs, expired: remainingMs <= 0, text: Math.floor(s / 60) + ":" + String(s % 60).padStart(2, "0") };
  }

  // ---------- pairing record and URLs ----------

  const isPairing = (p) => !!p && typeof p === "object" && /^[0-9a-f]{32}$/.test(p.room) && (p.me === "a" || p.me === "b") && /^[0-9a-f]{16,64}$/.test(p.key);

  /** A short non-secret tag of a pairing, so copies can tell "same hearth" without sending the key (FNV-1a of room, side and key). */
  function fingerprint(p) {
    if (!isPairing(p)) return "";
    let h = 0x811c9dc5;
    for (const ch of `${p.room}:${p.me}:${p.key}`) { h ^= ch.charCodeAt(0); h = Math.imul(h, 0x01000193) >>> 0; }
    return h.toString(16).padStart(8, "0");
  }

  /** https://<relay>/<path>?<params>; null and undefined parameters are left out. */
  function relayUrl(path, params) {
    const q = new URLSearchParams();
    for (const [k, v] of Object.entries(params || {})) if (v !== undefined && v !== null) q.set(k, String(v));
    const s = q.toString();
    return `https://${HOST}/${path}` + (s ? "?" + s : "");
  }

  /** /pair?new=1 (side a creates) */
  const pairNewUrl = () => relayUrl("pair", { new: 1 });
  /** /pair?code=ABCD2345 (side b joins, or the side a reinvite names) */
  const pairJoinUrl = (code) => relayUrl("pair", { code });

  /** /hearth/<route>?room&me&key&...extra. Throws for an unknown route, a bad pairing or a URL the relay would refuse. */
  function hearthUrl(route, pairing, extra) {
    if (!HEARTH_ROUTES.includes(route)) throw new Error("Unknown Hearth route: " + route);
    if (!isPairing(pairing)) throw new Error("Not paired");
    const u = relayUrl("hearth/" + route, Object.assign({ room: pairing.room, me: pairing.me, key: pairing.key }, extra));
    if (u.length > MAX_URL) throw new Error("Request too long");
    return u;
  }

  /**
   * What an answer means:
   *   ok        a normal answer          gone      the room does not know us any more: forget the pairing
   *   unpaired  someone called unpair    error     a bad request or parameter ({ message, retryIn? })
   *   offline   not a JSON object (the caller treats a thrown fetch the same way)
   */
  function classify(r) {
    if (!r || typeof r !== "object" || Array.isArray(r)) return { kind: "offline" };
    if (r.paired === false) {
      if (r.reason === "unpaired") return { kind: "unpaired", by: r.by === "you" ? "you" : "peer", graceUntil: Number(r.graceUntil) || 0, now: Number(r.now) || 0 };
      return { kind: "gone" };
    }
    if (typeof r.error === "string") return { kind: "error", message: r.error, retryIn: Number(r.retryIn) || 0 };
    return { kind: "ok" };
  }

  // ---------- polling policy ----------

  const clamp = (n, lo, hi) => Math.min(hi, Math.max(lo, n));

  /**
   * The wait before the next sync, or null for "do not poll". { paused, paired, active, idleMs, failures }.
   * Idle: the refresh interval (60 s by default). Active: 1.5 s. After failures the wait doubles up to a minute
   * (never below the normal wait).
   */
  function pollDelay(o) {
    if (!o || o.paused || !o.paired) return null;
    const idle = clamp(Number(o.idleMs) || POLL.idleMs, POLL.minIdleMs, POLL.maxIdleMs);
    const base = o.active ? POLL.activeMs : idle;
    const f = Math.max(0, Math.floor(Number(o.failures) || 0));
    if (!f) return base;
    return Math.min(Math.max(base, POLL.maxBackoffMs), base * Math.pow(2, Math.min(f, 6)));
  }

  /**
   * "The board is active": the pointer is over a board, or the partner drew within the last 30 s, but a session lasts at
   * most `capMs` (2 min), so a pointer left over the board does not poll fast for ever. A new session starts when
   * activity begins again (the pointer re-enters, or a partner stroke arrives after a capped session).
   */
  function createActivity(opts) {
    const capMs = (opts && opts.capMs) || POLL.activeCapMs;
    const peerMs = (opts && opts.peerWindowMs) || POLL.peerWindowMs;
    let hover = false, peerAt = -Infinity, since = null;
    const raw = (now) => hover || now - peerAt < peerMs;
    const lapse = (now) => { if (since !== null && !raw(now)) since = null; };
    const begin = (now) => { if (since === null || now - since >= capMs) since = now; };
    return {
      setHover(on, now) {
        lapse(now);
        const was = hover;
        hover = !!on;
        if (hover && !was) begin(now);
        lapse(now);
      },
      peerStroke(now) { lapse(now); peerAt = now; begin(now); },
      isActive(now) {
        lapse(now);
        if (!raw(now)) return false;
        if (since === null) since = now;
        return now - since < capMs;
      },
    };
  }

  // ---------- leader election (copies on one PC) ----------

  /**
   * Which copy polls. `peers` is a Map id -> { at, fp } of the copies heard from lately. Candidates are the copies with
   * the same pairing fingerprint that were heard within the ttl, plus ourselves; the smallest id wins, so every copy
   * reaches the same answer without negotiating. null when this copy is not paired.
   */
  function electLeader(selfId, selfFp, peers, now, ttlMs) {
    if (!selfFp) return null;
    const ttl = ttlMs || POLL.peerTtlMs;
    let best = selfId;
    for (const [id, p] of peers) if (id !== selfId && p && p.fp === selfFp && now - p.at < ttl && id < best) best = id;
    return best;
  }

  /**
   * Which copy shows the tap toasts: the smallest id among the copies of this pairing that Umbra lets notify (`nt` in
   * their heartbeats, `selfNotify` for ourselves). Only the copy that calls umbra.notify needs the user's switch, so this
   * keeps toasts coming when the polling copy has notifications off. Falls back to `leaderId` when no copy may notify.
   */
  function electToaster(selfId, selfFp, selfNotify, peers, now, leaderId, ttlMs) {
    if (!selfFp) return null;
    const ttl = ttlMs || POLL.peerTtlMs;
    let best = selfNotify ? selfId : null;
    for (const [id, p] of peers) if (id !== selfId && p && p.fp === selfFp && p.nt && now - p.at < ttl && (best === null || id < best)) best = id;
    return best === null ? leaderId : best;
  }

  // ---------- the merged relay state ----------

  const emptyBoard = () => ({ strokes: [], clear: null });
  const emptyState = () => ({
    rev: 0, now: 0, skew: 0,
    names: { a: "", b: "" }, togetherSince: "", petName: "", pet: null,
    taps: { a: { n: 0, at: 0 }, b: { n: 0, at: 0 } }, together: { n: 0, at: 0 },
    board: emptyBoard(),
  });

  const str = (v) => (typeof v === "string" ? v.slice(0, 64) : "");
  const int = (v) => (Number.isFinite(Number(v)) && Number(v) > 0 ? Math.floor(Number(v)) : 0);
  const tapOf = (t) => ({ n: int(t && t.n), at: int(t && t.at) });

  /** Apply a board patch: reset replaces everything; otherwise drop `removed` ids and add the strokes we do not have. */
  function mergeBoard(board, patch) {
    const b = board || emptyBoard();
    if (!patch || typeof patch !== "object") return b;
    const incoming = Array.isArray(patch.strokes) ? patch.strokes.filter((s) => s && Number.isFinite(s.i)) : [];
    let strokes;
    if (patch.reset) strokes = incoming.slice();
    else {
      const gone = new Set(Array.isArray(patch.removed) ? patch.removed : []);
      const have = new Set();
      strokes = b.strokes.filter((s) => !gone.has(s.i));
      strokes.forEach((s) => have.add(s.i));
      for (const s of incoming) if (!have.has(s.i) && !gone.has(s.i)) { strokes.push(s); have.add(s.i); }
    }
    strokes.sort((x, y) => x.i - y.i);
    const clear = patch.clear && typeof patch.clear === "object" ? { by: patch.clear.by === "b" ? "b" : "a", at: int(patch.clear.at), until: int(patch.clear.until) } : null;
    return { strokes, clear };
  }

  /**
   * Merge one `sync` answer into the state. Returns { state, changed, events }; the old state is never mutated.
   * `{ rev }` alone (nothing changed) returns the same object. A lower rev than ours means the room was recreated, so we start over.
   * events (never on the very first sync): { type: "peerTap" }, { type: "together" }, { type: "peerStroke" }.
   */
  function applySync(prev, resp, opts) {
    const events = [];
    if (!resp || typeof resp !== "object" || !Number.isFinite(resp.rev)) return { state: prev, changed: false, events };
    const o = opts || {};
    const peer = o.me === "a" ? "b" : "a";
    const base = resp.rev < prev.rev ? emptyState() : prev;
    const first = base.rev === 0;
    if (Object.keys(resp).every((k) => k === "rev") && resp.rev === base.rev) return { state: prev, changed: false, events };
    const next = Object.assign({}, base, { rev: resp.rev });
    if (Number.isFinite(resp.now)) { next.now = resp.now; next.skew = resp.now - (Number.isFinite(o.localNow) ? o.localNow : resp.now); }
    if (resp.names && typeof resp.names === "object") next.names = { a: str(resp.names.a), b: str(resp.names.b) };
    if ("togetherSince" in resp) next.togetherSince = /^\d{4}-\d{2}-\d{2}$/.test(resp.togetherSince) ? resp.togetherSince : "";
    if ("petName" in resp) next.petName = str(resp.petName);
    if (resp.pet && typeof resp.pet === "object") next.pet = Object.assign({}, resp.pet);
    if (resp.taps && typeof resp.taps === "object") next.taps = { a: tapOf(resp.taps.a), b: tapOf(resp.taps.b) };
    if (resp.together && typeof resp.together === "object") next.together = tapOf(resp.together);
    if (resp.board && typeof resp.board === "object") next.board = mergeBoard(base.board, resp.board);
    if (!first) {
      if (next.taps[peer].n > base.taps[peer].n) events.push({ type: "peerTap", at: next.taps[peer].at });
      if (next.together.n > base.together.n) events.push({ type: "together", at: next.together.at });
      if (resp.board && Array.isArray(resp.board.strokes) && resp.board.strokes.some((s) => s && s.by === peer)) events.push({ type: "peerStroke" });
    }
    return { state: next, changed: true, events };
  }

  /** A state received from outside (another copy, the saved snapshot) rebuilt through the same validation as a relay answer; null when it is not a synced state. */
  function normalizeState(s) {
    if (!s || typeof s !== "object" || !Number.isFinite(s.rev) || s.rev < 1) return null;
    const b = s.board && typeof s.board === "object" ? s.board : {};
    const now = Number.isFinite(s.now) ? s.now : undefined;
    const resp = { rev: Math.floor(s.rev), now, names: s.names, togetherSince: s.togetherSince, petName: s.petName, pet: s.pet, taps: s.taps, together: s.together, board: { reset: true, strokes: b.strokes, clear: b.clear } };
    return applySync(emptyState(), resp, { me: "a", localNow: now === undefined ? 0 : now - (Number(s.skew) || 0) }).state;
  }

  /** Whole days from a YYYY-MM-DD date to today in the user's local calendar (0 on the day itself), or null. */
  function daysTogether(since, nowDate) {
    const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(since || "");
    if (!m) return null;
    const d = nowDate instanceof Date ? nowDate : new Date();
    const days = Math.round((Date.UTC(d.getFullYear(), d.getMonth(), d.getDate()) - Date.UTC(+m[1], +m[2] - 1, +m[3])) / 86400000);
    return days >= 0 ? days : null;
  }

  // ---------- friendly messages ----------

  const MSG = Object.freeze({
    offline: "Could not reach the relay. Try again in a moment.",
    server: "The relay did not understand that. Please try again in a moment.",
    badCode: "That code does not look right. Check it and try again.",
    unknownCode: "That code is wrong or has expired. Ask your partner for a new one.",
    ended: "This hearth is no longer paired.",
    save: "Could not save the pairing on this PC.",
    notUnpair: "You unpaired this hearth. Ask your partner to invite you again, or create a new one.",
    noAnswer: "No paired copy answered. Open it and try again.",
    denied: "The other copy said no.",
  });

  // ---------- the client ----------

  /**
   * deps: { fetchJson(url) -> Promise<json>, store: { get(key, fallback), set(key, value), remove(key) },
   *         channel?: BroadcastChannel-like { postMessage, onmessage, close }, now?, setTimeout?, clearTimeout?,
   *         instance?: string, random?: () => [0,1) }
   */
  function createClient(deps) {
    const now = deps.now || (() => Date.now());
    const setT = deps.setTimeout || ((fn, ms) => setTimeout(fn, ms));
    const clearT = deps.clearTimeout || ((h) => clearTimeout(h));
    const rnd = deps.random || Math.random;
    const hexId = (n) => Array.from({ length: n }, () => Math.floor(rnd() * 16).toString(16)).join("");
    const selfId = (deps.instance || "hearth") + "." + hexId(8);
    const channel = deps.channel || null;
    const store = deps.store;

    let loaded = false, started = false;
    let pairing = null, fp = "";
    let invite = null;            // { kind: "create" | "reinvite", code, expiresAt, rebinds }
    let sync = emptyState();
    let unpaired = null;          // { by, graceUntil, now } while the room is in its 7-day grace period
    let paused = false, idleMs = POLL.idleMs;
    let failures = 0, lastOkAt = 0, lastError = "";
    let busy = false, notice = null;
    const peers = new Map();      // id -> { at, fp }
    const hoverRemote = new Map();// id -> true while that copy's pointer is over its board
    let hoverLocal = false;
    const activity = createActivity();
    let leaderId = null, settleUntil = 0;
    let lastPollAt = 0, inFlight = false;
    let pollTimer = null, beatTimer = null, snapTimer = null, lastSnapAt = 0;
    let linkRequests = [];        // [{ nonce, from, at }] unpaired copies asking us for the pairing (shown to the user)
    let linkPending = null;       // { nonce, at } our own request
    let linkOffer = false;        // another copy on this PC is paired and we are not
    let fast = false;             // the leader is polling fast (shared in state messages)
    let canNotify = false;        // Umbra lets this copy show toasts (setCanNotify; shared in heartbeats as nt)
    const listeners = new Set(), eventListeners = new Set();

    // ----- view -----

    const isLeader = () => !!pairing && leaderId === selfId;
    const isToaster = () => !!pairing && electToaster(selfId, fp, canNotify, peers, now(), leaderId) === selfId;
    const peerSide = () => (pairing && pairing.me === "a" ? "b" : "a");

    function getView() {
      const t = now();
      const inv = invite && Object.assign({ formatted: formatCode(invite.code) }, invite, countdown(invite.expiresAt, t));
      return {
        phase: !loaded ? "loading" : !pairing ? "setup" : unpaired && unpaired.by === "peer" ? "unpaired" : "paired",
        paired: !!pairing,
        me: pairing ? pairing.me : null,
        names: pairing ? { you: sync.names[pairing.me], partner: sync.names[peerSide()] } : { you: "", partner: "" },
        togetherSince: sync.togetherSince,
        sync,
        invite: inv || null,
        unpaired: unpaired ? { by: unpaired.by, graceUntil: unpaired.graceUntil } : null,
        offline: failures >= 2 || (failures >= 1 && lastOkAt === 0),
        lastOkAt,
        lastError,
        leader: isLeader(),
        toaster: isToaster(),
        fast: isLeader() ? activity.isActive(t) : fast,
        paused,
        busy,
        notice,
        link: { offer: !pairing && linkOffer, pending: !!linkPending, requests: linkRequests.map((r) => ({ nonce: r.nonce })) },
      };
    }
    function emit() { const v = getView(); listeners.forEach((fn) => { try { fn(v); } catch (e) { /* a view bug must not stop syncing */ } }); }
    function fire(events) { events.forEach((e) => eventListeners.forEach((fn) => { try { fn(e); } catch (err) { /* same */ } })); }
    const say = (kind, text) => { notice = text ? { kind, text, at: now() } : null; };

    // ----- the channel to the other copies -----

    function post(m) { if (channel) { try { channel.postMessage(Object.assign({ v: 1, from: selfId }, m)); } catch (e) { /* closed */ } } }
    const beat = () => post({ t: "hb", fp, nt: canNotify });

    function recompute() {
      const nl = pairing ? electLeader(selfId, fp, peers, now()) : null;
      if (nl === leaderId) return false;
      leaderId = nl;
      if (nl === selfId) { settleUntil = Math.max(settleUntil, now() + POLL.settleMs); schedule(); }
      else clearT(pollTimer);
      return true;
    }
    const anyHover = () => hoverLocal || hoverRemote.size > 0;
    const pairedPeers = () => [...peers.values()].some((p) => p.fp);
    function pruneLinks(t) {
      const before = linkRequests.length + (linkPending ? 1 : 0);
      linkRequests = linkRequests.filter((r) => t - r.at < POLL.linkTtlMs);
      if (linkPending && t - linkPending.at >= POLL.linkTtlMs) { linkPending = null; say("error", MSG.noAnswer); }
      return before !== linkRequests.length + (linkPending ? 1 : 0);
    }

    function tick() {
      beatTimer = null;
      if (!started || paused) return;
      const t = now();
      let changed = false;
      beat();
      for (const [id, p] of peers) if (t - p.at >= POLL.peerTtlMs) { peers.delete(id); hoverRemote.delete(id); changed = true; }
      for (const id of [...hoverRemote.keys()]) if (!peers.has(id)) { hoverRemote.delete(id); changed = true; }
      activity.setHover(anyHover(), t);
      if (invite && countdown(invite.expiresAt, t).expired && t - invite.expiresAt > 5000) { invite = null; store.remove(KEYS.invite).catch(() => {}); changed = true; }
      if (pruneLinks(t)) changed = true;
      if (recompute()) changed = true;
      linkOffer = !pairing && pairedPeers();
      if (changed) emit();
      beatTimer = setT(tick, POLL.heartbeatMs);
    }

    function onMessage(ev) {
      const m = ev && ev.data;
      if (!started || !m || m.v !== 1 || typeof m.t !== "string" || typeof m.from !== "string" || m.from === selfId) return;
      const t = now();
      switch (m.t) {
        case "hb": {
          const before = peers.get(m.from);
          peers.set(m.from, { at: t, fp: typeof m.fp === "string" ? m.fp : "", nt: m.nt === true });
          const offer = !pairing && pairedPeers();
          const flipped = recompute();
          const ntChanged = !before || before.nt !== (m.nt === true);
          if (flipped || ntChanged || offer !== linkOffer) { linkOffer = offer; emit(); }
          break;
        }
        case "bye":
          peers.delete(m.from); hoverRemote.delete(m.from);
          linkOffer = !pairing && pairedPeers();
          activity.setHover(anyHover(), t);
          recompute(); emit();
          break;
        case "hello":
          peers.set(m.from, { at: t, fp: typeof m.fp === "string" ? m.fp : "", nt: m.nt === true });
          recompute();
          beat(); // let the newcomer know who we are (and that this PC is paired) right away
          if (isLeader() && m.fp === fp) post({ t: "state", fp, sync, unpaired, lastOkAt, failures, lastError, fast: activity.isActive(t), events: [] });
          break;
        case "state":
          if (pairing && m.fp === fp) {
            const ns = normalizeState(m.sync);
            const changed = !!ns && ns.rev > sync.rev;
            if (changed) sync = ns;
            const u = m.unpaired;
            unpaired = u && typeof u === "object" ? { by: u.by === "you" ? "you" : "peer", graceUntil: int(u.graceUntil), now: int(u.now) } : null;
            lastOkAt = int(m.lastOkAt) || lastOkAt;
            failures = int(m.failures);
            lastError = typeof m.lastError === "string" ? m.lastError.slice(0, 100) : "";
            fast = !!m.fast;
            if (changed) snapSoon();
            emit();
            const events = Array.isArray(m.events) ? m.events.filter((e) => e && (e.type === "peerTap" || e.type === "together" || e.type === "peerStroke")).map((e) => ({ type: e.type, at: int(e.at) })) : [];
            if (events.length) fire(events);
          }
          break;
        case "poke":
          if (isLeader() && m.fp === fp) pollIfDue(Math.max(1000, Number(m.gap) || 0));
          break;
        case "hover":
          if (m.fp === fp) {
            if (m.on) hoverRemote.set(m.from, true); else hoverRemote.delete(m.from);
            activity.setHover(anyHover(), t);
            if (isLeader()) { schedule(); emit(); }
          }
          break;
        case "end":
          if (pairing && m.fp === fp) endPairing(m.why === "you" ? "you" : "gone", false);
          break;
        case "claim":
          if (pairing && typeof m.nonce === "string" && !linkRequests.some((r) => r.nonce === m.nonce)) {
            linkRequests.push({ nonce: m.nonce.slice(0, 32), from: m.from, at: t });
            emit();
          }
          break;
        case "claimed": // another copy already answered this request
          if (linkRequests.some((r) => r.nonce === m.nonce)) { linkRequests = linkRequests.filter((r) => r.nonce !== m.nonce); emit(); }
          break;
        case "deny":
          if (linkPending && m.to === selfId && m.nonce === linkPending.nonce) { linkPending = null; say("error", MSG.denied); emit(); }
          break;
        case "grant":
          if (!pairing && linkPending && m.to === selfId && m.nonce === linkPending.nonce && isPairing(m.pairing)) {
            linkPending = null;
            adopt({ room: m.pairing.room, me: m.pairing.me, key: m.pairing.key }, null);
          }
          break;
      }
    }

    // ----- pairing state -----

    /** Take on a pairing (new or granted): reset what belongs to the old one, save it, and tell the other copies. */
    async function adopt(p, inv) {
      pairing = { room: p.room, me: p.me, key: p.key };
      fp = fingerprint(pairing);
      invite = inv;
      sync = emptyState();
      unpaired = null; failures = 0; lastOkAt = 0; lastError = ""; lastPollAt = 0; fast = false;
      linkOffer = false; linkPending = null; leaderId = null;
      say("info", "");
      try {
        await store.set(KEYS.pair, pairing);
        if (inv) await store.set(KEYS.invite, inv); else await store.remove(KEYS.invite);
        await store.remove(KEYS.snap);
      } catch (e) { say("error", MSG.save); }
      beat(); post({ t: "hello", fp, nt: canNotify });
      recompute();
      settleUntil = Math.max(settleUntil, now() + POLL.settleMs);
      schedule();
      emit();
    }

    /** Forget the pairing on this copy. `broadcast`: tell the other copies of this PC (they check against the relay's answers anyway). */
    async function endPairing(why, broadcast) {
      const old = fp;
      pairing = null; fp = ""; invite = null; sync = emptyState(); unpaired = null;
      failures = 0; lastOkAt = 0; lastError = ""; leaderId = null; fast = false; linkRequests = [];
      linkOffer = pairedPeers();
      clearT(pollTimer);
      say(why === "gone" ? "error" : "info", why === "gone" ? MSG.ended : "");
      emit();
      try { await Promise.all([store.remove(KEYS.pair), store.remove(KEYS.invite), store.remove(KEYS.snap)]); } catch (e) { /* the next start finds nothing or the relay says unknown */ }
      if (broadcast && old) post({ t: "end", fp: old, why });
    }

    // ----- polling -----

    function schedule() {
      clearT(pollTimer); pollTimer = null;
      if (!started || !loaded || !isLeader()) return;
      const t = now();
      const delay = pollDelay({ paused, paired: !!pairing, active: activity.isActive(t), idleMs, failures });
      if (delay === null) return;
      const wait = Math.max(lastPollAt + delay - t, settleUntil - t, 0);
      pollTimer = setT(() => { pollTimer = null; poll(); }, wait);
    }

    /** Poll now if the last poll is at least `gapMs` ago (and we are the leader, not paused, not settling). */
    function pollIfDue(gapMs) {
      if (!started || paused || !isLeader() || inFlight) return;
      const t = now();
      if (t < settleUntil || t - lastPollAt < gapMs) { schedule(); return; }
      poll();
    }

    async function poll() {
      if (!started || paused || !isLeader() || inFlight) return;
      const p = pairing;
      inFlight = true; lastPollAt = now();
      try {
        const r = await deps.fetchJson(hearthUrl("sync", p, sync.rev > 0 ? { since: sync.rev } : {}));
        if (pairing === p) await onSyncAnswer(r, p);
      } catch (e) {
        if (pairing === p) { failures++; lastError = ""; shareState([]); emit(); }
      } finally {
        inFlight = false;
        schedule();
      }
    }

    function shareState(events) {
      post({ t: "state", fp, sync, unpaired, lastOkAt, failures, lastError, fast: activity.isActive(now()), events });
    }

    async function onSyncAnswer(r, p) {
      const c = classify(r);
      if (c.kind === "offline") { failures++; shareState([]); emit(); return; }
      if (c.kind === "gone") { await endPairing("gone", true); return; }
      if (c.kind === "unpaired") {
        if (c.by === "you") { await endPairing("you", true); return; }
        unpaired = { by: c.by, graceUntil: c.graceUntil, now: c.now };
        failures = 0; lastOkAt = now(); shareState([]); emit(); return;
      }
      if (c.kind === "error") { lastError = c.message; failures = 0; shareState([]); emit(); return; }
      failures = 0; lastError = ""; lastOkAt = now(); unpaired = null;
      const res = applySync(sync, r, { me: p.me, localNow: now() });
      if (res.changed) { sync = res.state; snapSoon(); }
      if (res.events.some((e) => e.type === "peerStroke")) activity.peerStroke(now());
      shareState(res.events);
      emit();
      fire(res.events);
    }

    // ----- snapshot: the last answer, minus the board, so a restart can show names and days before the network answers -----

    function snapSoon() {
      if (!pairing || snapTimer) return;
      const wait = Math.max(0, lastSnapAt + POLL.snapEveryMs - now());
      snapTimer = setT(async () => {
        snapTimer = null;
        if (!pairing) return;
        lastSnapAt = now();
        const s = Object.assign({}, sync, { rev: 0, board: emptyBoard() });
        try { await store.set(KEYS.snap, { fp, savedAt: now(), lastOkAt, sync: s }); } catch (e) { /* too big or gone: not essential */ }
      }, wait);
    }

    // ----- public API -----

    /** Friendly sentence for something that went wrong while talking to the relay. */
    function problem(e, c) {
      if (c && c.kind === "error") return c.message === "unknown or expired code" ? MSG.unknownCode : c.message === "bad code" ? MSG.badCode : MSG.server;
      return e ? MSG.offline : MSG.server;
    }

    async function createHearth() {
      if (busy || pairing) return false;
      busy = true; say("info", ""); emit();
      let ok = false;
      try {
        const r = await deps.fetchJson(pairNewUrl());
        const c = classify(r);
        if (c.kind !== "ok" || r.me !== "a" || !isPairing(r) || !CODE_RE.test(normalizeCode(r.code))) say("error", problem(null, c));
        else {
          await adopt({ room: r.room, me: "a", key: r.key }, { kind: "create", code: normalizeCode(r.code), expiresAt: now() + (Number(r.ttl) || 600) * 1000, rebinds: null });
          ok = true;
        }
      } catch (e) { say("error", problem(e)); }
      busy = false; emit();
      return ok;
    }

    async function join(input) {
      if (busy || pairing) return false;
      const k = checkCode(input);
      if (!k.ok) { say("error", k.error); emit(); return false; }
      busy = true; say("info", ""); emit();
      let ok = false;
      try {
        const r = await deps.fetchJson(pairJoinUrl(k.code));
        const c = classify(r);
        if (c.kind !== "ok" || !isPairing(r)) say("error", problem(null, c));
        else { await adopt({ room: r.room, me: r.me, key: r.key }, null); ok = true; }
      } catch (e) { say("error", problem(e)); }
      busy = false; emit();
      return ok;
    }

    /** Leave this hearth. The partner keeps it for 7 days and can invite us again. Keeps the pairing when the relay can't be reached. */
    async function unpair() {
      if (busy || !pairing) return false;
      busy = true; say("info", ""); emit();
      let done = false;
      try {
        const r = await deps.fetchJson(hearthUrl("unpair", pairing));
        const c = classify(r);
        if (c.kind === "ok" || c.kind === "gone" || c.kind === "unpaired") done = true;
        else say("error", problem(null, c));
      } catch (e) { say("error", problem(e)); }
      busy = false;
      if (done) await endPairing("you", true); else emit();
      return done;
    }

    /** A new one-time code that rebinds the partner's side (new PC, reinstall). Their current PC stops working at once. */
    async function reinvite() {
      if (busy || !pairing) return false;
      busy = true; say("info", ""); emit();
      let ok = false;
      try {
        const r = await deps.fetchJson(hearthUrl("reinvite", pairing));
        const c = classify(r);
        if (c.kind === "ok" && CODE_RE.test(normalizeCode(r.code))) {
          invite = { kind: "reinvite", code: normalizeCode(r.code), expiresAt: now() + (Number(r.ttl) || 600) * 1000, rebinds: r.rebinds === "a" ? "a" : "b" };
          unpaired = null; failures = 0;
          try { await store.set(KEYS.invite, invite); } catch (e) { /* shown anyway */ }
          ok = true;
          pollIfDue(1000);
        } else if (c.kind === "gone") { await endPairing("gone", true); }
        else if (c.kind === "unpaired") { unpaired = { by: c.by, graceUntil: c.graceUntil, now: c.now }; say("error", MSG.notUnpair); }
        else say("error", problem(null, c));
      } catch (e) { say("error", problem(e)); }
      busy = false; emit();
      return ok;
    }

    /** Hide the code card (the partner has it, or it expired). */
    async function dismissInvite() { invite = null; try { await store.remove(KEYS.invite); } catch (e) { /* ignore */ } emit(); }

    /**
     * Any authenticated /hearth/<route> (tap, care, set, stroke, ...). Resolves the relay's JSON answer (look for `error`);
     * rejects when the relay can't be reached. A successful change triggers a prompt sync. `paired: false` answers are handled here.
     */
    async function call(route, params) {
      if (!pairing) throw new Error("Not paired");
      const p = pairing;
      const r = await deps.fetchJson(hearthUrl(route, p, params));
      const c = classify(r);
      if (pairing === p) {
        if (c.kind === "gone") await endPairing("gone", true);
        else if (c.kind === "unpaired" && route !== "reinvite") {
          if (c.by === "you") await endPairing("you", true);
          else { unpaired = { by: c.by, graceUntil: c.graceUntil, now: c.now }; emit(); }
        } else if (c.kind === "ok" && r.ok === true) syncSoon();
      }
      return r;
    }

    /** Ask for a sync soon (after our own change): the leader polls if its last poll is over a second old. */
    function syncSoon() { if (isLeader()) pollIfDue(1000); else if (pairing) post({ t: "poke", fp }); }

    /** An outside nudge (umbra.every: "Refresh all", resume from sleep, network back, and its own tick): poll if the last poll is over half an interval old. */
    function refresh() {
      const gap = Math.floor(clamp(idleMs, POLL.minIdleMs, POLL.maxIdleMs) / 2);
      if (isLeader()) pollIfDue(gap); else if (pairing) post({ t: "poke", fp, gap });
    }

    /** The pointer is over (or left) the board: poll fast, for up to ~2 minutes. */
    function setBoardHover(on) {
      on = !!on;
      if (on === hoverLocal) return;
      hoverLocal = on;
      activity.setHover(anyHover(), now());
      post({ t: "hover", fp, on });
      if (isLeader()) { if (on) pollIfDue(POLL.activeMs); else schedule(); emit(); }
    }

    /** umbra.settings.paused: no timers and no requests while true; one sync shortly after it turns false. */
    function setPaused(p) {
      p = !!p;
      if (p === paused) return;
      paused = p;
      if (p) { clearT(pollTimer); pollTimer = null; clearT(beatTimer); beatTimer = null; clearT(snapTimer); snapTimer = null; }
      else if (started) {
        peers.clear(); hoverRemote.clear(); leaderId = null;
        settleUntil = now() + POLL.settleMs;
        beat(); post({ t: "hello", fp, nt: canNotify });
        recompute();
        beatTimer = setT(tick, POLL.heartbeatMs);
        schedule();
      }
      emit();
    }

    /** umbra.settings.refreshSeconds: the idle interval. */
    function setRefreshSeconds(s) { const n = Number(s); if (n > 0) { idleMs = clamp(n * 1000, POLL.minIdleMs, POLL.maxIdleMs); schedule(); } }

    // ----- linking another copy on this PC -----

    /** An unpaired copy asks the paired ones for the pairing. They show "Allow?"; nothing is sent without that click. */
    function requestLink() {
      if (pairing || linkPending || !linkOffer) return false;
      linkPending = { nonce: hexId(16), at: now() };
      say("info", "");
      post({ t: "claim", nonce: linkPending.nonce });
      emit();
      return true;
    }
    function approveLink(nonce) {
      const req = linkRequests.find((r) => r.nonce === nonce);
      if (!req || !pairing) return false;
      linkRequests = linkRequests.filter((r) => r.nonce !== nonce);
      post({ t: "grant", to: req.from, nonce, pairing });
      post({ t: "claimed", nonce });
      emit();
      return true;
    }
    function denyLink(nonce) {
      const req = linkRequests.find((r) => r.nonce === nonce);
      if (!req) return false;
      linkRequests = linkRequests.filter((r) => r.nonce !== nonce);
      post({ t: "deny", to: req.from, nonce });
      post({ t: "claimed", nonce });
      emit();
      return true;
    }

    // ----- lifecycle -----

    async function start() {
      if (started) return;
      let snap = null;
      try {
        const p = await store.get(KEYS.pair, null);
        if (isPairing(p)) { pairing = { room: p.room, me: p.me, key: p.key }; fp = fingerprint(pairing); }
        const inv = await store.get(KEYS.invite, null);
        if (pairing && inv && typeof inv === "object" && CODE_RE.test(normalizeCode(inv.code)) && Number(inv.expiresAt) > now()) {
          invite = { kind: inv.kind === "reinvite" ? "reinvite" : "create", code: normalizeCode(inv.code), expiresAt: Number(inv.expiresAt), rebinds: inv.rebinds === "a" || inv.rebinds === "b" ? inv.rebinds : null };
        }
        if (pairing) snap = await store.get(KEYS.snap, null);
      } catch (e) { /* a store that can't be read means "not paired yet" */ }
      if (pairing && snap && snap.fp === fp && snap.sync && typeof snap.sync === "object") {
        const clean = applySync(emptyState(), Object.assign({}, snap.sync, { rev: 1, now: undefined, board: undefined }), { me: pairing.me });
        sync = Object.assign({}, clean.state, { rev: 0 });
        lastOkAt = Number(snap.lastOkAt) || 0;
      }
      started = true; loaded = true;
      if (channel) channel.onmessage = onMessage;
      settleUntil = now() + POLL.settleMs;
      if (!paused) {
        beat(); post({ t: "hello", fp, nt: canNotify });
        beatTimer = setT(tick, POLL.heartbeatMs);
      }
      recompute();
      schedule();
      emit();
    }

    function stop() {
      if (!started) return;
      post({ t: "bye" });
      started = false;
      clearT(pollTimer); clearT(beatTimer); clearT(snapTimer);
      pollTimer = beatTimer = snapTimer = null;
      if (channel) { channel.onmessage = null; try { channel.close(); } catch (e) { /* already closed */ } }
    }

    return {
      id: selfId,
      start, stop, getView,
      subscribe(fn) { listeners.add(fn); return () => listeners.delete(fn); },
      onEvent(fn) { eventListeners.add(fn); return () => eventListeners.delete(fn); },
      createHearth, join, unpair, reinvite, dismissInvite,
      call, syncSoon, refresh, setBoardHover, setPaused, setRefreshSeconds,
      /** Whether Umbra lets this copy notify (umbra.requestNotifications / a refused umbra.notify). Shared with the other copies. */
      setCanNotify(v) { const n = v === true; if (n === canNotify) return; canNotify = n; beat(); emit(); },
      requestLink, approveLink, denyLink,
      dismissNotice() { notice = null; emit(); },
    };
  }

  const api = {
    HOST, HEARTH_ROUTES, MAX_URL, CODE_TTL_MS, KEYS, POLL, MSG,
    normalizeCode, checkCode, formatCode, countdown,
    isPairing, fingerprint, relayUrl, pairNewUrl, pairJoinUrl, hearthUrl, classify,
    pollDelay, createActivity, electLeader, electToaster,
    emptyState, emptyBoard, mergeBoard, applySync, normalizeState, daysTogether,
    createClient,
  };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else root.HearthCore = api;
})(typeof window !== "undefined" ? window : globalThis);
