// The user's search query: parse, score papers, rank, and find the spans to highlight. Pure functions (no DOM, no umbra),
// so Node can test them (tests/widgets/arxiv-digest.test.mjs).
//
// Query syntax (the "Search" setting): terms separated by commas or new lines.
//   wave function collapse     a phrase (quotes optional; use them only if the phrase contains a comma)
//   "quantum gravity"^3        ^n weighs the term (0.1-10, default 1)
//   -NISQ                      leading minus: hide papers that match
//   author:Penrose             a followed author (strong boost)
//   title:collapse             title only;  abs:collapse  abstract only
//   cat:gr-qc^2                boost papers listed in that category (only papers that match another term, unless the
//                              query is nothing but categories)
// Matching ignores case, accents, hyphens and simple plurals ("wave-function" = "wave function", "qubits" = "qubit").
(function (root) {
  "use strict";

  const WORD = /[\p{L}\p{N}]/u;

  /** Normalised text plus, for every character of it, the index of the source character it came from (for highlights). */
  function normMap(s) {
    s = String(s || "");
    let text = "";
    const map = [];
    let i = 0;
    for (const ch of s) {
      const base = ch.normalize("NFD").replace(/[̀-ͯ]/g, "").toLowerCase();
      for (const b of base) {
        if (WORD.test(b)) { text += b; map.push(i); }
        else if (text && text[text.length - 1] !== " ") { text += " "; map.push(i); }
      }
      i += ch.length;
    }
    if (text.endsWith(" ")) { text = text.slice(0, -1); map.pop(); }
    return { text, map };
  }
  const norm = (s) => normMap(s).text;
  const escRe = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");

  /** Splits on commas, semicolons and new lines outside double quotes. */
  function split(q) {
    const out = [];
    let cur = "", quoted = false;
    for (const ch of String(q || "")) {
      if (ch === '"' || ch === "“" || ch === "”") { quoted = !quoted; cur += '"'; }
      else if (!quoted && (ch === "," || ch === ";" || ch === "\n")) { out.push(cur); cur = ""; }
      else cur += ch;
    }
    out.push(cur);
    return out.map((s) => s.trim()).filter(Boolean);
  }

  const FIELDS = { author: "author", au: "author", title: "title", ti: "title", abs: "abs", abstract: "abs", cat: "cat", category: "cat" };

  function term(raw) {
    let s = raw.trim(), neg = false, weight = 1, field = "any";
    if (s[0] === "-" || s[0] === "!") { neg = true; s = s.slice(1).trim(); }
    const w = /\^\s*(-?\d+(?:[.,]\d+)?)\s*$/.exec(s);
    if (w) { weight = Math.min(10, Math.max(0.1, parseFloat(w[1].replace(",", ".")) || 1)); s = s.slice(0, w.index).trim(); }
    const f = /^([a-z]+)\s*:\s*/i.exec(s);
    if (f && FIELDS[f[1].toLowerCase()]) { field = FIELDS[f[1].toLowerCase()]; s = s.slice(f[0].length); }
    s = s.replace(/"/g, "").trim();
    const text = norm(s);
    if (!text) return null;
    // Each word may take a plural ending; words of 1-2 letters (cs, qc) must match exactly.
    const body = text.split(" ").map((x) => escRe(x) + (x.length > 2 && /\p{L}$/u.test(x) ? "(?:e?s)?" : "")).join(" ");
    return { label: s.replace(/\s+/g, " "), text, field, weight, neg, re: new RegExp(`(?<![^ ])${body}(?![^ ])`, "gu") };
  }

  /** Parses the query setting. Unknown prefixes stay part of the text ("ads:cft" searches for "ads cft"). */
  function parse(q) {
    const terms = split(q).map(term).filter(Boolean);
    // Category terms only boost papers that match something else, unless the query has nothing but categories.
    const textual = terms.some((t) => !t.neg && t.field !== "cat");
    return { terms, positive: terms.some((t) => !t.neg), textual };
  }

  const count = (re, s) => { re.lastIndex = 0; let n = 0; while (re.exec(s)) n++; return n; };
  const has = (re, s) => { re.lastIndex = 0; return re.test(s); };

  /** Normalised fields of a paper, computed once per paper object. */
  const prepped = new WeakMap();
  function prep(p) {
    let n = prepped.get(p);
    if (!n) {
      n = { t: norm(p.title), a: norm(p.abstract), au: (p.authors || []).map(norm), c: (p.cats || []).map(norm) };
      prepped.set(p, n);
    }
    return n;
  }

  // Points per hit, before the term's weight: a title hit counts three times an abstract hit, a followed author more.
  const PTS = { title: 3, abs: 1, absExtra: 0.25, author: 4, anyAuthor: 2, cat: 1 };

  /** Query score of one paper: { base, hits: [{ label, where }], excluded }. */
  function score(p, q) {
    const n = prep(p);
    let base = 0;
    const hits = [];
    for (const t of q.terms) {
      let pts = 0, where = "";
      if (t.field === "author") {
        if (n.au.some((a) => has(t.re, a))) { pts = PTS.author; where = "author"; }
      } else if (t.field === "cat") {
        if (n.c.some((c) => has(t.re, c))) { pts = PTS.cat; where = "category"; }
      } else {
        if (t.field !== "abs" && has(t.re, n.t)) { pts += PTS.title; where = "title"; }
        const k = t.field === "title" ? 0 : count(t.re, n.a);
        if (k) { pts += PTS.abs + PTS.absExtra * (Math.min(k, 3) - 1); where = where || "abstract"; }
        if (!pts && t.field === "any" && n.au.some((a) => has(t.re, a))) { pts = PTS.anyAuthor; where = "author"; }
      }
      if (!pts) continue;
      if (t.neg) return { base: 0, hits: [{ label: t.label, where }], excluded: true };
      base += pts * t.weight;
      hits.push({ label: t.label, where, pts: pts * t.weight });
    }
    if (q.textual && !hits.some((h) => h.where !== "category")) return { base: 0, hits: [], excluded: false };
    hits.sort((a, b) => b.pts - a.pts);
    return { base, hits, excluded: false };
  }

  const TYPE_FACTOR = { new: 1, cross: 0.9, replace: 0.5 };
  const JREF_BONUS = 0.3;       // already accepted in a journal
  const LEARNED_PICK = 2;       // learned boost a paper with no query hit needs to be shown anyway

  /**
   * Ranks papers: query score x announcement type (new > cross-list > replacement), a small bonus for a journal reference,
   * plus the learned boost (`learn(p)` returns { boost, why }). Papers matching an excluded term, and papers with no query
   * hit, are left out, unless the learner is confident about them ("learned pick"). An empty query ranks everything.
   */
  function rank(items, q, learn, skip) {
    const out = [];
    for (const p of items) {
      if (skip && skip(p)) continue;
      const r = score(p, q);
      if (r.excluded) continue;
      const base = q.positive ? r.base : 1;
      const l = learn ? learn(p) : { boost: 0, why: [] };
      const pick = base === 0 && l.boost >= LEARNED_PICK;
      if (base === 0 && !pick) continue;
      const f = TYPE_FACTOR[p.type] ?? 0.5;
      const s = (base > 0 ? base * f + (p.jref ? JREF_BONUS : 0) : 0) + l.boost * f;
      out.push({ p, score: s, hits: r.hits, why: l.why || [], pick });
    }
    return out.sort((a, b) => b.score - a.score || (TYPE_FACTOR[b.p.type] ?? 0) - (TYPE_FACTOR[a.p.type] ?? 0) || (a.p.id < b.p.id ? 1 : -1));
  }

  /** [start, end) ranges in `text` where a non-excluded title/abstract term matches, merged and sorted. */
  function highlights(text, q) {
    const { text: n, map } = normMap(text);
    const raw = String(text || "");
    const spans = [];
    for (const t of q.terms) {
      if (t.neg || t.field === "author" || t.field === "cat") continue;
      t.re.lastIndex = 0;
      let m;
      while ((m = t.re.exec(n))) {
        const a = map[m.index], lastSrc = map[m.index + m[0].length - 1];
        const cp = raw.codePointAt(lastSrc);
        spans.push([a, lastSrc + (cp > 0xffff ? 2 : 1)]);
        if (m[0].length === 0) t.re.lastIndex++;
      }
    }
    spans.sort((x, y) => x[0] - y[0]);
    const merged = [];
    for (const s of spans) {
      const last = merged[merged.length - 1];
      if (last && s[0] <= last[1]) last[1] = Math.max(last[1], s[1]);
      else merged.push([...s]);
    }
    return merged;
  }

  const api = { parse, score, rank, highlights, norm, normMap, split, TYPE_FACTOR, LEARNED_PICK };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  root.ArxivQuery = api;
})(typeof globalThis !== "undefined" ? globalThis : this);
