// In-memory stand-in for Cloudflare: a fake Durable Object namespace, storage and clock, so the real Worker
// (router + Room class + hearth.js) runs unchanged under `node --test`. Not a test file itself.
import worker, { Room } from "../../tools/umbra-relay/worker.js";

export const START = Date.UTC(2026, 5, 1, 12, 0, 0); // 2026-06-01 12:00 UTC
export const SEC = 1000, MIN = 60 * SEC, HOUR = 60 * MIN, DAY = 24 * HOUR;

/** The subset of Durable Object storage the relay uses. Values are cloned like the real (serialising) storage, and
 *  the real call limits (128 keys per get/put/delete, 128 KiB per value) are enforced so tests catch violations. */
export class FakeStorage {
  constructor() { this.m = new Map(); this.alarm = null; this.reads = 0; this.writes = 0; }
  #check(keys) { if (keys.length > 128) throw new Error("storage call with more than 128 keys"); }
  #val(v) {
    const c = structuredClone(v);
    if (JSON.stringify(c).length > 128 * 1024) throw new Error("value over 128 KiB");
    return c;
  }
  async get(k) {
    if (Array.isArray(k)) {
      this.#check(k);
      const out = new Map();
      for (const x of k) if (this.m.has(x)) { out.set(x, structuredClone(this.m.get(x))); this.reads++; }
      return out;
    }
    this.reads++;
    return this.m.has(k) ? structuredClone(this.m.get(k)) : undefined;
  }
  async put(k, v) {
    if (typeof k === "string") { this.m.set(k, this.#val(v)); this.writes++; return; }
    const entries = Object.entries(k);
    this.#check(entries);
    for (const [a, b] of entries) { this.m.set(a, this.#val(b)); this.writes++; }
  }
  async delete(k) {
    const keys = Array.isArray(k) ? k : [k];
    this.#check(keys);
    let n = 0;
    for (const x of keys) if (this.m.delete(x)) n++;
    return Array.isArray(k) ? n : n > 0;
  }
  async deleteAll() { this.m.clear(); } // like the real one: the alarm is NOT cleared
  async list({ prefix = "", start, end, limit, reverse } = {}) {
    let keys = [...this.m.keys()].filter((k) => k.startsWith(prefix) && (start === undefined || k >= start) && (end === undefined || k < end)).sort();
    if (reverse) keys.reverse();
    if (limit !== undefined) keys = keys.slice(0, limit);
    this.reads += keys.length;
    return new Map(keys.map((k) => [k, structuredClone(this.m.get(k))]));
  }
  async setAlarm(t) { this.alarm = t instanceof Date ? t.getTime() : t; }
  async getAlarm() { return this.alarm; }
  async deleteAlarm() { this.alarm = null; }
}

/** A fresh relay: Worker + fake namespace + a clock you move with advance(). */
export function createRelay(vars = {}) {
  const clock = { t: START };
  const objects = new Map(); // DO name -> { storage, room }
  const env = {
    ...vars,
    clock: () => clock.t,
    ROOMS: {
      idFromName: (name) => name,
      get: (name) => {
        if (!objects.has(name)) {
          const storage = new FakeStorage();
          objects.set(name, { storage, room: new Room({ storage }, env) });
        }
        return { fetch: (url, init) => objects.get(name).room.fetch(new Request(url, init)) };
      },
    },
  };

  const relay = {
    env, clock, objects,
    advance(ms) { clock.t += ms; },
    /** Runs the alarms that are due, like Cloudflare would. */
    async fireAlarms() {
      for (let again = true; again; ) {
        again = false;
        for (const o of objects.values()) {
          if (o.storage.alarm !== null && o.storage.alarm <= clock.t) {
            o.storage.alarm = null;
            await o.room.alarm();
            again = true;
          }
        }
      }
    },
    /** Raw GET against the Worker: { status, body, text }. */
    async raw(path, init) {
      const res = await worker.fetch(new Request("https://relay.test" + path, init), env);
      const text = await res.text();
      return { status: res.status, text, body: text ? JSON.parse(text) : null };
    },
    /** GET that asserts the 200 contract and returns the parsed body. */
    async get(path) {
      const r = await relay.raw(path);
      if (r.status !== 200) throw new Error(`${path} answered ${r.status}`);
      return r.body;
    },
    storageOf: (name) => objects.get(name)?.storage,
    /** True when no Durable Object holds any data any more. */
    allEmpty: () => [...objects.values()].every((o) => o.storage.m.size === 0),
    roomEmpty: (roomId) => objects.get("r:" + roomId).storage.m.size === 0,
  };
  return relay;
}

const qs = (o) => new URLSearchParams(o).toString();

/** A couple: pairs two clients through /pair like the widgets do. */
export async function pairCouple(relay) {
  const a = await relay.get("/pair?new=1");
  const b = await relay.get("/pair?code=" + a.code);
  return { relay, room: a.room, a: client(relay, a.room, "a", a.key), b: client(relay, b.room, "b", b.key), code: a.code };
}

/** One partner's view: c.sync(since), c.call("tap"), ... Each returns the parsed JSON body. */
export function client(relay, room, me, key) {
  const c = {
    room, me, key,
    path(route, params = {}) { return `/hearth/${route}?` + qs({ room: c.room, me: c.me, key: c.key, ...params }); },
    call: (route, params) => relay.get(c.path(route, params)),
    sync: (since = 0) => c.call("sync", { since }),
  };
  return c;
}

// ---- stroke helpers (the encoding in tools/umbra-relay/README.md) ----
/** Encodes points [[x, y], ...] as "x.y.dx.dy...". */
export const pts = (points) => points.map((p, i) => (i === 0 ? p.join(".") : [p[0] - points[i - 1][0], p[1] - points[i - 1][1]].join("."))).join(".");
/** One stroke record "[k]c.z.<points>". */
export const rec = (c, z, points, k = false) => `${k ? "k" : ""}${c}.${z}.${pts(points)}`;
export const batch = (...recs) => recs.join("_");
