import test from "node:test";
import assert from "node:assert/strict";
import { createRequire } from "node:module";
import { existsSync, readFileSync } from "node:fs";

const require = createRequire(import.meta.url);
const H = require("../../widgets/hearth/hearth-core.js");
const manifest = JSON.parse(readFileSync(new URL("../../widgets/hearth/widget.json", import.meta.url), "utf8"));

const HOST = "umbra-buddy-relay.umbra-server.workers.dev";
const flush = async () => { for (let i = 0; i < 6; i++) await new Promise((r) => setImmediate(r)); };

// ---------- fakes: clock, store, broadcast bus, and a relay that follows tools/umbra-relay/README.md ----------

function makeClock(start = 1_700_000_000_000) {
  let t = start, seq = 0;
  const timers = new Map();
  return {
    now: () => t,
    setTimeout(fn, ms) { const id = ++seq; timers.set(id, { at: t + Math.max(0, ms), fn }); return id; },
    clearTimeout(id) { timers.delete(id); },
    /** Runs every timer due within `ms`, in order, letting promises settle between them. */
    async advance(ms) {
      const end = t + ms;
      for (;;) {
        let best = null;
        for (const [id, x] of timers) if (x.at <= end && (!best || x.at < best[1].at)) best = [id, x];
        if (!best) break;
        timers.delete(best[0]);
        t = Math.max(t, best[1].at);
        best[1].fn();
        await flush();
      }
      t = end;
      await flush();
    },
    pending: () => timers.size,
  };
}

function makeStore() {
  const data = new Map();
  return {
    data,
    async get(k, fb) { return data.has(k) ? structuredClone(data.get(k)) : fb; },
    async set(k, v) { data.set(k, structuredClone(v)); },
    async remove(k) { data.delete(k); },
  };
}

function makeBus() {
  const chans = new Set();
  const traffic = [];
  return {
    traffic,
    channel() {
      const c = {
        onmessage: null, closed: false, muted: false,
        postMessage(data) {
          if (c.muted) return;
          traffic.push(structuredClone(data));
          for (const o of chans) if (o !== c && !o.closed && !o.muted) queueMicrotask(() => o.onmessage && o.onmessage({ data: structuredClone(data) }));
        },
        close() { c.closed = true; chans.delete(c); },
      };
      chans.add(c);
      return c;
    },
  };
}

const ALPHA = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
function makeRelay(clock) {
  const rooms = new Map(), codes = new Map(), log = [];
  let seq = 0;
  const hex = (n) => (++seq).toString(16).padStart(n, "0");
  const newCode = () => { let n = ++seq, s = ""; for (let i = 0; i < 8; i++) { s += ALPHA[n % 31]; n = Math.floor(n / 31); } return s; };
  const relay = {
    log, rooms, down: false, junk: false,
    syncs: () => log.filter((l) => l.path === "hearth/sync"),
    async fetchJson(u) {
      const url = new URL(u);
      assert.equal(url.protocol, "https:");
      assert.equal(url.hostname, HOST, "the widget may only talk to its declared host");
      if (relay.down) throw new Error("Request failed");
      if (relay.junk) return "<html>quota</html>";
      const path = url.pathname.slice(1), q = Object.fromEntries(url.searchParams);
      log.push({ path, q, at: clock.now() });
      return structuredClone(handle(path, q));
    },
    /** What the partner's other PC would do, without going through a client. */
    mutate(roomId, fn) { const r = rooms.get(roomId); fn(r); r.rev++; },
  };
  function handle(path, q) {
    const now = clock.now();
    if (path === "pair" && q.new) {
      const id = hex(32), ka = hex(32), kb = hex(32).replace(/^0/, "f"), code = newCode();
      rooms.set(id, { rev: 1, keys: { a: ka, b: kb }, names: { a: "", b: "" }, since: "", taps: { a: { n: 0, at: 0 }, b: { n: 0, at: 0 } }, together: { n: 0, at: 0 }, strokes: [], unpaired: null });
      codes.set(code, { room: id, kb, side: null, exp: now + 600000 });
      return { code, room: id, me: "a", key: ka, ttl: 600 };
    }
    if (path === "pair" && q.code) {
      const code = q.code.toUpperCase().replace(/[^A-Z0-9]/g, "");
      if (code.length !== 8) return { error: "bad code" };
      const c = codes.get(code);
      if (!c || c.exp < now) return { error: "unknown or expired code" };
      codes.delete(code);
      return { room: c.room, me: c.side || "b", key: c.kb };
    }
    if (path.startsWith("hearth/")) {
      const route = path.slice(7), r = rooms.get(q.room);
      if (!/^[0-9a-f]{32}$/.test(q.room || "") || (q.me !== "a" && q.me !== "b")) return { error: "bad request" };
      if (!r || r.keys[q.me] !== q.key) return { paired: false, reason: "unknown", now };
      const other = q.me === "a" ? "b" : "a";
      if (r.unpaired && route !== "reinvite" && route !== "unpair") return { paired: false, reason: "unpaired", by: r.unpaired.by === q.me ? "you" : "peer", graceUntil: r.unpaired.graceUntil, now };
      switch (route) {
        case "sync": {
          const since = Number(q.since) || 0;
          if (since === r.rev) return { rev: r.rev };
          const full = since === 0 || since > r.rev;
          return {
            rev: r.rev, now, names: r.names, togetherSince: r.since, petName: "", pet: { food: 70, water: 70, love: 70, mood: "content", level: 1 },
            taps: r.taps, together: r.together,
            board: { reset: full, strokes: r.strokes.filter((s) => full || s.i > since), removed: [], clear: null },
          };
        }
        case "tap": r.taps[q.me].n++; r.taps[q.me].at = now; r.rev++; return { ok: true, together: false };
        case "set": { if (q.names) { const [a, b] = q.names.split(","); if (a) r.names.a = a; if (b) r.names.b = b; } if (q.since) r.since = q.since; r.rev++; return { ok: true }; }
        case "stroke": r.rev++; r.strokes.push({ i: r.rev, by: q.me, c: 0, z: 0, k: 0, p: "1.1" }); return { ok: true, ids: [r.rev] };
        case "unpair":
          if (!r.unpaired) r.unpaired = { by: q.me, graceUntil: now + 7 * 86400000 };
          return { ok: true, graceUntil: r.unpaired.graceUntil };
        case "reinvite": {
          if (r.unpaired && r.unpaired.by === q.me) return { paired: false, reason: "unpaired", by: "you", graceUntil: r.unpaired.graceUntil, now };
          const code = newCode(), kb = hex(32).replace(/^0/, "e");
          r.keys[other] = kb; r.unpaired = null;
          codes.set(code, { room: q.room, kb, side: other, exp: now + 600000 });
          return { code, ttl: 600, rebinds: other };
        }
      }
      return { error: "not found" };
    }
    return { error: "not found" };
  }
  return relay;
}

