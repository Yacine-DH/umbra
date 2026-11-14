// Run with: node --test tests/widgets
// Fixtures are cut from real arXiv data: a weekday quant-ph RSS feed (21 Aug 2026), the weekend (empty) feed, and the
// quant-ph "new" listing page (13 Nov 2026) with a few entries per section and its section headers.
import test from "node:test";
import assert from "node:assert/strict";
import { createRequire } from "node:module";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { join } from "node:path";

const require = createRequire(import.meta.url);
const F = require("../../widgets/arxiv-digest/feed.js");
const Q = require("../../widgets/arxiv-digest/query.js");
const L = require("../../widgets/arxiv-digest/learn.js");
const fixture = (name) => readFileSync(join(fileURLToPath(import.meta.url), "..", "fixtures", name), "utf8");

const paper = (id, title, abstract, extra = {}) => ({ id, title, abstract, authors: ["A. Author"], cats: ["quant-ph"], type: "new", jref: false, ...extra });

// ---------- feed.js ----------

test("RSS: papers, types, counts and the announcement date", () => {
  const r = F.parseRss(fixture("arxiv-quant-ph.rss.xml"));
  assert.equal(r.date, "2026-08-21");
  assert.equal(r.items.length, 16);
  assert.deepEqual(r.counts, { new: 6, cross: 3, replace: 7 }); // replace-cross counts as an update
  const p = r.items[0];
  assert.equal(p.id, "2608.19242");
  assert.equal(p.type, "new");
  assert.deepEqual(p.authors, ["Timur F. Kamalov"]);
  assert.deepEqual(p.cats, ["quant-ph"]);
  assert.match(p.abstract, /^A standard Bell observable/);       // "arXiv:... Announce Type: new Abstract:" is cut
  assert.ok(!/Announce Type/.test(p.abstract));
  assert.equal(r.items.filter((x) => x.jref).length, 2);
  assert.ok(r.items.every((x) => /^\d{4}\.\d{4,5}$/.test(x.id)));
});

test("RSS: the weekend feed is empty, not an error; other XML is a format error", () => {
  const r = F.parseRss(fixture("arxiv-empty.rss.xml"));
  assert.equal(r.items.length, 0);
  assert.deepEqual(r.counts, { new: 0, cross: 0, replace: 0 });
  assert.throws(() => F.parseRss("<html><body>Rate limited</body></html>"), (e) => e.format === true);
});

test("listing page: totals come from the section headers, papers from the entries", () => {
  const h = F.parseHtml(fixture("arxiv-quant-ph-new.html"));
  assert.equal(h.date, "2026-11-13");
  assert.deepEqual(h.counts, { new: 120, cross: 42, replace: 122 });
  assert.equal(h.items.length, 12);
  assert.deepEqual(h.items.map((p) => p.type), [...Array(4).fill("new"), ...Array(4).fill("cross"), ...Array(4).fill("replace")]);
  const p = h.items[0];
  assert.equal(p.id, "2611.14583");
  assert.equal(p.title, "Circuit-aware protection against burst errors in lattice surgery");
  assert.deepEqual(p.authors, ["Jaebum Kim"]);
  assert.deepEqual(p.cats, ["quant-ph"]);
  assert.match(p.abstract, /^Large-scale quantum computing/);
  assert.match(p.abstract, /38\^\+6_-5%/);                        // $38^{+6}_{-5}\%$ tidied
  assert.ok(h.items.find((x) => x.type === "cross").cats.length > 1);
  assert.throws(() => F.parseHtml("<html>Not a listing</html>"), (e) => e.format === true);
});

test("past week: daily totals from the day links' offsets, oldest first", () => {
  assert.deepEqual(F.parsePastWeek(fixture("arxiv-quant-ph-pastweek.html")), [
    { d: "2026-11-06", n: 98 }, { d: "2026-11-09", n: 83 }, { d: "2026-11-10", n: 128 }, { d: "2026-11-11", n: 90 }, { d: "2026-11-13", n: 162 },
  ]);
  // 162 is the same new + cross-list total the "new" page gives for 13 Nov
  const h = F.parseHtml(fixture("arxiv-quant-ph-new.html"));
  assert.equal(h.counts.new + h.counts.cross, 162);
  assert.throws(() => F.parsePastWeek("<html></html>"), (e) => e.format === true);
});

test("merge: one entry per paper with its best type; truncated sections still counted", () => {
  const a = { date: "2026-11-13", items: [paper("1", "A", ""), paper("2", "B", "", { type: "cross", cats: ["gr-qc", "quant-ph"] })], counts: { new: 1, cross: 1, replace: 0 } };
  const b = { date: "2026-11-13", items: [paper("2", "B", "", { cats: ["gr-qc"] }), paper("3", "C", "", { type: "replace" })], counts: { new: 1, cross: 0, replace: 5 } };
  const m = F.merge([a, b]);
  assert.equal(m.items.length, 3);
  assert.equal(m.items.find((p) => p.id === "2").type, "new");
  assert.deepEqual(m.counts, { new: 2, cross: 0, replace: 5 }); // 4 replacements past show=500 in b
});

