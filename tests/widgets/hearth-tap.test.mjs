import test from "node:test";
import assert from "node:assert/strict";
import { createRequire } from "node:module";
import { readFileSync } from "node:fs";

const require = createRequire(import.meta.url);
const T = require("../../widgets/hearth/tap-logic.js");
const core = require("../../widgets/hearth/hearth-core.js");
const dir = new URL("../../widgets/hearth/", import.meta.url);
const read = (f) => readFileSync(new URL(f, dir), "utf8");

function clock(start = 1_700_000_000_000) {
  let t = start;
  return { now: () => t, advance: (ms) => { t += ms; } };
}

// ---------- cooldown ----------

test("the cooldown is 10 s: a second tap is refused until it ends", () => {
  const c = clock();
  const p = T.createPolicy({ now: c.now });
  assert.deepEqual(p.canSend(), { ok: true, remainingMs: 0 });
  p.sent();
  assert.equal(p.canSend().ok, false);
  assert.equal(p.canSend().remainingMs, 10000);
  c.advance(9999);
  assert.equal(p.canSend().ok, false);
  assert.equal(p.canSend().remainingMs, 1);
  c.advance(1);
  assert.equal(p.canSend().ok, true);
});

test("rapid tapping: only the first of 50 clicks in 5 s is allowed", () => {
  const c = clock();
  const p = T.createPolicy({ now: c.now });
  let allowed = 0;
  for (let i = 0; i < 50; i++) {
    if (p.canSend().ok) { p.sent(); allowed++; }
    c.advance(100);
  }
  assert.equal(allowed, 1);
});

test("a failed tap waits 2 s, or the relay's retryIn (capped at a minute), not the whole cooldown", () => {
  const c = clock();
  const p = T.createPolicy({ now: c.now });
  p.sent(); p.failed();
  assert.equal(p.canSend().remainingMs, T.RETRY_MS);
  p.failed(25);
  assert.equal(p.canSend().remainingMs, 25000);
  p.failed(9999);
  assert.equal(p.canSend().remainingMs, 60000);
});

test("the cooldown is configurable and can be cleared", () => {
  const c = clock();
  const cd = T.createCooldown({ now: c.now, ms: 500 });
  cd.start();
  assert.equal(cd.ready(), false);
  c.advance(500);
  assert.equal(cd.ready(), true);
  cd.start();
  cd.clear();
  assert.equal(cd.ready(), true);
});

// ---------- events: dedupe, freshness, batches ----------

const ctx = (c, extra) => Object.assign({ serverNow: c.now(), leader: true }, extra);

test("a partner tap pulses, shows the line and toasts once", () => {
  const c = clock();
  const p = T.createPolicy({ now: c.now });
  const d = p.decide([{ type: "peerTap", at: c.now() - 1000 }], ctx(c));
  assert.deepEqual(d.tap, { at: c.now() - 1000, fresh: true });
  assert.equal(d.pulse, true);
  assert.equal(d.toast, "tap");
  assert.equal(d.together, null);
});

test("the same event delivered twice is handled once (dedupe by type and time)", () => {
  const c = clock();
  const p = T.createPolicy({ now: c.now });
  const e = { type: "peerTap", at: c.now() - 500 };
  assert.ok(p.decide([e], ctx(c)).tap);
  const again = p.decide([{ ...e }], ctx(c));
  assert.equal(again.tap, null);
  assert.equal(again.pulse, false);
  assert.equal(again.toast, null);
  // a later tap has another time and is new
  c.advance(70000);
  assert.ok(p.decide([{ type: "peerTap", at: c.now() }], ctx(c)).tap);
});

test("the same event twice in one batch counts once", () => {
  const c = clock();
  const p = T.createPolicy({ now: c.now });
  const e = { type: "peerTap", at: c.now() };
  const d = p.decide([e, { ...e }], ctx(c));
  assert.ok(d.tap);
  assert.equal(p.decide([e], ctx(c)).tap, null);
});

test("an event without a time is never deduped away", () => {
  const c = clock();
  const p = T.createPolicy({ now: c.now });
  assert.ok(p.decide([{ type: "peerTap", at: 0 }], ctx(c)).tap);
  c.advance(120000);
  assert.ok(p.decide([{ type: "peerTap" }], ctx(c)).tap);
});