/** One copy of the widget: its own store (per copy, like Umbra), a channel on the shared bus, a counted fetch. */
function makeCopy(name, env, opts = {}) {
  const store = opts.store || makeStore();
  const channel = opts.noChannel ? null : env.bus.channel();
  const fetched = [];
  const client = H.createClient({
    fetchJson: (u) => { fetched.push(u); return env.relay.fetchJson(u); },
    store, channel, now: env.clock.now, setTimeout: env.clock.setTimeout, clearTimeout: env.clock.clearTimeout, instance: name,
    random: opts.random,
  });
  const events = [];
  client.onEvent((e) => events.push(e));
  return { name, client, store, channel, fetched, events, view: () => client.getView(), syncs: () => fetched.filter((u) => u.includes("/hearth/sync")).length };
}
const makeEnv = () => { const clock = makeClock(); return { clock, relay: makeRelay(clock), bus: makeBus() }; };

async function pairedCopy(env, name = "hearth", extra) {
  const c = makeCopy(name, env, extra);
  await c.client.start();
  assert.equal(await c.client.createHearth(), true);
  return c;
}
const minutes = (n) => n * 60000;

// ---------- codes, countdown, URLs ----------

test("codes: normalised, validated against the relay alphabet, shown with a dash", () => {
  assert.equal(H.normalizeCode(" abcd-2345 "), "ABCD2345");
  assert.equal(H.normalizeCode(null), "");
  assert.deepEqual(H.checkCode("abcd-2345"), { ok: true, code: "ABCD2345" });
  assert.deepEqual(H.checkCode("ABCD 2345"), { ok: true, code: "ABCD2345" });
  for (const bad of ["", "   ", "ABC", "ABCD23456", "ABCD234I", "ABCD2340", "ABCD2341", "ABCDLO23"]) {
    const r = H.checkCode(bad);
    assert.equal(r.ok, false, bad);
    assert.match(r.error, /\w/);
  }
  assert.match(H.checkCode("ABCD234I").error, /never use/);
  assert.equal(H.formatCode("abcd2345"), "ABCD-2345");
  assert.equal(H.formatCode("abc"), "ABC");
});

test("countdown: 10 minutes down to 0:00, never negative", () => {
  assert.deepEqual(H.countdown(1000 + 600000, 1000), { remainingMs: 600000, expired: false, text: "10:00" });
  assert.equal(H.countdown(1000 + 582000, 1000).text, "9:42");
  assert.equal(H.countdown(1000 + 1, 1000).text, "0:01");
  assert.deepEqual(H.countdown(1000, 1000), { remainingMs: 0, expired: true, text: "0:00" });
  assert.equal(H.countdown(500, 1000).expired, true);
  assert.equal(H.countdown(undefined, 1000).expired, true);
});

test("urls: HTTPS to the declared host, all parameters encoded, unknown routes and bad pairings refused", () => {
  const p = { room: "a".repeat(32), me: "a", key: "b".repeat(32) };
  assert.equal(H.pairNewUrl(), `https://${HOST}/pair?new=1`);
  assert.equal(H.pairJoinUrl("ABCD2345"), `https://${HOST}/pair?code=ABCD2345`);
  const u = new URL(H.hearthUrl("sync", p, { since: 57 }));
  assert.equal(u.host, HOST);
  assert.equal(u.pathname, "/hearth/sync");
  assert.deepEqual(Object.fromEntries(u.searchParams), { room: p.room, me: "a", key: p.key, since: "57" });
  assert.equal(new URL(H.hearthUrl("sync", p)).searchParams.has("since"), false);
  assert.equal(new URL(H.hearthUrl("set", p, { names: "Ada,Bo" })).searchParams.get("names"), "Ada,Bo");
  assert.match(H.hearthUrl("set", p, { names: "Ada,Bo" }), /names=Ada%2CBo/);
  assert.match(H.hearthUrl("stroke", p, { s: "3.2.100.200.10.5.-5.-15_k0.0.5.5" }), /s=3\.2\.100\.200\.10\.5\.-5\.-15_k0\.0\.5\.5/);
  assert.throws(() => H.hearthUrl("beat", p), /Unknown/);
  assert.throws(() => H.hearthUrl("sync", { room: "x", me: "a", key: "k" }), /Not paired/);
  assert.throws(() => H.hearthUrl("sync", null), /Not paired/);
  assert.throws(() => H.hearthUrl("stroke", p, { s: "9".repeat(6000) }), /too long/);
  for (const r of H.HEARTH_ROUTES) assert.doesNotThrow(() => H.hearthUrl(r, p));
});

test("pairing records and fingerprints", () => {
  const p = { room: "0".repeat(31) + "1", me: "b", key: "f".repeat(32) };
  assert.equal(H.isPairing(p), true);
  assert.equal(H.isPairing({ ...p, me: "c" }), false);
  assert.equal(H.isPairing({ ...p, room: "xyz" }), false);
  assert.equal(H.isPairing({ ...p, key: "" }), false);
  assert.equal(H.isPairing(null), false);
  const fp = H.fingerprint(p);
  assert.match(fp, /^[0-9a-f]{8}$/);
  assert.equal(fp, H.fingerprint({ ...p }));
  assert.notEqual(fp, H.fingerprint({ ...p, key: "e".repeat(32) }), "a rebound key is a different pairing");
  assert.equal(H.fingerprint({ me: "a" }), "");
  assert.equal(fp.includes(p.key.slice(0, 8)), false);
});

test("classify: the relay's answer shapes", () => {
  assert.equal(H.classify({ rev: 3 }).kind, "ok");
  assert.equal(H.classify({ code: "ABCD2345", ttl: 600 }).kind, "ok");
  assert.deepEqual(H.classify({ paired: false, reason: "unknown", now: 1 }), { kind: "gone" });
  assert.deepEqual(H.classify({ paired: false, reason: "unpaired", by: "peer", graceUntil: 9, now: 1 }), { kind: "unpaired", by: "peer", graceUntil: 9, now: 1 });
  assert.equal(H.classify({ paired: false, reason: "unpaired", by: "you" }).by, "you");
  assert.deepEqual(H.classify({ error: "too soon", retryIn: 30 }), { kind: "error", message: "too soon", retryIn: 30 });
  for (const junk of [null, undefined, "<html>", 5, [], true]) assert.equal(H.classify(junk).kind, "offline");
});

// ---------- polling policy ----------

