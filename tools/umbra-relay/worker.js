// Umbra relay: a tiny Cloudflare Worker + Durable Object behind two widgets. It pairs two people with a one-time
// code (Buddy and Hearth share that), tells each Buddy whether the other has sent a heartbeat recently, and keeps
// the small shared state of a Hearth couple. It stores random ids, secrets, timestamps and what the couple
// typed or drew: no accounts, no location.
//
// Buddy (unchanged since it shipped; the deployed Buddy widget depends on exactly this):
//   GET /pair?new=1                     -> { code, room, me:"a", key, ttl }      (start a pairing)
//   GET /pair?code=ABCD2345             -> { room, me:"b", key }                 (join; the code dies)
//   GET /beat?room=R&me=a|b&key=K&iv=60 -> { paired, peerSeen, peerIv, now }     (heartbeat + read peer)
//   GET /unpair?room=R&me=a|b&key=K     -> { ok }                                (peer sees paired:false)
//
// Hearth (see README.md for every parameter and response):
//   GET /hearth/sync|stroke|undo|clear|unclear|tap|care|set|unpair|reinvite?room=R&me=a|b&key=K&...
//
// Stats (see stats.js): anonymous install counts from the app and page views from the website, no ids of any kind.
//   GET /stats/install?w=ID   GET /stats/view?p=PATH&r=HOST   GET /stats/installs   GET /stats/summary (Bearer token)
//
// GET only, because widgets can only fetch with GET. Every response is JSON with status 200, errors as { error }
// (the host's fetch throws away the body of a non-2xx answer).

import { GRACE_MS, handleHearth } from "./hearth.js";
import { STATS_ROUTES, handleStats } from "./stats.js";

const ALPHABET = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"; // no I, L, O, 0, 1
const CODE_TTL_MS = 10 * 60 * 1000;
const ROOM_IDLE_MS = 30 * 24 * 60 * 60 * 1000; // a room nobody has beaten for 30 days is deleted
const MAX_HEARTH_URL = 6000; // a stroke batch is kept under ~4 KB by the client; anything near the edge limit is refused
const HEARTH_ROUTES = new Set(["sync", "stroke", "undo", "clear", "unclear", "tap", "care", "set", "unpair", "reinvite"]);

const rand = (n) => crypto.getRandomValues(new Uint8Array(n));
const hex = (b) => [...b].map((x) => x.toString(16).padStart(2, "0")).join("");
const newCode = () => [...rand(8)].map((x) => ALPHABET[x % ALPHABET.length]).join("");
const json = (o, status = 200) =>
  new Response(JSON.stringify(o), { status, headers: { "content-type": "application/json", "cache-control": "no-store" } });

export default {
  async fetch(req, env) {
    const u = new URL(req.url);
    const q = u.searchParams;
    const room = (name) => env.ROOMS.get(env.ROOMS.idFromName(name));
    const call = (stub, path, params) => stub.fetch("https://room/" + path + "?" + new URLSearchParams(params)).then((r) => r.json());

    if (u.pathname === "/pair" && q.get("new")) {
      const id = hex(rand(16)), ka = hex(rand(16)), kb = hex(rand(16)), code = newCode();
      await call(room("r:" + id), "init", { ka, kb });
      await call(room("c:" + code), "hold", { room: id, kb });
      return json({ code, room: id, me: "a", key: ka, ttl: CODE_TTL_MS / 1000 });
    }
    if (u.pathname === "/pair" && q.get("code")) {
      const code = q.get("code").toUpperCase().replace(/[^A-Z0-9]/g, "");
      if (code.length !== 8) return json({ error: "bad code" });
      const r = await call(room("c:" + code), "claim", {});
      // Buddy codes always join as "b"; a Hearth reinvite code names the side it rebinds.
      return r.room ? json({ room: r.room, me: r.side || "b", key: r.kb }) : json({ error: "unknown or expired code" });
    }
    if (u.pathname === "/beat" || u.pathname === "/unpair") {
      const id = q.get("room") || "", me = q.get("me");
      if (!/^[0-9a-f]{32}$/.test(id) || (me !== "a" && me !== "b")) return json({ error: "bad request" });
      return json(await call(room("r:" + id), u.pathname.slice(1), { me, key: q.get("key") || "", iv: q.get("iv") || "60" }));
    }
    if (u.pathname.startsWith("/stats/")) {
      // Read from the website and the app: CORS for any origin, never a non-2xx (except the preflight's 204).
      const cors = { "access-control-allow-origin": "*", "access-control-allow-headers": "authorization", "access-control-max-age": "86400" };
      if (req.method === "OPTIONS") return new Response(null, { status: 204, headers: cors });
      const out = (o, cache) => {
        const r = json(o);
        for (const [k, v] of Object.entries(cors)) r.headers.set(k, v);
        if (cache) r.headers.set("cache-control", cache);
        return r;
      };
      try {
        const name = u.pathname.slice("/stats/".length);
        if (!STATS_ROUTES.has(name)) return out({ error: "not found" });
        if (req.method !== "GET" && req.method !== "HEAD") return out({ error: "GET only" });
        if (req.url.length > 1000) return out({ error: "too long" });
        // Only the parameters each route reads go to the object. The token comes in a header (never a public URL) and is
        // handed to the object on this internal call only.
        const params = { w: q.get("w") || "", p: q.get("p") || "", r: q.get("r") || "", auth: req.headers.get("authorization") || "" };
        const r = await call(room("stats"), "stats/" + name, params);
        return out(r, name === "installs" && !r.error ? "public, max-age=600" : null);
      } catch {
        return out({ error: "server error" });
      }
    }
    if (u.pathname.startsWith("/hearth/")) {
      // Never a non-2xx here: whatever goes wrong is a JSON { error }.
      try {
        const name = u.pathname.slice("/hearth/".length);
        if (!HEARTH_ROUTES.has(name)) return json({ error: "not found" });
        if (req.method !== "GET" && req.method !== "HEAD") return json({ error: "GET only" });
        if (req.url.length > MAX_HEARTH_URL) return json({ error: "too long" });
        const id = q.get("room") || "", me = q.get("me");
        if (!/^[0-9a-f]{32}$/.test(id) || (me !== "a" && me !== "b")) return json({ error: "bad request" });
        if (name === "reinvite") {
          const code = newCode(), nk = hex(rand(16));
          const r = await call(room("r:" + id), "hearth/reinvite", { me, key: q.get("key") || "", nk });
          if (!r.ok) return json(r); // unknown room, wrong key, or this side left: { paired:false, ... }
          await call(room("c:" + code), "hold", { room: id, kb: nk, side: r.side });
          return json({ code, ttl: CODE_TTL_MS / 1000, rebinds: r.side });
        }
        return json(await call(room("r:" + id), "hearth/" + name, q));
      } catch {
        return json({ error: "server error" });
      }
    }
    return json({ error: "not found" }, 404);
  },
};