test("peerStroke and unknown events are ignored", () => {
  const c = clock();
  const p = T.createPolicy({ now: c.now });
  const d = p.decide([{ type: "peerStroke" }, { type: "other", at: 5 }, null, undefined], ctx(c));
  assert.deepEqual(d, { tap: null, together: null, pulse: false, toast: null });
  assert.deepEqual(p.decide("nope", ctx(c)), { tap: null, together: null, pulse: false, toast: null });
});

test("an old tap (after sleep) shows the line but does not pulse or toast", () => {
  const c = clock();
  const p = T.createPolicy({ now: c.now });
  const d = p.decide([{ type: "peerTap", at: c.now() - T.FRESH_MS - 1 }], ctx(c));
  assert.equal(d.tap.fresh, false);
  assert.equal(d.pulse, false);
  assert.equal(d.toast, null);
});

test("server time is what counts for freshness, not the local clock", () => {
  const c = clock();
  const p = T.createPolicy({ now: c.now });
  const skewed = c.now() + 3 * 3600 * 1000; // the relay's clock is 3 h ahead of this PC
  assert.equal(p.decide([{ type: "peerTap", at: skewed - 2000 }], { serverNow: skewed, leader: true }).tap.fresh, true);
});

test("only the polling copy toasts, so copies on one PC do not stack two toasts", () => {
  const c = clock();
  const p = T.createPolicy({ now: c.now });
  const d = p.decide([{ type: "peerTap", at: c.now() }], ctx(c, { leader: false }));
  assert.equal(d.toast, null);
  assert.equal(d.pulse, true);
  assert.ok(d.tap);
});

test("tap toasts stay inside the host budget of 5 per 10 minutes (4 for taps, 1 kept for a together moment)", () => {
  const c = clock();
  const p = T.createPolicy({ now: c.now });
  let toasts = 0;
  for (let i = 0; i < 40; i++) { // a partner tapping every 15 s for 10 minutes
    c.advance(15000);
    if (p.decide([{ type: "peerTap", at: c.now() }], ctx(c)).toast) toasts++;
  }
  assert.ok(toasts <= T.TOAST_TAP_CAP, `toasts in 10 min: ${toasts}`);
  assert.ok(toasts >= 1);
});

test("after four tap toasts the fifth slot is kept for a together moment, then nothing more", () => {
  const c = clock();
  const p = T.createPolicy({ now: c.now });
  for (let i = 0; i < 4; i++) {
    c.advance(T.TOAST_GAP_MS);
    assert.equal(p.decide([{ type: "peerTap", at: c.now() }], ctx(c)).toast, "tap");
  }
  c.advance(T.TOAST_GAP_MS);
  assert.equal(p.decide([{ type: "peerTap", at: c.now() }], ctx(c)).toast, null, "a 5th tap toast would use the together slot");
  c.advance(T.TOGETHER_GAP_MS);
  assert.equal(p.decide([{ type: "together", at: c.now() }], ctx(c)).toast, "together");
  c.advance(T.TOGETHER_GAP_MS);
  assert.equal(p.decide([{ type: "together", at: c.now() }], ctx(c)).toast, null, "the host would refuse a 6th");
  c.advance(T.TOAST_WINDOW_MS); // the window slides: toasts are allowed again
  assert.equal(p.decide([{ type: "peerTap", at: c.now() }], ctx(c)).toast, "tap");
});

test("a toast the host refused because notifications are off stops further tries for a while", () => {
  const c = clock();
  const p = T.createPolicy({ now: c.now });
  assert.equal(p.decide([{ type: "peerTap", at: c.now() }], ctx(c)).toast, "tap");
  p.toastFailed("Notifications are off for this widget (Settings)");
  c.advance(T.TOAST_GAP_MS + 1);
  assert.equal(p.decide([{ type: "peerTap", at: c.now() }], ctx(c)).toast, null);
  c.advance(T.TOAST_BLOCK_MS);
  assert.equal(p.decide([{ type: "peerTap", at: c.now() }], ctx(c)).toast, "tap");
});

test("allowing notifications in Umbra's prompt lifts the block at once", () => {
  const c = clock();
  const p = T.createPolicy({ now: c.now });
  p.toastFailed("Notifications are off for this widget (Settings)");
  c.advance(T.TOAST_GAP_MS + 1);
  assert.equal(p.decide([{ type: "peerTap", at: c.now() }], ctx(c)).toast, null);
  p.toastsAllowed();
  c.advance(T.TOAST_GAP_MS + 1);
  assert.equal(p.decide([{ type: "peerTap", at: c.now() }], ctx(c)).toast, "tap");
});