test("pollDelay: 60 s idle, 1.5 s active, nothing while paused or unpaired, backoff after failures", () => {
  const base = { paused: false, paired: true, active: false, idleMs: 60000, failures: 0 };
  assert.equal(H.pollDelay(base), 60000);
  assert.equal(H.pollDelay({ ...base, idleMs: undefined }), 60000);
  assert.equal(H.pollDelay({ ...base, active: true }), 1500);
  assert.equal(H.pollDelay({ ...base, paused: true }), null);
  assert.equal(H.pollDelay({ ...base, paused: true, active: true }), null);
  assert.equal(H.pollDelay({ ...base, paired: false }), null);
  assert.equal(H.pollDelay(null), null);
  assert.equal(H.pollDelay({ ...base, idleMs: 5000 }), 30000, "never below 30 s");
  assert.equal(H.pollDelay({ ...base, idleMs: 240000 }), 240000, "Energy Saver's slower interval is honoured");
  assert.deepEqual([1, 2, 3, 4, 5, 6, 9].map((f) => H.pollDelay({ ...base, active: true, failures: f })), [3000, 6000, 12000, 24000, 48000, 60000, 60000]);
  assert.equal(H.pollDelay({ ...base, failures: 4 }), 60000, "idle polling never gets slower than a minute because of failures");
  assert.equal(H.pollDelay({ ...base, idleMs: 240000, failures: 3 }), 240000);
});

test("activity: hover or a partner stroke, 30 s memory, 2 minute cap, then a new session needs new activity", () => {
  const a = H.createActivity();
  assert.equal(a.isActive(0), false);
  a.setHover(true, 1000);
  assert.equal(a.isActive(1000), true);
  assert.equal(a.isActive(1000 + 119000), true);
  assert.equal(a.isActive(1000 + 120000), false, "capped after 2 minutes of hovering");
  assert.equal(a.isActive(1000 + 600000), false, "a pointer parked on the board does not poll fast for ever");
  a.setHover(false, 700000);
  a.setHover(true, 710000);
  assert.equal(a.isActive(710000), true, "entering the board again starts a new session");
  a.setHover(false, 715000);
  assert.equal(a.isActive(716000), false, "leaving ends it at once");

  const b = H.createActivity();
  b.peerStroke(10000);
  assert.equal(b.isActive(10000), true);
  assert.equal(b.isActive(39999), true);
  assert.equal(b.isActive(40000), false, "30 s after the last partner stroke");
  for (const at of [100000, 125000, 150000, 175000, 200000, 215000]) b.peerStroke(at);
  assert.equal(b.isActive(150000), true, "strokes within 30 s of each other extend the window");
  assert.equal(b.isActive(219000), true);
  assert.equal(b.isActive(221000), false, "capped 2 minutes after the session began, even while strokes keep coming");
  b.peerStroke(225000);
  assert.equal(b.isActive(225001), true, "a stroke after a capped session starts a new one");
  assert.equal(b.isActive(225000 + 31000), false);
});

test("electLeader: smallest id among live copies of the same pairing; unpaired copies never lead", () => {
  const peers = new Map([
    ["hearth#2.aaaa", { at: 1000, fp: "f1" }],
    ["hearth#3.0000", { at: 1000, fp: "other" }],
    ["hearth#4.0001", { at: 1000, fp: "" }],
  ]);
  assert.equal(H.electLeader("hearth.bbbb", "f1", peers, 2000), "hearth#2.aaaa");
  assert.equal(H.electLeader("hearth#2.aaaa", "f1", peers, 2000), "hearth#2.aaaa");
  assert.equal(H.electLeader("hearth#2.zzzz", "f1", peers, 2000), "hearth#2.aaaa");
  assert.equal(H.electLeader("hearth.bbbb", "f1", peers, 1000 + 15000), "hearth.bbbb", "a copy not heard for 15 s is gone");
  assert.equal(H.electLeader("hearth.bbbb", "", peers, 2000), null);
  assert.equal(H.electLeader("hearth.bbbb", "f1", new Map(), 2000), "hearth.bbbb");
});

// ---------- merging the relay's state ----------

test("applySync: idle answer changes nothing; sections replace; rev only comes from sync", () => {
  const s0 = H.emptyState();
  const r1 = H.applySync(s0, { rev: 5, now: 5000, names: { a: "Ada", b: "Bo" }, togetherSince: "2024-02-14", petName: "Pip", pet: { mood: "happy" }, taps: { a: { n: 1, at: 10 }, b: { n: 2, at: 20 } }, together: { n: 0, at: 0 }, board: { reset: true, strokes: [{ i: 3, by: "a" }], removed: [], clear: null } }, { me: "a", localNow: 4000 });
  assert.equal(r1.changed, true);
  assert.equal(r1.state.rev, 5);
  assert.equal(r1.state.skew, 1000);
  assert.deepEqual(r1.state.names, { a: "Ada", b: "Bo" });
  assert.equal(r1.state.togetherSince, "2024-02-14");
  assert.equal(r1.events.length, 0, "no events on the first sync");
  const idle = H.applySync(r1.state, { rev: 5 }, { me: "a", localNow: 1 });
  assert.equal(idle.changed, false);
  assert.equal(idle.state, r1.state);
  const part = H.applySync(r1.state, { rev: 6, now: 6000, taps: { a: { n: 1, at: 10 }, b: { n: 3, at: 30 } } }, { me: "a", localNow: 6000 });
  assert.deepEqual(part.state.names, { a: "Ada", b: "Bo" }, "absent sections keep their last value");
  assert.equal(part.state.board, r1.state.board);
  assert.deepEqual(part.events, [{ type: "peerTap", at: 30 }]);
  assert.equal(H.applySync(r1.state, { rev: 6, taps: { a: { n: 2, at: 30 }, b: { n: 2, at: 20 } } }, { me: "a", localNow: 0 }).events.length, 0, "my own tap is not a partner tap");
  assert.deepEqual(H.applySync(part.state, { rev: 7, together: { n: 1, at: 40 } }, { me: "b", localNow: 0 }).events, [{ type: "together", at: 40 }]);
});

test("applySync: strokes by the partner are an event, my own are not; a lower rev starts over; junk is ignored", () => {
  const s = H.applySync(H.emptyState(), { rev: 2, board: { reset: true, strokes: [], removed: [], clear: null } }, { me: "a", localNow: 0 }).state;
  assert.deepEqual(H.applySync(s, { rev: 3, board: { reset: false, strokes: [{ i: 3, by: "b" }], removed: [], clear: null } }, { me: "a", localNow: 0 }).events, [{ type: "peerStroke" }]);
  assert.deepEqual(H.applySync(s, { rev: 3, board: { reset: false, strokes: [{ i: 3, by: "a" }], removed: [], clear: null } }, { me: "a", localNow: 0 }).events, []);
  const recreated = H.applySync({ ...s, rev: 40, names: { a: "Old", b: "Old" } }, { rev: 2, now: 1, names: { a: "", b: "" } }, { me: "a", localNow: 0 });
  assert.equal(recreated.state.rev, 2);
  assert.deepEqual(recreated.state.names, { a: "", b: "" });
  assert.equal(H.applySync(s, "<html>", {}).changed, false);
  assert.equal(H.applySync(s, { error: "x" }, {}).changed, false);
  assert.equal(H.applySync(s, { rev: 4, togetherSince: "yesterday", names: { a: 5, b: null } }, { me: "a", localNow: 0 }).state.togetherSince, "");
});

