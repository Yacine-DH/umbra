// Hearth board logic (#212): everything about strokes that does not need a DOM or the network. Dual-use module:
// browser `window.HearthBoardLogic`, Node `module.exports` (tests/widgets/hearth-board.test.mjs).
//
// The wire format is the relay's (tools/umbra-relay/README.md, "Board"):
//   s      = record ("_" record)*            at most 8 records and 4096 characters per request
//   record = ["k"] colour "." size "." x "." y ("." dx "." dy)*     colour 0..15, size 0..7, coordinates 0..1000
// The first point is absolute and every later point is a delta. A record has at most 600 points; a longer stroke is
// sent as several records, every one after the first flagged `k` ("continues the previous record of mine"), and the
// first point of a `k` record is where the previous record ended.
//
// What lives here: the palette and brush sizes, turning pointer positions into quantised points (smoothing and a
// minimum step), splitting a stroke into records, packing records into requests, decoding the relay's records back
// into points, chaining `k` records into strokes for drawing, the "local" overlay that makes the board feel instant
// (my strokes before the relay has echoed them, my undos, my clear) and the saved-pictures gallery budget.
(function (root) {
  "use strict";

  const GRID = 1000;
  const LIMITS = Object.freeze({
    colors: 16,
    sizes: 8,
    grid: GRID,
    recordsPerRequest: 8,
    pointsPerRecord: 600,
    relayChars: 4096,   // what the relay accepts for `s`
    requestChars: 3600, // what we send: the whole URL stays under ~4 KB
    groupRecords: 50,   // records the relay chains into one undoable stroke
  });
  const MIN_STEP = 3;        // grid units (0.3 % of the board): points closer than this are dropped
  const SMOOTHING = 0.55;    // 0 < alpha <= 1; 1 = raw pointer positions
  const FLUSH_MS = 300;      // a drawn stroke is sent in batches this often (and when the pen lifts)
  const SLOW_FLUSH_MS = 900; // ...and this often once a stroke is long, so one scribble does not eat the relay's 300 records
  const SLOW_AFTER_RECORDS = 8;

  /** The 16 colours. Index 15 is the eraser: the relay has no erase primitive, so erasing is a stroke in the paper colour. */
  const PAPER = "#FFFBEA";
  const PALETTE = Object.freeze([
    { name: "Ink", hex: "#3A2A20" },
    { name: "Brown", hex: "#8A6446" },
    { name: "Red", hex: "#E0524D" },
    { name: "Rose", hex: "#E5648F" },
    { name: "Pink", hex: "#F7A8C4" },
    { name: "Orange", hex: "#F4A04A" },
    { name: "Yellow", hex: "#F6D54E" },
    { name: "Lime", hex: "#A8D672" },
    { name: "Green", hex: "#4FA86B" },
    { name: "Teal", hex: "#3FB3A8" },
    { name: "Sky", hex: "#7EC4F0" },
    { name: "Blue", hex: "#4A7FD6" },
    { name: "Purple", hex: "#8B5CC0" },
    { name: "Lavender", hex: "#B69BE6" },
    { name: "White", hex: "#FFFFFF" },
    { name: "Eraser", hex: PAPER },
  ].map((c) => Object.freeze(c)));
  const ERASER = 15;
  /** Brush diameters in per mille of the board width, so a drawing keeps its proportions when the widget is resized. */
  const WIDTHS = Object.freeze([4, 7, 10, 14, 19, 26, 35, 50]);

  const clamp = (n, lo, hi) => Math.min(hi, Math.max(lo, n));
  const isInt = (n) => Number.isInteger(n);
  const intIn = (n, lo, hi) => isInt(n) && n >= lo && n <= hi;

  // ---------- coordinates ----------

  /** A number to a grid coordinate: rounded, kept inside 0..1000. */
  const quantize = (v) => clamp(Math.round(Number(v) || 0), 0, GRID);

  /** A position in pixels (0..sizePx across the board) to a grid coordinate. */
  const toGrid = (px, sizePx) => (sizePx > 0 ? quantize((px / sizePx) * GRID) : 0);

  /** Brush diameter in grid units, never thinner than `minPx` pixels on a board `boardPx` wide. */
  function brushWidth(size, boardPx, minPx) {
    const w = WIDTHS[clamp(Math.floor(Number(size) || 0), 0, WIDTHS.length - 1)];
    return boardPx > 0 ? Math.max(w, ((minPx == null ? 1.5 : minPx) / boardPx) * GRID) : w;
  }

  // ---------- pen: pointer positions to quantised points ----------

  /**
   * Smooths raw positions (an exponential follower) and keeps a point only when it is at least `minStep` grid units
   * from the last kept one. Input and output are grid units (output rounded to integers).
   * start() returns the first point; add() a new point or null; end() the pen-up point or null.
   */
  function createSmoother(opts) {
    const o = opts || {};
    const alpha = clamp(o.alpha == null ? SMOOTHING : o.alpha, 0.05, 1);
    const minStep = o.minStep == null ? MIN_STEP : o.minStep;
    let sx = 0, sy = 0, last = null;
    return {
      start(x, y) { sx = x; sy = y; last = [quantize(x), quantize(y)]; return last.slice(); },
      add(x, y) {
        sx += (x - sx) * alpha; sy += (y - sy) * alpha;
        const p = [quantize(sx), quantize(sy)];
        if (Math.hypot(p[0] - last[0], p[1] - last[1]) < minStep) return null;
        last = p;
        return p.slice();
      },
      end(x, y) {
        const p = [quantize(x), quantize(y)];
        if (p[0] === last[0] && p[1] === last[1]) return null;
        last = p;
        return p.slice();
      },
    };
  }

  // ---------- records ----------

  const numLen = (n) => String(n).length;

  /** Length of the text of a record with these points (without building it). */
  function recordLength(k, c, z, pts) {
    let n = (k ? 1 : 0) + numLen(c) + 1 + numLen(z) + 1 + numLen(pts[0][0]) + 1 + numLen(pts[0][1]);
    for (let i = 1; i < pts.length; i++) n += 2 + numLen(pts[i][0] - pts[i - 1][0]) + numLen(pts[i][1] - pts[i - 1][1]);
    return n;
  }

  /** { k, c, z, pts: [[x, y], ...] } to "[k]c.z.x.y(.dx.dy)*". Throws for anything the relay would refuse. */
  function encodeRecord(rec) {
    const { c, z, pts } = rec;
    if (!intIn(c, 0, LIMITS.colors - 1) || !intIn(z, 0, LIMITS.sizes - 1)) throw new RangeError("Bad colour or size");
    if (!Array.isArray(pts) || pts.length < 1 || pts.length > LIMITS.pointsPerRecord) throw new RangeError("A record has 1 to 600 points");
    for (const p of pts) if (!Array.isArray(p) || !intIn(p[0], 0, GRID) || !intIn(p[1], 0, GRID)) throw new RangeError("A point is outside 0..1000");
    let s = (rec.k ? "k" : "") + c + "." + z + "." + pts[0][0] + "." + pts[0][1];
    for (let i = 1; i < pts.length; i++) s += "." + (pts[i][0] - pts[i - 1][0]) + "." + (pts[i][1] - pts[i - 1][1]);
    return s;
  }

  /** "x.y.dx.dy..." (the `p` of a synced stroke) to absolute points; null when it is not well formed. */
  function decodePoints(p) {
    if (typeof p !== "string" || !p) return null;
    const t = p.split(".");
    if (t.length < 2 || t.length % 2) return null;
    const n = t.map(Number);
    if (n.some((v) => !isInt(v))) return null;
    const pts = [[n[0], n[1]]];
    if (!intIn(n[0], 0, GRID) || !intIn(n[1], 0, GRID)) return null;
    for (let i = 2; i < n.length; i += 2) {
      const prev = pts[pts.length - 1];
      const x = prev[0] + n[i], y = prev[1] + n[i + 1];
      if (!intIn(x, 0, GRID) || !intIn(y, 0, GRID)) return null;
      pts.push([x, y]);
    }
    return pts;
  }

  /** "[k]c.z.x.y(.dx.dy)*" to { k, c, z, pts }, or null. The inverse of encodeRecord. */
  function decodeRecord(s) {
    if (typeof s !== "string") return null;
    const k = s[0] === "k";
    const t = (k ? s.slice(1) : s).split(".");
    if (t.length < 4 || t.length % 2 || !/^k?\d/.test(s)) return null;
    const c = Number(t[0]), z = Number(t[1]);
    if (!/^\d{1,2}$/.test(t[0]) || !/^\d{1,2}$/.test(t[1]) || !intIn(c, 0, LIMITS.colors - 1) || !intIn(z, 0, LIMITS.sizes - 1)) return null;
    const pts = decodePoints(t.slice(2).join("."));
    if (!pts || pts.length > LIMITS.pointsPerRecord) return null;
    return { k: k ? 1 : 0, c, z, pts };
  }

  /**
   * One stroke piece to records the relay accepts: at most `maxPoints` points and `maxChars` characters each. Every record
   * after the first is a continuation (k) that starts at the last point of the one before.
   */
  function splitRecord(rec, opts) {
    const o = opts || {};
    const maxPoints = Math.min(o.maxPoints || LIMITS.pointsPerRecord, LIMITS.pointsPerRecord);
    const maxChars = Math.min(o.maxChars || LIMITS.requestChars, LIMITS.relayChars);
    const { c, z, pts } = rec;
    if (!pts.length) return [];
    const out = [];
    let start = 0, k = rec.k ? 1 : 0;
    for (;;) {
      const chunk = [pts[start]];
      let len = recordLength(k, c, z, chunk);
      let j = start + 1;
      while (j < pts.length && chunk.length < maxPoints) {
        const add = 2 + numLen(pts[j][0] - pts[j - 1][0]) + numLen(pts[j][1] - pts[j - 1][1]);
        if (len + add > maxChars && chunk.length > 1) break;
        chunk.push(pts[j]); len += add; j++;
      }
      out.push({ k, c, z, pts: chunk });
      if (j >= pts.length) break;
      start = j - 1; k = 1;
    }
    return out;
  }

  /**
   * Records to requests: [{ s, count }] where `s` is up to 8 records joined by "_" and at most `maxChars` characters
   * long, and `count` says how many of the records went into it. Order is kept.
   */
  function packRequests(records, opts) {
    const o = opts || {};
    const maxRecords = Math.min(o.maxRecords || LIMITS.recordsPerRequest, LIMITS.recordsPerRequest);
    const maxChars = Math.min(o.maxChars || LIMITS.requestChars, LIMITS.relayChars);
    const out = [];
    let parts = [], len = 0;
    const close = () => { if (parts.length) out.push({ s: parts.join("_"), count: parts.length }); parts = []; len = 0; };
    for (const r of records) {
      const text = encodeRecord(r);
      if (text.length > maxChars) throw new RangeError("A record is too long; split it first");
      if (parts.length && (parts.length >= maxRecords || len + 1 + text.length > maxChars)) close();
      len += (parts.length ? 1 : 0) + text.length;
      parts.push(text);
    }
    close();
    return out;
  }

  /** The relay's own parser as the README describes it, to check what we send. { records } or { error }. */
  function parseRequest(s) {
    if (!s) return { error: "no strokes" };
    if (s.length > LIMITS.relayChars) return { error: "too long" };
    const parts = s.split("_");
    if (parts.length > LIMITS.recordsPerRequest) return { error: "too many strokes" };
    const records = [];
    for (const part of parts) {
      if (!/^k?\d{1,2}\.\d{1,2}\.\d{1,4}\.\d{1,4}(\.-?\d{1,4}\.-?\d{1,4})*$/.test(part)) return { error: "bad stroke" };
      if (part.replace(/^k/, "").split(".").length / 2 - 1 > LIMITS.pointsPerRecord) return { error: "stroke too long" };
      const r = decodeRecord(part);
      if (!r) return { error: "bad stroke" };
      records.push(r);
    }
    return { records };
  }

  /** How long to wait before sending what has been drawn so far, given how many records this stroke has sent. */
  const flushDelay = (recordsSent) => (recordsSent >= SLOW_AFTER_RECORDS ? SLOW_FLUSH_MS : FLUSH_MS);

  // ---------- pen: one stroke from pen-down to pen-up ----------

  /**
   * A stroke being drawn. start/move/end take grid units (use toGrid). flush() returns the records for the points added
   * since the last flush (the first one plain, later ones continuations) and may be called as often as you like.
   */
  function createPen(opts) {
    const o = opts || {};
    const sm = createSmoother(o);
    const c = clamp(Math.floor(o.color) || 0, 0, LIMITS.colors - 1);
    const z = clamp(Math.floor(o.size) || 0, 0, LIMITS.sizes - 1);
    const all = [];
    let pending = [], lastSent = null, sentAny = false, sentRecords = 0, down = false;
    return {
      get color() { return c; },
      get size() { return z; },
      get active() { return down; },
      get points() { return all; },
      get recordsSent() { return sentRecords; },
      start(x, y) { down = true; const p = sm.start(x, y); all.push(p); pending.push(p); return p; },
      move(x, y) { if (!down) return null; const p = sm.add(x, y); if (p) { all.push(p); pending.push(p); } return p; },
      end(x, y) { if (!down) return null; down = false; const p = x == null ? null : sm.end(x, y); if (p) { all.push(p); pending.push(p); } return p; },
      hasPending() { return pending.length > 0; },
      flush() {
        if (!pending.length) return [];
        const pts = sentAny ? [lastSent].concat(pending) : pending;
        const recs = splitRecord({ k: sentAny ? 1 : 0, c, z, pts }, o);
        lastSent = pending[pending.length - 1];
        pending = []; sentAny = true; sentRecords += recs.length;
        return recs;
      },
    };
  }

  // ---------- strokes to draw ----------

  const decoded = new WeakMap();
  /** A synced stroke { i, by, c, z, k, p } to { i, by, c, z, k, pts }; null when it can't be drawn. Cached per object. */
  function normalizeStroke(s) {
    if (!s || typeof s !== "object") return null;
    if (decoded.has(s)) return decoded.get(s);
    const pts = decodePoints(s.p);
    const ok = pts && intIn(s.c, 0, LIMITS.colors - 1) && intIn(s.z, 0, LIMITS.sizes - 1) && Number.isFinite(s.i);
    const r = ok ? { i: s.i, by: s.by === "b" ? "b" : "a", c: s.c, z: s.z, k: s.k ? 1 : 0, pts } : null;
    decoded.set(s, r);
    return r;
  }

  /** Records in drawing order to strokes: a `k` record continues the previous record of the same author. */
  function buildChains(records) {
    const chains = [];
    const open = {};
    for (const r of records) {
      if (!r || !r.pts || !r.pts.length) continue;
      const cur = open[r.by];
      if (r.k && cur && cur.c === r.c && cur.z === r.z) {
        const last = cur.pts[cur.pts.length - 1];
        for (let j = last[0] === r.pts[0][0] && last[1] === r.pts[0][1] ? 1 : 0; j < r.pts.length; j++) cur.pts.push(r.pts[j]);
        cur.ids.push(r.i);
      } else {
        const ch = { by: r.by, c: r.c, z: r.z, pts: r.pts.slice(), ids: [r.i] };
        chains.push(ch);
        open[r.by] = ch;
      }
    }
    return chains;
  }

  // ---------- the local overlay ----------

  /**
   * What this copy knows that the relay's answers do not yet say. Records move through three places:
   *   pending    drawn, request not answered yet (no id)
   *   confirmed  the relay gave it an id; shown until a sync carries it (then the sync's copy wins)
   *   gone       ids I undid; never shown again (a sync computed before the undo may still list them)
   * A clear hides what was visible (and keeps it for Undo) until a newer sync has seen the clear.
   * Times are the caller's clock in ms; `skew` (server minus local) converts for comparisons with relay times.
   */
  function createLocal(opts) {
    const expireMs = (opts && opts.expireMs) || 30000;
    let seq = 0, pending = [], confirmed = [], hide = null, hideRev = 0, stash = [], myClear = null;
    const gone = new Set();

    const api = {
      /** A record I just drew. Returns the object (with `seq`). */
      addPending(rec, by) {
        const r = { seq: ++seq, i: null, by: by === "b" ? "b" : "a", c: rec.c, z: rec.z, k: rec.k ? 1 : 0, pts: rec.pts };
        pending.push(r);
        return r;
      },
      /** The relay answered a stroke request: [{ seq, i }]. */
      confirm(items, nowMs) {
        for (const it of items) {
          const idx = pending.findIndex((r) => r.seq === it.seq);
          if (idx < 0) continue;
          const r = pending.splice(idx, 1)[0];
          if (!gone.has(it.i)) confirmed.push(Object.assign({}, r, { i: it.i, at: nowMs }));
        }
      },
      /** Requests the relay refused or that can't be sent any more. */
      drop(seqs) { const s = new Set(seqs); pending = pending.filter((r) => !s.has(r.seq)); },
      /** I undid these ids. */
      forget(ids) { for (const i of ids) gone.add(i); confirmed = confirmed.filter((r) => !gone.has(r.i)); },
      /** I cleared the board: `visible` is what was shown, `info` { by, at, until } the relay's answer. */
      startClear(visible, rev, info) {
        stash = visible.filter((r) => r.i != null);
        hide = new Set(stash.map((r) => r.i));
        hideRev = rev;
        confirmed = [];
        myClear = info || null;
      },
      /** The board came back (my Undo on the clear). The stashed strokes show until a sync carries them. */
      unclear(nowMs) {
        confirmed = confirmed.concat(stash.filter((r) => !gone.has(r.i)).map((r) => Object.assign({}, r, { at: nowMs })));
        stash = []; hide = null; myClear = null;
      },
      /** A sync answer changed the board: forget local copies the relay now has, and stale ones. */
      observe(syncStrokes, rev, clear, nowMs, skew) {
        const have = new Set();
        for (const s of syncStrokes) have.add(s.i);
        const sk = Number(skew) || 0;
        confirmed = confirmed.filter((c) => !have.has(c.i) && nowMs - c.at < expireMs && !(clear && c.at + sk <= clear.at));
        if (rev > hideRev) { hide = null; myClear = null; stash = clear ? stash : []; }
      },
      /** Everything to draw, in drawing order: the relay's strokes (minus undone and hidden), then mine not yet echoed, then pending. */
      records(syncStrokes) {
        const have = new Set();
        const out = [];
        for (const s of syncStrokes) {
          have.add(s.i);
          if (gone.has(s.i) || (hide && hide.has(s.i))) continue;
          const r = normalizeStroke(s);
          if (r) out.push(r);
        }
        for (const c of confirmed) if (!have.has(c.i) && !gone.has(c.i)) out.push(c);
        out.sort((a, b) => a.i - b.i);
        return out.concat(pending);
      },
      /** The clear to show a banner for: the relay's, else my own until a sync reports it. */
      clearInfo(syncClear) { return syncClear || myClear; },
      reset() { pending = []; confirmed = []; hide = null; stash = []; myClear = null; gone.clear(); },
      get pendingCount() { return pending.length; },
      get confirmedCount() { return confirmed.length; },
      get hiding() { return !!hide; },
      get goneIds() { return [...gone]; },
    };
    return api;
  }

  /** Banner data for a clear { by, at, until } seen from side `me`: { mine, remainingMs, active }. Times: relay clock; `skew` = server - local. */
  function clearState(clear, me, nowLocal, skew) {
    if (!clear || !Number.isFinite(clear.until)) return null;
    const remainingMs = clear.until - (nowLocal + (Number(skew) || 0));
    return { by: clear.by, mine: clear.by === me, remainingMs: Math.max(0, remainingMs), active: remainingMs > 0 };
  }

  const hasMine = (records, me) => records.some((r) => r.by === me);

  // ---------- gallery ----------

  const GALLERY = Object.freeze({
    maxItems: 6,
    budgetChars: 640000,   // all saved pictures together; umbra.store allows 1 MB per copy and Hearth keeps little else
    maxImageChars: 100000, // one picture as a data URL
    sizes: Object.freeze([360, 288, 224, 160]), // try these edge lengths until a picture fits
    key: "gallery",
  });
  const PNG_PREFIX = "data:image/png;base64,";

  /** Anything read back from the store to a clean list (oldest first). */
  function sanitizeGallery(raw, limits) {
    const g = Object.assign({}, GALLERY, limits);
    if (!Array.isArray(raw)) return [];
    const list = raw.filter((x) => x && typeof x.png === "string" && x.png.startsWith(PNG_PREFIX) && x.png.length <= g.maxImageChars && Number.isFinite(x.at))
      .map((x) => ({ id: String(x.id || x.at), at: x.at, png: x.png }));
    return list.slice(-g.maxItems);
  }

  const itemChars = (x) => x.png.length + 64;

  /**
   * Adding `item` to `list`: the new list and which pictures it pushes out (oldest first). The caller asks the user
   * before saving when `dropped` is not empty.
   */
  function planGallery(list, item, limits) {
    const g = Object.assign({}, GALLERY, limits);
    const out = list.concat([item]);
    const dropped = [];
    const total = () => out.reduce((n, x) => n + itemChars(x), 0);
    while (out.length > 1 && (out.length > g.maxItems || total() > g.budgetChars)) dropped.push(out.shift());
    return { list: out, dropped, chars: total() };
  }

  const api = {
    GRID, LIMITS, MIN_STEP, SMOOTHING, FLUSH_MS, SLOW_FLUSH_MS, PAPER, PALETTE, ERASER, WIDTHS, GALLERY,
    quantize, toGrid, brushWidth, createSmoother, createPen, flushDelay,
    recordLength, encodeRecord, decodeRecord, decodePoints, splitRecord, packRequests, parseRequest,
    normalizeStroke, buildChains, createLocal, clearState, hasMine,
    sanitizeGallery, planGallery,
  };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else root.HearthBoardLogic = api;
})(typeof window !== "undefined" ? window : globalThis);
