import test from "node:test";
import assert from "node:assert/strict";
import { createRelay, DAY } from "./harness.mjs";
import { REPORT_LIMITS, parseReport } from "../../tools/umbra-relay/reports.js";

const TOKEN = "s3cret-token";
const good = { text: "The clock widget froze after sleep.", version: "1.9.0", os: "Windows 11 (26200)", where: "about" };
const send = (r, body) => r.raw("/stats/report", { method: "POST", body: typeof body === "string" ? body : JSON.stringify(body) });
const list = (r, token = TOKEN) => r.raw("/stats/reports", { headers: { authorization: "Bearer " + token } }).then((x) => x.body);
const op = (r, body, token = TOKEN) =>
  r.raw("/stats/reports", { method: "POST", headers: { authorization: "Bearer " + token }, body: JSON.stringify(body) }).then((x) => x.body);

test("reports: a report is stored with exactly the fields it sent, nothing about the sender", async () => {
  const r = createRelay({ STATS_TOKEN: TOKEN });
  const res = await r.raw("/stats/report", {
    method: "POST",
    headers: { "user-agent": "Umbra/1.9.0", "cf-connecting-ip": "203.0.113.9", "x-forwarded-for": "203.0.113.9" },
    body: JSON.stringify({ ...good, where: "crash", widget: "clock", widgetVersion: "1.2.0", error: "Renderer exited", extra: "dropped" }),
  });
  assert.equal(res.status, 200);
  assert.deepEqual(res.body, { ok: true });
  const s = r.storageOf("reports");
  const [[k, v]] = [...s.m].filter(([k]) => k.startsWith("r:"));
  assert.match(k, /^r:\d{15}-[0-9a-f]{4}$/);
  assert.deepEqual(v, { at: r.clock.t, ...good, where: "crash", widget: "clock", widgetVersion: "1.2.0", error: "Renderer exited", done: false });
  assert.ok(!JSON.stringify([...s.m]).includes("203.0.113.9"));
  assert.ok(!JSON.stringify([...s.m]).includes("Umbra/1.9.0"));
  assert.deepEqual([...r.objects.keys()], ["reports"], "never touches stats or pairing rooms");
});

test("reports: bad bodies are refused with 200 JSON and store nothing", async () => {
  const r = createRelay({ STATS_TOKEN: TOKEN });
  const bad = [
    "not json", "[]", "null",
    { ...good, text: "short" },
    { ...good, text: "x".repeat(REPORT_LIMITS.text + 1) },
    { ...good, version: "latest" },
    { ...good, os: "<script>" },
    { ...good, where: "somewhere" },
    { ...good, widget: "../etc" },
    { ...good, widget: "clock", widgetVersion: "v1" },
    { ...good, error: "e".repeat(REPORT_LIMITS.error + 1) },
  ];
  for (const b of bad) {
    const res = await send(r, b);
    assert.equal(res.status, 200, JSON.stringify(b));
    assert.ok(res.body.error, JSON.stringify(b));
  }
  assert.equal((await r.raw("/stats/report")).body.error, "POST only");
  assert.equal((await send(r, "x".repeat(REPORT_LIMITS.body + 1))).body.error, "too long");
  assert.equal(r.storageOf("reports")?.m.size ?? 0, 0);
});

test("reports: text is cleaned (control characters, CRLF) and counted in characters", () => {
  assert.equal(parseReport({ ...good, text: "  line one\r\nline\u0007 two  " }).report.text, "line one\nline two");
  assert.equal(parseReport({ ...good, text: "日本語".repeat(600) }).report.text.length, 1800, "2000 characters, not bytes");
  assert.equal(parseReport({ ...good, widget: "" }).report.widget, undefined);
});

test("reports: listing, done and delete need the stats token", async () => {
  const r = createRelay({ STATS_TOKEN: TOKEN });
  await send(r, good);
  r.advance(1000);
  await send(r, { ...good, text: "Second problem report text" });

  assert.deepEqual(await list(r, "wrong-token!"), { error: "unauthorized" });
  assert.deepEqual((await r.raw("/stats/reports")).body, { error: "unauthorized" });
  assert.deepEqual(await op(r, { op: "delete", id: "r:000000000000000-0000" }, "nope"), { error: "unauthorized" });

  const l = await list(r);
  assert.equal(l.total, 2);
  assert.equal(l.newCount, 2);
  assert.equal(l.reports[0].text, "Second problem report text", "newest first");

  const [a, b] = l.reports;
  assert.deepEqual(await op(r, { op: "done", id: a.id }), { ok: true });
  assert.equal((await list(r)).newCount, 1);
  assert.deepEqual(await op(r, { op: "undone", id: a.id }), { ok: true });
  assert.equal((await list(r)).newCount, 2);
  assert.deepEqual(await op(r, { op: "delete", id: b.id }), { ok: true });
  assert.deepEqual((await list(r)).reports.map((x) => x.id), [a.id]);
  assert.deepEqual(await op(r, { op: "delete", id: b.id }), { error: "not found" });
  assert.deepEqual(await op(r, { op: "explode", id: a.id }), { error: "bad op" });
  assert.deepEqual(await op(r, { op: "done", id: "t" }), { error: "bad id" });

  const unset = createRelay();
  await send(unset, good);
  assert.deepEqual(await list(unset, "undefined"), { error: "unauthorized" }, "no secret configured: nobody reads reports");
});

test("reports: at most perDay a day; old reports and counters are pruned", async () => {
  const r = createRelay({ STATS_TOKEN: TOKEN });
  for (let i = 0; i < REPORT_LIMITS.perDay; i++) assert.deepEqual((await send(r, good)).body, { ok: true });
  assert.match((await send(r, good)).body.error, /busy/);
  r.advance(DAY);
  assert.deepEqual((await send(r, good)).body, { ok: true }, "a new day has a new budget");
  r.advance((REPORT_LIMITS.keepDays + 1) * DAY);
  await send(r, good);
  const keys = [...r.storageOf("reports").m.keys()];
  assert.equal(keys.filter((k) => k.startsWith("r:")).length, 1, "reports past keepDays are deleted");
  assert.equal(keys.filter((k) => k.startsWith("c:")).length, 1, "old day counters are deleted");
});

test("reports: the store keeps at most `stored` reports, dropping the oldest", async () => {
  const r = createRelay({ STATS_TOKEN: TOKEN });
  const s = r.storageOf("reports") ?? (await send(r, good), r.storageOf("reports"));
  for (let i = 0; i < REPORT_LIMITS.stored + 3; i++) s.m.set("r:" + String(r.clock.t - 100000 + i).padStart(15, "0") + "-0000", { ...good, at: 0, done: false });
  r.advance(1000);
  await send(r, { ...good, text: "The newest report of all" });
  const left = [...s.m.keys()].filter((k) => k.startsWith("r:")).sort();
  assert.equal(left.length, REPORT_LIMITS.stored);
  assert.equal(s.m.get(left.at(-1)).text, "The newest report of all");
});

test("reports: CORS allows the stats page to POST with its token", async () => {
  const r = createRelay();
  const { default: w } = await import("../../tools/umbra-relay/worker.js");
  const pre = await w.fetch(new Request("https://relay.test/stats/reports", { method: "OPTIONS" }), r.env);
  assert.equal(pre.status, 204);
  assert.match(pre.headers.get("access-control-allow-methods"), /POST/);
  assert.match(pre.headers.get("access-control-allow-headers"), /content-type/);
});
