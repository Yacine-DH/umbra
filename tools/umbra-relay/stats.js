// Umbra stats: anonymous counters, nothing about who sent them. One Durable Object ("stats") keeps
//   t           { installs: { <widget id>: n }, since: "YYYY-MM-DD" }      totals since counting started
//   d:<day>     { i: { <widget id>: n }, v: { <path>: n }, r: { <host>: n } }   one UTC day: installs, site views, referrers
// No ids, IP addresses, user agents or versions are stored or derived. Days older than KEEP_DAYS are deleted.
//
// Public (CORS *, status 200 JSON):
//   GET /stats/install?w=<widget id>   -> { ok }        the app: a catalog widget was installed (not on updates)
//   GET /stats/view?p=<path>&r=<host>  -> { ok }        the website: a page was viewed (r = referrer host or empty)
//   GET /stats/installs                -> { widgets: { id: n }, since }   totals, for Browse and the website catalog
// Private:
//   GET /stats/summary  (Authorization: Bearer <STATS_TOKEN>)  -> { days: [{ day, i, v, r }], totals, since }
//   Without the secret configured (wrangler secret put STATS_TOKEN) or with a wrong token: { error: "unauthorized" }.

export const STATS_ROUTES = new Set(["install", "view", "installs", "summary"]);
export const LIMITS = Object.freeze({ widgets: 500, paths: 300, hosts: 100, keepDays: 400, summaryDays: 120 });

const WIDGET_ID = /^[a-z0-9][a-z0-9-]{1,39}$/;
const HOST = /^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,}$/;
const PATH = /^\/[a-z0-9/_.-]{0,99}$/;

const day = (ms) => new Date(ms).toISOString().slice(0, 10);

/** "/docs/Intro/?x=1#y" -> "/docs/intro/"; null when it is not a plain site path. */
export function cleanPath(p) {
  if (typeof p !== "string") return null;
  const s = p.split(/[?#]/)[0].toLowerCase();
  return PATH.test(s) && !s.includes("..") ? s : null;
}

/** A referrer host ("www.google.com"), "" for none, or null when it is not a hostname. */
export function cleanHost(h) {
  if (h == null || h === "") return "";
  const s = String(h).toLowerCase().replace(/^www\./, "");
  return HOST.test(s) ? s : null;
}

/** Adds one to `map[key]` unless that would add a new key beyond `cap`. */
function bump(map, key, cap) {
  if (key in map) map[key]++;
  else if (Object.keys(map).length < cap) map[key] = 1;
  else return false;
  return true;
}

/** Constant-time string compare (the token is short; this avoids leaking how much of it matched). */
function same(a, b) {
  if (typeof a !== "string" || typeof b !== "string" || !a || a.length !== b.length) return false;
  let d = 0;
  for (let i = 0; i < a.length; i++) d |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return d === 0;
}

async function today(s, now) {
  const key = "d:" + day(now);
  let d = await s.get(key);
  if (!d) {
    d = { i: {}, v: {}, r: {} };
    // A new day: drop the days that fell out of the window.
    const cutoff = "d:" + day(now - LIMITS.keepDays * 86400000);
    const old = [...(await s.list({ prefix: "d:", end: cutoff })).keys()];
    for (let k = 0; k < old.length; k += 128) await s.delete(old.slice(k, k + 128));
  }
  return { key, d };
}

/** Runs inside the "stats" Durable Object. `q` are the query parameters, `auth` the Authorization header. */
export async function handleStats(s, route, q, env, now, auth) {
  switch (route) {
    case "install": {
      const w = q.get("w") || "";
      if (!WIDGET_ID.test(w)) return { error: "bad widget id" };
      const t = (await s.get("t")) || { installs: {}, since: day(now) };
      const { key, d } = await today(s, now);
      bump(t.installs, w, LIMITS.widgets);
      bump(d.i, w, LIMITS.widgets);
      await s.put({ t, [key]: d });
      return { ok: true };
    }
    case "view": {
      const p = cleanPath(q.get("p"));
      const r = cleanHost(q.get("r"));
      if (p === null || r === null) return { error: "bad request" };
      const { key, d } = await today(s, now);
      bump(d.v, p, LIMITS.paths);
      if (r) bump(d.r, r, LIMITS.hosts);
      await s.put(key, d);
      return { ok: true };
    }
    case "installs": {
      const t = (await s.get("t")) || { installs: {}, since: day(now) };
      return { widgets: t.installs, since: t.since };
    }
    case "summary": {
      const token = /^Bearer (.+)$/.exec(auth || "")?.[1];
      if (!same(token, env && env.STATS_TOKEN)) return { error: "unauthorized" };
      const from = "d:" + day(now - (LIMITS.summaryDays - 1) * 86400000);
      const rows = await s.list({ prefix: "d:", start: from });
      const t = (await s.get("t")) || { installs: {}, since: day(now) };
      return { days: [...rows].map(([k, v]) => ({ day: k.slice(2), ...v })), totals: t.installs, since: t.since, now };
    }
  }
  return { error: "not found" };
}