test("TeX in titles and abstracts becomes readable text", () => {
  assert.equal(F.tex("Di\\'osi--Penrose model with $\\hbar\\omega \\ll k_B T$"), "Diósi--Penrose model with ħω ≪ k_B T");
  assert.equal(F.tex("$\\mathcal{O}(N^{2})$ scaling of \\textit{exact} \\ket{\\psi}"), "O(N^2) scaling of exact |ψ⟩");
  assert.equal(F.tex("Schr\\\"odinger cats at 50\\% fidelity"), "Schrödinger cats at 50% fidelity");
});

test("category values are validated before they reach a URL", () => {
  assert.equal(F.cleanCats("quant-ph"), "quant-ph");
  assert.equal(F.cleanCats("cs.AI+cs.LG"), "cs.AI+cs.LG");
  assert.equal(F.cleanCats(" physics.optics, quant-ph "), "physics.optics+quant-ph");
  for (const bad of ["", "../list", "quant-ph?x=1", "a".repeat(40), "cs.AI/new", "https://evil.example"]) assert.equal(F.cleanCats(bad), "", bad);
});

// ---------- query.js ----------

test("query: weights, exclusions, fields, quoted commas", () => {
  const q = Q.parse('quantum gravity^3, -NISQ, author:Diósi, title:collapse, cat:gr-qc^2, "Diósi, Penrose"^0.5, abs:CSL');
  assert.deepEqual(q.terms.map((t) => [t.text, t.field, t.weight, t.neg]), [
    ["quantum gravity", "any", 3, false], ["nisq", "any", 1, true], ["diosi", "author", 1, false],
    ["collapse", "title", 1, false], ["gr qc", "cat", 2, false], ["diosi penrose", "any", 0.5, false], ["csl", "abs", 1, false],
  ]);
  assert.equal(Q.parse("x^99").terms[0].weight, 10);
  assert.equal(Q.parse(" , ,").terms.length, 0);
});

test("query: matching ignores case, accents, hyphens and plurals", () => {
  const q = Q.parse("wave function collapse, Diosi Penrose, qubit");
  const p = paper("1", "Testing Wave-Function Collapses", "The Diósi–Penrose model with two qubits.");
  const r = Q.score(p, q);
  assert.deepEqual(r.hits.map((h) => h.label).sort(), ["Diosi Penrose", "qubit", "wave function collapse"]);
  assert.equal(Q.score(paper("2", "Qubitization", ""), Q.parse("qubit")).base, 0); // whole words only
});

test("score: a title hit counts more than an abstract hit; followed authors count most", () => {
  const q = Q.parse("decoherence");
  const inTitle = Q.score(paper("1", "Gravitational decoherence", "nothing"), q).base;
  const inAbs = Q.score(paper("2", "Something", "We study decoherence."), q).base;
  assert.ok(inTitle > inAbs && inAbs > 0);
  const byAuthor = Q.score(paper("3", "X", "Y", { authors: ["Lajos Diósi"] }), Q.parse("author:Diosi")).base;
  assert.ok(byAuthor > inTitle);
});

test("score: a category term alone never makes a match; an excluded term removes the paper", () => {
  const q = Q.parse("collapse model, cat:gr-qc^2, -spherical collapse");
  const gr = paper("1", "Shear and vorticity", "f(R) gravity", { cats: ["gr-qc"] });
  assert.equal(Q.score(gr, q).base, 0);
  const both = paper("2", "A collapse model", "Tests", { cats: ["gr-qc"] });
  assert.ok(Q.score(both, q).base > Q.score(paper("3", "A collapse model", "Tests"), q).base);
  assert.equal(Q.score(paper("4", "X", "An extended spherical collapse model"), q).excluded, true);
  assert.ok(Q.score(gr, Q.parse("cat:gr-qc")).base > 0); // only categories in the query: they do match
});

test("rank: new beats a replacement with the same hits; empty query ranks everything; dismissed are skipped", () => {
  const items = [paper("1", "Collapse", "", { type: "replace" }), paper("2", "Collapse", ""), paper("3", "Other", ""), paper("4", "Collapse", "", { type: "cross" })];
  const q = Q.parse("collapse");
  assert.deepEqual(Q.rank(items, q).map((r) => r.p.id), ["2", "4", "1"]);
  assert.equal(Q.rank(items, Q.parse("")).length, 4);
  assert.deepEqual(Q.rank(items, q, null, (p) => p.id === "2").map((r) => r.p.id), ["4", "1"]);
});

