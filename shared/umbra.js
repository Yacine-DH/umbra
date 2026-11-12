// Umbra widget SDK. Include with:
//   <script src="https://umbra.local/shared/umbra.js"></script>
// It talks to the host app through the host's message bridge (WebView2 on Windows, WebKitGTK on Linux).
(() => {
// BRIDGE-START
  // WebView2 (Windows): window.chrome.webview. WebKitGTK (Linux): page -> host through
  // window.webkit.messageHandlers.umbra, host -> page by the host calling window.__umbraReceive(message).
  // Same message objects either way; null when the page runs outside Umbra.
  const umbraBridge = (() => {
    const wv = window.chrome && window.chrome.webview;
    if (wv) return { post: (m) => wv.postMessage(m), listen: (fn) => wv.addEventListener("message", (e) => fn(e.data)) };
    const wk = window.webkit && window.webkit.messageHandlers && window.webkit.messageHandlers.umbra;
    if (!wk) return null;
    const listeners = [];
    if (!("__umbraReceive" in window))
      Object.defineProperty(window, "__umbraReceive", { value: (m) => listeners.forEach((fn) => fn(m)) });
    return { post: (m) => wk.postMessage(m), listen: (fn) => listeners.push(fn) };
  })();
// BRIDGE-END
  const bridge = umbraBridge;
  const post = (msg) => bridge && bridge.post(msg);

  let settings = { refreshSeconds: 60, animations: true, locked: false, scale: 1, locale: "en-US", hour12: false, editMode: false, options: {} };
  let widget = { id: "", instance: "", name: "" }; // instance = id of this copy ("sp500", "sp500#2")
  const handlers = { settings: [], refresh: [], store: [], status: [], time: [], notification: [], timelapse: [], commands: [] };
  const pending = new Map();
  let nextId = 1;
  let resolveReady;
  const ready = new Promise((r) => (resolveReady = r));

  const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
  function applyMotion() {
    document.documentElement.dataset.motion = settings.animations && !reducedMotion.matches ? "on" : "off";
  }
  reducedMotion.addEventListener("change", applyMotion);

  // The active theme arrives as one generated ":root{--token:value;...}" rule from the host. It is applied after
  // umbra.css, so every widget picks it up without changes. Anything that isn't plain tokens is ignored.
  const THEME_CSS = /^:root\{(?:--[a-z0-9-]+:[#a-z0-9.,%() \/-]+;)+\}$/i;
  function applyTheme() {
    const css = settings.themeCss && THEME_CSS.test(settings.themeCss) ? settings.themeCss : "";
    let el = document.getElementById("umbra-theme");
    if (!css) { if (el) el.remove(); return; }
    if (!el) { el = document.createElement("style"); el.id = "umbra-theme"; document.head.append(el); }
    if (el.textContent !== css) el.textContent = css;
  }
  // umbra.css says color-scheme: dark; a light theme flips it so native controls (scrollbars, selects, date fields) match.
  // Same test as applyTheme in settings/settings.js: keep the two in step.
  function applyScheme() {
    const m = /rgba?\((\d+), (\d+), (\d+)/.exec(getComputedStyle(document.documentElement).backgroundColor);
    const light = !!m && 0.2126 * m[1] + 0.7152 * m[2] + 0.0722 * m[3] > 140;
    document.documentElement.style.colorScheme = light ? "light" : "";
  }

  // Appearance options from Settings (corner radius, calm motion) arrive as one more :root rule, applied after the theme.
  function applyAppearance() {
    const css = settings.appearanceCss && THEME_CSS.test(settings.appearanceCss) ? settings.appearanceCss : "";
    let el = document.getElementById("umbra-appearance");
    if (!css) { if (el) el.remove(); return; }
    if (!el) { el = document.createElement("style"); el.id = "umbra-appearance"; document.head.append(el); }
    if (el.textContent !== css) el.textContent = css;
  }

  bridge && bridge.listen((m) => {
    switch (m.type) {
      case "init":
        widget = m.widget;
        settings = m.settings;
        applyMotion();
        applyEdit();
        applyTheme(); applyAppearance(); applyScheme();
        resolveReady();
        break;
      case "settings":
        settings = m.settings;
        applyMotion();
        applyEdit();
        applyTheme(); applyAppearance(); applyScheme();
        updateStatus(); // the stale threshold follows the refresh interval
        handlers.settings.forEach((fn) => fn(settings));
        break;
      case "editSelected": // the last widget pressed in edit mode gets a stronger outline (arrow keys nudge it)
        settings.editSelected = !!m.selected;
        applyEdit();
        break;
      case "editScale": // live scale while the corner handle is dragged; keeps the overlay's outline size steady
        if (overlay && typeof m.scale === "number" && m.scale > 0) overlay.style.setProperty("--k", String(1 / m.scale));
        break;
      case "refresh":
        handlers.refresh.forEach((fn) => fn());
        break;
      case "timeChanged": // system clock or time zone changed; widgets that don't listen get a refresh
        (handlers.time.length ? handlers.time : handlers.refresh).forEach((fn) => fn());
        break;
      case "fetchResult":
      case "runResult":
      case "commandsResult":
      case "systemResult":
      case "notifyResult":
      case "requestNotificationsResult":
      case "mediaResult":
      case "timelapseResult":
      case "imagesResult":
      case "storeResult": {
        const p = pending.get(m.id);
        if (!p) return;
        pending.delete(m.id);
        clearTimeout(p.timer);
        m.ok ? p.resolve(m.data) : p.reject(new Error(m.error || "Request failed"));
        break;
      }
      case "notificationAction":
        handlers.notification.forEach((fn) => fn({ id: m.action, tag: m.tag ?? null }));
        break;
      case "timelapseState":
        handlers.timelapse.forEach((fn) => fn(m.state));
        break;
      case "storeChanged":
        handlers.store.forEach((fn) => fn(m.key ?? null));
        break;
      case "commandsChanged": // a script was added, changed or deleted in Settings, or one that keeps running started or stopped
        handlers.commands.forEach((fn) => fn());
        break;
    }
  });

  /** Send a request to the host and resolve with its reply's data. */
  function request(msg, timeoutMs) {
    return new Promise((resolve, reject) => {
      const id = nextId++;
      const timer = setTimeout(() => { pending.delete(id); reject(new Error("Timed out")); }, timeoutMs);
      pending.set(id, { resolve, reject, timer });
      post({ ...msg, id });
    });
  }

  // Data freshness. Every fetchJson/fetchText outcome is tracked, so a widget that fetches gets a shared
  // "Updated 3 min ago" / "Offline" state for free; ok() and failed(err) override it for data that arrives another way.
  // States: none (nothing fetched yet), live, stale (data older than 3x the refresh interval), offline (2+ failures in a
  // row, or a failure with nothing loaded since the widget started). <html data-stale> is set for stale and offline.
  const STALE_AFTER = 3;  // x refreshSeconds
  let lastData = null;    // when the data on screen was fetched (restored data counts, see ok(at))
  let sessionOk = false;  // a fetch succeeded since the widget started
  let failures = 0;       // consecutive failures
  let lastError = null;
  let statusState = "none";
  let statusTimer = 0;
  const statusEls = new Set();

  const clock = (d) => d.toLocaleTimeString(locale(), { hour: "2-digit", minute: "2-digit", hour12: !!settings.hour12 });
  function ago(d) {
    const m = Math.floor((Date.now() - d.getTime()) / 60000);
    if (m < 1) return "just now";
    if (m < 60) return `${m} min ago`;
    return m < 1440 ? `${Math.floor(m / 60)} h ago` : `${Math.floor(m / 1440)} d ago`;
  }
  function computeStatus() {
    if (!lastData && !failures) return "none";
    if (failures >= 2 || (failures >= 1 && !sessionOk)) return "offline";
    if (lastData && Date.now() - lastData.getTime() > STALE_AFTER * (settings.refreshSeconds || 60) * 1000) return "stale";
    return "live";
  }
  function paintStatus(el) {
    el.dataset.state = statusState;
    const text = statusState === "offline" ? (lastData ? `Offline · last data ${clock(lastData)}` : "Offline")
      : lastData ? `Updated ${ago(lastData)}` : "";
    const label = el.lastElementChild;
    if (label && label.textContent !== text) label.textContent = text;
  }
  function updateStatus() {
    const next = computeStatus();
    const changed = next !== statusState;
    statusState = next;
    document.documentElement.toggleAttribute("data-stale", next === "stale" || next === "offline");
    statusEls.forEach(paintStatus);
    if (changed) handlers.status.forEach((fn) => fn(next));
  }
  function markStatus() {
    if (!statusTimer) statusTimer = setInterval(updateStatus, 30000); // ages the label, flips live to stale
    updateStatus();
  }
  const status = {
    /** Data arrived (fetchJson/fetchText call this). Pass a Date only to show data restored from umbra.store as "last data". */
    ok(at) {
      if (at instanceof Date && !isNaN(at)) {
        if (!lastData || at > lastData) lastData = at;
      } else {
        lastData = new Date(); sessionOk = true; failures = 0; lastError = null;
      }
      markStatus();
    },
    /** Loading failed: the request, or a response you couldn't use. The Error fetchJson rejected with is only counted once. */
    failed(err) {
      if (err != null && err === lastError) return;
      lastError = err ?? new Error("Failed");
      failures++;
      markStatus();
    },
    /** "none" | "live" | "stale" | "offline" */
    get state() { return statusState; },
    /** When the data on screen was fetched, or null. */
    get lastOk() { return lastData; },
    /** Failures in a row since the last success. */
    get failures() { return failures; },
    /** fn(state) runs whenever the state changes. */
    onChange: (fn) => handlers.status.push(fn),
  };

  /** <umbra-status>: a small dot. Hover for "Updated 3 min ago"; "Offline, last data 10:42" stays visible when offline. Styled in umbra.css. */
  class UmbraStatus extends HTMLElement {
    connectedCallback() {
      if (!this.firstElementChild) this.innerHTML = '<i aria-hidden="true"></i><span></span>';
      if (!this._hover) { this._hover = () => paintStatus(this); this.addEventListener("pointerenter", this._hover); }
      statusEls.add(this);
      paintStatus(this);
    }
    disconnectedCallback() { statusEls.delete(this); }
  }
  if (!customElements.get("umbra-status")) customElements.define("umbra-status", UmbraStatus);

  const tracked = (p) => p.then((v) => { status.ok(); return v; }, (e) => { status.failed(e); throw e; });

  /** Fetch JSON through the host (bypasses CORS). The host must be listed in widget.json permissions.hosts. */
  const fetchJson = (url) => tracked(request({ type: "fetchJson", url }, 25000));

  /**
   * Run one of the commands declared in widget.json permissions.commands. Only the id and string params are sent;
   * the host owns the program and arguments, asks the user to approve the first run, and caps time and output.
   * Resolves { exitCode, stdout, stderr, timedOut, stopped, truncated, durationMs }; rejects when not declared, not approved, or invalid.
   * A user script marked "Keeps running" in Settings resolves { started: true } as soon as it is up.
   */
  const run = (command, params) => request({ type: "run", command, params: params ?? undefined }, 300000);
  /**
   * What umbra.run can run: the commands in widget.json, then (with permissions.userScripts) the scripts the user added
   * in Settings. Resolves [{ id, title, description, user, keepsRunning, running }]; programs, arguments and paths stay in the host.
   */
  const commands = () => request({ type: "commands" }, 10000);
  /** Stop this widget's run of a command, or a user script that keeps running. Resolves true when something was stopped. */
  const stop = (command) => request({ type: "stop", command }, 10000);
  /**
   * What a command printed so far (the last 64 KB, stdout and stderr as they came): this copy's run in progress, or a
   * user script that keeps running. Resolves { running, keepsRunning, since, exitCode, restarting, restarts, text }.
   */
  const commandOutput = (command) => request({ type: "commandOutput", command }, 10000);
  /**
   * Machine totals declared in widget.json permissions.system: { cpu:{percent}, memory:{totalBytes,usedBytes,percent},
   * network:{downBytesPerSec,upBytesPerSec}, gpu:{percent,memoryUsedBytes}, disk:{activePercent,readBytesPerSec,writeBytesPerSec,
   * drives:[{name,totalBytes,freeBytes,usedPercent}]} }. gpu.percent is null on a PC without GPU counters.
   */
  const system = () => request({ type: "system" }, 10000);
  /**
   * Windows toast from this widget. Needs permissions.notifications and the user's per-widget switch; 5 per 10 minutes.
   * Clicking opens url (HTTPS, host in permissions.hosts). tag replaces this widget's earlier toast with the same tag.
   * actions (up to 2 of { id, label }) become buttons; a click calls the umbra.onNotificationAction handlers.
   */
  const notify = ({ title, body, url, tag, actions }) => request({ type: "notify", title, body, url, tag, actions }, 10000);
  /**
   * true when this widget may notify. When the user has not decided yet, Umbra shows its own "Allow notifications?"
   * prompt, but only when called right after a click on the widget (otherwise it rejects); waits up to 2 minutes for the answer.
   */
  const requestNotifications = () => request({ type: "requestNotifications" }, 130000);
  /** Windows media session. nowPlaying() needs permissions.media "read": { title, artist, album, app, status, playing, canPlay, canPause, canNext, canPrevious, art? (data URL) } or null. */
  const media = {
    nowPlaying: () => request({ type: "media", action: "get" }, 10000),
    /** command: play | pause | toggle | next | previous. Needs "control" and a click on the widget just before. */
    control: (command) => request({ type: "media", action: "control", command }, 10000),
  };
  /**
   * Webcam timelapse made by the host (needs permissions.camera). The page never sees the camera or a frame: the host takes
   * a still every `interval` seconds, then renders `length` seconds of MP4 at `fps` into the user's folder and deletes the stills.
   * start() and chooseFolder() work only right after a left click on the widget; the first start asks the user to allow the camera. Paths are never returned: file is a name, folder a label. A red dot shows on the widget and in the tray while recording.
   * State: { state: "idle"|"recording"|"paused"|"rendering", framesCaptured, framesPlanned, startedAt, endsBy, plan, file, error, capReached, cancelled }.
   */
  const timelapse = {
    /** { interval (1-3600 s), length (video seconds, up to 3600), fps (1-60) }; recording is capped at 12 h. Resolves with the state. */
    start: ({ interval, length, fps = 30 }) => request({ type: "timelapse", op: "start", interval, length, fps }, 130000), // the first start waits for the 2-minute camera prompt
    /** Stop early and render what was captured. */
    stop: () => request({ type: "timelapse", op: "stop" }, 10000),
    /** Stop and delete the stills without making a video. */
    cancel: () => request({ type: "timelapse", op: "cancel" }, 10000),
    /** The current (or last finished) session's state, plus the output folder. */
    status: () => request({ type: "timelapse", op: "status" }, 10000),
    /** Host folder dialog for where videos go (per widget). Resolves { folder, changed }. */
    chooseFolder: () => request({ type: "timelapse", op: "chooseFolder" }, 600000),
    /** fn(state) on every change: started, each still, rendering, finished (state "idle" with file or error). */
    onChange: (fn) => handlers.timelapse.push(fn),
  };
  /**
   * Keep pictures in the widget's picture folder (needs permissions.saveImages): Pictures\Umbra\<widget name>, or a folder
   * the user picked in Settings or through chooseFolder(). The host writes the file under its own name;
   * the page never gives a path and can't read files back. chooseFolder() works only right after a left click on the widget.
   */
  const images = {
    /** The picture folder's name ("Hearth"); "" only when the system has no Pictures folder and none was chosen. */
    folder: async () => (await request({ type: "images", op: "folder" }, 10000)).folder,
    /** Host folder dialog. Resolves { folder, changed }. */
    chooseFolder: () => request({ type: "images", op: "chooseFolder" }, 600000),
    /** Save a PNG data URL (up to 1 MB). Resolves { file, folder }; rejects when the picture is refused. */
    save: (png) => request({ type: "images", op: "save", png }, 20000),
  };
  /** Fetch plain text (up to 2 MB, e.g. Markdown or CSV) through the host, same rules as fetchJson. */
  const fetchText = (url) => tracked(request({ type: "fetchText", url }, 25000));

  /** This widget's own saved data (any JSON per key), kept by the host across restarts. */
  const store = {
    async get(key, fallback) {
      const r = await request({ type: "store", op: "get", key }, 5000);
      return r.found ? r.value : fallback;
    },
    set: (key, value) => request({ type: "store", op: "set", key, value: value === undefined ? null : value }, 5000),
    remove: (key) => request({ type: "store", op: "remove", key }, 5000),
    keys: () => request({ type: "store", op: "keys" }, 5000),
    /** fn(key) runs when the data was changed from outside the widget (umbractl data, MCP); key is null for "everything". */
    onChange: (fn) => handlers.store.push(fn),
  };

  /**
   * Run fn now and then every refreshSeconds (re-scheduled when the user changes the interval).
   * While the PC is locked or the display is off (settings.paused) nothing runs; fn runs once when it resumes.
   */
  function every(fn) {
    let timer;
    let wasPaused = false;
    const schedule = () => {
      clearTimeout(timer);
      if (settings.paused) return;
      timer = setTimeout(async () => { await run(); }, settings.refreshSeconds * 1000);
    };
    const run = async () => {
      if (settings.paused) return;
      try { await fn(); } catch (err) { console.error(err); }
      schedule();
    };
    handlers.refresh.push(run);
    handlers.settings.push(() => {
      const resumed = wasPaused && !settings.paused;
      wasPaused = !!settings.paused;
      if (resumed) run(); // one refresh on resume, then the normal interval
      else schedule();
    });
    ready.then(() => { wasPaused = !!settings.paused; run(); });
  }

  // Drag anywhere to move the widget: once the pointer travels a few pixels with the
  // button held, the host starts a native window move. A plain click stays a click.
  // Text fields need the keyboard, which widgets only get right after the user presses the mouse
  // on them (the host checks the physical button state). Editable elements never start a drag.
  const EDITABLE = "input, textarea, select, [contenteditable]:not([contenteditable='false'])";
  let down = null;
  window.addEventListener("pointerdown", (e) => {
    const path = grip ? e.composedPath() : [];
    const edge = path.includes(grip) ? "corner" : path.includes(edgeRight) ? "right" : path.includes(edgeBottom) ? "bottom" : null;
    if (edge) {
      // Edit-mode handles: the host runs a native resize. The corner and the edges both resize freely; the page
      // lays out at window size / zoom, so a widget that fills its window reflows.
      e.preventDefault();
      e.stopPropagation();
      if (e.button === 0) post({ type: "resize", edge });
      return;
    }
    const editable = e.target.closest(EDITABLE);
    // In edit mode a press also selects the widget and takes the keyboard, so the arrow keys can nudge it.
    post({ type: editable || editing() ? "focus" : "pointer" });
    if (e.button !== 0 || editable || e.target.closest("[data-no-drag]")) return;
    down = { x: e.screenX, y: e.screenY };
  }, true);
  window.addEventListener("pointermove", (e) => {
    if (!down || !(e.buttons & 1) || settings.locked) return;
    if (Math.hypot(e.screenX - down.x, e.screenY - down.y) > 4) {
      down = null;
      post({ type: "drag" });
    }
  });
  window.addEventListener("pointerup", () => (down = null), true);

  // Right-click opens the host's widget menu (Refresh, Options, Arrange widgets...). A page that calls preventDefault() on
  // "contextmenu" keeps its own menu; Shift+right-click always reaches the host, so the page never sees that event.
  window.addEventListener("contextmenu", (e) => { if (e.shiftKey) e.stopImmediatePropagation(); }, true);

  // Edit mode (app-wide, switched from the tray or Settings): an unlocked widget gets an outline and a corner
  // handle. The overlay covers the page, so mouse presses anywhere move the widget and its own controls don't
  // react to them (capture-phase listeners and keyboard events can still reach the page).
  // Ctrl+wheel zooms in 10 % steps. The host checks edit mode, the lock and the physical mouse/keyboard state.
  // The widget last pressed gets a stronger outline, and its arrow keys nudge it (1 px, Shift 10 px or a grid step).
  let overlay = null;
  let grip = null;
  let edgeRight = null;
  let edgeBottom = null;
  let frame = null;
  const editing = () => !!(settings.editMode && !settings.locked);
  function applyEdit() {
    if (!editing()) {
      if (overlay) overlay.remove();
      return;
    }
    if (!overlay) {
      overlay = document.createElement("umbra-edit");
      overlay.setAttribute("aria-hidden", "true");
      overlay.style.cssText = "position:fixed;inset:0;z-index:2147483647;display:block;cursor:move;";
      const root = overlay.attachShadow({ mode: "open" });
      // Sizes are multiplied by --k = 1 / scale, so the outline and handle look the same at any zoom.
      root.innerHTML = `<style>
        .frame { position: absolute; inset: 0; pointer-events: none; border-radius: calc(8px * var(--k));
                 background: rgba(139, 147, 255, 0.05); box-shadow: inset 0 0 0 calc(1.5px * var(--k)) rgba(139, 147, 255, 0.75); }
        .frame.sel { background: rgba(139, 147, 255, 0.1); box-shadow: inset 0 0 0 calc(3px * var(--k)) rgba(170, 176, 255, 1); }
        .edge-r { position: absolute; top: 0; right: 0; bottom: calc(24px * var(--k)); width: calc(10px * var(--k)); cursor: ew-resize; }
        .edge-b { position: absolute; left: 0; right: calc(24px * var(--k)); bottom: 0; height: calc(10px * var(--k)); cursor: ns-resize; }
        .grip { position: absolute; right: 0; bottom: 0; width: calc(24px * var(--k)); height: calc(24px * var(--k)); cursor: nwse-resize; }
        .grip svg { position: absolute; right: calc(5px * var(--k)); bottom: calc(5px * var(--k)); width: calc(11px * var(--k)); height: calc(11px * var(--k)); }
        /* Windows contrast themes drop box-shadows and backgrounds, so draw the frame as a real border. */
        @media (forced-colors: active) {
          .frame { background: none; box-shadow: none; border: calc(2px * var(--k)) solid Highlight; }
          .grip { background: Highlight; }
          .grip svg { stroke: HighlightText; }
        }
      </style><div class="frame"></div><div class="edge-r" title="Drag to resize"></div><div class="edge-b" title="Drag to resize"></div><div class="grip" title="Drag to resize"><svg viewBox="0 0 11 11" fill="none"
        stroke="#aab0ff" stroke-width="1.6" stroke-linecap="round"><path d="M10 2 2 10M10 6.5 6.5 10"/></svg></div>`;
      grip = root.querySelector(".grip");
      edgeRight = root.querySelector(".edge-r");
      edgeBottom = root.querySelector(".edge-b");
      frame = root.querySelector(".frame");
      // Clicks on the overlay are Umbra's, not the widget's.
      for (const type of ["pointerdown", "pointerup", "mousedown", "mouseup", "click", "dblclick", "contextmenu"])
        overlay.addEventListener(type, (e) => e.stopPropagation());
    }
    overlay.style.setProperty("--k", String(1 / (settings.scale || 1)));
    if (frame) frame.classList.toggle("sel", !!settings.editSelected);
    if (!overlay.isConnected) document.documentElement.append(overlay);
  }

  const ARROWS = { ArrowLeft: "left", ArrowRight: "right", ArrowUp: "up", ArrowDown: "down" };
  window.addEventListener("keydown", (e) => {
    const dir = ARROWS[e.key];
    if (!dir || !editing() || !settings.editSelected || e.ctrlKey || e.altKey || e.metaKey) return;
    e.preventDefault();
    post({ type: "nudge", dir });
  }, true);

  // Esc ends edit mode. The host also hears the key itself; this covers the cases where the page swallows it.
  window.addEventListener("keydown", (e) => {
    if (e.key === "Escape" && e.isTrusted && settings.editMode) post({ type: "exitEdit" });
  });

  let wheelSum = 0;
  let wheelTimer;
  window.addEventListener("wheel", (e) => {
    if (!editing() || !e.ctrlKey) return;
    e.preventDefault();
    wheelSum += e.deltaMode === 1 ? e.deltaY * 40 : e.deltaMode === 2 ? e.deltaY * 800 : e.deltaY;
    clearTimeout(wheelTimer);
    wheelTimer = setTimeout(() => (wheelSum = 0), 250);
    // At most one step per event, and about half a mouse-wheel notch per step so touchpads don't race through
    // the sizes. Chromium may report deltas divided by the display scale and zoom, hence devicePixelRatio.
    if (Math.abs(wheelSum) >= 50 / (window.devicePixelRatio || 1)) {
      post({ type: "zoom", steps: wheelSum < 0 ? 1 : -1 });
      wheelSum = 0;
    }
  }, { passive: false, capture: true });

  /** Animate a number from a to b with ease-out-cubic: f(t) = 1 - (1 - t)^3. */
  function tween(from, to, ms, onFrame) {
    // requestAnimationFrame doesn't run while the window is covered, so jump straight to the value.
    if (document.documentElement.dataset.motion === "off" || document.visibilityState !== "visible" || !isFinite(from) || from === to) {
      onFrame(to);
      return;
    }
    const start = performance.now();
    const step = (now) => {
      const t = Math.min(1, (now - start) / ms);
      const eased = 1 - Math.pow(1 - t, 3);
      onFrame(from + (to - from) * eased);
      if (t < 1) requestAnimationFrame(step);
    };
    requestAnimationFrame(step);
  }

  // The Windows region format (Settings > Region), sent by the host as a BCP-47 tag. A tag Intl rejects falls back to en-US.
  function locale() {
    const tag = settings.locale;
    if (typeof tag === "string" && tag) {
      try { return Intl.getCanonicalLocales(tag)[0]; } catch { /* fall through */ }
    }
    return "en-US";
  }

  const formatters = new Map();
  /** 7744.06 -> "7,744.06" (or "7.744,06" etc. in the user's Windows region) */
  function fmt(n, decimals = 2) {
    const key = locale() + "|" + decimals;
    if (!formatters.has(key))
      formatters.set(key, new Intl.NumberFormat(locale(), { minimumFractionDigits: decimals, maximumFractionDigits: decimals }));
    return formatters.get(key).format(n);
  }

  window.umbra = {
    ready,
    get settings() { return settings; },
    get options() { return settings.options || {}; },
    get widget() { return widget; },
    /** The user's Windows region format as a BCP-47 tag, e.g. "de-DE". Pass it to Intl or toLocale*String. */
    get locale() { return locale(); },
    /** True when Windows (Region > Time format) uses a 12-hour clock. Pass it as `hour12` to Intl.DateTimeFormat. */
    get hour12() { return !!settings.hour12; },
    onSettings: (fn) => handlers.settings.push(fn),
    /** fn({ id, tag }) when the user clicks a button on one of this widget's toasts (id from notify's actions, tag as sent or null). */
    onNotificationAction: (fn) => handlers.notification.push(fn),
    /** fn() runs when the system clock or time zone changed (travel, clock correction). Date already reflects the new zone; re-render and re-schedule anything tied to "today" or midnight. Without a listener, umbra.every callbacks run instead. */
    onTimeChanged: (fn) => handlers.time.push(fn),
    fetchJson,
    run,
    commands,
    stop,
    commandOutput,
    /** fn() when the user adds, changes or deletes one of this widget's scripts in Settings, or one that keeps running starts or stops; call umbra.commands() again. */
    onCommandsChange: (fn) => handlers.commands.push(fn),
    system,
    notify,
    requestNotifications,
    media,
    timelapse,
    images,
    fetchText,
    status,
    store,
    every,
    openUrl: (url) => post({ type: "openUrl", url }),
    /** Ask for keyboard focus, e.g. before focusing an input from a double-click. Only works right after a click on the widget. */
    requestFocus: () => post({ type: "focus" }),
    /**
     * Suggest this copy's tab icon when it sits in a tab group: a name from the bundled shared/tabicons (e.g. "chart-line"), or null to
     * clear. Call it again when the view changes. Names the host doesn't know are ignored and logged; the user's own pick in Settings always wins.
     */
    setTabIcon: (name) => post({ type: "tabIcon", name: typeof name === "string" ? name : null }),
    /** Call after the first real frame is drawn; screenshots (umbractl capture) wait for it. */
    rendered: () => post({ type: "rendered" }),
    tween,
    fmt,
  };

  applyMotion();
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", () => post({ type: "ready" }));
  else post({ type: "ready" });
})();
