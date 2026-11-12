import test from "node:test";
import assert from "node:assert/strict";
import { createRequire } from "node:module";
import { readFileSync } from "node:fs";

const require = createRequire(import.meta.url);
const B = require("../../widgets/hearth/board-logic.js");
const H = require("../../widgets/hearth/hearth-core.js");

// ---------- the relay's grammar, written out from tools/umbra-relay/README.md (not from our own encoder) ----------

const README_RECORD = /^k?\d{1,2}\.\d{1,2}\.\d{1,4}\.\d{1,4}(\.-?\d{1,4}\.-?\d{1,4})*$/;

/** What the relay checks for one `s`; returns the decoded records (c, z, k, absolute points) or throws with the relay's message. */
function relayParse(s) {
  if (!s) throw new Error("no strokes");
  if (s.length > 4096) throw new Error("too long");
  const parts = s.split("_");
  if (parts.length > 8) throw new Error("too many strokes");
  return parts.map((part) => {
    if (!README_RECORD.test(part)) throw new Error("bad stroke: " + part);
    const k = part[0] === "k";
    const t = (k ? part.slice(1) : part).split(".").map(Number);
    const [c, z, x, y] = t;
    if (c > 15 || z > 7 || x > 1000 || y > 1000) throw new Error("bad stroke");
    const pts = [[x, y]];
    for (let i = 4; i < t.length; i += 2) {
      const px = pts[pts.length - 1][0] + t[i], py = pts[pts.length - 1][1] + t[i + 1];
      if (px < 0 || px > 1000 || py < 0 || py > 1000) throw new Error("bad stroke: point outside");
      pts.push([px, py]);
    }
    if (pts.length > 600) throw new Error("stroke too long");
    return { k: k ? 1 : 0, c, z, pts };
  });
}

// a small deterministic random generator
function rng(seed) { let s = seed >>> 0; return () => { s = (Math.imul(s, 1664525) + 1013904223) >>> 0; return s / 4294967296; }; }

/** A scribble of n raw positions in grid units (float), wandering over the whole board. */
function scribble(n, seed, wild) {
  const r = rng(seed);
  const out = [];
  let x = r() * 1000, y = r() * 1000;
  for (let i = 0; i < n; i++) {
    if (wild) { x = r() < 0.5 ? 0 : 1000; y = r() < 0.5 ? 0 : 1000; }
    else { x = Math.min(1000, Math.max(0, x + (r() - 0.5) * 60)); y = Math.min(1000, Math.max(0, y + (r() - 0.5) * 60)); }
    out.push([x, y]);
  }
  return out;
}

function draw(pen, raw) { pen.start(raw[0][0], raw[0][1]); for (const p of raw.slice(1)) pen.move(p[0], p[1]); pen.end(raw[raw.length - 1][0], raw[raw.length - 1][1]); }

/** Records of one stroke joined back into its points (a `k` record starts where the previous one ended). */
function joined(records) {
  const pts = [];
  records.forEach((r, i) => {
    if (i) { assert.equal(r.k, 1, "every record after the first is a continuation"); assert.deepEqual(r.pts[0], pts[pts.length - 1], "a continuation starts where the previous record ended"); }
    else assert.equal(r.k, 0);
    pts.push(...(i ? r.pts.slice(1) : r.pts));
  });
  return pts;
}

// ---------- palette, coordinates ----------