test("rank: a confident learner can add a paper with no query hit, a weak one can't", () => {
  const items = [paper("1", "Collapse", ""), paper("2", "Other", "")];
  const q = Q.parse("collapse");
  const strong = (p) => (p.id === "2" ? { boost: 2.5, why: ["unruh dewitt"] } : { boost: 0, why: [] });
  const weak = (p) => (p.id === "2" ? { boost: 1, why: [] } : { boost: 0, why: [] });
  const r = Q.rank(items, q, strong);
  assert.equal(r.length, 2);
  assert.equal(r.find((x) => x.p.id === "2").pick, true);
  assert.equal(Q.rank(items, q, weak).length, 1);
});

test("highlights map matches back onto the original text", () => {
  const text = "Phenomenological models of quantum-gravity-induced decoherence (Diósi).";
  const spans = Q.highlights(text, Q.parse("quantum gravity, decoherence, diosi, -nothing"));
  assert.deepEqual(spans.map(([a, b]) => text.slice(a, b)), ["quantum-gravity", "decoherence", "Diósi"]);
  assert.deepEqual(Q.highlights("Gamma 𝔊 qubits", Q.parse("qubit")).length, 1);
});

// ---------- learn.js ----------

const grav = [
  paper("g1", "Gravitationally induced entanglement between masses", "We propose a tabletop test of gravity mediated entanglement with levitated masses."),
  paper("g2", "Testing the quantum nature of gravity", "Gravity mediated entanglement of two mesoscopic masses as a witness of quantum gravity."),
  paper("g3", "Entanglement through gravity in optomechanics", "Levitated masses and gravity mediated entanglement in an optomechanical setup."),
  paper("g4", "Decoherence limits on gravitational entanglement", "Gravity mediated entanglement survives environmental decoherence for levitated masses."),
  paper("g5", "Schrodinger-Newton dynamics", "Semiclassical gravity and levitated masses."),
];
const other = [
  paper("o1", "Surface code decoders on FPGAs", "Real-time decoding of the surface code with low latency hardware."),
  paper("o2", "Variational eigensolvers for chemistry", "A variational quantum eigensolver for molecular ground states."),
  paper("o3", "Photonic boson sampling at scale", "Boson sampling with a large photonic interferometer."),
];
const fresh = paper("n1", "Witnessing gravity mediated entanglement with levitated masses", "A new protocol for gravity mediated entanglement.");
const offTopic = paper("n2", "Faster surface code decoding", "Low latency decoding of the surface code on hardware.");

function taught() {
  const all = [...grav, ...other, fresh, offTopic];
  const rare = L.rarity(all);
  let m = L.empty();
  const t = Date.parse("2026-11-14T12:00:00Z");
  for (const p of grav.slice(0, 4)) m = L.update(m, p, 2, rare, t);
  m = L.update(m, grav[4], 1, rare, t);
  for (const p of other) m = L.update(m, p, -1, rare, t);
  return { m, rare, t };
}

test("learner: silent until a few signals, then papers like the liked ones rank up, hidden-like ones down", () => {
  const rare = L.rarity([...grav, fresh]);
  let m = L.empty();
  for (const p of grav.slice(0, L.MIN_SIGNALS - 1)) m = L.update(m, p, 2, rare, 0);
  assert.equal(L.boost(m, fresh, rare).boost, 0);
  const { m: full, rare: r2 } = taught();
  const up = L.boost(full, fresh, r2), down = L.boost(full, offTopic, r2);
  assert.ok(up.boost > 1, `boost ${up.boost}`);
  assert.ok(down.boost < 0, `boost ${down.boost}`);
  assert.ok(up.boost <= L.CAP);
  assert.ok(up.why.length > 0 && up.why.every((w) => /gravity|entanglement|levitated|mass/.test(w)), up.why.join(", "));
});

test("learner: undo takes a signal back; old signals fade; the model survives a JSON round trip", () => {
  const { m, rare, t } = taught();
  const before = L.boost(m, fresh, rare).boost;
  const copy = JSON.parse(JSON.stringify(m));
  assert.equal(L.boost(copy, fresh, rare).boost, before);
  const n = copy.n;
  L.update(copy, grav[0], 2, rare, t, true);                     // unsave
  assert.equal(copy.n, n - 1);
  assert.ok(L.boost(copy, fresh, rare).boost < before);
  const later = JSON.parse(JSON.stringify(m));
  L.update(later, offTopic, -1, rare, t + 60 * 86400000);        // two half-lives later
  assert.ok(L.boost(later, fresh, rare).boost < before);
});

test("learner: the model stays small", () => {
  const rare = () => 1;
  let m = L.empty();
  for (let i = 0; i < 60; i++) {
    const words = Array.from({ length: 120 }, (_, k) => `w${i}x${k}`).join(" ");
    m = L.update(m, paper(String(i), `title ${i}`, words), i % 3 ? 2 : -1, rare, i * 1000);
  }
  assert.ok(Object.keys(m.w).length <= 1500);
  assert.ok(JSON.stringify(m).length < 150000, `${JSON.stringify(m).length} bytes`);
});