test("mergeBoard: reset, removals, duplicates and ordering", () => {
  let b = H.mergeBoard(H.emptyBoard(), { reset: true, strokes: [{ i: 5 }, { i: 3 }, { i: 4 }], removed: [], clear: null });
  assert.deepEqual(b.strokes.map((s) => s.i), [3, 4, 5]);
  b = H.mergeBoard(b, { reset: false, strokes: [{ i: 5 }, { i: 6 }], removed: [4], clear: null });
  assert.deepEqual(b.strokes.map((s) => s.i), [3, 5, 6]);
  b = H.mergeBoard(b, { reset: false, strokes: [], removed: [], clear: { by: "b", at: 10, until: 70 } });
  assert.deepEqual(b.clear, { by: "b", at: 10, until: 70 });
  b = H.mergeBoard(b, { reset: true, strokes: [], removed: [], clear: null });
  assert.deepEqual(b, { strokes: [], clear: null });
  assert.equal(H.mergeBoard(b, null), b);
});

test("daysTogether: whole local days, never negative", () => {
  const today = new Date(2026, 5, 10, 23, 30);
  assert.equal(H.daysTogether("2026-06-10", today), 0);
  assert.equal(H.daysTogether("2026-06-09", today), 1);
  assert.equal(H.daysTogether("2024-02-14", today), 847);
  assert.equal(H.daysTogether("2026-06-11", today), null);
  assert.equal(H.daysTogether("", today), null);
  assert.equal(H.daysTogether("14.02.2024", today), null);
});

// ---------- the client against the relay contract ----------

test("an unpaired copy makes no requests at all", async () => {
  const env = makeEnv();
  const c = makeCopy("hearth", env);
  assert.equal(c.view().phase, "loading");
  await c.client.start();
  assert.equal(c.view().phase, "setup");
  await env.clock.advance(minutes(30));
  assert.equal(env.relay.log.length, 0);
});

test("create: pairs as side a, keeps the key only in umbra.store, shows the code with a 10 minute countdown", async () => {
  const env = makeEnv();
  const c = await pairedCopy(env);
  const v = c.view();
  assert.equal(v.phase, "paired");
  assert.equal(v.me, "a");
  assert.equal(v.invite.kind, "create");
  assert.match(v.invite.code, /^[A-HJ-KM-NP-Z2-9]{8}$/);
  assert.equal(v.invite.formatted, v.invite.code.slice(0, 4) + "-" + v.invite.code.slice(4));
  assert.equal(v.invite.text, "10:00");
  const saved = c.store.data.get("pair");
  assert.match(saved.room, /^[0-9a-f]{32}$/);
  assert.equal(saved.me, "a");
  assert.match(saved.key, /^[0-9a-f]{32}$/);
  assert.equal(JSON.stringify(v).includes(saved.key), false, "the view never carries the key");
  await env.clock.advance(minutes(3));
  assert.equal(c.view().invite.text, "7:00");
  await env.clock.advance(minutes(7));
  assert.equal(c.view().invite.expired, true);
  await env.clock.advance(10000);
  assert.equal(c.view().invite, null, "an expired code goes away");
  assert.equal(c.store.data.has("invite"), false);
  assert.equal(c.view().phase, "paired");
});

test("create offline or against a junk answer: friendly message, nothing saved", async () => {
  const env = makeEnv();
  const c = makeCopy("hearth", env);
  await c.client.start();
  env.relay.down = true;
  assert.equal(await c.client.createHearth(), false);
  assert.equal(c.view().notice.kind, "error");
  assert.match(c.view().notice.text, /Could not reach the relay/);
  assert.equal(c.view().phase, "setup");
  assert.equal(c.store.data.size, 0);
  env.relay.down = false; env.relay.junk = true;
  assert.equal(await c.client.createHearth(), false);
  assert.equal(c.store.data.size, 0);
  env.relay.junk = false;
  assert.equal(await c.client.createHearth(), true);
  assert.equal(c.view().notice, null);
});

test("join: two PCs pair with a code; the code works once; wrong and expired codes get friendly errors", async () => {
  const env = makeEnv();
  const a = await pairedCopy(env, "hearth", { noChannel: true });
  const code = a.view().invite.code;
  const b = makeCopy("hearth", env, { noChannel: true });
  await b.client.start();

  const before = env.relay.log.length;
  for (const junk of ["", "abc", "ABCD234I"]) { assert.equal(await b.client.join(junk), false); assert.equal(b.view().notice.kind, "error"); }
  assert.equal(env.relay.log.length, before, "a malformed code never reaches the relay");

  assert.equal(await b.client.join("ZZZZZZZZ"), false);
  assert.match(b.view().notice.text, /wrong or has expired/);
  assert.equal(b.view().phase, "setup");

  assert.equal(await b.client.join(code.toLowerCase().slice(0, 4) + " - " + code.slice(4)), true);
  assert.equal(b.view().me, "b");
  assert.equal(b.view().invite, null);
  assert.equal(b.store.data.get("pair").room, a.store.data.get("pair").room);
  assert.equal(await makeCopy("x", env, { noChannel: true }).client.join(code), false, "the code is used up");

  // an expired code
  const a2 = await pairedCopy(env, "hearth", { noChannel: true });
  const c2 = a2.view().invite.code;
  await env.clock.advance(minutes(11));
  const d = makeCopy("hearth", env, { noChannel: true });
  await d.client.start();
  assert.equal(await d.client.join(c2), false);
  assert.match(d.view().notice.text, /expired/);
  env.relay.down = true;
  assert.equal(await d.client.join("ABCD2345"), false);
  assert.match(d.view().notice.text, /Could not reach/);
});

test("idle polling: one request a minute, the first without since, later ones with the rev, idle answers cost {rev}", async () => {
  const env = makeEnv();
  const c = await pairedCopy(env);
  await env.clock.advance(minutes(10));
  const syncs = env.relay.syncs();
  assert.ok(syncs.length >= 10 && syncs.length <= 12, `got ${syncs.length}`);
  assert.equal(syncs[0].q.since, undefined);
  assert.ok(Number(syncs[1].q.since) >= 1);
  for (let i = 1; i < syncs.length; i++) assert.ok(syncs[i].at - syncs[i - 1].at >= 59000, "no faster than a minute");
  assert.equal(c.view().offline, false);
  assert.ok(c.view().lastOkAt > 0);
  assert.equal(c.view().fast, false);
});

