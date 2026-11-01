// Timelapse helpers for the Pomodoro widget: pure settings/estimate/error rules (no DOM), tested in tests/widgets/pomodoro.test.mjs.
// Browser: window.PomodoroTimelapse. Node: module.exports.
(function (root) {
  "use strict";
  const MAX_STILLS = 3600, MAX_SECONDS = 12 * 3600; // the host's caps per video (TimelapsePlan.Create)
  const INTERVALS = [15, 20, 30, 60], FPS = 30, SPARE_SECONDS = 3600;
  const DEFAULTS = { on: false, interval: 30 };

  /** Clean saved settings: the on switch and one of INTERVALS (older saves with any other number snap to the nearest). */
  function clean(t) {
    t = t || {};
    const n = Number(t.interval);
    const interval = Number.isFinite(n) ? INTERVALS.reduce((best, v) => (Math.abs(v - n) < Math.abs(best - n) ? v : best)) : DEFAULTS.interval;
    return { on: t.on === true, interval };
  }

  /** The neighbouring speed: dir -1 is a shorter gap, +1 a longer one. Stays put at either end. */
  function stepInterval(interval, dir) {
    const i = INTERVALS.indexOf(clean({ interval }).interval);
    return INTERVALS[Math.min(INTERVALS.length - 1, Math.max(0, i + dir))];
  }

  /** What a session of `minutes` will give: photos taken and the resulting video seconds. */
  function estimate(t, minutes) {
    const frames = Math.max(1, Math.floor((minutes * 60) / clean(t).interval));
    return { frames, seconds: frames / FPS };
  }

  /** "0.8 s", "8 s", "1 min 20 s": one decimal only while a tenth is still noticeable. */
  function videoLength(seconds) {
    if (seconds < 10) return `${Math.round(seconds * 10) / 10} s`;
    const s = Math.round(seconds), m = Math.floor(s / 60);
    return m ? `${m} min${s % 60 ? ` ${s % 60} s` : ""}` : `${s} s`;
  }

  /** The card's subtitle for a session of `minutes`. */
  function summary(t, minutes) {
    const e = estimate(t, minutes);
    return `${videoLength(e.seconds)} video · ${e.frames} photo${e.frames === 1 ? "" : "s"}`;
  }

  /**
   * Arguments for umbra.timelapse.start for a session of `minutes`. Ending the session stops the recording and renders what was
   * taken. The host stops by itself at the stills asked for, so this asks for the session plus SPARE_SECONDS: Add 5 minutes or a
   * short pause can't end it early, and if the page can't stop it (crashed, or a session left paused) the camera still goes off soon after.
   */
  function request(t, minutes) {
    const interval = clean(t).interval;
    const stills = Math.floor(Math.min(MAX_SECONDS, minutes * 60 + SPARE_SECONDS) / interval) + 1;
    return { interval, length: Math.min(MAX_STILLS, stills) / FPS, fps: FPS };
  }

  /** Plain-words version of a host error. */
  function friendly(msg) {
    const m = String(msg || "");
    if (/disk space/i.test(m)) return `${m.replace(/\.?$/, ".")} A slower speed needs less.`;
    if (/click/i.test(m)) return "Click Start on the widget to begin the timelapse.";
    if (/den(y|ied)|not allowed|approv|declin/i.test(m)) return "Camera access was not allowed for this widget. Allow it in Settings > My widgets.";
    if (/privacy|access is off|blocked/i.test(m)) return "Windows is blocking the camera. Turn on Settings > Privacy & security > Camera for desktop apps.";
    if (/no camera|not found|no webcam/i.test(m)) return "No webcam was found.";
    if (/another|already|in use/i.test(m)) return "The camera is already used by another timelapse or app.";
    return m || "The timelapse could not start.";
  }

  /** What the status pill shows for a host state: kind (rec, held, busy, saved, err, idle), text, an optional tooltip and action label. */
  function status(s, { busy = false, error = "", folder = "" } = {}) {
    const st = s && s.state, photos = (n) => `${n} photo${n === 1 ? "" : "s"}`;
    if (st === "recording") return { kind: "rec", text: `Recording · ${photos(s.framesCaptured || 0)}` };
    if (st === "paused") return { kind: "held", text: `Timelapse paused · ${photos(s.framesCaptured || 0)}` };
    if (st === "rendering") return { kind: "busy", text: "Making the video..." };
    if (s && s.file) {
      const where = s.folder || folder, title = `${s.file} in ${where}`;
      // Recording can stop early (disk full, camera lost) and still save what it had.
      return error ? { kind: "err", text: `${error.replace(/\.?$/, ".")} What was recorded is saved in ${where}.`, title } : { kind: "saved", text: `Video saved in ${where}`, title };
    }
    if (error) return { kind: "err", text: error, action: "Try again" };
    if (busy) return { kind: "busy", text: "Starting the camera..." };
    return { kind: "idle", text: "Timelapse not running", action: "Start" };
  }

  const api = { MAX_STILLS, MAX_SECONDS, INTERVALS, FPS, SPARE_SECONDS, DEFAULTS, clean, stepInterval, estimate, videoLength, summary, request, friendly, status };
  if (typeof module !== "undefined" && module.exports) module.exports = api; else root.PomodoroTimelapse = api;
})(typeof window !== "undefined" ? window : globalThis);