test("a rate-limit refusal does not block toasts", () => {
  const c = clock();
  const p = T.createPolicy({ now: c.now });
  p.toastFailed("Rate limit reached (5 per 10 minutes)");
  assert.equal(p.decide([{ type: "peerTap", at: c.now() }], ctx(c)).toast, "tap");
});

// ---------- together ----------

test("a tap that completes a together moment arrives with it: only the together moment is shown", () => {
  const c = clock();
  const p = T.createPolicy({ now: c.now });
  const d = p.decide([{ type: "peerTap", at: c.now() }, { type: "together", at: c.now() }], ctx(c));
  assert.equal(d.tap, null);
  assert.deepEqual(d.together, { at: c.now(), fresh: true, play: true });
  assert.equal(d.pulse, true);
  assert.equal(d.toast, "together");
});

test("a partner tap newer than the together moment in the same batch is a separate tap", () => {
  const c = clock();
  const p = T.createPolicy({ now: c.now });
  const d = p.decide([{ type: "peerTap", at: c.now() + 800 }, { type: "together", at: c.now() }], ctx(c));
  assert.equal(d.together.at, c.now());
  assert.deepEqual(d.tap, { at: c.now() + 800, fresh: true });
  assert.equal(d.toast, "together"); // one toast per batch
});

test("together plays once per pair of taps: the answer to our own tap and the later sync event are one moment", () => {
  const c = clock();
  const p = T.createPolicy({ now: c.now });
  assert.equal(p.claimTogether(), true);   // the relay answered our tap with together: true
  c.advance(1500);
  const d = p.decide([{ type: "together", at: c.now() }], ctx(c)); // the next sync reports the same moment
  assert.equal(d.together.play, false);
  assert.equal(d.pulse, false);
  assert.equal(d.toast, null); // we caused it and saw it: no toast
});

test("the same together event twice plays once; a new moment later plays again", () => {
  const c = clock();
  const p = T.createPolicy({ now: c.now });
  const e = { type: "together", at: c.now() };
  assert.equal(p.decide([e], ctx(c)).together.play, true);
  assert.equal(p.decide([{ ...e }], ctx(c)).together, null);
  c.advance(10 * 60 * 1000);
  assert.equal(p.decide([{ type: "together", at: c.now() }], ctx(c)).together.play, true);
});

test("a stale together event (after sleep) is remembered in the line but not played or toasted", () => {
  const c = clock();
  const p = T.createPolicy({ now: c.now });
  const d = p.decide([{ type: "together", at: c.now() - T.FRESH_MS - 5 }], ctx(c));
  assert.equal(d.together.fresh, false);
  assert.equal(d.together.play, false);
  assert.equal(d.toast, null);
});

test("together toast bypasses the gap between tap toasts but not the budget", () => {
  const c = clock();
  const p = T.createPolicy({ now: c.now });
  assert.equal(p.decide([{ type: "peerTap", at: c.now() }], ctx(c)).toast, "tap");
  c.advance(5000);
  assert.equal(p.decide([{ type: "peerTap", at: c.now() }, { type: "together", at: c.now() }], ctx(c)).toast, "together");
});

// ---------- using the real client's event batches ----------

test("works with HearthCore.applySync events: a partner tap, then a tap that makes a together moment", () => {
  const c = clock();
  const p = T.createPolicy({ now: c.now });
  const first = core.applySync(core.emptyState(), { rev: 5, now: c.now(), taps: { a: { n: 0, at: 0 }, b: { n: 0, at: 0 } }, together: { n: 0, at: 0 } }, { me: "a", localNow: c.now() });
  assert.deepEqual(first.events, []); // nothing fires for the very first sync
  const tap = core.applySync(first.state, { rev: 6, now: c.now(), taps: { a: { n: 0, at: 0 }, b: { n: 1, at: c.now() } } }, { me: "a", localNow: c.now() });
  assert.deepEqual(tap.events.map((e) => e.type), ["peerTap"]);
  assert.ok(p.decide(tap.events, ctx(c)).tap);
  c.advance(30000);
  const tog = core.applySync(tap.state, { rev: 7, now: c.now(), taps: { a: { n: 1, at: c.now() - 20000 }, b: { n: 2, at: c.now() } }, together: { n: 1, at: c.now() } }, { me: "a", localNow: c.now() });
  assert.deepEqual(tog.events.map((e) => e.type).sort(), ["peerTap", "together"]);
  const d = p.decide(tog.events, ctx(c));
  assert.equal(d.tap, null);
  assert.equal(d.together.play, true);
});