test("the refresh interval from Umbra settings is the idle interval", async () => {
  const env = makeEnv();
  const c = await pairedCopy(env);
  c.client.setRefreshSeconds(240);
  await env.clock.advance(minutes(1));
  const n0 = env.relay.syncs().length;
  await env.clock.advance(minutes(20));
  const n = env.relay.syncs().length - n0;
  assert.ok(n >= 4 && n <= 6, `got ${n}`);
});

test("paused (PC locked or display off): no requests; one sync soon after it resumes", async () => {
  const env = makeEnv();
  const c = await pairedCopy(env);
  await env.clock.advance(minutes(2));
  c.client.setPaused(true);
  c.client.setBoardHover(true);
  const n0 = env.relay.log.length;
  await env.clock.advance(minutes(30));
  assert.equal(env.relay.log.length, n0, "nothing while paused, not even with the board hovered");
  c.client.refresh();
  assert.equal(env.relay.log.length, n0);
  c.client.setPaused(false);
  await env.clock.advance(3000);
  assert.equal(env.relay.log.length, n0 + 1, "exactly one sync when it resumes");
  await env.clock.advance(minutes(5));
  assert.ok(env.relay.log.length - n0 <= 1 + 6, "then the normal rhythm");
});

test("board hover: ~1.5 s polling for at most 2 minutes, then idle again until the pointer comes back", async () => {
  const env = makeEnv();
  const c = await pairedCopy(env);
  await env.clock.advance(minutes(3));
  const count = () => env.relay.syncs().length;
  const n0 = count();
  c.client.setBoardHover(true);
  await env.clock.advance(120000);
  const fastCount = count() - n0;
  assert.ok(fastCount >= 70 && fastCount <= 82, `fast polls in 2 min: ${fastCount}`);
  assert.equal(c.view().fast, false, "capped");
  const n1 = count();
  await env.clock.advance(minutes(5));
  assert.ok(count() - n1 <= 6, `after the cap: ${count() - n1}`);
  c.client.setBoardHover(false);
  await env.clock.advance(minutes(2));
  const n2 = count();
  c.client.setBoardHover(true);
  await env.clock.advance(30000);
  assert.ok(count() - n2 >= 15, "a new visit polls fast again");
  c.client.setBoardHover(false);
  const n3 = count();
  await env.clock.advance(minutes(3));
  assert.ok(count() - n3 <= 4, "leaving the board returns to the idle rhythm");
});

test("a partner stroke keeps the board active for 30 s", async () => {
  const env = makeEnv();
  const c = await pairedCopy(env);
  await env.clock.advance(minutes(2));
  const roomId = c.store.data.get("pair").room;
  env.relay.mutate(roomId, (r) => r.strokes.push({ i: r.rev + 1, by: "b", c: 0, z: 0, k: 0, p: "5.5" }));
  for (let i = 0; i < 130 && !c.events.length; i++) await env.clock.advance(500);   // until the idle poll sees it
  assert.ok(c.events.some((e) => e.type === "peerStroke"));
  const n0 = env.relay.syncs().length;
  await env.clock.advance(25000);
  assert.ok(env.relay.syncs().length - n0 >= 14, "fast while the partner is drawing");
  await env.clock.advance(15000);
  const n1 = env.relay.syncs().length;
  await env.clock.advance(minutes(2));
  assert.ok(env.relay.syncs().length - n1 <= 3, "idle again");
});

test("three copies on one PC cost about one request per minute, followers never fetch, and everyone sees the data", async () => {
  const env = makeEnv();
  const one = makeEnv();
  const solo = await pairedCopy(one);
  await one.clock.advance(minutes(10));
  const soloCount = one.relay.syncs().length;

  const c1 = await pairedCopy(env, "hearth");
  const c2 = makeCopy("hearth#2", env), c3 = makeCopy("hearth#3", env);
  await c2.client.start(); await c3.client.start();
  await env.clock.advance(100);
  for (const c of [c2, c3]) {
    assert.equal(c.view().link.offer, true, "a paired copy is on this PC");
    assert.equal(c.client.requestLink(), true);
    await flush();
    const req = c1.view().link.requests;
    assert.equal(req.length >= 1, true);
    assert.equal(c.view().paired, false, "nothing is shared until the user allows it");
    assert.equal(c1.client.approveLink(req[req.length - 1].nonce), true);
    await flush();
    assert.equal(c.view().paired, true);
    assert.deepEqual(c.store.data.get("pair"), c1.store.data.get("pair"), "every copy keeps the pairing in its own umbra.store");
  }
  assert.equal(c1.view().link.requests.length, 0);
  const copies = [c1, c2, c3];
  const mark = copies.map((c) => c.syncs());
  const total0 = env.relay.syncs().length;
  const roomId = c1.store.data.get("pair").room;
  env.relay.mutate(roomId, (r) => { r.names = { a: "Ada", b: "Bo" }; r.since = "2024-02-14"; });
  await env.clock.advance(minutes(10));
  const total = env.relay.syncs().length - total0;
  assert.ok(total >= 9 && total <= 12, `3 copies: ${total} syncs in 10 minutes (one copy alone: ${soloCount})`);
  assert.ok(Math.abs(total - soloCount) <= 3);
  const deltas = copies.map((c, i) => c.syncs() - mark[i]);
  assert.equal(deltas.filter((d) => d > 0).length, 1, `only the leader fetches: ${deltas}`);
  assert.equal(deltas.reduce((s, d) => s + d, 0), total);
  for (const c of copies) {
    assert.deepEqual(c.view().names, { you: "Ada", partner: "Bo" }, c.name);
    assert.equal(c.view().togetherSince, "2024-02-14");
    assert.equal(c.view().offline, false);
  }
  assert.equal(copies.filter((c) => c.view().leader).length, 1);
});

test("a hover on a follower's board makes the leader poll fast", async () => {
  const env = makeEnv();
  const c1 = await pairedCopy(env, "hearth");
  const c2 = makeCopy("hearth#2", env);
  await c2.client.start();
  await env.clock.advance(100);
  c2.client.requestLink(); await flush(); c1.client.approveLink(c1.view().link.requests[0].nonce); await flush();
  await env.clock.advance(minutes(3));
  const n0 = env.relay.syncs().length;
  const follower = [c1, c2].find((c) => !c.view().leader);
  follower.client.setBoardHover(true);
  await env.clock.advance(60000);
  assert.ok(env.relay.syncs().length - n0 >= 35, `fast: ${env.relay.syncs().length - n0}`);
  follower.client.setBoardHover(false);
  await env.clock.advance(5000);
  const n1 = env.relay.syncs().length;
  await env.clock.advance(minutes(3));
  assert.ok(env.relay.syncs().length - n1 <= 4);
});

