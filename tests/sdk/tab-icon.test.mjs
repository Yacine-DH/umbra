import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";

// Loads shared/umbra.js against a stub of the WebView2 bridge and records what the page posts to the host.
function load() {
  const posted = [];
  const el = () => ({ dataset: {}, toggleAttribute() {}, append() {}, remove() {}, addEventListener() {} });
  const window = {
    chrome: { webview: { postMessage: (m) => posted.push(m), addEventListener() {} } },
    matchMedia: () => ({ matches: false, addEventListener() {} }),
    addEventListener() {},
    devicePixelRatio: 1,
  };
  const document = {
    readyState: "complete",
    visibilityState: "visible",
    documentElement: el(),
    head: el(),
    getElementById: () => null,
    createElement: () => el(),
    addEventListener() {},
  };
  window.window = window;
  vm.runInNewContext(readFileSync(new URL("../../shared/umbra.js", import.meta.url), "utf8"), {
    window, document, console, setInterval() {}, setTimeout, Intl, Date, Math,
    HTMLElement: class {}, customElements: { get: () => undefined, define() {} },
  });
  return { umbra: window.umbra, posted };
}

// Messages are cloned through JSON: objects built inside the vm context have another Object prototype.
const tabIcons = (posted) => JSON.parse(JSON.stringify(posted)).filter((m) => m.type === "tabIcon");

test("setTabIcon posts the name", () => {
  const { umbra, posted } = load();
  umbra.setTabIcon("chart-line");
  assert.deepEqual(tabIcons(posted), [{ type: "tabIcon", name: "chart-line" }]);
});

test("setTabIcon with null or a non-string clears the suggestion and sends nothing else", () => {
  const { umbra, posted } = load();
  umbra.setTabIcon(null);
  umbra.setTabIcon(undefined);
  umbra.setTabIcon({ toString: () => "rocket" });
  umbra.setTabIcon(42);
  assert.deepEqual(tabIcons(posted).map((m) => m.name), [null, null, null, null]);
});

const keepAlives = (posted) => JSON.parse(JSON.stringify(posted)).filter((m) => m.type === "keepAlive");

test("keepAlive posts only when the boolean state changes, and defaults to true", () => {
  const { umbra, posted } = load();
  umbra.keepAlive(false); // already false: no message
  umbra.keepAlive();      // defaults to true
  umbra.keepAlive(true);  // unchanged: no message
  umbra.keepAlive(1);     // coerced to true: no message
  umbra.keepAlive(false);
  umbra.keepAlive(0);     // coerced to false: no message
  assert.deepEqual(keepAlives(posted), [
    { type: "keepAlive", on: true },
    { type: "keepAlive", on: false },
  ]);
});

test("ready carries the page's own keepAlive state, so a reloaded page starts from false", () => {
  const { posted } = load();
  const ready = JSON.parse(JSON.stringify(posted)).filter((m) => m.type === "ready");
  assert.deepEqual(ready, [{ type: "ready", keepAlive: false }]);
});