// ---------- what is on screen ----------

const view = (over) => Object.assign({
  paired: true, phase: "paired", me: "a", unpaired: null,
  names: { you: "Ada", partner: "Sam" },
  sync: { skew: 0, taps: { a: { n: 0, at: 0 }, b: { n: 2, at: 1_700_000_000_000 } } },
}, over);

test("unseenTap: the partner's latest tap that the user has not seen", () => {
  const now = 1_700_000_005_000;
  assert.deepEqual(T.unseenTap(view(), 0, now), { n: 2, at: 1_700_000_000_000 });
  assert.equal(T.unseenTap(view(), 1_700_000_000_000, now), null);   // seen
  assert.equal(T.unseenTap(view(), 1_800_000_000_000, now), null);
  assert.equal(T.unseenTap(view({ me: "b", sync: { skew: 0, taps: { a: { n: 1, at: 1_700_000_002_000 }, b: { n: 9, at: 1_700_000_003_000 } } } }), 0, now).n, 1); // b looks at a's taps
  assert.equal(T.unseenTap(view(), 0, now + T.UNSEEN_MS + 1), null); // a day later it is no longer news
  assert.equal(T.unseenTap(view({ paired: false }), 0, now), null);
  assert.equal(T.unseenTap(null, 0, now), null);
  assert.equal(T.unseenTap(view({ sync: { skew: 0, taps: { a: { n: 0, at: 0 }, b: { n: 0, at: 0 } } } }), 0, now), null);
});

test("our own taps are never shown as received", () => {
  const v = view({ sync: { skew: 0, taps: { a: { n: 5, at: 1_700_000_001_000 }, b: { n: 0, at: 0 } } } });
  assert.equal(T.unseenTap(v, 0, 1_700_000_002_000), null);
  assert.equal(T.peerTapAt(v), 0);
  assert.equal(T.peerTapAt(view()), 1_700_000_000_000);
});

test("tapLine: name and time, a weekday for another day, and a fallback name", () => {
  const at = new Date(2026, 5, 10, 14, 2, 0).getTime();
  const same = T.tapLine("tap", "Sam", at, 0, { locale: "en-GB", hour12: false, now: new Date(2026, 5, 10, 18, 0).getTime() });
  assert.equal(same, "Sam tapped you\u00a0\u00b7\u00a014:02");
  const other = T.tapLine("tap", "Sam", at, 0, { locale: "en-GB", hour12: false, now: new Date(2026, 5, 12, 9, 0).getTime() });
  assert.match(other, /^Sam tapped you\u00a0\u00b7\u00a0\w{3}\u00a014:02$/);
  assert.match(T.tapLine("tap", "", at, 0, { locale: "en-GB", now: at }), /^Your partner tapped you/);
  assert.match(T.tapLine("together", "Sam", at, 0, { locale: "en-GB", now: at }), /^Thinking of each other\u00a0\u00b7\u00a0/);
  assert.equal(T.tapLine("tap", "Sam", 0, 0, {}), "Sam tapped you");
});

test("tapLine converts server time to this PC's clock with the skew", () => {
  const local = new Date(2026, 5, 10, 14, 2, 0).getTime();
  const skew = 5 * 60 * 1000; // the relay is 5 minutes ahead
  assert.equal(T.tapLine("tap", "Sam", local + skew, skew, { locale: "en-GB", hour12: false, now: local }), "Sam tapped you\u00a0\u00b7\u00a014:02");
});

test("failure texts: offline, rate limited with retryIn, other", () => {
  assert.match(T.failure(null, new Error("x")).message, /Could not send that tap/);
  const slow = T.failure({ error: "too soon", retryIn: 7.2 }, null);
  assert.equal(slow.retryInSeconds, 7.2);
  assert.match(slow.message, /Slow down a little/);
  assert.equal(T.failure({ error: "x" }, null).retryInSeconds, 0);
});