test("when the polling copy goes away another one takes over, also when it vanished without saying goodbye", async () => {
  const env = makeEnv();
  const c1 = await pairedCopy(env, "hearth");
  const rest = [makeCopy("hearth#2", env), makeCopy("hearth#3", env)];
  for (const c of rest) { await c.client.start(); await env.clock.advance(100); c.client.requestLink(); await flush(); c1.client.approveLink(c1.view().link.requests[0].nonce); await flush(); }
  await env.clock.advance(minutes(3));
  const copies = [c1, ...rest];
  let leader = copies.find((c) => c.view().leader);
  const n0 = leader.syncs();
  leader.client.stop();                           // closed cleanly
  const others = copies.filter((c) => c !== leader);
  await env.clock.advance(minutes(5));
  const after = others.map((c) => c.syncs());
  assert.ok(after.reduce((s, n) => s + n, 0) >= 4, `someone else polls: ${after}`);
  assert.equal(others.filter((c) => c.view().leader).length, 1);
  assert.ok(leader.syncs() - n0 <= 1);

  leader = others.find((c) => c.view().leader);
  const survivor = others.find((c) => c !== leader);
  leader.channel.muted = true;                    // vanished: nobody hears it any more (its own timers are irrelevant here)
  const n1 = survivor.syncs();
  await env.clock.advance(minutes(5));
  assert.ok(survivor.syncs() - n1 >= 3, `the survivor took over after the ttl: ${survivor.syncs() - n1}`);
  assert.equal(survivor.view().leader, true);
});

test("no copy channel at all (BroadcastChannel missing): the copy simply polls for itself", async () => {
  const env = makeEnv();
  const c = await pairedCopy(env, "hearth", { noChannel: true });
  await env.clock.advance(minutes(5));
  assert.ok(env.relay.syncs().length >= 5);
  assert.equal(c.view().leader, true);
});

test("linking: the paired copy can say no, an unanswered request times out, and an unpaired copy can't take a pairing unasked", async () => {
  const env = makeEnv();
  const c1 = await pairedCopy(env, "hearth");
  const c2 = makeCopy("hearth#2", env);
  await c2.client.start();
  await env.clock.advance(100);
  assert.equal(c2.client.requestLink(), true);
  assert.equal(c2.client.requestLink(), false, "one request at a time");
  await flush();
  const nonce = c1.view().link.requests[0].nonce;
  c1.client.denyLink(nonce); await flush();
  assert.equal(c2.view().paired, false);
  assert.match(c2.view().notice.text, /said no/);
  assert.equal(c1.view().link.requests.length, 0);

  assert.equal(c2.client.requestLink(), true);
  await flush();
  await env.clock.advance(70000);
  assert.equal(c2.view().link.pending, false);
  assert.match(c2.view().notice.text, /No paired copy answered/);
  assert.equal(c1.view().link.requests.length, 0, "an unanswered request expires on the paired copy too");

  // a forged grant (nobody asked) is ignored
  const forged = env.bus.channel();
  forged.postMessage({ v: 1, from: "evil", t: "grant", to: c2.client.id, nonce: "00", pairing: c1.store.data.get("pair") });
  await flush();
  assert.equal(c2.view().paired, false);
});

test("the key crosses the channel only in an allowed grant", async () => {
  const env = makeEnv();
  const c1 = await pairedCopy(env, "hearth");
  const c2 = makeCopy("hearth#2", env);
  await c2.client.start();
  await env.clock.advance(100);
  c2.client.requestLink(); await flush();
  await env.clock.advance(20000);
  const key = c1.store.data.get("pair").key;
  assert.equal(env.bus.traffic.filter((m) => JSON.stringify(m).includes(key)).length, 0, "no key before the user allows it");
  c1.client.approveLink(c1.view().link.requests[0].nonce); await flush();
  const withKey = env.bus.traffic.filter((m) => JSON.stringify(m).includes(key));
  assert.deepEqual(withKey.map((m) => m.t), ["grant"]);
  await env.clock.advance(minutes(5));
  assert.equal(env.bus.traffic.filter((m) => m.t !== "grant" && JSON.stringify(m).includes(key)).length, 0);
});

test("unpair and invite again: the partner keeps the room, reinvites, and the one who left joins with the new code", async () => {
  const env = makeEnv();
  const a1 = await pairedCopy(env, "hearth");
  const a2 = makeCopy("hearth#2", env);
  await a2.client.start(); await env.clock.advance(100);
  a2.client.requestLink(); await flush(); a1.client.approveLink(a1.view().link.requests[0].nonce); await flush();
  const b = makeCopy("hearth", makeShared(env), { noChannel: true });   // the partner's PC: same relay, own clock/bus
  await b.client.start();
  assert.equal(await b.client.join(a1.view().invite.code), true);
  await env.clock.advance(minutes(2));
  assert.equal(b.view().phase, "paired");

  // leave from the second copy: every copy on that PC forgets, the relay keeps the room
  assert.equal(await a2.client.unpair(), true);
  await flush();
  assert.equal(a2.view().phase, "setup");
  assert.equal(a1.view().phase, "setup", "the other copy on this PC forgets too");
  assert.equal(a1.store.data.has("pair"), false);
  assert.equal(a2.store.data.has("pair"), false);
  assert.equal(env.relay.rooms.size, 1);

  // the partner finds out on its next sync and may invite again
  await env.clock.advance(minutes(1.5));
  assert.equal(b.view().phase, "unpaired");
  assert.equal(b.view().unpaired.by, "peer");
  assert.ok(b.view().unpaired.graceUntil > env.clock.now());
  assert.equal(await b.client.reinvite(), true);
  assert.equal(b.view().invite.kind, "reinvite");
  assert.equal(b.view().invite.rebinds, "a");
  assert.equal(b.view().phase, "paired");

  assert.equal(await a1.client.join(b.view().invite.code), true);
  assert.equal(a1.view().me, "a");
  assert.equal(a1.store.data.get("pair").room, b.store.data.get("pair").room);
  await env.clock.advance(minutes(3));
  assert.equal(b.view().phase, "paired");
  assert.equal(a1.view().offline, false);
});

function makeShared(env) { return { clock: env.clock, relay: env.relay, bus: makeBus() }; }

test("invite again while still paired (new PC): the old key stops working and that copy forgets, on a fresh install the new one joins", async () => {
  const env = makeEnv();
  const a = await pairedCopy(env, "hearth", { noChannel: true });
  const b = makeCopy("hearth", env, { noChannel: true });
  await b.client.start();
  await b.client.join(a.view().invite.code);
  await env.clock.advance(minutes(2));
  assert.equal(await a.client.reinvite(), true);
  assert.equal(a.view().invite.rebinds, "b");
  const fresh = makeCopy("hearth", env, { noChannel: true });    // reinstalled: empty store
  await fresh.client.start();
  assert.equal(await fresh.client.join(a.view().invite.code), true);
  assert.equal(fresh.view().me, "b");
  await env.clock.advance(minutes(2));
  assert.equal(b.view().phase, "setup", "the old PC of that side is told it is no longer paired");
  assert.match(b.view().notice.text, /no longer paired/);
  assert.equal(b.store.data.has("pair"), false);
  assert.equal(fresh.view().offline, false);
  assert.equal(a.view().phase, "paired");
});

