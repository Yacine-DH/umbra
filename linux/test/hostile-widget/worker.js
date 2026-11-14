// Runs in a dedicated worker: its requests must be blocked too.
onmessage = async (e) => {
  const out = {};
  for (const [name, base] of Object.entries(e.data)) {
    try { await fetch(base + "?v=worker-fetch", { mode: "no-cors" }); out["worker-fetch-" + name] = "loaded"; }
    catch { out["worker-fetch-" + name] = "error"; }
    try { importScripts(base + "?v=worker-import"); out["worker-import-" + name] = "loaded"; }
    catch { out["worker-import-" + name] = "error"; }
  }
  postMessage(out);
};