test("follow-up syncs after our own tap cover the relay's together window and stop after it", () => {
  const all = T.followUps(0);
  assert.ok(all.length >= 4 && all.length <= 10, "a handful of extra syncs, not a flood");
  assert.ok(all[0] <= 10000, "the first one comes quickly");
  assert.ok(all.at(-1) >= 120000, "the last one is past the relay's ~2 min together window");
  assert.ok(all.at(-1) <= 180000);
  assert.deepEqual(T.followUps(50000).every((d) => d > 0), true);
  assert.equal(T.followUps(10 * 60 * 1000).length, 0);
});

test("buttonState: pair first, ready, cooling down", () => {
  assert.equal(T.buttonState({ phase: "setup" }, 0).disabled, true);
  assert.equal(T.buttonState(null, 0).disabled, true);
  assert.equal(T.buttonState({ phase: "paired", unpaired: { by: "peer" } }, 0).disabled, true);
  const ready = T.buttonState({ phase: "paired", unpaired: null }, 0);
  assert.deepEqual([ready.label, ready.sub, ready.disabled], ["Tap", "", false]);
  const cooling = T.buttonState({ phase: "paired", unpaired: null }, 6200);
  assert.deepEqual([cooling.label, cooling.sub, cooling.disabled], ["Sent", "7s", true]);
  const failed = T.buttonState({ phase: "paired", unpaired: null }, 1200, true); // a tap that was not sent must not say "Sent"
  assert.deepEqual([failed.label, failed.sub, failed.disabled], ["Tap", "2s", true]);
});

// ---------- the files themselves ----------

test("the toast contract: tag, action id and the exact together sentence", () => {
  assert.equal(T.TOAST_TAG, "hearth-tap");
  assert.equal(T.TAPBACK_ID, "tapback");
  assert.equal(T.TOGETHER_TEXT, "You thought of each other at the same time");
  const js = read("tap.js");
  assert.match(js, /actions: \[\{ id: L\.TAPBACK_ID, label: "Tap back" \}\]/);
  assert.match(js, /onNotificationAction/);
  assert.match(js, /typeof n\.notify !== "function"/); // degrades when the host has no umbra.notify
  assert.match(js, /typeof n\.requestNotifications !== "function"/); // and on an Umbra without the notification prompt
  assert.match(js, /addEventListener\("click", [^]*?requestNotifications\(\)/); // asked from a click: Umbra prompts only then
});

test("index.html loads the tap files after the core and before the shell, and the shell attaches and mounts", () => {
  const html = read("index.html");
  const at = (s) => html.indexOf(s);
  assert.ok(at("tap.css") > 0);
  assert.ok(at("hearth-core.js") < at("tap-logic.js") && at("tap-logic.js") < at("tap.js") && at("tap.js") < at("hearth.js"));
  const shell = read("hearth.js");
  assert.match(shell, /HearthTap\.attach\(client\)/);
  assert.match(shell, /HearthTap\.mount\(tapSlot, client\)/);
});

test("the Tap button is draggable-safe, motion-safe and the files stay inside the Hearth rules", () => {
  const js = read("tap.js");
  assert.match(js, /class="ht-btn" data-no-drag/);
  assert.match(js, /dataset\.motion !== "off"/);
  for (const f of ["tap.js", "tap-logic.js"]) {
    const src = read(f);
    assert.doesNotMatch(src, /localStorage|sessionStorage|indexedDB/);
    assert.doesNotMatch(src, /https?:\/\/(?!umbra\.local)/); // no host of its own: all relay traffic goes through client.call
  }
  const css = read("tap.css");
  for (const m of css.matchAll(/font-size:\s*([\d.]+)(rem|px)/g)) {
    const px = m[2] === "px" ? Number(m[1]) : Number(m[1]) * 13; // umbra.css sets 13px on html
    assert.ok(px >= 11, `font-size ${m[0]} is ${px}px`);
  }
});

test("adding taps changed no permission: still one host and notifications", () => {
  const m = JSON.parse(read("widget.json"));
  assert.deepEqual(m.permissions.hosts, ["umbra-buddy-relay.umbra-server.workers.dev"]);
  assert.equal(m.permissions.notifications, true);
  assert.ok(core.HEARTH_ROUTES.includes("tap"));
});