test("a copy the relay says has left forgets its pairing", async () => {
  const env = makeEnv();
  const a = await pairedCopy(env, "hearth", { noChannel: true });
  const roomId = a.store.data.get("pair").room;
  env.relay.rooms.get(roomId).unpaired = { by: "a", graceUntil: env.clock.now() + 1e9 };
  await env.clock.advance(minutes(1));
  assert.equal(a.view().phase, "setup", "told by the relay that it left");
  assert.equal(env.relay.rooms.size, 1);
});

test("unpair offline keeps the pairing and says so", async () => {
  const env = makeEnv();
  const a = await pairedCopy(env, "hearth", { noChannel: true });
  env.relay.down = true;
  assert.equal(await a.client.unpair(), false);
  assert.equal(a.view().phase, "paired");
  assert.match(a.view().notice.text, /Could not reach/);
  assert.equal(a.store.data.has("pair"), true);
});

test("offline is soft: data stays, the flag shows after two failures, polling keeps its rhythm, and recovery clears it", async () => {
  const env = makeEnv();
  const a = await pairedCopy(env, "hearth", { noChannel: true });
  const roomId = a.store.data.get("pair").room;
  env.relay.mutate(roomId, (r) => { r.names = { a: "Ada", b: "Bo" }; });
  await env.clock.advance(minutes(2));
  assert.equal(a.view().names.you, "Ada");
  env.relay.down = true;
  const n0 = env.relay.log.length;
  await env.clock.advance(minutes(1));
  assert.equal(a.view().offline, false, "one failure is not enough to call it offline");
  await env.clock.advance(minutes(1));
  assert.equal(a.view().offline, true);
  assert.equal(a.view().names.you, "Ada", "the last data stays on screen");
  assert.equal(a.view().phase, "paired");
  assert.equal(env.relay.log.length, n0, "failed requests never reached the fake relay");
  env.relay.down = false;
  await env.clock.advance(minutes(1));
  assert.equal(a.view().offline, false);
  env.relay.junk = true;                          // e.g. Cloudflare's own quota page
  await env.clock.advance(minutes(3));
  assert.equal(a.view().offline, true);
  assert.equal(a.view().phase, "paired");
});

test("an {error} answer is shown but is not 'offline' and does not drop the pairing", async () => {
  const env = makeEnv();
  const a = await pairedCopy(env, "hearth", { noChannel: true });
  const real = env.relay.fetchJson;
  env.relay.fetchJson = async () => ({ error: "server error" });
  a.client.refresh();
  await env.clock.advance(minutes(1.2));
  assert.equal(a.view().lastError, "server error");
  env.relay.fetchJson = real;
  assert.equal(a.view().offline, false);
  assert.equal(a.view().phase, "paired");
});

test("a restart shows the saved names before the network answers, and an invite still counting down survives", async () => {
  const env = makeEnv();
  const a = await pairedCopy(env, "hearth", { noChannel: true });
  const roomId = a.store.data.get("pair").room;
  env.relay.mutate(roomId, (r) => { r.names = { a: "Ada", b: "Bo" }; r.since = "2024-02-14"; });
  await env.clock.advance(minutes(2));
  await env.clock.advance(35000);
  assert.ok(a.store.data.has("snap"));
  assert.deepEqual(a.store.data.get("snap").sync.board.strokes, [], "the board is not cached");
  a.client.stop();
  env.relay.down = true;
  const again = makeCopy("hearth", env, { store: a.store, noChannel: true });
  await again.client.start();
  assert.equal(again.view().phase, "paired");
  assert.deepEqual(again.view().names, { you: "Ada", partner: "Bo" });
  assert.equal(again.view().togetherSince, "2024-02-14");
  assert.ok(again.view().invite && !again.view().invite.expired, "a code that is still counting down is shown again");
  await env.clock.advance(3000);
  assert.equal(again.view().offline, false, "one failed sync with data on screen is not 'offline'");
  await env.clock.advance(minutes(9));
  assert.equal(again.view().invite, null, "an expired code is dropped");
  assert.equal(again.store.data.has("invite"), false);
  assert.equal(again.view().offline, true);
  assert.deepEqual(again.view().names, { you: "Ada", partner: "Bo" }, "still showing the last data");
});

test("writes go through call(): authenticated, then a prompt sync; a partner tap becomes an event", async () => {
  const env = makeEnv();
  const a = await pairedCopy(env, "hearth", { noChannel: true });
  await env.clock.advance(minutes(2));
  const n0 = env.relay.syncs().length;
  const r = await a.client.call("set", { names: "Ada,Bo", since: "2024-02-14" });
  assert.equal(r.ok, true);
  await env.clock.advance(3000);
  assert.equal(env.relay.syncs().length, n0 + 1, "a prompt sync after our own change");
  assert.deepEqual(a.view().names, { you: "Ada", partner: "Bo" });
  const roomId = a.store.data.get("pair").room;
  env.relay.mutate(roomId, (rm) => { rm.taps.b = { n: 1, at: env.clock.now() }; });
  await env.clock.advance(minutes(1.1));
  assert.deepEqual(a.events.map((e) => e.type), ["peerTap"]);
  await assert.rejects(() => a.client.call("beat"), /Unknown/);
  env.relay.down = true;
  await assert.rejects(() => a.client.call("tap"), /Request failed/);
  const lone = makeCopy("x", env, { noChannel: true });
  await lone.client.start();
  await assert.rejects(() => lone.client.call("tap"), /Not paired/);
});

test("events reach every copy of the PC", async () => {
  const env = makeEnv();
  const c1 = await pairedCopy(env, "hearth");
  const c2 = makeCopy("hearth#2", env);
  await c2.client.start(); await env.clock.advance(100);
  c2.client.requestLink(); await flush(); c1.client.approveLink(c1.view().link.requests[0].nonce); await flush();
  await env.clock.advance(minutes(2));
  env.relay.mutate(c1.store.data.get("pair").room, (r) => { r.taps.b = { n: 4, at: env.clock.now() }; });
  await env.clock.advance(minutes(1.1));
  assert.deepEqual(c1.events.map((e) => e.type), ["peerTap"]);
  assert.deepEqual(c2.events.map((e) => e.type), ["peerTap"]);
});

// ---------- the widget folder ----------