test("the palette has 16 colours, the last is the eraser in the paper colour, and 8 growing brush sizes", () => {
  assert.equal(B.PALETTE.length, 16);
  assert.equal(B.ERASER, 15);
  assert.equal(B.PALETTE[B.ERASER].hex, B.PAPER);
  assert.equal(new Set(B.PALETTE.map((c) => c.hex.toLowerCase())).size, 16, "no two colours alike");
  assert.ok(B.PALETTE.every((c) => /^#[0-9A-F]{6}$/i.test(c.hex) && c.name));
  assert.equal(B.WIDTHS.length, 8);
  assert.ok(B.WIDTHS.every((w, i) => i === 0 || w > B.WIDTHS[i - 1]));
  assert.equal(B.LIMITS.colors, 16);
  assert.equal(B.LIMITS.sizes, 8);
});

test("quantise: rounded, clamped to 0..1000, scaled with the board", () => {
  assert.equal(B.quantize(-5), 0);
  assert.equal(B.quantize(1200.4), 1000);
  assert.equal(B.quantize(499.5), 500);
  assert.equal(B.quantize(NaN), 0);
  assert.equal(B.toGrid(150, 300), 500);
  assert.equal(B.toGrid(-10, 300), 0);
  assert.equal(B.toGrid(999, 300), 1000);
  assert.equal(B.toGrid(10, 0), 0);
  assert.equal(B.toGrid(150, 600), 250, "the same pixel on a bigger board is a smaller part of it");
});

test("brush width scales with the board and never gets thinner than a hair line", () => {
  assert.equal(B.brushWidth(7, 300), 50);
  assert.ok(B.brushWidth(0, 100, 1.5) >= 15, "4 per mille of a 100 px board is under a pixel, so it is lifted to 1.5 px (15 per mille)");
  assert.equal(B.brushWidth(0, 1000, 1.5), 4);
  assert.equal(B.brushWidth(99, 300), 50, "sizes are clamped");
});

// ---------- the wire format ----------

test("the README example round-trips: 3.2.100.200.10.5.-5.-15 is (100,200) (110,205) (105,190)", () => {
  const rec = B.decodeRecord("3.2.100.200.10.5.-5.-15");
  assert.deepEqual(rec, { k: 0, c: 3, z: 2, pts: [[100, 200], [110, 205], [105, 190]] });
  assert.equal(B.encodeRecord(rec), "3.2.100.200.10.5.-5.-15");
  assert.deepEqual(B.decodePoints("100.200.10.5.-5.-15"), [[100, 200], [110, 205], [105, 190]]);
  assert.equal(B.encodeRecord({ k: 0, c: 0, z: 0, pts: [[500, 500]] }), "0.0.500.500", "a record with one point is a dot");
  assert.equal(B.encodeRecord({ k: 1, c: 15, z: 7, pts: [[0, 1000], [1000, 0]] }), "k15.7.0.1000.1000.-1000");
  assert.deepEqual(B.decodeRecord("k15.7.0.1000.1000.-1000"), { k: 1, c: 15, z: 7, pts: [[0, 1000], [1000, 0]] });
});

test("encoding refuses what the relay would refuse; decoding refuses junk", () => {
  const ok = { k: 0, c: 1, z: 1, pts: [[1, 1]] };
  assert.throws(() => B.encodeRecord({ ...ok, c: 16 }), RangeError);
  assert.throws(() => B.encodeRecord({ ...ok, z: 8 }), RangeError);
  assert.throws(() => B.encodeRecord({ ...ok, c: -1 }), RangeError);
  assert.throws(() => B.encodeRecord({ ...ok, pts: [[1001, 0]] }), RangeError);
  assert.throws(() => B.encodeRecord({ ...ok, pts: [[0, -1]] }), RangeError);
  assert.throws(() => B.encodeRecord({ ...ok, pts: [[0.5, 1]] }), RangeError);
  assert.throws(() => B.encodeRecord({ ...ok, pts: [] }), RangeError);
  assert.throws(() => B.encodeRecord({ ...ok, pts: Array.from({ length: 601 }, () => [1, 1]) }), RangeError);
  for (const bad of ["", "1.2", "1.2.3", "a.b.c.d", "1.2.3.4.5", "16.0.0.0", "0.8.0.0", "0.0.1001.0", "0.0.10.10.-11.0", "0.0.10.10.0.1000", "k", "0.0.1.1.", null, 5]) {
    assert.equal(B.decodeRecord(bad), null, String(bad));
  }
  assert.equal(B.decodePoints("1.2.3"), null);
  assert.equal(B.decodePoints("1.2.x.4"), null);
  assert.equal(B.decodePoints("1000.0.1.0"), null, "a delta may not leave the board");
});

test("a stroke of any length is split into records of at most 600 points, each a continuation of the last", () => {
  const pts = Array.from({ length: 1700 }, (_, i) => [i % 1000, (i * 7) % 1000]);
  const recs = B.splitRecord({ k: 0, c: 4, z: 3, pts }, { maxChars: 4096 });
  assert.ok(recs.length >= 3);
  assert.ok(recs.every((r) => r.pts.length <= 600 && r.c === 4 && r.z === 3));
  assert.deepEqual(joined(recs), pts);
  for (const r of recs) assert.doesNotThrow(() => B.encodeRecord(r));
  assert.equal(B.splitRecord({ k: 0, c: 1, z: 1, pts: [[5, 5]] }).length, 1);
  assert.deepEqual(B.splitRecord({ k: 0, c: 1, z: 1, pts: [] }), []);
});

test("records also split by length: worst-case 4-digit deltas never push a request past the cap", () => {
  const pts = scribble(600, 7, true).map((p) => [Math.round(p[0]), Math.round(p[1])]);
  const whole = B.encodeRecord({ k: 0, c: 15, z: 7, pts });
  assert.ok(whole.length > 4096, "the test needs a stroke that is too long for one request: " + whole.length);
  const recs = B.splitRecord({ k: 0, c: 15, z: 7, pts });
  assert.ok(recs.length > 1);
  assert.deepEqual(joined(recs), pts);
  for (const r of recs) assert.ok(B.encodeRecord(r).length <= B.LIMITS.requestChars);
  for (const { s } of B.packRequests(recs)) assert.ok(s.length <= B.LIMITS.requestChars);
});

test("continuation records of an already started stroke begin with k", () => {
  const pts = Array.from({ length: 650 }, (_, i) => [i, i]);
  const recs = B.splitRecord({ k: 1, c: 2, z: 2, pts }, { maxChars: 4096 });
  assert.equal(recs[0].k, 1);
  assert.equal(recs.length, 2);
  assert.ok(B.encodeRecord(recs[0]).startsWith("k2.2.0.0"));
});

test("requests hold at most 8 records and 3600 characters, in order, and `count` says how many went in", () => {
  const recs = Array.from({ length: 20 }, (_, i) => ({ k: 0, c: i % 16, z: i % 8, pts: [[i, i], [i + 1, i + 2]] }));
  const packs = B.packRequests(recs);
  assert.deepEqual(packs.map((p) => p.count), [8, 8, 4]);
  assert.equal(packs.reduce((n, p) => n + p.count, 0), 20);
  assert.equal(packs.map((p) => p.s).join("_"), recs.map(B.encodeRecord).join("_"));
  for (const p of packs) assert.equal(relayParse(p.s).length, p.count);

  const fat = Array.from({ length: 5 }, () => ({ k: 0, c: 1, z: 1, pts: Array.from({ length: 250 }, (_, i) => [i * 9 % 1000, (i * 13) % 1000]) }));
  const fatPacks = B.packRequests(fat);
  assert.ok(fatPacks.length >= 2, "long records go into separate requests");
  assert.ok(fatPacks.every((p) => p.s.length <= B.LIMITS.requestChars && p.count <= 8));
  assert.equal(fatPacks.reduce((n, p) => n + p.count, 0), 5);
  assert.deepEqual(B.packRequests([]), []);
  assert.throws(() => B.packRequests([{ k: 0, c: 0, z: 0, pts: scribble(600, 3, true).map((p) => p.map(Math.round)) }]), RangeError);
});

test("the relay's own parser (as the README describes it) accepts everything the pen produces, and the points come back exactly", () => {
  const r = rng(99);
  for (let n = 0; n < 60; n++) {
    const pen = B.createPen({ color: Math.floor(r() * 16), size: Math.floor(r() * 8) });
    const raw = scribble(1 + Math.floor(r() * 900), n + 1, n % 7 === 0);
    pen.start(raw[0][0], raw[0][1]);
    const sent = [];
    raw.slice(1).forEach((p, i) => { pen.move(p[0], p[1]); if (i % 37 === 0) sent.push(...pen.flush()); });
    pen.end(raw[raw.length - 1][0], raw[raw.length - 1][1]);
    sent.push(...pen.flush());
    assert.deepEqual(pen.flush(), [], "nothing left after the last flush");
    assert.deepEqual(joined(sent), pen.points, "the records carry exactly the points that were drawn");
    const seen = [];
    for (const { s, count } of B.packRequests(sent)) {
      const parsed = relayParse(s);
      assert.equal(parsed.length, count);
      assert.ok(s.length <= 3600 && parsed.length <= 8);
      assert.ok(H.hearthUrl("stroke", { room: "a".repeat(32), me: "a", key: "b".repeat(32) }, { s }).length < 4000, "the whole URL stays under ~4 KB");
      seen.push(...parsed);
      assert.equal(B.parseRequest(s).records.length, count, "our checker agrees with the README one");
    }
    assert.deepEqual(joined(seen), pen.points, "and the relay's reading is the same");
    assert.ok(seen.every((x) => x.c === pen.color && x.z === pen.size));
  }
});

test("our parseRequest reports the relay's error messages", () => {
  assert.equal(B.parseRequest("").error, "no strokes");
  assert.equal(B.parseRequest("0.0.1.1_".repeat(9).slice(0, -1)).error, "too many strokes");
  assert.equal(B.parseRequest("0.0." + "1.".repeat(2100) + "1").error, "too long");
  assert.equal(B.parseRequest("0.0.1000.1000.1.1").error, "bad stroke");
  assert.equal(B.parseRequest("0.0.1.1_x").error, "bad stroke");
  assert.equal(B.parseRequest("0.0.0.0" + ".0.0".repeat(601)).error, "stroke too long");
  assert.deepEqual(B.parseRequest("3.2.100.200.10.5.-5.-15_k0.0.5.5").records.map((x) => x.k), [0, 1]);
});

// ---------- the pen ----------

test("smoothing follows the pointer, drops points closer than the minimum step and ends where the pen lifted", () => {
  const sm = B.createSmoother({ alpha: 1, minStep: 3 });
  assert.deepEqual(sm.start(100.4, 200.6), [100, 201]);
  assert.equal(sm.add(101, 201), null, "1 unit is under the step");
  assert.equal(sm.add(102, 202), null);
  assert.deepEqual(sm.add(104, 202), [104, 202]);
  assert.deepEqual(sm.end(105, 203), [105, 203], "pen-up always lands where the pointer was");
  assert.equal(sm.end(105, 203), null);
  const soft = B.createSmoother({ alpha: 0.5, minStep: 1 });
  soft.start(0, 0);
  assert.deepEqual(soft.add(100, 0), [50, 0], "an exponential follower lags behind a jump");
  assert.deepEqual(soft.add(100, 0), [75, 0]);
});

test("points leaving the board are clamped, never sent outside", () => {
  const pen = B.createPen({ color: 1, size: 1, alpha: 1 });
  pen.start(-50, 2000);
  pen.move(5000, -5000);
  pen.end(5000, -5000);
  assert.deepEqual(pen.points, [[0, 1000], [1000, 0]]);
  assert.doesNotThrow(() => pen.flush().forEach(B.encodeRecord));
});

test("a tap is a single dot; batches continue from the last sent point and never repeat or skip a point", () => {
  const dot = B.createPen({ color: 3, size: 2 });
  dot.start(500, 500);
  dot.end(500, 500);
  const recs = dot.flush();
  assert.deepEqual(recs.map(B.encodeRecord), ["3.2.500.500"]);

  const pen = B.createPen({ color: 6, size: 4, alpha: 1 });
  pen.start(0, 0);
  assert.equal(pen.hasPending(), true);
  assert.deepEqual(pen.flush().map(B.encodeRecord), ["6.4.0.0"]);
  assert.deepEqual(pen.flush(), [], "nothing new, nothing sent");
  pen.move(10, 0); pen.move(20, 0);
  assert.deepEqual(pen.flush().map(B.encodeRecord), ["k6.4.0.0.10.0.10.0"], "the continuation starts at the last point already sent");
  pen.move(20, 30);
  pen.end(20, 30);
  assert.deepEqual(pen.flush().map(B.encodeRecord), ["k6.4.20.0.0.30"]);
  assert.equal(pen.recordsSent, 3);
  assert.deepEqual(pen.points, [[0, 0], [10, 0], [20, 0], [20, 30]]);
  assert.equal(pen.move(50, 50), null, "a finished pen ignores the pointer");
});

test("batches go out every 300 ms, and a long scribble slows down so it does not eat the relay's 300 records", () => {
  assert.equal(B.FLUSH_MS, 300);
  assert.equal(B.flushDelay(0), 300);
  assert.equal(B.flushDelay(7), 300);
  assert.ok(B.flushDelay(8) > 300);
  assert.ok(B.flushDelay(100) > 300);
});

// ---------- reading what the relay sends ----------

const stroke = (i, by, c, z, k, pts) => ({ i, by, c, z, k, p: B.encodeRecord({ c, z, pts }).split(".").slice(2).join(".") });

test("a synced stroke is decoded once; strokes the relay would never send are skipped", () => {
  const s = stroke(5, "a", 3, 2, 0, [[100, 200], [110, 205]]);
  const n = B.normalizeStroke(s);
  assert.deepEqual(n, { i: 5, by: "a", c: 3, z: 2, k: 0, pts: [[100, 200], [110, 205]] });
  assert.equal(B.normalizeStroke(s), n, "cached per object");
  assert.equal(B.normalizeStroke({ i: 1, by: "a", c: 99, z: 0, k: 0, p: "1.1" }), null);
  assert.equal(B.normalizeStroke({ i: 1, by: "a", c: 1, z: 0, k: 0, p: "1.1.5000.1" }), null);
  assert.equal(B.normalizeStroke({ i: 1, by: "a", c: 1, z: 0, k: 0 }), null);
  assert.equal(B.normalizeStroke(null), null);
});

test("chains: a k record continues the previous record of the SAME author, other authors' strokes in between don't matter", () => {
  const recs = [
    { i: 1, by: "a", c: 1, z: 1, k: 0, pts: [[0, 0], [10, 10]] },
    { i: 2, by: "b", c: 2, z: 2, k: 0, pts: [[500, 500]] },
    { i: 3, by: "a", c: 1, z: 1, k: 1, pts: [[10, 10], [20, 20]] },
    { i: 4, by: "a", c: 1, z: 1, k: 1, pts: [[20, 20], [30, 30]] },
    { i: 5, by: "a", c: 4, z: 1, k: 0, pts: [[1, 1]] },
    { i: 6, by: "b", c: 9, z: 1, k: 1, pts: [[7, 7], [8, 8]] }, // nothing to continue for b except the dot of another colour
  ];
  const chains = B.buildChains(recs);
  assert.equal(chains.length, 4);
  assert.deepEqual(chains[0].pts, [[0, 0], [10, 10], [20, 20], [30, 30]]);
  assert.deepEqual(chains[0].ids, [1, 3, 4]);
  assert.deepEqual(chains[1].pts, [[500, 500]]);
  assert.deepEqual(chains[3].pts, [[7, 7], [8, 8]], "an orphan continuation still draws");
  assert.deepEqual(B.buildChains([]), []);
});

// ---------- the local overlay ----------

const rec = (c, pts, k = 0) => ({ c, z: 2, k, pts });

test("my stroke shows at once, keeps showing until the sync echoes it, then the sync's copy wins", () => {
  const local = B.createLocal();
  const sync0 = [stroke(1, "b", 5, 1, 0, [[1, 1], [2, 2]])];
  const p = local.addPending(rec(3, [[10, 10], [20, 20]]), "a");
  assert.deepEqual(local.records(sync0).map((r) => [r.i, r.by]), [[1, "b"], [null, "a"]], "pending is drawn last");
  local.confirm([{ seq: p.seq, i: 2 }], 1000);
  assert.equal(local.pendingCount, 0);
  assert.deepEqual(local.records(sync0).map((r) => r.i), [1, 2], "confirmed by id, in id order");
  const echoed = sync0.concat([stroke(2, "a", 3, 2, 0, [[10, 10], [20, 20]]), stroke(3, "b", 5, 1, 0, [[3, 3]])]);
  assert.deepEqual(local.records(echoed).map((r) => r.i), [1, 2, 3], "no duplicate once the sync has it");
  local.observe(echoed, 4, null, 2000, 0);
  assert.equal(local.confirmedCount, 0);
  assert.deepEqual(local.records(echoed).map((r) => r.i), [1, 2, 3]);
});

test("a stale sync can not bring back a stroke I undid; a refused request is dropped", () => {
  const local = B.createLocal();
  const sync = [stroke(7, "a", 1, 1, 0, [[1, 1], [9, 9]]), stroke(8, "a", 1, 1, 1, [[9, 9], [20, 20]])];
  local.forget([7, 8]);
  assert.deepEqual(local.records(sync), []);
  const p = local.addPending(rec(1, [[5, 5]]), "a");
  local.drop([p.seq]);
  assert.deepEqual(local.records([]), []);
  const q = local.addPending(rec(1, [[5, 5]]), "a");
  local.confirm([{ seq: q.seq, i: 9 }], 0);
  local.forget([9]);
  assert.deepEqual(local.records([]), [], "undoing a confirmed, not yet echoed stroke removes it");
  const r = local.addPending(rec(1, [[5, 5]]), "a");
  local.forget([10]);
  local.confirm([{ seq: r.seq, i: 10 }], 0);
  assert.deepEqual(local.records([]), [], "an undo that raced the confirmation wins");
});

test("clear hides the board at once and keeps it for Undo; unclear brings it back before the next sync", () => {
  const local = B.createLocal();
  const sync = [stroke(1, "a", 1, 1, 0, [[1, 1], [9, 9]]), stroke(2, "b", 2, 1, 0, [[3, 3]])];
  const p = local.addPending(rec(4, [[7, 7]]), "a");
  local.confirm([{ seq: p.seq, i: 3 }], 100);
  assert.equal(local.records(sync).length, 3);
  local.startClear(local.records(sync), 3, { by: "a", at: 5000, until: 65000 });
  assert.deepEqual(local.records(sync), [], "hidden even though the sync still lists them");
  assert.equal(local.clearInfo(null).until, 65000, "the banner doesn't wait for the next sync");
  assert.deepEqual(local.clearInfo({ by: "b", at: 1, until: 2 }).by, "b", "the relay's own info wins once it arrives");
  local.unclear(7000);
  assert.deepEqual(local.records(sync).map((r) => r.i), [1, 2, 3], "all back, mine included");
  assert.equal(local.clearInfo(null), null);

  local.startClear(local.records(sync), 3, { by: "a", at: 5000, until: 65000 });
  local.observe([], 4, { by: "a", at: 5000, until: 65000 }, 8000, 0); // a sync that has seen the clear
  assert.equal(local.hiding, false, "the hide ends once a newer sync says the same");
  assert.deepEqual(local.records([]), []);
  local.unclear(9000);
  assert.deepEqual(local.records([]).map((r) => r.i), [1, 2, 3], "Undo still works after the sync");
  local.observe(sync.concat([stroke(3, "a", 4, 2, 0, [[7, 7]])]), 5, null, 9500, 0);
  assert.equal(local.confirmedCount, 0, "and the sync's copies take over");
});

test("local copies expire, and a partner's clear drops what I drew before it", () => {
  const local = B.createLocal({ expireMs: 1000 });
  const p = local.addPending(rec(1, [[1, 1]]), "a");
  local.confirm([{ seq: p.seq, i: 4 }], 100);
  local.observe([], 2, null, 500, 0);
  assert.equal(local.confirmedCount, 1);
  local.observe([], 3, null, 1200, 0);
  assert.equal(local.confirmedCount, 0, "never echoed within the window: gone");

  const early = local.addPending(rec(1, [[2, 2]]), "a"), late = local.addPending(rec(1, [[3, 3]]), "a");
  local.confirm([{ seq: early.seq, i: 5 }], 9000);
  local.confirm([{ seq: late.seq, i: 6 }], 10000);
  // the server clock is 2.5 s ahead: the strokes were confirmed at 11.5 s and 12.5 s server time; the clear was at 12 s
  local.observe([], 7, { by: "b", at: 12000, until: 72000 }, 10500, 2500);
  assert.deepEqual(local.records([]).map((r) => r.i), [6], "what I drew before the partner cleared is gone, what I drew after it stays");
});

test("clear banner: whose, how long is left, and when it ends", () => {
  assert.equal(B.clearState(null, "a", 0, 0), null);
  const c = { by: "b", at: 1000, until: 61000 };
  assert.deepEqual(B.clearState(c, "a", 1000, 0), { by: "b", mine: false, remainingMs: 60000, active: true });
  assert.equal(B.clearState(c, "b", 1000, 0).mine, true);
  assert.equal(B.clearState(c, "a", 31000, 0).remainingMs, 30000);
  assert.equal(B.clearState(c, "a", 40000, 25000).remainingMs, 0, "server clock 25 s ahead: it is over");
  assert.equal(B.clearState(c, "a", 61000, 0).active, false);
  assert.equal(B.hasMine([{ by: "b" }], "a"), false);
  assert.equal(B.hasMine([{ by: "b" }, { by: "a" }], "a"), true);
});

// ---------- two PCs and a relay that follows the README ----------

function fakeRelay() {
  let rev = 1, clr = null, brs = 1;
  const strokes = [], removedLog = [], last = { a: [], b: [] };
  const visible = () => (clr ? strokes.filter((s) => s.i > clr.upTo) : strokes);
  return {
    strokes,
    stroke(me, s) {
      let recs;
      try { recs = relayParse(s); } catch (e) { return { error: String(e.message).split(":")[0] }; }
      const ids = [];
      for (const r of recs) {
        const i = ++rev;
        strokes.push({ i, by: me, c: r.c, z: r.z, k: r.k ? 1 : 0, p: B.encodeRecord({ c: r.c, z: r.z, pts: r.pts }).split(".").slice(2).join(".") });
        if (r.k && last[me].length && last[me][last[me].length - 1].length < 50) last[me][last[me].length - 1].push(i); else last[me].push([i]);
        ids.push(i);
      }
      return { ok: true, ids };
    },
    undo(me) {
      const g = last[me].pop();
      if (!g) return { error: "nothing to undo" };
      for (const i of g) strokes.splice(strokes.findIndex((s) => s.i === i), 1);
      rev++; removedLog.push([rev, g]);
      return { ok: true, removed: g };
    },
    clear(me, now) { if (!strokes.length) return { ok: true, noop: true }; rev++; brs = rev; clr = { by: me, at: now, until: now + 60000, upTo: rev }; return { ok: true, until: clr.until }; },
    unclear(now) { if (!clr) return { error: "nothing to restore" }; if (now >= clr.until) return { error: "too late" }; rev++; brs = rev; clr = null; return { ok: true }; },
    sync(since, now) {
      if (since === rev) return { rev };
      const reset = !since || since < brs || since > rev;
      const board = {
        reset,
        strokes: reset ? visible().slice() : visible().filter((s) => s.i > since),
        removed: reset ? [] : removedLog.filter(([r]) => r > since).flatMap(([, ids]) => ids),
        clear: clr && now < clr.until ? { by: clr.by, at: clr.at, until: clr.until } : null,
      };
      return { rev, now, board };
    },
  };
}

/** One PC: sync state (hearth-core), a local overlay, and the same flow board.js runs (pen -> records -> requests -> ids). */
function pc(me, relay, clock) {
  let state = H.emptyState();
  const local = B.createLocal();
  const self = {
    me, local,
    get state() { return state; },
    records: () => local.records(state.board.strokes),
    sync() {
      const res = H.applySync(state, relay.sync(state.rev, clock.t), { me, localNow: clock.t });
      state = res.state;
      local.observe(state.board.strokes, state.rev, state.board.clear, clock.t, 0);
      return res.events;
    },
    drawStroke(raw, color = 1, size = 2, flushEvery = 25) {
      const pen = B.createPen({ color, size });
      const out = [];
      pen.start(raw[0][0], raw[0][1]);
      raw.slice(1).forEach((p, i) => { pen.move(p[0], p[1]); if (i % flushEvery === 0) out.push(...pen.flush()); });
      pen.end(raw[raw.length - 1][0], raw[raw.length - 1][1]);
      out.push(...pen.flush());
      const items = out.map((r) => local.addPending(r, me));
      let done = 0;
      for (const { s, count } of B.packRequests(items)) {
        const r = relay.stroke(me, s);
        assert.equal(r.ok, true, r.error);
        local.confirm(items.slice(done, done + count).map((x, n) => ({ seq: x.seq, i: r.ids[n] })), clock.t);
        done += count;
      }
      return pen.points;
    },
    undo() { const r = relay.undo(me); if (r.ok) local.forget(r.removed); return r; },
    clear() { const before = self.records(); const r = relay.clear(me, clock.t); if (r.ok && !r.noop) local.startClear(before, state.rev, { by: me, at: clock.t, until: r.until }); return r; },
    unclear() { const r = relay.unclear(clock.t); if (r.ok) local.unclear(clock.t); return r; },
  };
  return self;
}
const shape = (list) => list.map((r) => [r.i, r.by, r.c, r.z, r.k, r.pts]);

test("two PCs drawing at the same time end up with the same board, in the same order", () => {
  const clock = { t: 1_000_000 };
  const relay = fakeRelay();
  const A = pc("a", relay, clock), Bp = pc("b", relay, clock);
  A.sync(); Bp.sync();
  const a1 = A.drawStroke(scribble(300, 1), 3, 2); // long: several records
  const b1 = Bp.drawStroke(scribble(80, 2), 9, 5);
  const a2 = A.drawStroke(scribble(10, 3), 15, 7);
  assert.ok(relay.strokes.length > 4, "long strokes were split into records");
  assert.ok(relay.strokes.some((s) => s.k === 1));
  // before syncing, each PC already shows its own work
  assert.ok(A.records().length > 0 && Bp.records().length > 0);
  A.sync(); Bp.sync(); A.sync();
  assert.deepEqual(shape(A.records()), shape(Bp.records()));
  assert.equal(A.local.confirmedCount + Bp.local.confirmedCount, 0, "the relay's copies took over");
  const chains = B.buildChains(A.records());
  assert.equal(chains.length, 3, "three strokes, however many records they were sent as");
  assert.deepEqual(chains.find((c) => c.by === "a" && c.c === 3).pts, a1, "what A drew is what B sees, point for point");
  assert.deepEqual(chains.find((c) => c.by === "b").pts, b1);
  assert.deepEqual(chains.find((c) => c.c === 15).pts, a2);
});

test("undo removes only my last stroke (all its records) on both PCs, even if the other PC drew in between", () => {
  const clock = { t: 5_000 };
  const relay = fakeRelay();
  const A = pc("a", relay, clock), Bp = pc("b", relay, clock);
  A.sync(); Bp.sync();
  A.drawStroke(scribble(120, 11), 2, 2);
  Bp.drawStroke(scribble(30, 12), 4, 2);
  A.drawStroke(scribble(150, 13), 6, 2); // several records, one undoable stroke
  A.sync(); Bp.sync();
  const beforeUndo = B.buildChains(A.records()).length;
  assert.equal(beforeUndo, 3);
  assert.equal(A.undo().ok, true);
  assert.equal(B.buildChains(A.records()).filter((c) => c.c === 6).length, 0, "gone at once on my screen, before any sync");
  A.sync(); Bp.sync();
  assert.deepEqual(shape(A.records()), shape(Bp.records()));
  const left = B.buildChains(Bp.records());
  assert.equal(left.length, 2);
  assert.deepEqual(left.map((c) => c.c).sort((x, y) => x - y), [2, 4]);
  assert.equal(Bp.undo().ok, true);
  assert.equal(Bp.undo().error, "nothing to undo", "b has nothing else; a's strokes are not b's to undo");
  A.sync();
  assert.deepEqual(B.buildChains(A.records()).map((c) => c.c), [2]);
  assert.equal(A.undo().ok, true);
  assert.equal(A.undo().error, "nothing to undo");
});

test("clear on one PC, Undo on the other: the same board comes back everywhere", () => {
  const clock = { t: 100_000 };
  const relay = fakeRelay();
  const A = pc("a", relay, clock), Bp = pc("b", relay, clock);
  A.sync(); Bp.sync();
  A.drawStroke(scribble(60, 21), 1, 1);
  Bp.drawStroke(scribble(60, 22), 2, 3);
  A.sync(); Bp.sync();
  const before = shape(A.records());
  assert.equal(before.length > 0, true);
  assert.equal(A.clear().ok, true);
  assert.deepEqual(A.records(), [], "cleared on my screen at once");
  assert.equal(B.clearState(A.local.clearInfo(null), "a", clock.t, 0).mine, true);
  clock.t += 2000;
  A.sync(); Bp.sync();
  assert.deepEqual(Bp.records(), []);
  const banner = B.clearState(Bp.state.board.clear, "b", clock.t, 0);
  assert.equal(banner.by, "a");
  assert.equal(banner.mine, false);
  assert.equal(banner.active, true);
  assert.equal(Bp.unclear().ok, true, "the OTHER side may undo the clear");
  clock.t += 1500;
  A.sync(); Bp.sync();
  assert.deepEqual(shape(A.records()), before);
  assert.deepEqual(shape(Bp.records()), before);
  assert.equal(A.state.board.clear, null);

  assert.equal(A.clear().ok, true);
  clock.t += 61_000;
  assert.equal(A.unclear().error, "too late");
  assert.equal(A.clear().noop, undefined, "clearing again works");
});

test("drawing after a clear, then Undo clear: the new stroke and the old ones are all there", () => {
  const clock = { t: 7_000 };
  const relay = fakeRelay();
  const A = pc("a", relay, clock), Bp = pc("b", relay, clock);
  A.sync(); Bp.sync();
  A.drawStroke(scribble(40, 31), 1, 1);
  A.sync(); Bp.sync();
  A.clear();
  Bp.drawStroke(scribble(40, 32), 2, 2);
  A.sync(); Bp.sync();
  assert.equal(B.buildChains(A.records()).length, 1, "only the stroke drawn after the clear");
  Bp.unclear();
  A.sync(); Bp.sync();
  assert.equal(B.buildChains(A.records()).length, 2);
  assert.deepEqual(shape(A.records()), shape(Bp.records()));
});

test("a restart: a PC that syncs from scratch gets the whole board back (strokes live on the relay)", () => {
  const clock = { t: 9_000 };
  const relay = fakeRelay();
  const A = pc("a", relay, clock);
  A.sync();
  const pts = A.drawStroke(scribble(500, 41), 5, 4);
  const fresh = pc("a", relay, clock);
  fresh.sync();
  assert.deepEqual(B.buildChains(fresh.records())[0].pts, pts);
  assert.equal(fresh.state.board.strokes.length, relay.strokes.length);
});

// ---------- gallery ----------

const png = (n) => B.GALLERY.maxImageChars > n ? "data:image/png;base64," + "A".repeat(n) : "";

test("the gallery keeps the last N pictures and tells you what saving would drop", () => {
  const item = (n, chars = 1000) => ({ id: String(n), at: n, png: png(chars) });
  let list = [];
  for (let n = 1; n <= B.GALLERY.maxItems; n++) {
    const plan = B.planGallery(list, item(n));
    assert.deepEqual(plan.dropped, []);
    list = plan.list;
  }
  assert.equal(list.length, B.GALLERY.maxItems);
  const full = B.planGallery(list, item(99));
  assert.deepEqual(full.dropped.map((x) => x.id), ["1"], "the oldest goes first");
  assert.equal(full.list.length, B.GALLERY.maxItems);
  assert.equal(full.list[full.list.length - 1].id, "99");
  assert.equal(list.length, B.GALLERY.maxItems, "planning never changes the list it was given");
});

test("the gallery budget keeps umbra.store under its 1 MB cap, even with the biggest pictures", () => {
  assert.ok(B.GALLERY.budgetChars <= 700000);
  let list = [];
  for (let n = 1; n <= 20; n++) {
    const big = { id: String(n), at: n, png: png(B.GALLERY.maxImageChars - 30) };
    list = B.planGallery(list, big).list;
    const stored = JSON.stringify(list).length;
    assert.ok(stored < 1_000_000 - 100_000, `stored ${stored} bytes leaves room for the pairing and the snapshot`);
  }
  const huge = B.planGallery([{ id: "1", at: 1, png: png(90000) }, { id: "2", at: 2, png: png(90000) }], { id: "3", at: 3, png: png(99000) });
  assert.ok(huge.chars <= B.GALLERY.budgetChars);
  assert.ok(B.GALLERY.sizes.every((n, i) => i === 0 || n < B.GALLERY.sizes[i - 1]), "export sizes shrink");
});

test("pictures read back from the store are checked", () => {
  const good = { id: "1", at: 5, png: png(100) };
  const list = B.sanitizeGallery([good, { id: "x", at: 6, png: "data:text/html;base64,AAAA" }, { at: 7 }, null, { id: "y", at: "no", png: png(10) }, { id: "z", at: 8, png: png(B.GALLERY.maxImageChars) }]);
  assert.deepEqual(list, [good]);
  assert.deepEqual(B.sanitizeGallery("junk"), []);
  const many = Array.from({ length: 12 }, (_, n) => ({ id: String(n), at: n, png: png(10) }));
  assert.equal(B.sanitizeGallery(many).length, B.GALLERY.maxItems);
  assert.equal(B.sanitizeGallery(many)[B.GALLERY.maxItems - 1].id, "11", "the newest are kept");
});

// ---------- the page wiring ----------

const read = (f) => readFileSync(new URL(`../../widgets/hearth/${f}`, import.meta.url), "utf8");

test("the page loads the board files after the core and before the shell, and the board marks itself no-drag", () => {
  const html = read("index.html");
  const at = (s) => html.indexOf(s);
  assert.ok(at("hearth-core.js") < at("board-logic.js") && at("board-logic.js") < at("board.js") && at("board.js") < at("hearth.js"));
  assert.ok(html.includes('href="board.css"'));
  const js = read("board.js");
  assert.match(js, /<canvas class="bd-canvas" data-no-drag/);
  for (const part of ["bd-tools", "bd-bar", "bd-top", "bd-gallery"]) assert.match(js, new RegExp(`class="${part}"[^>]*data-no-drag`), part);
  assert.match(js, /setBoardHover/);
  assert.match(js, /onSettings/);
  assert.match(js, /requestFocus/);
  assert.match(js, /touch-action|setPointerCapture/);
  assert.match(read("board.css"), /touch-action:\s*none/);
  assert.match(read("hearth.js"), /HearthBoard\.mount/);
});

test("the board never uses localStorage, only reaches the declared host through the client, and has no text under 11 px (umbra.css sets 1rem = 13px)", () => {
  for (const f of ["board.js", "board-logic.js", "board.css"]) {
    const src = read(f);
    assert.equal(/localStorage|sessionStorage|indexedDB|fetch\(|XMLHttpRequest/.test(src), false, f);
    assert.deepEqual([...src.matchAll(/https?:\/\/([a-z0-9.-]+)/gi)].map((m) => m[1]), [], f + " names no host");
  }
  const css = read("board.css");
  for (const m of css.matchAll(/font-size:\s*([\d.]+)(px|rem)/g)) {
    const px = m[2] === "px" ? Number(m[1]) : Number(m[1]) * 13;
    assert.ok(px >= 11, `font-size ${m[0]}`);
  }
  assert.match(css, /data-motion="off"/);
  assert.match(css, /--motion-scale/);
});
