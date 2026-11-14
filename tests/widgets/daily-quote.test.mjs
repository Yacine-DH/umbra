import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";

// quotes.js and words.js are plain scripts that declare globals (QUOTES, WIKI, WORDS); run them in one context.
const dir = new URL("../../widgets/daily-quote/", import.meta.url);
const ctx = vm.createContext({});
for (const f of ["quotes.js", "words.js"]) vm.runInContext(readFileSync(new URL(f, dir), "utf8"), ctx, { filename: f });
const { QUOTES, WIKI, WORDS } = vm.runInContext("({ QUOTES, WIKI, WORDS })", ctx);

const TOPICS = ["discipline", "calm", "courage", "focus", "growth", "kindness", "resilience", "wisdom"];
const LEVELS = ["everyday", "advanced", "rare"];
const filled = s => typeof s === "string" && s.trim() === s && s.length > 0;

test("quotes: at least a year of them, each with text, author and a known topic", () => {
  assert.ok(QUOTES.length >= 365, `only ${QUOTES.length} quotes`);
  for (const q of QUOTES) {
    assert.equal(q.length, 3, JSON.stringify(q));
    assert.ok(filled(q[0]) && filled(q[1]), JSON.stringify(q));
    assert.ok(TOPICS.includes(q[2]), `unknown topic in ${JSON.stringify(q)}`);
  }
});

test("quotes: no duplicates, and every topic has enough for its own switch", () => {
  const seen = new Set();
  for (const [text] of QUOTES) {
    const k = text.toLowerCase();
    assert.ok(!seen.has(k), `duplicate quote: ${text}`);
    seen.add(k);
  }
  for (const t of TOPICS) {
    const n = QUOTES.filter(q => q[2] === t).length;
    assert.ok(n >= 30, `topic ${t} has only ${n} quotes`);
  }
});

test("WIKI only maps authors that have quotes, to plain article titles (null: no link)", () => {
  const authors = new Set(QUOTES.map(q => q[1]));
  for (const [author, title] of Object.entries(WIKI)) {
    assert.ok(authors.has(author), `WIKI entry for an author with no quote: ${author}`);
    if (title === null) continue;
    assert.ok(filled(title) && !/[/?#]/.test(title), `odd article title for ${author}: ${title}`);
  }
});

test("words: three levels, complete entries, no word in two places", () => {
  assert.deepEqual(Object.keys(WORDS).sort(), [...LEVELS].sort());
  const seen = new Map();
  for (const level of LEVELS) {
    assert.ok(WORDS[level].length >= 300, `${level} has only ${WORDS[level].length} words`);
    for (const w of WORDS[level]) {
      assert.equal(w.length, 5, JSON.stringify(w));
      assert.ok(w.every(filled), JSON.stringify(w));
      const k = w[0].toLowerCase();
      assert.ok(!seen.has(k), `${w[0]} is in ${seen.get(k)} and ${level}`);
      seen.set(k, level);
    }
  }
});
