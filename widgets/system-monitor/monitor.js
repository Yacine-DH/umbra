// System Monitor: CPU, RAM, GPU, disk and network from umbra.system(). Everything is local (no network calls).
// Samples on its own timer (the Update every option), slowed 4x under Energy Saver, stopped while the PC is locked
// or the widget is hidden, as the SDK's design rules ask.
const $ = (id) => document.getElementById(id);
const HISTORY = 60;
const hist = { cpu: [], gpu: [] };
let timer = null, first = true, netPeak = 1, failures = 0;

const opts = () => umbra.options;

// ---------- formatting ----------

function bytes(n) {
  if (n == null) return "–";
  const u = ["B", "KB", "MB", "GB", "TB"];
  let i = 0;
  while (n >= 1000 && i < u.length - 1) { n /= 1024; i++; }
  return `${n >= 100 || i === 0 ? Math.round(n) : n.toFixed(1)} ${u[i]}`;
}

/** A transfer rate in bits (Mb/s, like internet plans) or bytes (MB/s, like file copies), by the Units option. */
function rate(bps) {
  if (bps == null) return "–";
  if (opts().units === "bytes") return `${bytes(bps)}/s`;
  let v = bps * 8;
  const u = ["b/s", "Kb/s", "Mb/s", "Gb/s"];
  let i = 0;
  while (v >= 1000 && i < u.length - 1) { v /= 1000; i++; }
  return `${v >= 100 || i === 0 ? Math.round(v) : v.toFixed(1)} ${u[i]}`;
}

const level = (p) => (p >= 90 ? "bad" : p >= 70 ? "warn" : "");

function setMeter(id, pct) {
  const m = $(id);
  const p = Math.max(0, Math.min(100, pct || 0));
  m.style.width = `${p}%`;
  m.className = level(p);
}

function setValue(id, pct) {
  const el = $(id);
  if (pct == null) { el.textContent = "n/a"; el.classList.add("na"); return; }
  el.classList.remove("na");
  el.innerHTML = `${Math.round(pct)}<small>%</small>`;
}

function spark(id, values) {
  const svg = $(id);
  if (!svg || values.length < 2) { if (svg) svg.innerHTML = ""; return; }
  const step = 60 / (HISTORY - 1), start = (HISTORY - values.length) * step;
  const pts = values.map((v, i) => `${(start + i * step).toFixed(2)},${(100 - Math.max(0, Math.min(100, v))).toFixed(1)}`);
  svg.innerHTML = `<path class="fill" d="M${start.toFixed(2)},100 L${pts.join(" L")} L60,100 Z"/><path d="M${pts.join(" L")}"/>`;
}

function push(list, v) { if (v == null) return; list.push(v); if (list.length > HISTORY) list.shift(); }

// ---------- rendering ----------

function render(s) {
  const o = opts();
  $("title").textContent = String(o.title || "System");
  $("gpu").hidden = !o.showGpu;
  $("disk").hidden = !o.showDisk;
  $("net").hidden = !o.showNetwork;

  if (s.cpu) {
    push(hist.cpu, s.cpu.percent);
    setValue("cpu-v", s.cpu.percent); setMeter("cpu-m", s.cpu.percent);
    $("cpu-d").textContent = `${navigator.hardwareConcurrency || "?"} threads`;
    spark("cpu-s", o.graphs ? hist.cpu : []);
  }
  if (s.memory) {
    setValue("ram-v", s.memory.percent); setMeter("ram-m", s.memory.percent);
    $("ram-d").textContent = `${bytes(s.memory.usedBytes)} of ${bytes(s.memory.totalBytes)}`;
  }
  if (s.gpu) {
    push(hist.gpu, s.gpu.percent);
    setValue("gpu-v", s.gpu.percent); setMeter("gpu-m", s.gpu.percent);
    $("gpu-d").textContent = s.gpu.percent == null ? "No GPU counters on this PC"
      : s.gpu.memoryUsedBytes ? `${bytes(s.gpu.memoryUsedBytes)} GPU memory` : " ";
    spark("gpu-s", o.graphs ? hist.gpu : []);
  }
  if (s.disk) {
    setValue("disk-v", s.disk.activePercent); setMeter("disk-m", s.disk.activePercent);
    $("disk-d").textContent = `Read ${bytes(s.disk.readBytesPerSec ?? 0)}/s · Write ${bytes(s.disk.writeBytesPerSec ?? 0)}/s`;
    renderDrives(s.disk.drives || []);
  } else renderDrives([]);
  if (s.network) {
    const down = s.network.downBytesPerSec || 0, up = s.network.upBytesPerSec || 0;
    netPeak = Math.max(netPeak * 0.995, down + up, 1); // a slowly fading peak, so the bar stays meaningful
    $("net-down").textContent = rate(down);
    $("net-up").textContent = rate(up);
    setMeter("net-m", (100 * (down + up)) / netPeak);
    $("net-m").className = "";
  }
  $("stamp").textContent = new Date().toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
}

function renderDrives(drives) {
  const box = $("drives");
  const show = opts().showDrives && opts().showDisk && drives.length > 0;
  box.hidden = !show;
  if (!show) return;
  const max = 3, shown = drives.slice(0, max);
  box.innerHTML = shown.map((d) => `<div class="drive num"><b>${d.name}</b><div class="meter"><i class="${level(d.usedPercent)}" style="width:${d.usedPercent}%"></i></div><span>${bytes(d.freeBytes)} free</span></div>`).join("")
    + (drives.length > max ? `<div class="more">+${drives.length - max} more drive${drives.length - max > 1 ? "s" : ""}</div>` : "");
}

function showError(err) {
  $("rows").hidden = true; $("drives").hidden = true;
  $("empty").hidden = false;
  $("why").textContent = /permissions|unknown/i.test(String(err && err.message)) ? "Update Umbra to the latest version." : "It will try again in a moment.";
}

// ---------- sampling ----------

async function sample() {
  try {
    let s = await umbra.system();
    // The host's GPU and disk counters need two samples, so the very first read has no percent yet: read again
    // shortly instead of showing "n/a" until the next tick.
    if (first && ((s.gpu && s.gpu.percent == null) || (s.disk && s.disk.activePercent == null))) {
      await new Promise((r) => setTimeout(r, 700));
      s = await umbra.system();
    }
    failures = 0;
    $("rows").hidden = false; $("empty").hidden = true;
    render(s);
  } catch (err) {
    if (++failures >= 3) showError(err);
  }
  if (first) { first = false; requestAnimationFrame(() => umbra.rendered()); }
}

/** (Re)start the timer: the Update every option, 4x slower under Energy Saver, nothing while paused or hidden. */
function schedule() {
  clearInterval(timer); timer = null;
  if (umbra.settings.paused || document.hidden) return;
  const secs = Math.max(1, Number(opts().interval) || 2) * (umbra.settings.energySaver ? 4 : 1);
  timer = setInterval(sample, secs * 1000);
}

(async () => {
  await umbra.ready;
  await sample();
  schedule();
  umbra.onSettings(() => { schedule(); sample(); });
  document.addEventListener("visibilitychange", () => { schedule(); if (!document.hidden) sample(); });
})();
