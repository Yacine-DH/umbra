// Hearth tap (issue #211): the "thinking of you" tap, its pulse on the receiving widget, the optional toast with
// "Tap back" and the together moment. The rules are in tap-logic.js; this file is the DOM and umbra glue.
//
// Public API (window.HearthTap), for the shell and for the Overview view (#210):
//   HearthTap.attach(client)        once per copy, right after HearthCore.createClient: listens to the client's events
//                                   (pulse, glow, toast, together moment) in EVERY view, even one without a Tap button.
//                                   Idempotent. Returns the controller.
//   HearthTap.mount(el, client)     draws the Tap button and the "<Name> tapped you · 14:02" line into `el`
//                                   (attaches first if needed). Call it again after each re-render: it redraws from the
//                                   controller's state, so the cooldown, the line and a running pulse survive.
//                                   Returns { send(), update(), destroy(), controller }.
// Controller: send({ source }) -> Promise<{ ok, reason?, together? }>, dismissLine(), getState(), onChange(fn).
//
// Other views can react to taps (for example the Pet copy playing HearthMascot's "tap" reaction) with
//   window.addEventListener("hearth-tap", (e) => e.detail)   // { type: "sent" | "received" | "together", at }
//
// The host side of the contract: umbra.notify({ title, body, tag: "hearth-tap", actions: [{ id: "tapback", label: "Tap back" }] })
// needs permissions.notifications and the user's per-widget switch (off by default); without them the toast is simply
// skipped, and a host without `actions` or `onNotificationAction` just never shows the button.
// Which copy toasts: the client's `toaster` (the first copy Umbra lets notify, else the polling one), so one copy with
// notifications off can't silence the others. Each copy asks Umbra once at start whether it may notify, without a prompt;
// while the user hasn't decided, the Tap view says that a click turns tap notifications on.
(function (root) {
  "use strict";

  const L = root.HearthTapLogic;
  const PULSE_MS = 6000;
  const SEEN_KEY = "tapSeen"; // umbra.store key (per copy): { at } = server time of the last partner tap the user has seen
  const HEART_PATH = "M12 21C5 15 2 11.5 2 7.5 2 4.5 4.3 2.5 7 2.5c2 0 3.8 1.1 5 3 1.2-1.9 3-3 5-3 2.7 0 5 2 5 5 0 4-3 7.5-10 13.5z";
  const heartSvg = (fill, cls) => `<svg class="${cls || ""}" viewBox="0 0 24 22" aria-hidden="true"><path d="${HEART_PATH}" fill="${fill}" stroke="#6B4F3A" stroke-width="2" stroke-linejoin="round"/></svg>`;
  const sparkle = (cls) => `<svg class="ht-spark ${cls}" viewBox="0 0 10 10" aria-hidden="true"><path d="M5 0l1.2 3.8L10 5 6.2 6.2 5 10 3.8 6.2 0 5l3.8-1.2z" fill="#FFF3B0" stroke="#6B4F3A" stroke-width="0.8" stroke-linejoin="round"/></svg>`;

  const controllers = new WeakMap();

  function createController(client, opts) {
    const doc = root.document;
    const policy = L.createPolicy({});
    const changeFns = new Set();
    let line = null;              // { kind: "tap" | "together", at } or { kind: "error", text }
    let seenAt = 0, seenLoaded = false;
    let pulseUntil = 0, pulseTimer = 0, lineTimer = 0, errTimer = 0, tickTimer = 0;
    let followTimers = [];
    let ui = null;                // the one mounted button: { el, root, btn, label, sub, line }
    let batch = [], flushing = false;
    let destroyed = false;
    let failedCool = false;       // the current short wait follows a tap that was not sent, so the button does not say "Sent"
    let askHint = false;          // Umbra hasn't been told yet whether Hearth may notify: show "Click to get notified"

    const um = () => root.umbra;
    const cardEl = () => (opts && opts.card) || doc.getElementById("app") || doc.body;
    const motionOn = () => doc.documentElement.dataset.motion !== "off";
    const scaled = (ms) => {
      const s = parseFloat(root.getComputedStyle(doc.documentElement).getPropertyValue("--motion-scale"));
      return ms * (Number.isFinite(s) && s > 0 ? s : 1);
    };
    const announce = (type, at) => { try { root.dispatchEvent(new root.CustomEvent("hearth-tap", { detail: { type, at: at || 0 } })); } catch (e) { /* no CustomEvent */ } };
    const notifyChange = () => changeFns.forEach((fn) => { try { fn(getState()); } catch (e) { /* a listener bug must not stop taps */ } });

    // ----- overlay: a layer above the card that survives the shell's re-renders -----

    function overlay() {
      let o = doc.getElementById("ht-overlay");
      if (!o) { o = doc.createElement("div"); o.id = "ht-overlay"; o.className = "ht-overlay"; doc.body.appendChild(o); }
      return o;
    }
    function drop(el, ms) { setTimeout(() => { if (el.parentNode) el.remove(); }, ms); }

    /** A heart that flies up out of `from` (the button) or the middle of the card. */
    function flyHeart(from, cls) {
      if (!motionOn()) return;
      const r = (from || cardEl()).getBoundingClientRect();
      const h = doc.createElement("span");
      h.className = "ht-fly " + (cls || "");
      h.innerHTML = heartSvg("#F7A8C4");
      h.style.left = r.left + r.width / 2 + "px";
      h.style.top = r.top + r.height / 2 + "px";
      overlay().appendChild(h);
      drop(h, scaled(1400));
    }

    /** The receiving side: three hearts rise over the card. */
    function risingHearts() {
      if (!motionOn()) return;
      const r = cardEl().getBoundingClientRect();
      const wrap = doc.createElement("div");
      wrap.className = "ht-rise";
      wrap.style.left = r.left + r.width / 2 + "px";
      wrap.style.top = r.top + r.height * 0.6 + "px";
      wrap.innerHTML = heartSvg("#F7A8C4", "ht-r1") + heartSvg("#E5648F", "ht-r2") + heartSvg("#F7A8C4", "ht-r3");
      overlay().appendChild(wrap);
      drop(wrap, scaled(2200));
    }

    /** Two hearts meet and merge. Static (merged heart and the sentence) when motion is off. */
    function playTogether() {
      const r = cardEl().getBoundingClientRect();
      const t = doc.createElement("div");
      t.className = "ht-together";
      t.setAttribute("role", "status");
      t.style.cssText = `left:${r.left + 2}px;top:${r.top + 2}px;width:${r.width - 4}px;height:${r.height - 4}px`; // inside the card border
      t.innerHTML =
        `<div class="ht-merge">${heartSvg("#F7A8C4", "ht-th ht-th1")}${heartSvg("#FFF3B0", "ht-th ht-th2")}${heartSvg("#E5648F", "ht-merged")}` +
        `${sparkle("ht-sp1")}${sparkle("ht-sp2")}${sparkle("ht-sp3")}</div>` +
        `<p class="ht-cap">${L.TOGETHER_TEXT}</p>`;
      const o = overlay();
      o.querySelectorAll(".ht-together").forEach((x) => x.remove());
      o.appendChild(t);
      drop(t, motionOn() ? scaled(4200) : 6000);
      announce("together");
    }

    // ----- state the button shows -----

    function getState() {
      return { cooldownMs: policy.cooldownRemaining(), pulsing: Date.now() < pulseUntil, seenAt, line: line ? Object.assign({}, line) : null, text: lineText() };
    }

    function lineText() {
      if (!line) return "";
      if (line.kind === "error") return line.text;
      const v = client.getView();
      return L.tapLine(line.kind, v.names.partner, line.at, v.sync.skew, { locale: um() && um().locale, hour12: um() && um().hour12, now: Date.now() });
    }

    function paint() {
      if (ui && !ui.el.isConnected) ui = null;
      if (ui) {
        const v = client.getView();
        const rem = policy.cooldownRemaining();
        const bs = L.buttonState(v, rem, failedCool);
        ui.btn.disabled = bs.disabled;
        ui.btn.title = bs.why;
        ui.btn.setAttribute("aria-label", rem > 0 ? "Tap sent. You can tap again in " + Math.ceil(rem / 1000) + " seconds" : bs.why);
        if (ui.label.textContent !== bs.label) ui.label.textContent = bs.label;
        if (ui.sub.textContent !== bs.sub) ui.sub.textContent = bs.sub;
        ui.root.classList.toggle("ht-pulse", Date.now() < pulseUntil);
        ui.root.classList.toggle("ht-cooling", rem > 0 && !failedCool);
        const text = lineText();
        ui.line.hidden = !text;
        if (ui.line.textContent !== text) ui.line.textContent = text;
        ui.line.dataset.kind = line ? line.kind : "";
        const ask = askHint && v.phase === "paired" && !text ? L.askText(v.names.partner) : "";
        ui.ask.hidden = !ask;
        if (ui.ask.textContent !== ask) ui.ask.textContent = ask;
      }
      notifyChange();
    }

    function startTick() {
      if (tickTimer) return;
      tickTimer = setInterval(() => {
        if (!policy.cooldownRemaining()) { clearInterval(tickTimer); tickTimer = 0; }
        paint();
      }, 1000);
    }

    // ----- the line: "Sam tapped you · 14:02" until the user hovers or clicks -----

    function rememberSeen() {
      const at = L.peerTapAt(client.getView());
      if (at > seenAt) {
        seenAt = at;
        try { const p = um().store.set(SEEN_KEY, { at: seenAt }); if (p && p.catch) p.catch(() => {}); } catch (e) { /* not essential */ }
      }
    }

    function setLine(next) {
      clearTimeout(lineTimer); clearTimeout(errTimer);
      line = next;
      if (next && next.kind !== "error") {
        // The pointer is already on the widget: it has seen it, so let it go after a moment instead of waiting for a re-entry.
        let over = false;
        try { over = cardEl().matches(":hover"); } catch (e) { /* no :hover support */ }
        if (over) lineTimer = setTimeout(dismissLine, 6000);
      } else if (next) errTimer = setTimeout(() => { if (line === next) { line = null; paint(); } }, 6000);
      paint();
    }

    function dismissLine() {
      clearTimeout(lineTimer);
      if (!line || line.kind === "error") return;
      rememberSeen();
      line = null;
      paint();
    }

    function fromView(v) {
      if (!seenLoaded || !v || v.phase !== "paired") return;
      const u = L.unseenTap(v, seenAt, Date.now());
      if (!u) return;
      if (line && line.kind !== "error" && line.at >= u.at) return; // already showing this tap, or a together moment at least as new
      setLine({ kind: "tap", at: u.at });
    }

    // ----- sending -----

    function cancelFollowUps() { followTimers.forEach(clearTimeout); followTimers = []; }
    /** After our own tap, sync a few more times for ~2 minutes so a together moment shows up soon on this side too. */
    function scheduleFollowUps() {
      cancelFollowUps();
      followTimers = L.followUps(0).map((d) => setTimeout(() => {
        const s = um() && um().settings;
        if (!destroyed && !(s && s.paused)) client.syncSoon();
      }, d));
    }

    function fail(f) {
      policy.failed(f.retryInSeconds); failedCool = true;
      startTick();
      if (f.message) setLine({ kind: "error", text: f.message }); else paint();
      return { ok: false, reason: "relay" };
    }

    async function send(o) {
      const source = (o && o.source) || "api";
      const v = client.getView();
      if (v.phase !== "paired" || (v.unpaired && v.unpaired.by === "peer")) return { ok: false, reason: "unpaired" };
      const can = policy.canSend();
      if (!can.ok) return { ok: false, reason: "cooldown", remainingMs: can.remainingMs };
      policy.sent(); failedCool = false;
      startTick();
      if (line && line.kind === "error") { clearTimeout(errTimer); line = null; }
      if (source === "toast") dismissLine();
      flyHeart(source === "button" && ui ? ui.btn : null);
      announce("sent");
      paint();
      let r;
      try { r = await client.call("tap"); } catch (err) { return fail(L.failure(null, err)); }
      if (!r || typeof r !== "object") return fail(L.failure(r, null));
      if (r.paired === false) { policy.failed(); failedCool = true; startTick(); paint(); return { ok: false, reason: "unpaired" }; }
      if (r.ok !== true || typeof r.error === "string") return fail(L.failure(r, null));
      if (r.together === true) {
        cancelFollowUps();
        if (policy.claimTogether()) { setLine({ kind: "together", at: Date.now() + (client.getView().sync.skew || 0) }); playTogether(); }
      } else scheduleFollowUps();
      return { ok: true, together: r.together === true };
    }

    // ----- receiving -----

    function pulse() {
      pulseUntil = Date.now() + PULSE_MS;
      const c = cardEl();
      c.dataset.htGlow = "";
      clearTimeout(pulseTimer);
      pulseTimer = setTimeout(() => { delete c.dataset.htGlow; paint(); }, PULSE_MS);
      risingHearts();
      paint();
    }

    function toast(kind, v) {
      const n = um();
      if (!n || typeof n.notify !== "function") return;
      const name = (v.names.partner || "").trim() || "Your partner";
      const msg = kind === "together"
        ? { title: "Hearth", body: L.TOGETHER_TEXT + ".", tag: L.TOAST_TAG }
        : { title: name + " is thinking of you", body: "Tap back to say it too.", tag: L.TOAST_TAG, actions: [{ id: L.TAPBACK_ID, label: "Tap back" }] };
      Promise.resolve().then(() => n.notify(msg)).catch((e) => {
        const m = e && e.message ? e.message : String(e);
        policy.toastFailed(m);
        if (/off for this widget|no permissions\.notifications/i.test(m)) client.setCanNotify(false); // let a copy that may notify take over
      });
    }

    function flush() {
      flushing = false;
      const events = batch; batch = [];
      if (destroyed || !events.length) return;
      const v = client.getView();
      // `toaster` (a copy Umbra lets notify) when the client knows it, else the polling copy as before.
      const d = policy.decide(events, { serverNow: Date.now() + (v.sync.skew || 0), leader: !!(v.toaster !== undefined ? v.toaster : v.leader) });
      if (d.together) {
        cancelFollowUps();
        setLine({ kind: "together", at: d.together.at });
        if (d.together.play) playTogether();
      }
      if (d.tap) { // after a together moment: a tap newer than it replaces its line
        fromView(v);
        announce("received", d.tap.at);
      }
      if (d.pulse) pulse();
      if (d.toast) toast(d.toast, v);
    }

    client.onEvent((e) => {
      if (!e || (e.type !== "peerTap" && e.type !== "together")) return;
      batch.push(e);
      // The client fires a batch synchronously; wait a microtask so a tap that completes a together moment arrives with it.
      if (!flushing) { flushing = true; Promise.resolve().then(flush); }
    });
    client.subscribe((v) => { fromView(v); paint(); });

    // hover or click on the widget: the user has seen it
    const card = cardEl();
    card.addEventListener("pointerenter", dismissLine);
    card.addEventListener("pointerdown", dismissLine);

    // Notifications: on a click, while the user has not decided yet, Umbra shows its own "Allow notifications?" prompt
    // (Allow / Not now / Don't ask again). Umbra answers without a prompt once decided, so this asks at most once per run.
    let notifAsk = "unknown"; // "unknown" | "busy" | "done"
    card.addEventListener("click", () => {
      const n = um();
      if (notifAsk !== "unknown" || destroyed || !n || typeof n.requestNotifications !== "function" || !client.getView().paired) return;
      notifAsk = "busy";
      Promise.resolve().then(() => n.requestNotifications())
        .then((allowed) => { notifAsk = "done"; askHint = false; client.setCanNotify(!!allowed); if (allowed) policy.toastsAllowed(); paint(); })
        // a prompt already open, too soon, or a click Umbra did not count (edit mode): try again on the next click
        .catch((e) => { notifAsk = /already open|wait a moment|click the widget/i.test(String(e && e.message)) ? "unknown" : "done"; });
    }, true);

    // At start, and when Umbra's settings change: may this copy notify? Asked without a click, so Umbra answers from the
    // user's switch and never prompts; "click the widget first" means the user hasn't decided yet.
    function probeNotify() {
      const n = um();
      if (destroyed || !n || typeof n.requestNotifications !== "function" || notifAsk === "busy") return;
      Promise.resolve().then(() => n.requestNotifications())
        .then((allowed) => { client.setCanNotify(!!allowed); askHint = false; if (allowed) policy.toastsAllowed(); paint(); })
        .catch((e) => {
          client.setCanNotify(false);
          askHint = /click the widget/i.test(String(e && e.message));
          if (askHint) notifAsk = "unknown"; // the next click asks
          paint();
        });
    }
    probeNotify();
    const n1 = um();
    if (n1 && typeof n1.onSettings === "function") n1.onSettings(() => probeNotify());

    // "Tap back" on the toast sends a tap
    const n0 = um();
    if (n0 && typeof n0.onNotificationAction === "function") {
      n0.onNotificationAction((a) => {
        if (a && a.id === L.TAPBACK_ID && (a.tag == null || a.tag === L.TOAST_TAG)) send({ source: "toast" }).catch(() => {});
      });
    }

    (async () => {
      try { const s = await um().store.get(SEEN_KEY, null); if (s && Number(s.at) > 0) seenAt = Number(s.at); } catch (e) { /* first run */ }
      seenLoaded = true;
      fromView(client.getView());
    })();

    // ----- the Tap button -----

    function mount(el) {
      const rootEl = doc.createElement("div");
      rootEl.className = "ht";
      rootEl.innerHTML =
        `<button type="button" class="ht-btn" data-no-drag>${heartSvg("#E5648F", "ht-heart")}<span class="ht-label">Tap</span><span class="ht-sub num"></span></button>` +
        `<p class="ht-line" role="status" aria-live="polite" hidden></p>` +
        `<p class="ht-ask" hidden></p>`;
      el.textContent = "";
      el.appendChild(rootEl);
      ui = { el, root: rootEl, btn: rootEl.querySelector(".ht-btn"), label: rootEl.querySelector(".ht-label"), sub: rootEl.querySelector(".ht-sub"), line: rootEl.querySelector(".ht-line"), ask: rootEl.querySelector(".ht-ask") };
      ui.btn.addEventListener("click", () => { send({ source: "button" }).catch(() => {}); });
      if (policy.cooldownRemaining()) startTick();
      paint();
      return { send, update: paint, destroy() { if (ui && ui.el === el) { el.textContent = ""; ui = null; } }, controller: api };
    }

    function destroy() {
      destroyed = true;
      cancelFollowUps();
      clearTimeout(pulseTimer); clearTimeout(lineTimer); clearTimeout(errTimer); clearInterval(tickTimer);
      card.removeEventListener("pointerenter", dismissLine);
      card.removeEventListener("pointerdown", dismissLine);
    }

    const api = { send, dismissLine, getState, mount, destroy, onChange(fn) { changeFns.add(fn); return () => changeFns.delete(fn); } };
    return api;
  }

  function attach(client, opts) {
    if (!client || typeof client.getView !== "function") throw new Error("HearthTap.attach needs a Hearth client");
    if (!controllers.has(client)) controllers.set(client, createController(client, opts || {}));
    return controllers.get(client);
  }

  function mount(el, client, opts) {
    if (!el) throw new Error("HearthTap.mount needs an element");
    return attach(client, opts).mount(el);
  }

  root.HearthTap = { attach, mount };
})(window);
