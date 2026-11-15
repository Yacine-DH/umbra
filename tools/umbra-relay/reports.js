// Umbra problem reports: what a user typed into Settings > Report a problem, plus the app version, OS and (when it was
// about a widget) that widget's id and version. One Durable Object ("reports") keeps
//   r:<ms, 15 digits>-<4 hex>   { at, text, version, os, where, widget?, widgetVersion?, error?, done }
//   c:<day>                     how many reports arrived that UTC day (the daily cap)
// No ids, IP addresses, user agents or headers are stored or derived: the user is anonymous and stays so.
//
// Public (CORS *, status 200 JSON):
//   POST /stats/report   body JSON (at most 4 KB)  -> { ok } | { error }
// Private (Authorization: Bearer <STATS_TOKEN>, the stats page's token):
//   GET  /stats/reports                        -> { reports: [{ id, ...report }], newCount, total }
//   POST /stats/reports  { op: "done"|"undone"|"delete", id }   -> { ok } | { error }

import { same } from "./stats.js";

export const REPORT_ROUTES = new Set(["report", "reports"]);
export const REPORT_LIMITS = Object.freeze({ body: 4096, textMin: 10, text: 2000, error: 300, perDay: 100, stored: 1000, keepDays: 180, list: 500 });

const WIDGET_ID = /^[a-z0-9][a-z0-9-]{1,39}$/;
const VERSION = /^\d{1,4}\.\d{1,4}\.\d{1,6}([.-][0-9A-Za-z.-]{1,20})?$/;
const OS = /^[\w .(),/-]{1,60}$/;
const WHERE = new Set(["about", "tray", "error", "crash"]);
const ID = /^r:\d{15}-[0-9a-f]{4}$/;

const day = (ms) => new Date(ms).toISOString().slice(0, 10);
const key = (ms) => "r:" + String(ms).padStart(15, "0");

/** Control characters other than newline and tab removed, newlines normalised, trimmed. */
function clean(s, max) {
  if (typeof s !== "string") return null;
  const t = s.replace(/\r\n?/g, "\n").replace(/[\u0000-\u0008\u000b-\u001f\u007f]/g, "").trim();
  return [...t].length <= max ? t : null;
}

/** { report } (the record to store) or { error } for a report body. Pure, exported for tests. */
export function parseReport(raw) {
  let b;
  try { b = typeof raw === "string" ? JSON.parse(raw) : raw; } catch { return { error: "bad request" }; }
  if (!b || typeof b !== "object" || Array.isArray(b)) return { error: "bad request" };
  const text = clean(b.text, REPORT_LIMITS.text);
  if (text === null) return { error: "too long" };
  if ([...text].length < REPORT_LIMITS.textMin) return { error: "too short" };
  if (typeof b.version !== "string" || !VERSION.test(b.version)) return { error: "bad version" };
  if (typeof b.os !== "string" || !OS.test(b.os)) return { error: "bad os" };
  if (!WHERE.has(b.where)) return { error: "bad where" };
  const r = { text, version: b.version, os: b.os, where: b.where };
  if (b.widget != null && b.widget !== "") {
    if (typeof b.widget !== "string" || !WIDGET_ID.test(b.widget)) return { error: "bad widget" };
    r.widget = b.widget;
    if (b.widgetVersion != null && b.widgetVersion !== "") {
      if (typeof b.widgetVersion !== "string" || !VERSION.test(b.widgetVersion)) return { error: "bad widget version" };
      r.widgetVersion = b.widgetVersion;
    }
  }
  if (b.error != null && b.error !== "") {
    const e = clean(b.error, REPORT_LIMITS.error);
    if (e === null) return { error: "bad error" };
    if (e) r.error = e;
  }
  return { report: r };
}

/** Runs inside the "reports" Durable Object. `body` is the request text (POST) or "", `auth` the Authorization header. */
export async function handleReports(s, route, method, body, env, now, auth) {
  if (route === "report") {
    if (method !== "POST") return { error: "POST only" };
    const { report: r, error } = parseReport(body);
    if (!r) return { error };
    const ck = "c:" + day(now);
    const count = (await s.get(ck)) || 0;
    if (count >= REPORT_LIMITS.perDay) return { error: "busy, try again tomorrow" };
    await prune(s, now, ck);
    const id = key(now) + "-" + [...crypto.getRandomValues(new Uint8Array(2))].map((x) => x.toString(16).padStart(2, "0")).join("");
    await s.put({ [id]: { at: now, ...r, done: false }, [ck]: count + 1 });
    return { ok: true };
  }
  if (route === "reports") {
    const token = /^Bearer (.+)$/.exec(auth || "")?.[1];
    if (!same(token, env && env.STATS_TOKEN)) return { error: "unauthorized" };
    if (method === "POST") {
      let b;
      try { b = JSON.parse(body); } catch { return { error: "bad request" }; }
      if (!b || typeof b.id !== "string" || !ID.test(b.id)) return { error: "bad id" };
      const cur = await s.get(b.id);
      if (!cur) return { error: "not found" };
      if (b.op === "delete") await s.delete(b.id);
      else if (b.op === "done" || b.op === "undone") await s.put(b.id, { ...cur, done: b.op === "done" });
      else return { error: "bad op" };
      return { ok: true };
    }
    const rows = await s.list({ prefix: "r:", reverse: true, limit: REPORT_LIMITS.list });
    const reports = [...rows].map(([id, v]) => ({ id, ...v }));
    return { reports, newCount: reports.filter((r) => !r.done).length, total: reports.length, now };
  }
  return { error: "not found" };
}

/** Drops reports older than keepDays, the oldest beyond the stored cap, and old day counters. */
async function prune(s, now, todayKey) {
  const cutoff = key(now - REPORT_LIMITS.keepDays * 86400000);
  const del = [...(await s.list({ prefix: "r:", end: cutoff })).keys()];
  const kept = [...(await s.list({ prefix: "r:", start: cutoff })).keys()];
  if (kept.length >= REPORT_LIMITS.stored) del.push(...kept.slice(0, kept.length - REPORT_LIMITS.stored + 1));
  del.push(...[...(await s.list({ prefix: "c:" })).keys()].filter((k) => k !== todayKey));
  for (let i = 0; i < del.length; i += 128) await s.delete(del.slice(i, i + 128));
}
