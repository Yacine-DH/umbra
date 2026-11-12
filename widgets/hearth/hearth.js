// Hearth: the per-copy view shell and the pairing UI. All relay and pairing logic lives in hearth-core.js; this file
// only paints what `client.getView()` says and forwards clicks. Three small tabs in the header (Us, Board, Pet) switch
// the view inside the widget; the View option is the tab a copy opens on, and the last tab picked is remembered per copy.
// The content of those views lives in overview.js, board.js and pet.js.
(() => {
  "use strict";
  const core = window.HearthCore;
  const mascot = window.HearthMascot;
  const app = document.getElementById("app");

  const VIEWS = ["overview", "board", "pet"];
  const TABS = [["overview", "Us"], ["board", "Board"], ["pet", "Pet"]];
  const TAB_KEY = "tab";
  const esc = (s) => String(s == null ? "" : s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const optionView = () => (VIEWS.includes(umbra.options.view) ? umbra.options.view : "overview");
  const which = () => (VIEWS.includes(tab.view) ? tab.view : optionView());
  // The open tab, and the View option it was picked under: changing the option in Settings moves the copy to that tab.
  const tab = { view: "", opt: "" };

  let client = null;
  let view = null;               // the last client view
  let lastSig = "";
  let lastStatusAt = 0;
  let sentRendered = false;
  const ui = { panel: false, joining: false, draft: "", confirm: null };
  let confirmTimer = 0;
  let countdownTimer = 0;

  const HEART = '<svg viewBox="0 0 24 22" aria-hidden="true"><path d="M12 21C5 15 2 11.5 2 7.5 2 4.5 4.3 2.5 7 2.5c2 0 3.8 1.1 5 3 1.2-1.9 3-3 5-3 2.7 0 5 2 5 5 0 4-3 7.5-10 13.5z" fill="#F7A8C4" stroke="#6B4F3A" stroke-width="2" stroke-linejoin="round"/></svg>';

  // ---------- pieces ----------

  const statusOffline = () => !!(view && view.offline) || !!(umbra.status && umbra.status.state === "offline");
  const btn = (act, label, cls = "", extra = "") => `<button class="btn ${cls}" data-act="${act}" data-no-drag ${extra}>${label}</button>`;

  const showTabs = (v) => v.phase === "paired" && !v.invite;
  function tabs() {
    const w = which();
    return `<nav class="tabs" role="tablist" aria-label="Hearth view" data-no-drag>` +
      TABS.map(([id, label]) => `<button type="button" class="tab" role="tab" data-act="tab" data-tab="${id}" aria-selected="${id === w}"${id === w ? "" : ' tabindex="-1"'}>${label}</button>`).join("") +
      `</nav>`;
  }

  function top(v) {
    return `<header class="top"><span class="brand">${HEART}<span class="brand-name">Hearth</span></span>` +
      (showTabs(v) ? tabs() : "") + `<span class="grow"></span>` +
      (statusOffline() ? `<span class="chip off" title="The relay can't be reached. Showing the last data.">Offline</span>` : "") +
      `<umbra-status placement="below-end"></umbra-status>` +
      (v.paired ? `<button class="icon-btn" data-act="panel" data-no-drag aria-label="Hearth settings" title="Hearth settings">&#9881;</button>` : "") +
      `</header>`;
  }

  function setup(v) {
    const art = `<div class="art">${mascot.renderSvg({ mood: "content" })}</div>`;
    if (ui.joining) {
      return `<section class="body">${art}<div class="col">` +
        `<p class="msg">Type the code your partner got.</p>` +
        `<form class="join" data-no-drag><input id="code" maxlength="12" placeholder="ABCD-2345" autocomplete="off" spellcheck="false" value="${esc(ui.draft)}" aria-label="Pairing code">` +
        `<button class="btn primary" type="submit" ${v.busy ? "disabled" : ""}>Join</button>` +
        `<button class="btn quiet" type="button" data-act="join-cancel">Back</button></form></div></section>`;
    }
    const link = v.link.offer
      ? btn("link", v.link.pending ? "Waiting for the paired copy..." : "Use this PC's pairing", "primary", v.link.pending ? "disabled" : "")
      : "";
    return `<section class="body">${art}<div class="col"><p class="lead">Two desktops, one cozy space.</p><div class="row">` +
      link +
      btn("create", v.busy ? "Creating..." : "Create our hearth", link ? "" : "primary", v.busy ? "disabled" : "") +
      btn("join-open", "Join with a code", "", v.busy ? "disabled" : "") +
      `</div></div></section>`;
  }

  function inviteCard(v) {
    const i = v.invite;
    const text = i.kind === "reinvite"
      ? "Type this code in Hearth on the new PC. The old PC of your partner is signed out."
      : "Give this code to your partner. They choose Join with a code.";
    return `<section class="body invite-card${i.expired ? " expired" : ""}"><p class="lead">${esc(text)}</p>` +
      `<div class="code num">${esc(i.formatted)}</div>` +
      `<p class="muted"><span data-expiry>${i.expired ? "The code has expired." : 'Valid for <b class="num" data-countdown>' + esc(i.text) + "</b>, works once."}</span> ` +
      btn("dismiss-invite", i.expired ? "Close" : "Done", "quiet") + `</p></section>`;
  }

  function unpairedCard(v) {
    const until = v.unpaired && v.unpaired.graceUntil ? new Date(v.unpaired.graceUntil).toLocaleDateString(umbra.locale, { month: "short", day: "numeric" }) : "";
    return `<section class="body"><div class="art">${mascot.renderSvg({ mood: "sad" })}</div><div class="col">` +
      `<p class="msg">Your partner left this hearth.${until ? " It is kept for you until " + esc(until) + "." : ""}</p><div class="row">` +
      btn("reinvite-now", v.busy ? "Making a code..." : "Invite again", "primary", v.busy ? "disabled" : "") +
      btn("leave", "Leave", "", v.busy ? "disabled" : "") + `</div></div></section>`;
  }

  // The Pet view (#214) lives in pet.js; the shell only provides its element and calls HearthPet.attach after each render.
  function petStub() {
    return `<section data-pet-root></section>`;
  }

  function toasts(v) {
    const out = [];
    if (v.link.requests.length) {
      const n = v.link.requests[0].nonce;
      out.push(`<div class="toast link" role="status"><span>Another Hearth copy wants this pairing.</span>` +
        `<button class="btn primary" data-act="allow" data-nonce="${esc(n)}" data-no-drag>Allow</button>` +
        `<button class="btn" data-act="deny" data-nonce="${esc(n)}" data-no-drag>No</button></div>`);
    }
    if (v.notice && v.notice.text) {
      out.push(`<div class="toast ${v.notice.kind === "error" ? "error" : ""}" role="status"><span>${esc(v.notice.text)}</span>` +
        `<button class="btn quiet" data-act="dismiss-notice" data-no-drag aria-label="Dismiss">OK</button></div>`);
    }
    return out.length ? `<div class="toasts">${out.join("")}</div>` : "";
  }

  function panel(v) {
    let inner;
    if (ui.confirm === "invite") {
      inner = `<p>Make a new code for your partner's new PC? Their current PC is signed out right away.</p><div class="row">` +
        btn("invite-yes", v.busy ? "Making a code..." : "Make a code", "primary", v.busy ? "disabled" : "") + btn("confirm-no", "Cancel") + `</div>`;
    } else if (ui.confirm === "unpair") {
      inner = `<p>Leave this hearth? Your partner keeps it for 7 days and can invite you again.</p><div class="row">` +
        btn("unpair-yes", v.busy ? "Leaving..." : "Unpair", "primary", v.busy ? "disabled" : "") + btn("confirm-no", "Cancel") + `</div>`;
    } else {
      inner = `<p class="muted hint">Every copy of Hearth on this PC uses this pairing. Invite again moves your partner to a new PC.</p><div class="row">` +
        btn("invite-ask", "Invite again") + btn("unpair-ask", "Unpair") + btn("panel", "Close", "quiet") + `</div>`;
    }
    return `<section class="panel" data-no-drag><h2>Hearth settings</h2>${inner}</section>`;
  }

  // ---------- render ----------

  function moodOf(v) {
    const m = v.sync.pet && v.sync.pet.mood;
    return mascot.MOODS.includes(m) ? m : "content";
  }

  function render(force) {
    const v = view;
    if (!v || v.phase === "loading") return;
    const w = which();
    const days = core.daysTogether(v.togetherSince);
    const mood = moodOf(v);
    const sig = JSON.stringify([w, showTabs(v), v.phase, v.names, days, statusOffline(), v.invite && [v.invite.code, v.invite.kind, v.invite.expired], v.unpaired, v.notice && v.notice.text,
      v.busy, v.link, ui.panel, ui.joining, ui.confirm, w === "pet" ? [mood, v.sync.petName] : 0]);
    if (!force && sig === lastSig) return;
    lastSig = sig;
    const typing = document.activeElement && document.activeElement.id === "code";

    app.dataset.view = w;
    app.dataset.phase = v.phase;
    let body;
    if (v.phase === "setup") body = setup(v);
    else if (v.phase === "unpaired") body = unpairedCard(v);
    else if (v.invite) body = inviteCard(v);
    else if (w === "board") body = '<section class="board-slot"></section>'; // board.js owns what goes in it
    else if (w === "pet") body = petStub();
    else body = window.HearthOverview.html(v);
    app.innerHTML = top(v) + body + toasts(v) + (ui.panel && v.paired ? panel(v) : "");

    // hooks that need the new elements
    const slot = app.querySelector(".board-slot"); // the board keeps its own element (and its fast-polling hover) across renders
    if (slot) window.HearthBoard.mount(slot, client, v); else window.HearthBoard.unmount();
    const pet = app.querySelector("[data-pet-root]");
    if (pet) window.HearthPet.attach(pet, client);
    // #211: any view may hold a [data-hearth-tap] element
    const tapSlot = app.querySelector("[data-hearth-tap]");
    if (tapSlot && window.HearthTap) window.HearthTap.mount(tapSlot, client);
    const code = app.querySelector("#code");
    if (code && (typing || ui.focusCode)) { ui.focusCode = false; code.focus(); code.setSelectionRange(code.value.length, code.value.length); }
    startCountdown();
    window.HearthOverview.mounted(v);   // only acts when the card body is the Overview

    if (!sentRendered) {
      sentRendered = true;
      let sent = false;
      const send = () => { if (!sent) { sent = true; umbra.rendered(); } };
      requestAnimationFrame(send);
      setTimeout(send, 400);
    }
  }

  // The invite countdown ticks once a second in place; nothing else re-renders for it.
  function tickCountdown() {
    const el = app.querySelector("[data-countdown]");
    if (!el || !view || !view.invite) { clearInterval(countdownTimer); countdownTimer = 0; return; }
    if (umbra.settings.paused) return;
    const c = core.countdown(view.invite.expiresAt, Date.now());
    if (c.expired) { render(true); return; }
    if (el.textContent !== c.text) el.textContent = c.text;
  }
  function startCountdown() {
    if (!app.querySelector("[data-countdown]")) { clearInterval(countdownTimer); countdownTimer = 0; return; }
    if (!countdownTimer) countdownTimer = setInterval(tickCountdown, 1000);
  }

  // ---------- input ----------

  function setTab(id) {
    if (!VIEWS.includes(id) || id === which()) return;
    tab.view = id; tab.opt = optionView();
    ui.panel = false; resetConfirm();
    render(true);
    Promise.resolve(umbra.store.set(TAB_KEY, { view: tab.view, opt: tab.opt })).catch(() => { /* the tab is a convenience */ });
  }

  function resetConfirm() { clearTimeout(confirmTimer); confirmTimer = 0; ui.confirm = null; }
  function askConfirm(kind) {
    ui.confirm = kind;
    clearTimeout(confirmTimer);
    confirmTimer = setTimeout(() => { ui.confirm = null; render(); }, 10000);
    render();
  }

  const ACTIONS = {
    tab(el) { setTab(el.dataset.tab); },
    async create() { if (await client.createHearth()) { ui.joining = false; } render(true); },
    "join-open"() { ui.joining = true; ui.focusCode = true; umbra.requestFocus(); render(true); },
    "join-cancel"() { ui.joining = false; ui.draft = ""; client.dismissNotice(); render(true); },
    link() { client.requestLink(); },
    allow(el) { client.approveLink(el.dataset.nonce); },
    deny(el) { client.denyLink(el.dataset.nonce); },
    "dismiss-notice"() { client.dismissNotice(); },
    "dismiss-invite"() { client.dismissInvite(); },
    panel() { ui.panel = !ui.panel; resetConfirm(); render(true); },
    "invite-ask"() { askConfirm("invite"); },
    "unpair-ask"() { askConfirm("unpair"); },
    "confirm-no"() { resetConfirm(); render(true); },
    async "invite-yes"() { if (await client.reinvite()) { ui.panel = false; resetConfirm(); } render(true); },
    async "unpair-yes"() { if (await client.unpair()) { ui.panel = false; resetConfirm(); } render(true); },
    async "reinvite-now"() { await client.reinvite(); },
    leave() { ui.panel = true; askConfirm("unpair"); },
  };

  app.addEventListener("click", (e) => {
    const el = e.target.closest("[data-act]");
    if (!el || !client || el.disabled) return;
    const fn = ACTIONS[el.dataset.act];
    if (fn) Promise.resolve(fn(el)).catch((err) => console.error(err));
  });
  app.addEventListener("input", (e) => { if (e.target.id === "code") ui.draft = e.target.value; });
  app.addEventListener("submit", async (e) => {
    e.preventDefault();
    if (!client) return;
    ui.draft = (app.querySelector("#code") || {}).value || ui.draft;
    if (await client.join(ui.draft)) { ui.joining = false; ui.draft = ""; }
    render(true);
  });
  document.addEventListener("keydown", (e) => {
    // arrow keys move between the tabs, like any tab strip
    if ((e.key === "ArrowLeft" || e.key === "ArrowRight") && e.target.classList && e.target.classList.contains("tab")) {
      const i = VIEWS.indexOf(which());
      setTab(VIEWS[(i + (e.key === "ArrowRight" ? 1 : VIEWS.length - 1)) % VIEWS.length]);
      const next = app.querySelector(`.tab[data-tab="${which()}"]`);
      if (next) next.focus();
      e.preventDefault();
      return;
    }
    if (e.key !== "Escape") return;
    if (ui.panel) { ui.panel = false; resetConfirm(); render(true); }
    else if (ui.joining) { ui.joining = false; render(true); }
  });

  // ---------- wiring ----------

  function onView(v) {
    view = v;
    // Copies that don't poll still show data freshness: the polling copy's last success counts for this copy's status dot.
    if (!v.leader && v.lastOkAt && v.lastOkAt !== lastStatusAt && umbra.status && umbra.status.ok) {
      lastStatusAt = v.lastOkAt;
      umbra.status.ok(new Date(v.lastOkAt));
    }
    render();
  }

  function onSettings(s) {
    if (!client) return;
    if (tab.view && tab.opt !== optionView()) { tab.view = ""; tab.opt = ""; Promise.resolve(umbra.store.set(TAB_KEY, null)).catch(() => {}); }
    client.setRefreshSeconds(s.refreshSeconds);
    client.setPaused(!!s.paused);
    if (s.paused) { clearInterval(countdownTimer); countdownTimer = 0; } else startCountdown();
    render();
  }

  umbra.ready.then(async () => {
    try {
      const saved = await umbra.store.get(TAB_KEY, null);
      if (saved && VIEWS.includes(saved.view) && saved.opt === optionView()) { tab.view = saved.view; tab.opt = saved.opt; }
    } catch (e) { /* first run, or storage unavailable: open on the View option */ }
    const channel = typeof BroadcastChannel === "function" ? new BroadcastChannel("umbra-hearth-v1") : null;
    client = core.createClient({
      fetchJson: (url) => umbra.fetchJson(url),
      store: umbra.store,
      channel,
      instance: (umbra.widget && (umbra.widget.instance || umbra.widget.id)) || "hearth",
    });
    await window.HearthOverview.init({ app, client, core, mascot, rerender: () => render(true) });
    if (window.HearthTap) {
      window.HearthTap.attach(client); // #211: taps, pulse, toast and the together moment work in every view
      app.addEventListener("hearth-overview", (e) => { if (e.detail && e.detail.tapSlot) window.HearthTap.mount(e.detail.tapSlot, client); });
    }
    client.subscribe(onView);
    client.setRefreshSeconds(umbra.settings.refreshSeconds);
    client.setPaused(!!umbra.settings.paused);
    await client.start();
    umbra.onSettings(onSettings);
    umbra.onTimeChanged(() => { lastSig = ""; render(); });
    if (umbra.status && umbra.status.onChange) umbra.status.onChange(() => render());
    // "Refresh all", resume from sleep and network-back arrive through umbra.every; the client decides whether a request is due.
    umbra.every(() => client.refresh());
    window.addEventListener("pagehide", () => client.stop());
  });
})();
