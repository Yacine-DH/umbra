// Network Monitor: live download/upload speed (umbra.system, local) plus ping, public IP and country from Cloudflare's
// trace page and IPv4 echo (umbra.fetchText, on the user's refresh interval). The speeds are what this PC is using right now, not a
// speed test: nothing is downloaded to measure them.
const $ = (id) => document.getElementById(id);
const TRACE = "https://www.cloudflare.com/cdn-cgi/trace";
const IPV4 = "https://ipv4.icanhazip.com/"; // Cloudflare's IPv4-only address echo: the short, familiar form of the IP
const HISTORY = 60;
const down = [], up = [];
let peakDown = 0, peakUp = 0, timer = null, first = true, lastPing = 0, ip = "", ipv6 = "";

const opts = () => umbra.options;

// ---------- formatting ----------

/** A rate in bits (Mb/s, like internet plans) or bytes (MB/s), by the Units option. Returns [number, unit]. */
function rate(bps) {
  if (opts().units === "bytes") {
    const u = ["B/s", "KB/s", "MB/s", "GB/s"];
    let v = bps, i = 0;
    while (v >= 1000 && i < u.length - 1) { v /= 1024; i++; }
    return [v >= 100 || i === 0 ? String(Math.round(v)) : v.toFixed(1), u[i]];
  }
  const u = ["b/s", "Kb/s", "Mb/s", "Gb/s"];
  let v = bps * 8, i = 0;
  while (v >= 1000 && i < u.length - 1) { v /= 1000; i++; }
  return [v >= 100 || i === 0 ? String(Math.round(v)) : v.toFixed(1), u[i]];
}
const rateHtml = (bps) => { const [n, u] = rate(bps); return `${n}<small>${u}</small>`; };
const rateText = (bps) => rate(bps).join(" ");

// ---------- speeds (local, own timer) ----------

function chart() {
  const svg = $("chart");
  const max = Math.max(1, ...down, ...up) * 1.15;
  const line = (list) => {
    const step = 100 / (HISTORY - 1), start = (HISTORY - list.length) * step;
    return list.map((v, i) => `${(start + i * step).toFixed(2)},${(100 - (v / max) * 100).toFixed(1)}`);
  };
  if (down.length < 2) { svg.innerHTML = ""; return; }
  const d = line(down), u = line(up), x0 = d[0].split(",")[0];
  svg.innerHTML = `<path class="down-fill" d="M${x0},100 L${d.join(" L")} L100,100 Z"/>`
    + `<path class="down" d="M${d.join(" L")}"/>`
    + (opts().showUpload ? `<path class="up" d="M${u.join(" L")}"/>` : "");
}

async function sampleSpeeds() {
  try {
    const n = (await umbra.system()).network || {};
    const dn = n.downBytesPerSec || 0, upn = n.upBytesPerSec || 0;
    down.push(dn); up.push(upn);
    if (down.length > HISTORY) { down.shift(); up.shift(); }
    peakDown = Math.max(peakDown, dn); peakUp = Math.max(peakUp, upn);
    $("down").innerHTML = rateHtml(dn);
    $("up").innerHTML = rateHtml(upn);
    $("down-p").textContent = `peak ${rateText(peakDown)}`;
    $("up-p").textContent = `peak ${rateText(peakUp)}`;
    chart();
  } catch { /* try again next tick */ }
  if (first) { first = false; requestAnimationFrame(() => umbra.rendered()); }
}

function schedule() {
  clearInterval(timer); timer = null;
  if (umbra.settings.paused || document.hidden) return;
  const secs = Math.max(1, Number(opts().interval) || 2) * (umbra.settings.energySaver ? 4 : 1);
  timer = setInterval(sampleSpeeds, secs * 1000);
}

// ---------- ping, IP and country (network, on umbra.every) ----------

function parseTrace(text) {
  const o = {};
  for (const line of String(text).split("\n")) {
    const i = line.indexOf("=");
    if (i > 0) o[line.slice(0, i).trim()] = line.slice(i + 1).trim();
  }
  return o;
}

const quality = (ms) => (ms < 40 ? ["Great", "good"] : ms < 100 ? ["Good", "good"] : ms < 200 ? ["Fair", "warn"] : ["Slow", "bad"]);

function countryName(code) {
  if (!code || code === "XX") return "–";
  try { return new Intl.DisplayNames([navigator.language || "en"], { type: "region" }).of(code) || code; } catch { return code; }
}

function setState(online) {
  $("state").className = `state ${online ? "on" : "off"}`;
  $("state-t").textContent = online ? "Online" : "Offline";
}

/** Three requests to Cloudflare's nearest server; the fastest is the ping (the first one also pays for the connection). */
async function checkNetwork() {
  lastPing = Date.now();
  let best = Infinity, trace = null;
  for (let i = 0; i < 3; i++) {
    const t0 = performance.now();
    try {
      const text = await umbra.fetchText(`${TRACE}?t=${Date.now()}`);
      best = Math.min(best, performance.now() - t0);
      trace = trace || parseTrace(text);
    } catch { /* counted below */ }
  }
  if (!trace) {
    setState(false);
    $("ping").innerHTML = '<span class="dim">–</span>';
    $("ip").classList.add("dim");
    return;
  }
  setState(true);
  const ms = Math.round(best), [word, cls] = quality(ms);
  $("ping").innerHTML = `${ms} ms<small class="${cls}">${word}</small>`;
  // The trace answers over IPv6 when the PC has it; ask the IPv4-only echo for the short form, keep IPv6 if there is none.
  let v4 = "";
  try { v4 = String(await umbra.fetchText(`${IPV4}?t=${Date.now()}`)).trim(); } catch { /* IPv6-only network */ }
  ip = /^\d{1,3}(\.\d{1,3}){3}$/.test(v4) ? v4 : trace.ip || "";
  ipv6 = v4 && trace.ip && trace.ip.includes(":") ? trace.ip : "";
  renderIp();
  $("loc").textContent = countryName(trace.loc);
  $("loc").title = trace.loc || "";
}

function renderIp() {
  const el = $("ip");
  el.classList.remove("dim");
  if (!opts().showIp) { el.textContent = "Hidden"; el.classList.add("dim"); $("ip-btn").title = "Turn on Show public IP in the options"; return; }
  el.textContent = ip || "–";
  el.style.fontSize = ip.length > 15 ? "11px" : ""; // an IPv6 address only fits smaller
  el.title = ipv6 ? `${ip}\nIPv6: ${ipv6}` : ip;
  $("ip-btn").title = "Click to copy";
}

async function copyIp() {
  if (!ip || !opts().showIp) return;
  const k = $("ip-k");
  try { await navigator.clipboard.writeText(ip); k.textContent = "Copied"; } catch { k.textContent = "Couldn't copy"; }
  setTimeout(() => { k.textContent = "Public IP"; }, 1400);
}

// ---------- start ----------

$("ping-btn").addEventListener("click", () => { if (Date.now() - lastPing > 5000) checkNetwork(); });
$("ip-btn").addEventListener("click", copyIp);

(async () => {
  await umbra.ready;
  $("title").textContent = String(opts().title || "Network");
  await sampleSpeeds();
  schedule();
  umbra.every(checkNetwork); // ping and IP on the user's refresh interval, like any network call
  umbra.onSettings(() => {
    $("title").textContent = String(opts().title || "Network");
    renderIp(); chart(); schedule(); sampleSpeeds();
  });
  document.addEventListener("visibilitychange", () => { schedule(); if (!document.hidden) sampleSpeeds(); });
})();