test("manifest: id, one declared host, notifications, the view option, a preview", () => {
  assert.equal(manifest.id, "hearth");
  assert.match(manifest.version, /^\d+\.\d+\.\d+$/); // bumped on every change, so no exact pin (a pin broke CI at 1.5.0)
  assert.equal(manifest.author, "Yacine-DH");
  assert.deepEqual(manifest.permissions.hosts, [HOST]);
  assert.equal(manifest.permissions.notifications, true);
  assert.equal(H.HOST, HOST);
  const v = manifest.options.view;
  assert.equal(v.type, "select");
  assert.equal(v.default, "overview");
  assert.deepEqual(v.choices.map((c) => c.value), ["overview", "board", "pet"]);
  assert.deepEqual(Object.keys(manifest.options), ["view"]);
  // The base size is the strip: Umbra resizes from 50 % to 300 % of it and zooms by the smaller stretch, so a small base
  // lets the user go from a thin strip to a tall card. A tall base (1.8.0: 340 x 540) blocked shrinking below 270 px.
  assert.equal(manifest.width, 300);
  assert.equal(manifest.height, 120);
  assert.equal(manifest.preview, "preview.png");
  assert.ok(existsSync(new URL("../../widgets/hearth/preview.png", import.meta.url)));
});

test("the bundled font: every @font-face file exists, the licence ships with it, and it is first in the stack", () => {
  const css = readFileSync(new URL("../../widgets/hearth/hearth.css", import.meta.url), "utf8");
  const files = [...css.matchAll(/url\("([^"]+)"\)/g)].map((m) => m[1]);
  assert.deepEqual(files.sort(), ["nunito-latin-ext.woff2", "nunito-latin.woff2"]);
  for (const f of files) assert.ok(existsSync(new URL(`../../widgets/hearth/${f}`, import.meta.url)), f);
  assert.match(readFileSync(new URL("../../widgets/hearth/OFL.txt", import.meta.url), "utf8"), /SIL Open Font License/);
  assert.match(css, /--hm-font:\s*"Nunito",/);
});

test("the pairing secret never goes to localStorage, and nothing but the relay host is named", () => {
  for (const f of ["hearth-core.js", "hearth.js", "index.html", "hearth.css"]) {
    const src = readFileSync(new URL(`../../widgets/hearth/${f}`, import.meta.url), "utf8");
    assert.equal(/localStorage|sessionStorage|indexedDB/.test(src), false, f);
    const hosts = [...src.matchAll(/https?:\/\/([a-z0-9.-]+)/gi)].map((m) => m[1]).filter((h) => !/^(umbra\.local|www\.w3\.org|community\.umbra\.local)$/.test(h));
    for (const h of hosts) assert.equal(h, HOST, `${f} mentions ${h}`);
  }
});

test("normalizeState rebuilds a received state through the same validation; junk is refused", () => {
  assert.equal(H.normalizeState(null), null);
  assert.equal(H.normalizeState({ rev: 0 }), null);
  assert.equal(H.normalizeState({ rev: "x" }), null);
  const s = H.normalizeState({ rev: 4, now: 100, skew: 10, names: { a: "<b>", b: 7 }, togetherSince: "x", taps: "no", board: { strokes: [{ i: 2 }, "junk", null], clear: 5 } });
  assert.equal(s.rev, 4);
  assert.equal(s.skew, 10);
  assert.deepEqual(s.names, { a: "<b>", b: "" });
  assert.equal(s.togetherSince, "");
  assert.deepEqual(s.taps, H.emptyState().taps);
  assert.deepEqual(s.board, { strokes: [{ i: 2 }], clear: null });
});

test("a forged channel message full of garbage can neither break a copy nor smuggle in odd events", async () => {
  const env = makeEnv();
  const c = await pairedCopy(env, "hearth");
  const evil = env.bus.channel();
  const fp = H.fingerprint(c.store.data.get("pair"));
  evil.postMessage({ v: 1, from: "evil", t: "state", fp, sync: { rev: 99, board: 5, names: 5, taps: [] }, unpaired: "x", failures: "many", lastError: 12, events: [{ type: "boom" }, null, { type: "peerTap", at: "now" }] });
  await flush();
  const v = c.view();
  assert.equal(Array.isArray(v.sync.board.strokes), true);
  assert.deepEqual(v.names, { you: "", partner: "" });
  assert.equal(v.phase, "paired");
  assert.deepEqual(c.events.map((e) => e.type), ["peerTap"]);
  evil.postMessage({ v: 1, from: "evil", t: "claim", nonce: 5 });
  evil.postMessage({ t: "state" });
  evil.postMessage(null);
  await flush();
  assert.equal(c.view().link.requests.length, 0);
});

// ---------- which copy shows the tap toasts ----------

test("electToaster: the smallest copy that may notify, else the polling copy", () => {
  const peers = new Map([
    ["hearth#2.aa", { at: 1000, fp: "F", nt: false }],
    ["hearth#3.bb", { at: 1000, fp: "F", nt: true }],
    ["hearth#1.cc", { at: 1000, fp: "OTHER", nt: true }], // another pairing on this PC
    ["hearth#0.dd", { at: -99999, fp: "F", nt: true }],   // not heard from lately
  ]);
  assert.equal(H.electToaster("hearth.zz", "F", false, peers, 2000, "hearth#2.aa"), "hearth#3.bb");
  assert.equal(H.electToaster("hearth#2.aa", "F", true, peers, 2000, "hearth#2.aa"), "hearth#2.aa");
  assert.equal(H.electToaster("hearth.zz", "F", false, new Map(), 2000, "hearth.zz"), "hearth.zz", "nobody may notify: the leader tries");
  assert.equal(H.electToaster("hearth.zz", "", true, peers, 2000, null), null, "not paired");
});

test("tap toasts come from a copy that may notify, even when the polling copy may not", async () => {
  const env = makeEnv();
  const c1 = await pairedCopy(env, "hearth");
  const rest = [makeCopy("hearth#2", env), makeCopy("hearth#3", env)];
  for (const c of rest) { await c.client.start(); await env.clock.advance(100); c.client.requestLink(); await flush(); c1.client.approveLink(c1.view().link.requests[0].nonce); await flush(); }
  await env.clock.advance(minutes(1));
  const copies = [c1, ...rest];
  const leader = copies.find((c) => c.view().leader);
  assert.equal(leader.view().toaster, true, "nobody may notify yet: the polling copy keeps trying, as before");

  const allowed = copies.find((c) => c !== leader);
  allowed.client.setCanNotify(true);
  await env.clock.advance(minutes(1));             // heartbeats carry it to the other copies
  for (const c of copies) assert.equal(c.view().toaster, c === allowed, c.name);
  assert.equal(copies.filter((c) => c.view().leader).length, 1, "polling is unchanged");
  assert.equal(leader.view().leader, true);

  allowed.client.setCanNotify(false);               // the user turned it off again
  await env.clock.advance(minutes(1));
  for (const c of copies) assert.equal(c.view().toaster, c === leader, c.name);
});
