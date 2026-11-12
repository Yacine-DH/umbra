// Hearth Board view (#212): a drawing board both partners paint on. The DOM, pointer handling, canvas painting, the
// write queue to the relay and the saved-pictures gallery. All decisions that can be tested without a browser live in
// board-logic.js; the relay talk goes through the shared sync client (hearth-core.js: client.call / subscribe /
// setBoardHover).
//
// The shell (hearth.js) calls HearthBoard.mount(slot, client, view) whenever it renders the Board view and
// HearthBoard.unmount() when another view or the pairing card is shown. The board keeps its own element, so a shell
// re-render only moves it; sync updates reach it through client.subscribe and never rebuild the page.
(() => {
  "use strict";
  const L = window.HearthBoardLogic;
  const PREFS_KEY = "boardPrefs";
  const MIN_LINE_PX = 1.5;
  const TINY_PX = 90; // an empty board narrower than this gets a "make it bigger" hint (board.css hides the paper under 60 px)

  const esc = (s) => String(s == null ? "" : s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const ERASER_SVG = '<svg viewBox="0 0 20 20" aria-hidden="true"><path d="M3.5 12.5 11 5a2 2 0 0 1 2.8 0l2.2 2.2a2 2 0 0 1 0 2.8L10 16H6.5z" fill="#F7A8C4" stroke="#6B4F3A" stroke-width="1.5" stroke-linejoin="round"/><path d="M8 8.5l5 5M10 16h7" stroke="#6B4F3A" stroke-width="1.5" stroke-linecap="round" fill="none"/></svg>';

  function create(client) {
    const local = L.createLocal();
    const root = document.createElement("div");
    root.className = "bd";
    root.dataset.tools = "closed";
    root.innerHTML =
      '<div class="bd-stage"><div class="bd-sq">' +
      '<canvas class="bd-canvas" data-no-drag aria-label="Shared drawing board"></canvas>' +
      '<div class="bd-top" data-no-drag></div>' +
      '<div class="bd-note" role="status" aria-live="polite"></div>' +
      '<div class="bd-gallery" data-no-drag hidden></div>' +
      "</div>" +
      '<div class="bd-hint" aria-live="polite"></div></div>' +
      '<div class="bd-tools" data-no-drag>' +
      '<div class="bd-pal" role="radiogroup" aria-label="Colour">' +
      L.PALETTE.map((p, i) => `<button type="button" class="sw${i === L.ERASER ? " eraser" : ""}" data-c="${i}" style="--c:${p.hex}" role="radio" aria-checked="false" title="${esc(p.name)}" aria-label="${esc(p.name)}">${i === L.ERASER ? ERASER_SVG : ""}</button>`).join("") +
      "</div>" +
      '<div class="bd-sizes" role="radiogroup" aria-label="Brush size">' +
      L.WIDTHS.map((w, i) => `<button type="button" class="sz" data-z="${i}" role="radio" aria-checked="false" title="Brush ${i + 1}" aria-label="Brush size ${i + 1}"><i style="--d:${Math.round(3 + w * 0.42)}px"></i></button>`).join("") +
      "</div></div>" +
      '<div class="bd-bar" data-no-drag>' +
      '<button type="button" class="bd-btn bd-toggle" data-a="tools" aria-expanded="false" aria-label="Colour and brush" title="Colour and brush"><span class="bd-cur"></span></button>' +
      '<button type="button" class="bd-btn" data-a="undo" title="Undo my last stroke (Ctrl+Z)">Undo</button>' +
      '<button type="button" class="bd-btn" data-a="clear" title="Clear the board for both of you (you can bring it back for a minute)">Clear</button>' +
      '<button type="button" class="bd-btn" data-a="save" title="Save a small copy of this picture">Save</button>' +
      '<button type="button" class="bd-btn" data-a="gallery" title="Saved pictures">Saved<b class="num bd-count"></b></button>' +
      "</div>";

    const $ = (sel) => root.querySelector(sel);
    const canvas = $(".bd-canvas");
    const ctx = canvas.getContext("2d");
    const el = { top: $(".bd-top"), hint: $(".bd-hint"), note: $(".bd-note"), gallery: $(".bd-gallery"), count: $(".bd-count"), toggle: $(".bd-toggle"), cur: $(".bd-cur") };
    const btn = (a) => root.querySelector(`[data-a="${a}"]`);

    let view = null, lastRev = 0, lastMe = null, lastBoard = null;
    let recs = [];                    // what is drawn now (relay strokes + local overlay), in drawing order
    let size = 0, dpr = 1;            // css pixels of the (square) canvas
    let tool = { c: 0, z: 2 }, lastColor = 0;
    let pen = null, penId = -1, flushTimer = 0, inside = false, hovering = false;
    let outbox = [], sending = false, failures = 0, busyOp = false;
    let gallery = [], galleryLoaded = false, archiveFolder = "";
    const ui = { gallery: false, preview: null, confirmSave: null };
    let noteTimer = 0, tickTimer = 0, dismissedClearAt = 0;
    let hintShown = "", topShown = "";

    // ---------- painting ----------

    const hex = (c) => (L.PALETTE[c] || L.PALETTE[0]).hex;
    function lineStyle(c2d, c, z, boardPx, minPx) {
      c2d.strokeStyle = c2d.fillStyle = hex(c);
      c2d.lineCap = c2d.lineJoin = "round";
      c2d.lineWidth = L.brushWidth(z, boardPx, minPx);
    }
    const mid = (a, b) => [(a[0] + b[0]) / 2, (a[1] + b[1]) / 2];
    function dot(c2d, p, c, z, boardPx, minPx) {
      lineStyle(c2d, c, z, boardPx, minPx);
      c2d.beginPath(); c2d.arc(p[0], p[1], c2d.lineWidth / 2, 0, Math.PI * 2); c2d.fill();
    }
    /** A whole stroke as a smooth curve through the midpoints of its points. */
    function chain(c2d, ch, boardPx, minPx) {
      const p = ch.pts;
      if (!p.length) return;
      if (p.length === 1) { dot(c2d, p[0], ch.c, ch.z, boardPx, minPx); return; }
      lineStyle(c2d, ch.c, ch.z, boardPx, minPx);
      c2d.beginPath();
      c2d.moveTo(p[0][0], p[0][1]);
      if (p.length === 2) c2d.lineTo(p[1][0], p[1][1]);
      else {
        const first = mid(p[0], p[1]);
        c2d.lineTo(first[0], first[1]);
        for (let i = 1; i < p.length - 1; i++) { const m = mid(p[i], p[i + 1]); c2d.quadraticCurveTo(p[i][0], p[i][1], m[0], m[1]); }
        c2d.lineTo(p[p.length - 1][0], p[p.length - 1][1]);
      }
      c2d.stroke();
    }
    /** The segment that a newly added point completes (same geometry as chain()), so a stroke is drawn as it grows. */
    function growStroke(c2d, p) {
      const m = p.length - 1;
      if (m === 0) { dot(c2d, p[0], pen.color, pen.size, size, MIN_LINE_PX); return; }
      lineStyle(c2d, pen.color, pen.size, size, MIN_LINE_PX);
      c2d.beginPath();
      if (m === 1) {
        c2d.moveTo(p[0][0], p[0][1]);
        const a = mid(p[0], p[1]); c2d.lineTo(a[0], a[1]);
      } else {
        const a = mid(p[m - 2], p[m - 1]), b = mid(p[m - 1], p[m]);
        c2d.moveTo(a[0], a[1]); c2d.quadraticCurveTo(p[m - 1][0], p[m - 1][1], b[0], b[1]);
      }
      c2d.stroke();
    }
    /** Pen up: the last bit from the final midpoint to the final point. */
    function finishStroke(c2d, p) {
      if (p.length < 2) return;
      const a = mid(p[p.length - 2], p[p.length - 1]);
      lineStyle(c2d, pen.color, pen.size, size, MIN_LINE_PX);
      c2d.beginPath(); c2d.moveTo(a[0], a[1]); c2d.lineTo(p[p.length - 1][0], p[p.length - 1][1]); c2d.stroke();
    }
    function paint(c2d, px, records, extra, minPx) {
      const cv = c2d.canvas;
      c2d.setTransform(1, 0, 0, 1, 0, 0);
      c2d.fillStyle = L.PAPER; c2d.fillRect(0, 0, cv.width, cv.height);
      c2d.setTransform(cv.width / L.GRID, 0, 0, cv.height / L.GRID, 0, 0);
      for (const ch of L.buildChains(records)) chain(c2d, ch, px, minPx);
      if (extra) chain(c2d, extra, px, minPx);
    }

    function currentRecords() {
      const strokes = view && view.sync && view.sync.board ? view.sync.board.strokes : [];
      return local.records(strokes);
    }
    function redraw() {
      recs = currentRecords();
      if (size <= 0) return;
      paint(ctx, size, recs, pen ? { c: pen.color, z: pen.size, pts: pen.points } : null, MIN_LINE_PX);
    }
    function layout() {
      const w = canvas.clientWidth;
      dpr = window.devicePixelRatio || 1;
      if (!w) { size = 0; return; }
      size = w;
      const px = Math.max(1, Math.round(w * dpr));
      if (canvas.width !== px || canvas.height !== px) { canvas.width = px; canvas.height = px; }
      redraw();
      chrome();
    }
    const ro = typeof ResizeObserver === "function" ? new ResizeObserver(layout) : null;
    if (ro) ro.observe(canvas);

    // ---------- the write queue ----------

    const me = () => (view && view.me) || "a";
    const serverNow = () => Date.now() + ((view && view.sync && view.sync.skew) || 0);
    const paired = () => !!(view && view.phase === "paired" && view.paired);

    function queueRecords(records) {
      for (const r of records) outbox.push(local.addPending(r, me()));
      if (records.length) pump();
    }
    function enqueueOp(op) {
      return new Promise((resolve) => { outbox.push({ op, resolve }); busyOp = true; chrome(); pump(); });
    }
    function endedPairing() { outbox = []; local.reset(); }
    const asText = (name, err) => ({
      "nothing to undo": "Nothing of yours to undo.",
      "nothing to restore": "Nothing to bring back.",
      "too late": "Too late to bring the board back.",
    }[err] || `Could not ${name} (${err}).`);

    async function runOp(it) {
      let r;
      try { r = await client.call(it.op); } catch (e) { note("Offline. Try again in a moment.", true); it.resolve(false); return; }
      if (!r || r.paired === false) { endedPairing(); it.resolve(false); return; }
      if (r.error) { note(asText(it.op, r.error), true); it.resolve(false); return; }
      if (it.op === "undo") local.forget(Array.isArray(r.removed) ? r.removed : []);
      else if (it.op === "clear" && !r.noop) local.startClear(currentRecords().filter((x) => x.i != null), view.sync.rev, { by: me(), at: serverNow(), until: Number(r.until) || serverNow() + 60000 });
      else if (it.op === "unclear") { local.unclear(Date.now()); dismissedClearAt = (view.sync.board.clear && view.sync.board.clear.at) || dismissedClearAt; }
      redraw();
      it.resolve(true);
    }

    async function pump() {
      if (sending) return;
      sending = true;
      try {
        while (outbox.length) {
          const head = outbox[0];
          if (head.op) { outbox.shift(); await runOp(head); busyOp = outbox.some((x) => x.op); chrome(); continue; }
          if (!paired()) { endedPairing(); break; }
          const run = [];
          for (const it of outbox) { if (it.op) break; run.push(it); }
          const pack = L.packRequests(run)[0];
          const batch = run.slice(0, pack.count);
          let r;
          try { r = await client.call("stroke", { s: pack.s }); }
          catch (e) {
            failures++; chrome();
            await sleep(Math.min(10000, 1000 * Math.pow(2, failures - 1)));
            continue;
          }
          failures = 0;
          if (!r || r.paired === false) { endedPairing(); break; }
          outbox.splice(0, batch.length);
          if (r.error) {
            local.drop(batch.map((x) => x.seq));
            note(`A stroke was not accepted (${r.error}).`, true);
            redraw();
          } else {
            const ids = Array.isArray(r.ids) ? r.ids : [];
            local.confirm(batch.slice(0, ids.length).map((x, n) => ({ seq: x.seq, i: ids[n] })), Date.now());
            if (ids.length < batch.length) local.drop(batch.slice(ids.length).map((x) => x.seq));
          }
          chrome();
        }
      } finally { sending = false; chrome(); }
    }

    // ---------- drawing ----------

    function pos(e) {
      const r = canvas.getBoundingClientRect();
      return [L.toGrid(e.clientX - r.left, r.width), L.toGrid(e.clientY - r.top, r.height)];
    }
    function setHover(on) {
      if (on === hovering) return;
      hovering = on;
      try { client.setBoardHover(on); } catch (e) { /* the client is gone */ }
    }
    function scheduleFlush() {
      clearTimeout(flushTimer);
      flushTimer = setTimeout(() => { if (!pen) return; queueRecords(pen.flush()); if (pen.active) scheduleFlush(); }, L.flushDelay(pen ? pen.recordsSent : 0));
    }
    function endPen(e) {
      if (!pen) return;
      clearTimeout(flushTimer);
      window.removeEventListener("pointerup", onUp, true);
      window.removeEventListener("pointercancel", onUp, true);
      if (e && penId !== -1) { try { canvas.releasePointerCapture(penId); } catch (err) { /* not captured */ } }
      if (e && e.type !== "pointercancel" && Number.isFinite(e.clientX)) { const p = pos(e); if (pen.end(p[0], p[1])) growStroke(ctx, pen.points); }
      else pen.end(null);
      finishStroke(ctx, pen.points);
      queueRecords(pen.flush());
      pen = null; penId = -1;
      if (!inside) setHover(false);
      redraw();
      chrome();
    }
    function onUp(e) { if (pen && e.pointerId === penId) endPen(e); }

    canvas.addEventListener("pointerdown", (e) => {
      if (e.button !== 0 || pen || !paired() || size <= 0) return;
      e.preventDefault();
      try { canvas.setPointerCapture(e.pointerId); } catch (err) { /* ignore */ }
      penId = e.pointerId;
      pen = L.createPen({ color: tool.c, size: tool.z });
      const p = pos(e);
      pen.start(p[0], p[1]);
      growStroke(ctx, pen.points);
      window.addEventListener("pointerup", onUp, true);
      window.addEventListener("pointercancel", onUp, true);
      setHover(true);
      scheduleFlush();
      closeTools();
      if (typeof umbra.requestFocus === "function") umbra.requestFocus();
      chrome();
    });
    canvas.addEventListener("pointermove", (e) => {
      if (!pen || e.pointerId !== penId) return;
      if (!(e.buttons & 1)) { endPen(e); return; }
      const list = typeof e.getCoalescedEvents === "function" ? e.getCoalescedEvents() : [];
      for (const ev of list.length ? list : [e]) {
        const p = pos(ev);
        if (pen.move(p[0], p[1])) growStroke(ctx, pen.points);
      }
    });
    canvas.addEventListener("contextmenu", (e) => { if (pen) e.preventDefault(); });
    root.addEventListener("pointerenter", () => { inside = true; setHover(true); });
    root.addEventListener("pointerleave", () => { inside = false; if (!pen) setHover(false); });

    // ---------- tools ----------

    function savePrefs() { try { Promise.resolve(umbra.store.set(PREFS_KEY, { c: tool.c === L.ERASER ? lastColor : tool.c, z: tool.z })).catch(() => {}); } catch (e) { /* ignore */ } }
    function toolUi() {
      root.querySelectorAll(".sw").forEach((b) => { const on = Number(b.dataset.c) === tool.c; b.classList.toggle("on", on); b.setAttribute("aria-checked", String(on)); });
      root.querySelectorAll(".sz").forEach((b) => { const on = Number(b.dataset.z) === tool.z; b.classList.toggle("on", on); b.setAttribute("aria-checked", String(on)); });
      el.cur.style.setProperty("--c", hex(tool.c));
      el.cur.classList.toggle("eraser", tool.c === L.ERASER);
    }
    function setColor(c) { if (c !== L.ERASER) lastColor = c; tool.c = c; toolUi(); savePrefs(); }
    function setSize(z) { tool.z = Math.max(0, Math.min(L.WIDTHS.length - 1, z)); toolUi(); savePrefs(); }
    function closeTools() { if (root.dataset.tools === "open") { root.dataset.tools = "closed"; el.toggle.setAttribute("aria-expanded", "false"); } }
    function toggleTools() { const open = root.dataset.tools !== "open"; root.dataset.tools = open ? "open" : "closed"; el.toggle.setAttribute("aria-expanded", String(open)); }

    // ---------- gallery ----------

    async function loadGallery() {
      try { gallery = L.sanitizeGallery(await umbra.store.get(L.GALLERY.key, [])); } catch (e) { gallery = []; }
      galleryLoaded = true;
      chrome(); renderGallery();
    }
    function exportPng() {
      for (const edge of L.GALLERY.sizes) {
        const c = document.createElement("canvas");
        c.width = c.height = edge;
        paint(c.getContext("2d"), edge, recs, null, 1);
        let url = "";
        try { url = c.toDataURL("image/png"); } catch (e) { return ""; }
        if (url.length <= L.GALLERY.maxImageChars) return url;
      }
      return "";
    }
    async function writeGallery(next) {
      try { await umbra.store.set(L.GALLERY.key, next); gallery = next; return true; }
      catch (e) { note("Could not save: this widget's storage is full.", true); return false; }
    }
    // Every saved picture is also written to the picture folder (umbra.images: Pictures > Umbra > Hearth unless the user
    // picked another one in Settings), so the gallery is only the newest few and the folder is the backup. A picture that is
    // not on disk yet (saved before this, or its copy failed) moves there before the gallery drops it.
    const canArchive = () => !!(umbra.images && umbra.images.save);
    async function loadArchiveFolder() {
      if (!canArchive()) return;
      try { archiveFolder = await umbra.images.folder(); } catch (e) { archiveFolder = ""; }
      chrome();
    }
    const why = (e) => (e && e.message ? e.message : "try again");
    async function saveNow(item) {
      let copyError = "";
      if (archiveFolder) {
        try { await umbra.images.save(item.png); item = Object.assign({}, item, { kept: true }); }
        catch (e) { copyError = why(e); }
      }
      const plan = L.planGallery(gallery, item);
      const toMove = archiveFolder ? plan.dropped.filter((d) => !d.kept) : [];
      let moved = 0;
      try { for (const d of toMove) { await umbra.images.save(d.png); moved++; } }
      catch (e) {
        // nothing is removed unless it is safely in the folder
        note("Could not save to your folder: " + why(e) + ". Nothing was removed.", true);
        ui.confirmSave = null; chrome(); return;
      }
      if (await writeGallery(plan.list)) {
        note(copyError ? `Saved here, but not in your folder: ${copyError}.`
          : item.kept ? `Saved, with a copy in ${archiveFolder}.${moved ? " Older pictures are there too." : ""}`
          : plan.dropped.length ? "Saved. The oldest picture was removed." : "Saved to your pictures.");
      }
      ui.confirmSave = null;
      chrome(); renderGallery();
    }
    async function chooseArchiveFolder(thenSave) {
      try {
        const r = await umbra.images.chooseFolder();
        if (r && r.folder) archiveFolder = r.folder;
      } catch (e) { note("Could not open the folder dialog. Click the widget and try again.", true); }
      if (thenSave && archiveFolder && ui.confirmSave) saveNow(ui.confirmSave); else chrome();
    }
    function startSave() {
      if (!recs.length) { note("Draw something first, then save it."); return; }
      if (!galleryLoaded) { note("Loading your pictures. Try again.", true); return; }
      const png = exportPng();
      if (!png) { note("Could not make a small enough picture.", true); return; }
      const item = { id: String(Date.now()), at: Date.now(), png };
      const plan = L.planGallery(gallery, item);
      if (plan.dropped.length && !archiveFolder) { ui.confirmSave = item; chrome(); return; }
      saveNow(item); // with a folder chosen, the oldest picture moves there instead of being removed
    }
    function renderGallery() {
      el.gallery.hidden = !ui.gallery;
      if (!ui.gallery) return;
      const fmt = (t) => new Date(t).toLocaleString(umbra.locale, { month: "short", day: "numeric", hour: "numeric", minute: "2-digit", hour12: umbra.hour12 });
      const head = (title) => `<div class="bd-gal-head"><b>${esc(title)}</b><span class="grow"></span><button type="button" class="bd-btn quiet" data-a="gal-close">Close</button></div>`;
      const item = ui.preview && gallery.find((g) => g.id === ui.preview);
      if (item) {
        el.gallery.innerHTML = head(fmt(item.at)) + `<div class="bd-prev"><img alt="Saved picture from ${esc(fmt(item.at))}" src="${item.png}"></div>` +
          `<div class="bd-gal-foot"><button type="button" class="bd-btn" data-a="gal-back">Back</button><button type="button" class="bd-btn" data-a="gal-del" data-id="${esc(item.id)}">Delete</button></div>`;
        return;
      }
      ui.preview = null;
      el.gallery.innerHTML = head("Saved pictures") +
        (gallery.length
          ? `<p class="bd-gal-sub num">${gallery.length} of ${L.GALLERY.maxItems}. ${archiveFolder ? "Every picture is also in your " + esc(archiveFolder) + " folder." : "The oldest goes first when it is full."}</p><div class="bd-thumbs">` +
            gallery.slice().reverse().map((g) => `<button type="button" class="thumb" data-a="gal-open" data-id="${esc(g.id)}" title="${esc(fmt(g.at))}"><img alt="Saved picture from ${esc(fmt(g.at))}" src="${g.png}"></button>`).join("") + "</div>"
          : '<p class="bd-gal-sub">Nothing saved yet. Press Save to keep a small copy of the board.</p>');
    }

    // ---------- chrome: buttons, banner, hint, notes ----------

    function note(text, isError) {
      el.note.textContent = text;
      el.note.classList.toggle("error", !!isError);
      el.note.classList.toggle("show", !!text);
      clearTimeout(noteTimer);
      if (text) noteTimer = setTimeout(() => { el.note.classList.remove("show"); }, 3800);
    }

    function clearInfo() {
      const b = view && view.sync ? view.sync.board : null;
      const c = local.clearInfo(b && b.clear);
      if (!c || (c.at && c.at === dismissedClearAt)) return null;
      const st = L.clearState(c, me(), Date.now(), view.sync.skew);
      return st && st.active ? st : null;
    }

    function chrome() {
      if (!view) return;
      const ok = paired();
      const cs = clearInfo();
      const tiny = size > 0 && size < TINY_PX;
      btn("undo").disabled = !ok || busyOp || !!pen || !(L.hasMine(recs, me()) || local.pendingCount > 0);
      btn("clear").disabled = !ok || busyOp || !recs.length;
      btn("save").disabled = !ok || !recs.length;
      el.count.textContent = gallery.length ? String(gallery.length) : "";

      // top pills: save confirmation, clear banner
      let top = "";
      if (ui.confirmSave) top += '<div class="pill"><span>The gallery is full. Saving removes your oldest picture.</span>' +
        (canArchive() ? '<button type="button" class="bd-btn" data-a="save-folder">Keep it in a folder</button>' : "") +
        '<button type="button" class="bd-btn" data-a="save-yes">Save</button><button type="button" class="bd-btn quiet" data-a="save-no">Cancel</button></div>';
      if (cs) {
        const who = cs.mine ? "you" : (view.names.partner || "your partner");
        top += `<div class="pill alert"><span>Cleared by ${esc(who)}</span><button type="button" class="bd-btn" data-a="unclear"${busyOp ? " disabled" : ""}>Undo</button><span class="num bd-remain" data-remain></span></div>`;
      }
      if (failures > 0 && local.pendingCount > 0) top += `<div class="pill"><span class="num">Offline. ${local.pendingCount} ${local.pendingCount === 1 ? "stroke waits" : "strokes wait"} to be sent.</span></div>`;
      if (top !== topShown) { topShown = top; el.top.innerHTML = top; } // rebuilt only when it changes, so a click never lands on a replaced button
      const remain = el.top.querySelector("[data-remain]");
      if (remain && cs) remain.textContent = Math.ceil(cs.remainingMs / 1000) + "s";
      if (cs && !tickTimer) tickTimer = setInterval(tick, 1000);
      else if (!cs && tickTimer) { clearInterval(tickTimer); tickTimer = 0; }

      // empty-board hint
      let hint = "";
      if (ok && !(pen && pen.active) && !recs.length && !cs) {
        const rev = view.sync.rev;
        if (tiny) hint = "Make this widget bigger to draw.";
        else if (rev === 0) hint = view.offline ? "Offline. The board loads when the relay answers." : "Loading the board...";
        else hint = `Draw anything. ${view.names.partner || "Your partner"} sees it live.`;
      }
      if (hint !== hintShown) { hintShown = hint; el.hint.textContent = hint; el.hint.classList.toggle("show", !!hint); }
    }
    const tick = () => chrome(); // re-evaluates the clear banner: the countdown, and its end

    // ---------- clicks and keys ----------

    const ACTIONS = {
      tools: toggleTools,
      async undo() { if (!pen) await enqueueOp("undo"); },
      async clear() { if (await enqueueOp("clear")) note("Board cleared. You can bring it back for a minute."); },
      async unclear() { await enqueueOp("unclear"); },
      save: startSave,
      "save-yes"() { if (ui.confirmSave) saveNow(ui.confirmSave); },
      "save-folder"() { chooseArchiveFolder(true); },
      "save-no"() { ui.confirmSave = null; chrome(); },
      gallery() { ui.gallery = !ui.gallery; ui.preview = null; renderGallery(); },
      "gal-close"() { ui.gallery = false; ui.preview = null; renderGallery(); },
      "gal-open"(b) { ui.preview = b.dataset.id; renderGallery(); },
      "gal-back"() { ui.preview = null; renderGallery(); },
      async "gal-del"(b) { const next = gallery.filter((g) => g.id !== b.dataset.id); if (await writeGallery(next)) { ui.preview = null; chrome(); renderGallery(); } },
    };

    root.addEventListener("click", (e) => {
      const b = e.target.closest("button");
      if (!b || b.disabled || !root.contains(b)) return;
      if (b.dataset.c !== undefined) { setColor(Number(b.dataset.c)); return; }
      if (b.dataset.z !== undefined) { setSize(Number(b.dataset.z)); return; }
      const fn = ACTIONS[b.dataset.a];
      if (fn) Promise.resolve(fn(b)).catch((err) => console.error(err));
    });
    document.addEventListener("keydown", (e) => {
      if (!root.isConnected || !root.offsetParent) return;
      const t = e.target;
      if (t && t.closest && t.closest("input, textarea, select, [contenteditable]")) return;
      const key = e.key.length === 1 ? e.key.toLowerCase() : e.key;
      if ((e.ctrlKey || e.metaKey) && key === "z") { e.preventDefault(); if (!btn("undo").disabled) ACTIONS.undo(); }
      else if (e.ctrlKey || e.metaKey || e.altKey) return;
      else if (key === "e") setColor(tool.c === L.ERASER ? lastColor : L.ERASER);
      else if (key === "[") setSize(tool.z - 1);
      else if (key === "]") setSize(tool.z + 1);
      else if (key === "Escape") { closeTools(); if (ui.gallery) ACTIONS["gal-close"](); }
    });

    // ---------- view updates ----------

    function resetAll() {
      clearTimeout(flushTimer); pen = null; penId = -1;
      outbox = []; failures = 0; busyOp = false;
      local.reset(); lastBoard = null; dismissedClearAt = 0; ui.confirmSave = null;
    }

    function update(v) {
      view = v;
      if (v.sync.rev < lastRev || v.me !== lastMe) resetAll();
      lastRev = v.sync.rev; lastMe = v.me;
      const b = v.sync.board;
      if (b !== lastBoard) {
        lastBoard = b;
        local.observe(b.strokes, v.sync.rev, b.clear, Date.now(), v.sync.skew);
        redraw();
      }
      chrome();
    }

    const unsub = client.subscribe(update);
    umbra.onSettings(() => { layout(); });
    (async () => {
      try {
        const p = await umbra.store.get(PREFS_KEY, null);
        if (p && Number.isInteger(p.c) && p.c >= 0 && p.c < L.ERASER) { lastColor = p.c; tool.c = p.c; }
        if (p && Number.isInteger(p.z) && p.z >= 0 && p.z < L.WIDTHS.length) tool.z = p.z;
      } catch (e) { /* defaults */ }
      toolUi();
    })();
    toolUi();
    loadGallery();
    loadArchiveFolder();
    update(client.getView());

    return {
      client, root,
      attach(slot) { if (root.parentNode !== slot) slot.appendChild(root); },
      detach() { setHover(false); if (pen) endPen(null); root.remove(); },
      update,
      destroy() { unsub(); if (ro) ro.disconnect(); clearInterval(tickTimer); clearTimeout(noteTimer); this.detach(); },
    };
  }

  let inst = null;
  window.HearthBoard = {
    /** Put the board into `slot` (a fresh element of the shell's render) and bring it up to date with `view`. */
    mount(slot, client, view) {
      if (inst && inst.client !== client) { inst.destroy(); inst = null; }
      if (!inst) inst = create(client);
      inst.attach(slot);
      inst.update(view || client.getView());
    },
    /** The Board view is not shown (another view, or the pairing card). Pending strokes keep being sent. */
    unmount() { if (inst) { inst.detach(); } },
  };
})();
