import test from "node:test";
import assert from "node:assert/strict";
import { createRelay, DAY } from "./harness.mjs";
import { LIMITS, cleanPath, cleanHost } from "../../tools/umbra-relay/stats.js";

const auth = (token) => ({ headers: { authorization: "Bearer " + token } });

test("stats: installs are counted per widget, in total and per day, and nothing else is stored", async () => {
  const r = createRelay();
  for (const w of ["clock", "clock", "weather"]) assert.deepEqual(await r.get("/stats/install?w=" + w), { ok: true });
  assert.deepEqual(await r.get("/stats/installs"), { widgets: { clock: 2, weather: 1 }, since: "2026-06-01" });
  const s = r.storageOf("stats");
  assert.deepEqual([...s.m.keys()].sort(), ["d:2026-06-01", "t"]);
  assert.deepEqual(s.m.get("d:2026-06-01"), { i: { clock: 2, weather: 1 }, v: {}, r: {} });
});

test("stats: bad input changes nothing and still answers 200 JSON with CORS", async () => {
  const r = createRelay();
  for (const path of ["/stats/install", "/stats/install?w=Bad_Id", "/stats/install?w=a", "/stats/view?p=nope", "/stats/view?p=/a/../b", "/stats/view?p=/&r=not a host", "/stats/nope"]) {
    const res = await r.raw(path);
    assert.equal(res.status, 200, path);
    assert.ok(res.body.error, path);
  }
  assert.equal((await r.raw("/stats/install?w=clock", { method: "POST" })).body.error, "GET only");
  const pre = await worker(r, "/stats/summary", { method: "OPTIONS" });
  assert.equal(pre.status, 204);
  assert.equal(pre.headers.get("access-control-allow-origin"), "*");
  assert.match(pre.headers.get("access-control-allow-headers"), /authorization/);
  const ok = await worker(r, "/stats/installs");
  assert.equal(ok.headers.get("access-control-allow-origin"), "*");
  assert.equal(ok.headers.get("cache-control"), "public, max-age=600");
  assert.deepEqual((await r.get("/stats/installs")).widgets, {});
});

test("stats: page views keep only a clean path and a referrer host", async () => {
  const r = createRelay();
  await r.get("/stats/view?p=" + encodeURIComponent("/Widgets/?category=games#x") + "&r=www.Google.com");
  await r.get("/stats/view?p=/widgets/&r=");
  await r.get("/stats/view?p=/docs/");
  assert.deepEqual(r.storageOf("stats").m.get("d:2026-06-01"), { i: {}, v: { "/widgets/": 2, "/docs/": 1 }, r: { "google.com": 1 } });
  assert.equal(cleanPath("https://evil.example/"), null);
  assert.equal(cleanHost("localhost"), null);
  assert.equal(cleanHost(""), "");
});

test("stats: the number of distinct widgets, paths and hosts is capped", async () => {
  const r = createRelay();
  for (let i = 0; i < LIMITS.paths + 5; i++) await r.get(`/stats/view?p=/p${i}/`);
  const d = r.storageOf("stats").m.get("d:2026-06-01");
  assert.equal(Object.keys(d.v).length, LIMITS.paths);
  await r.get("/stats/view?p=/p0/");
  assert.equal(r.storageOf("stats").m.get("d:2026-06-01").v["/p0/"], 2, "known keys still count past the cap");
});

test("stats: the summary needs the secret token; old days are dropped", async () => {
  const r = createRelay({ STATS_TOKEN: "s3cret-token" });
  await r.get("/stats/install?w=clock");
  r.advance(DAY);
  await r.get("/stats/install?w=todo");
  await r.get("/stats/view?p=/");

  assert.deepEqual(await r.get("/stats/summary"), { error: "unauthorized" });
  assert.deepEqual((await r.raw("/stats/summary", auth("wrong-token!"))).body, { error: "unauthorized" });
  const sum = (await r.raw("/stats/summary", auth("s3cret-token"))).body;
  assert.deepEqual(sum.totals, { clock: 1, todo: 1 });
  assert.deepEqual(sum.days.map((d) => d.day), ["2026-06-01", "2026-06-02"]);
  assert.deepEqual(sum.days[1], { day: "2026-06-02", i: { todo: 1 }, v: { "/": 1 }, r: {} });
  assert.equal((await r.raw("/stats/summary?auth=" + encodeURIComponent("Bearer s3cret-token"))).body.error, "unauthorized", "the token in a URL is ignored");

  const unset = createRelay();
  assert.deepEqual((await unset.raw("/stats/summary", auth("undefined"))).body, { error: "unauthorized" }, "no secret configured: nobody gets in");

  r.advance((LIMITS.keepDays + 1) * DAY);
  await r.get("/stats/install?w=clock");
  const keys = [...r.storageOf("stats").m.keys()].filter((k) => k.startsWith("d:"));
  assert.deepEqual(keys, ["d:" + new Date(r.clock.t).toISOString().slice(0, 10)], "days past the window are deleted");
  assert.deepEqual((await r.get("/stats/installs")).widgets, { clock: 2, todo: 1 }, "totals stay");
});

test("stats: never touches pairing rooms", async () => {
  const r = createRelay();
  await r.get("/stats/install?w=clock");
  assert.deepEqual([...r.objects.keys()], ["stats"]);
});

/** The raw Response (headers included) from the Worker. */
async function worker(r, path, init) {
  const { default: w } = await import("../../tools/umbra-relay/worker.js");
  return w.fetch(new Request("https://relay.test" + path, init), r.env);
}