/** One Durable Object per pairing code and per room. Both kinds share this class. */
export class Room {
  constructor(state, env) {
    this.state = state;
    this.s = state.storage;
    this.env = env || {};
    this.clock = typeof this.env.clock === "function" ? this.env.clock : Date.now; // env.clock is a test hook only
  }

  async fetch(req) {
    const u = new URL(req.url);
    const q = u.searchParams;
    const now = this.clock();
    if (u.pathname.startsWith("/stats/")) {
      return Response.json(await handleStats(this.s, u.pathname.slice("/stats/".length), q, this.env, now, q.get("auth")));
    }
    if (u.pathname.startsWith("/hearth/")) {
      return Response.json(await handleHearth(this.s, u.pathname.slice("/hearth/".length), q, this.env, now));
    }
    switch (u.pathname) {
      case "/init":
        await this.s.put({ ka: q.get("ka"), kb: q.get("kb"), seenA: 0, seenB: 0, ivA: 60, ivB: 60, gone: false });
        await this.s.setAlarm(now + ROOM_IDLE_MS);
        return Response.json({ ok: true });

      case "/hold": // a pairing code: valid once, for 10 minutes. `side` (Hearth reinvite only) is the side it rebinds.
        await this.s.put({ room: q.get("room"), kb: q.get("kb"), exp: now + CODE_TTL_MS });
        if (q.get("side")) await this.s.put("side", q.get("side"));
        await this.s.setAlarm(now + CODE_TTL_MS);
        return Response.json({ ok: true });

      case "/claim": {
        const c = await this.s.get(["room", "kb", "exp", "side"]);
        await this.s.deleteAll();
        if (!c.get("room") || c.get("exp") < now) return Response.json({});
        return Response.json({ room: c.get("room"), kb: c.get("kb"), side: c.get("side") });
      }

      case "/beat":
      case "/unpair": {
        const me = q.get("me"), peer = me === "a" ? "b" : "a";
        const v = await this.s.get(["ka", "kb", "gone", "seenA", "seenB", "ivA", "ivB"]);
        if (!v.get("ka")) return Response.json({ paired: false, now });
        if (q.get("key") !== v.get("k" + me) || !q.get("key")) return Response.json({ paired: false, now });
        if (u.pathname === "/unpair") {
          await this.s.put("gone", true);
          return Response.json({ ok: true });
        }
        if (v.get("gone")) return Response.json({ paired: false, now });
        const iv = Math.min(600, Math.max(30, Number(q.get("iv")) || 60));
        await this.s.put({ ["seen" + me.toUpperCase()]: now, ["iv" + me.toUpperCase()]: iv });
        const P = peer.toUpperCase();
        return Response.json({ paired: true, peerSeen: v.get("seen" + P), peerIv: v.get("iv" + P), now });
      }
    }
    return Response.json({}, { status: 404 });
  }

  /**
   * Expired codes, abandoned rooms and rooms past their Hearth unpair grace period clean themselves up.
   * A room is abandoned after 30 days without a Buddy beat or a Hearth call.
   */
  async alarm() {
    const now = this.clock();
    const v = await this.s.get(["seenA", "seenB", "ka", "h"]);
    if (v.get("ka")) {
      const h = v.get("h");
      const last = Math.max(v.get("seenA") || 0, v.get("seenB") || 0, (h && h.seen) || 0);
      const idleAt = last ? last + ROOM_IDLE_MS : 0;
      const goneAt = h && h.gone ? h.gone.at + GRACE_MS : Infinity;
      if (now < idleAt && now < goneAt) return void (await this.s.setAlarm(Math.min(idleAt, goneAt)));
    }
    await this.s.deleteAll();
  }
}
