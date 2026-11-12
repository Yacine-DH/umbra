// Hearth tap: the pure rules behind the "thinking of you" tap (issue #211). No DOM and no umbra here, so
// tests/widgets/hearth-tap.test.mjs drives it with a fake clock. Browser: window.HearthTapLogic. Node: module.exports.
//
//   const policy = HearthTapLogic.createPolicy({ now });
//   policy.canSend()                       -> { ok, remainingMs }       the client cooldown (10 s) so the relay is not hammered
//   policy.sent() / policy.failed(retryMs) call around the relay `tap` route
//   policy.claimTogether()                 -> bool                      "may I play the together moment now?" (once per pair of taps)
//   policy.decide(events, ctx)             -> what to do with a batch of client events (dedupe, freshness, toast budget)
//
// The relay decides what "together" means (two taps within its window, ~2 min); nothing here measures that.
(function (root) {
  "use strict";

  const COOLDOWN_MS = 10000;            // after a tap, the button waits this long
  const RETRY_MS = 2000;                // after a failed tap the wait is short: nothing was sent
  const FRESH_MS = 10 * 60 * 1000;      // an event older than this (server time) is shown but does not pulse or toast
  const UNSEEN_MS = 24 * 60 * 60 * 1000;// "Sam tapped you" is remembered for a day after a restart
  const TOAST_GAP_MS = 30000;           // at least this long between two tap toasts
  const TOAST_WINDOW_MS = 10 * 60 * 1000;// Umbra allows 5 toasts per 10 minutes per widget (host rate limit)
  const TOAST_TAP_CAP = 4;              // tap toasts per window; the 5th is kept for a together moment
  const TOAST_BLOCK_MS = 10 * 60 * 1000;// after "notifications are off", do not ask the host again for a while
  const TOGETHER_GAP_MS = 30000;        // a second together moment within 30 s is the same one seen twice
  const SEEN_CAP = 64;
  const TOAST_TAG = "hearth-tap";       // a new toast replaces the old one instead of stacking
  const TAPBACK_ID = "tapback";
  /** Extra syncs after our own tap, so the side that tapped first learns about a together moment quickly (the window is ~2 min). */
  const FOLLOW_UP_MS = Object.freeze([6000, 15000, 30000, 50000, 75000, 105000, 135000]);
  const TOGETHER_TEXT = "You thought of each other at the same time";

  // ---------- cooldown ----------

  /** { ready(), remaining(), start(ms?), clear() } on an injected clock. */
  function createCooldown(opts) {
    const now = (opts && opts.now) || (() => Date.now());
    const ms = (opts && opts.ms) || COOLDOWN_MS;
    let until = -Infinity;
    return {
      ready() { return now() >= until; },
      remaining() { return Math.max(0, Math.ceil(until - now())); },
      start(wait) { until = now() + (Number(wait) > 0 ? Number(wait) : ms); },
      clear() { until = -Infinity; },
    };
  }

  // ---------- policy ----------

  /**
   * One policy per widget copy. Everything stateful about taps that is not on screen:
   * the cooldown, the ids of events already handled, the toast budget and the together-moment latch.
   * opts: { now?, cooldownMs? }
   */
  function createPolicy(opts) {
    const now = (opts && opts.now) || (() => Date.now());
    const cooldown = createCooldown({ now, ms: opts && opts.cooldownMs });
    const seen = new Map(); // "type:at" -> true, oldest first
    let toastTimes = [], toastBlockedUntil = -Infinity, lastTogetherAt = -Infinity;

    const remember = (key) => {
      seen.set(key, true);
      while (seen.size > SEEN_CAP) seen.delete(seen.keys().next().value);
    };

    /** The tap button: { ok: true } or { ok: false, remainingMs }. */
    function canSend() {
      return cooldown.ready() ? { ok: true, remainingMs: 0 } : { ok: false, remainingMs: cooldown.remaining() };
    }
    /** Call right when a tap is sent (before the relay answers), so a double click cannot send twice. */
    function sent() { cooldown.start(); }
    /** The relay did not take the tap: wait a short time (or the relay's `retryIn` seconds) instead of the full cooldown. */
    function failed(retryInSeconds) {
      const s = Number(retryInSeconds);
      cooldown.start(s > 0 ? Math.min(s, 60) * 1000 : RETRY_MS);
    }

    /** True once per together moment: the answer to our own tap and the later sync event are the same moment. */
    function claimTogether() {
      const t = now();
      if (t - lastTogetherAt < TOGETHER_GAP_MS) return false;
      lastTogetherAt = t;
      return true;
    }

    const toastAllowed = () => now() >= toastBlockedUntil;
    /** Toasts we asked for in the last 10 minutes (the host counts the same way: 5 per 10 minutes per widget). */
    const toastsUsed = (t) => { toastTimes = toastTimes.filter((x) => t - x < TOAST_WINDOW_MS); return toastTimes.length; };
    const tapToastOk = (t) => toastsUsed(t) < TOAST_TAP_CAP && (toastTimes.length === 0 || t - toastTimes[toastTimes.length - 1] >= TOAST_GAP_MS);
    /** The host refused a toast. When the user has them off for this widget, stop asking for a while. */
    function toastFailed(message) {
      if (/off for this widget|no permissions\.notifications/i.test(String(message || ""))) toastBlockedUntil = now() + TOAST_BLOCK_MS;
    }

    /**
     * What to do with the client's events (peerTap, together, peerStroke) that arrived together.
     * ctx: { serverNow, leader }. `leader`: only the copy that polls toasts, so copies on one PC do not stack two.
     * Returns { tap, together, pulse, toast }:
     *   tap       { at, fresh } | null    show "<Name> tapped you"
     *   together  { at, fresh, play } | null   `play` is false when the moment was already played (we answered the second tap)
     *   pulse     the heart should pulse and glow (fresh)
     *   toast     null | "tap" | "together"
     * A partner's tap that completes a together moment arrives with it in the same batch; then only `together` is returned
     * (unless that tap is newer than the moment: a later, separate tap).
     */
    function decide(events, ctx) {
      const out = { tap: null, together: null, pulse: false, toast: null };
      const c = ctx || {};
      const t = now();
      const serverNow = Number.isFinite(c.serverNow) ? c.serverNow : t;
      const fresh = (at) => !(at > 0) || serverNow - at <= FRESH_MS;
      let tapEv = null, togEv = null;
      for (const e of Array.isArray(events) ? events : []) {
        if (!e || (e.type !== "peerTap" && e.type !== "together")) continue;
        const at = Number(e.at) > 0 ? Math.floor(Number(e.at)) : 0;
        const key = e.type + ":" + at;
        if (at > 0) { if (seen.has(key)) continue; remember(key); }
        if (e.type === "peerTap") tapEv = { at, fresh: fresh(at) };
        else togEv = { at, fresh: fresh(at) };
      }
      if (togEv) {
        out.together = { at: togEv.at, fresh: togEv.fresh, play: togEv.fresh ? claimTogether() : false };
        out.pulse = out.together.play;
        if (out.together.play && c.leader && toastAllowed() && toastsUsed(t) < 5) { out.toast = "together"; toastTimes.push(t); }
      }
      // The partner's tap that completes a together moment has the moment's time; a strictly later tap is a separate one.
      if (tapEv && (!togEv || tapEv.at > togEv.at)) {
        out.tap = tapEv;
        out.pulse = out.pulse || tapEv.fresh;
        if (!out.toast && tapEv.fresh && c.leader && toastAllowed() && tapToastOk(t)) { out.toast = "tap"; toastTimes.push(t); }
      }
      return out;
    }

    /** The user just allowed notifications: stop holding toasts back after an earlier refusal. */
    function toastsAllowed() { toastBlockedUntil = -Infinity; }

    return { canSend, sent, failed, claimTogether, decide, toastFailed, toastsAllowed, cooldownRemaining: cooldown.remaining };
  }

  /** The hint under the Tap button while the user hasn't told Umbra whether Hearth may notify. */
  function askText(partner) {
    const name = String(partner || "").trim();
    return "Click here to get a notification when " + (name || "your partner") + " taps you";
  }

  // ---------- what the screen says ----------

  /** The partner's latest tap that this copy has not shown yet, or null. view: HearthCore client view. seenAt: server time of the last tap the user has seen. */
  function unseenTap(view, seenAt, nowMs) {
    if (!view || !view.paired || !view.me || !view.sync || !view.sync.taps) return null;
    const peer = view.sync.taps[view.me === "a" ? "b" : "a"];
    if (!peer || !(peer.at > 0) || !(peer.n > 0)) return null;
    if (peer.at <= (Number(seenAt) || 0)) return null;
    const serverNow = Number.isFinite(nowMs) ? nowMs + (Number(view.sync.skew) || 0) : peer.at;
    if (serverNow - peer.at > UNSEEN_MS) return null;
    return { n: peer.n, at: peer.at };
  }

  /** Server time of the partner's latest tap (0 when none), to remember as "seen". */
  function peerTapAt(view) {
    if (!view || !view.me || !view.sync || !view.sync.taps) return 0;
    const peer = view.sync.taps[view.me === "a" ? "b" : "a"];
    return peer && peer.at > 0 ? peer.at : 0;
  }

  /**
   * "Sam tapped you · 14:02" (today) or "Sam tapped you · Mon 14:02" (another day). `at` is server time; `skew` is the
   * server clock minus ours. o: { locale, hour12, now (ms) }.
   */
  function tapLine(kind, name, at, skew, o) {
    const opt = o || {};
    const who = String(name || "").trim() || "Your partner";
    const when = formatWhen(at, skew, opt);
    const dot = "\u00a0\u00b7\u00a0"; // no line break around the dot, so a narrow slot wraps before "you", not in the middle of the time
    if (kind === "together") return "Thinking of each other" + (when ? dot + when : "");
    return who + " tapped you" + (when ? dot + when : "");
  }

  function formatWhen(at, skew, o) {
    if (!(at > 0)) return "";
    const d = new Date(at - (Number(skew) || 0));
    if (Number.isNaN(d.getTime())) return "";
    const now = new Date(Number.isFinite(o.now) ? o.now : Date.now());
    const locale = o.locale || undefined;
    const tf = { hour: "2-digit", minute: "2-digit" };
    if (typeof o.hour12 === "boolean") tf.hour12 = o.hour12;
    let time;
    try { time = d.toLocaleTimeString(locale, tf); } catch (e) { time = d.toLocaleTimeString(undefined, tf); }
    const sameDay = d.getFullYear() === now.getFullYear() && d.getMonth() === now.getMonth() && d.getDate() === now.getDate();
    if (sameDay) return time;
    let day;
    try { day = d.toLocaleDateString(locale, { weekday: "short" }); } catch (e) { day = d.toLocaleDateString(undefined, { weekday: "short" }); }
    return day + "\u00a0" + time;
  }

  /** A sentence for a tap that did not go through. `r`: the relay's JSON (look for `error`), `err`: a rejected fetch. */
  function failure(r, err) {
    if (err || !r || typeof r !== "object") return { message: "Could not send that tap. Try again.", retryInSeconds: 0 };
    const retry = Number(r.retryIn) > 0 ? Number(r.retryIn) : 0;
    if (retry) return { message: "Slow down a little.", retryInSeconds: retry };
    return { message: "That tap was not accepted. Try again.", retryInSeconds: 0 };
  }

  /** Delays (ms, from the moment of the tap) of the extra syncs still to come, `elapsedMs` after the tap. */
  function followUps(elapsedMs) {
    const e = Math.max(0, Number(elapsedMs) || 0);
    return FOLLOW_UP_MS.filter((d) => d > e).map((d) => d - e);
  }

  /** What the button says: { label, sub, disabled } for a client view and the cooldown left. */
  function buttonState(view, cooldownMs, failed) {
    const paired = !!view && view.phase === "paired" && !(view.unpaired && view.unpaired.by === "peer");
    if (!paired) return { label: "Tap", sub: "", disabled: true, why: "Pair first to send a tap." };
    if (cooldownMs > 0 && failed) return { label: "Tap", sub: Math.ceil(cooldownMs / 1000) + "s", disabled: true, why: "That tap was not sent. Try again in a moment." };
    if (cooldownMs > 0) return { label: "Sent", sub: Math.ceil(cooldownMs / 1000) + "s", disabled: true, why: "Sent. You can tap again in a few seconds." };
    return { label: "Tap", sub: "", disabled: false, why: "Send a tap: thinking of you" };
  }

  const api = {
    COOLDOWN_MS, RETRY_MS, FRESH_MS, UNSEEN_MS, TOAST_GAP_MS, TOAST_WINDOW_MS, TOAST_TAP_CAP, TOAST_BLOCK_MS, TOGETHER_GAP_MS, SEEN_CAP, TOAST_TAG, TAPBACK_ID, FOLLOW_UP_MS, TOGETHER_TEXT, askText,
    createCooldown, createPolicy, unseenTap, peerTapAt, tapLine, failure, followUps, buttonState,
  };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else root.HearthTapLogic = api;
})(typeof window !== "undefined" ? window : globalThis);
