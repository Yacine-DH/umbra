// arXiv listing parsers. Pure functions (no DOM, no umbra), so Node can test them (tests/widgets/arxiv-digest.test.mjs).
//
// Sources (the first two describe the same daily announcement):
//   RSS   https://rss.arxiv.org/rss/<cat>[+<cat>...]   one <item> per paper, empty on weekends and holidays (<skipDays>)
//   HTML  https://arxiv.org/list/<cat>/new?show=500    always shows the last announcement; used when the RSS is empty
//                                                       or too large, and nothing is cached yet
//   HTML  https://arxiv.org/list/<cat>/pastweek?show=25  once per single category: the last five daily totals (sparkline)
// RSS and "new" both become { date: "YYYY-MM-DD", items: [Paper], counts: { new, cross, replace } } where
//   Paper = { id, title, authors: [..], abstract, cats: [..], type: "new" | "cross" | "replace", jref: bool }
(function (root) {
  "use strict";

  const ENT = { amp: "&", lt: "<", gt: ">", quot: '"', apos: "'", nbsp: " " };
  const decode = (s) => s.replace(/&(#x[0-9a-f]+|#\d+|[a-z]+);/gi, (m, e) => {
    if (e[0] === "#") {
      const n = e[1] === "x" || e[1] === "X" ? parseInt(e.slice(2), 16) : parseInt(e.slice(1), 10);
      return Number.isFinite(n) && n > 0 && n < 0x110000 ? String.fromCodePoint(n) : m;
    }
    return ENT[e.toLowerCase()] ?? m;
  });
  const clean = (s) => decode(String(s || "").replace(/<!\[CDATA\[([\s\S]*?)\]\]>/g, "$1").replace(/<[^>]*>/g, " "))
    .replace(/\s+/g, " ").trim();
  const esc = (s) => s.replace(/[.*+?^${}()|[\]\\:]/g, "\\$&");
  const tagRe = (name, flags) => new RegExp(`<${esc(name)}(?:\\s[^>]*)?>([\\s\\S]*?)</${esc(name)}>`, flags);
  const tag = (xml, name) => { const m = tagRe(name, "i").exec(xml); return m ? m[1] : ""; };
  const tags = (xml, name) => [...xml.matchAll(tagRe(name, "gi"))].map((m) => m[1]);

  // ---------- light LaTeX tidy for display: arXiv titles and abstracts are raw TeX ----------
  const SYM = {
    alpha: "α", beta: "β", gamma: "γ", delta: "δ", epsilon: "ε", varepsilon: "ε", zeta: "ζ", eta: "η", theta: "θ",
    vartheta: "θ", kappa: "κ", lambda: "λ", mu: "μ", nu: "ν", xi: "ξ", pi: "π", rho: "ρ", sigma: "σ", tau: "τ",
    phi: "φ", varphi: "φ", chi: "χ", psi: "ψ", omega: "ω", Gamma: "Γ", Delta: "Δ", Theta: "Θ", Lambda: "Λ", Xi: "Ξ",
    Pi: "Π", Sigma: "Σ", Phi: "Φ", Psi: "Ψ", Omega: "Ω", hbar: "ħ", infty: "∞", sim: "∼", approx: "≈", simeq: "≃",
    le: "≤", leq: "≤", ge: "≥", geq: "≥", neq: "≠", ne: "≠", times: "×", pm: "±", mp: "∓", to: "→", rightarrow: "→",
    leftarrow: "←", leftrightarrow: "↔", Rightarrow: "⇒", langle: "⟨", rangle: "⟩", ell: "ℓ", partial: "∂",
    nabla: "∇", cdot: "·", cdots: "⋯", ldots: "…", dots: "…", dagger: "†", otimes: "⊗", oplus: "⊕", propto: "∝",
    sqrt: "√", sum: "Σ", int: "∫", in: "∈", ll: "≪", gg: "≫", equiv: "≡", lesssim: "≲", gtrsim: "≳",
    mathbb: "", mathcal: "", ket: "", bra: "",
  };
  const ACCENT = { "'": "́", '"': "̈", "`": "̀", "^": "̂", "~": "̃", c: "̧", v: "̌", u: "̆", "=": "̄", H: "̋" };

  /** Turns common TeX in titles and abstracts into readable Unicode. Unknown commands keep their name. */
  function tex(s) {
    return String(s || "")
      .replace(/\{\\([`'"^~=])\s*\{?([A-Za-z])\}?\}|\\([`'"^~=])\s*\{?([A-Za-z])\}?|\\([cvuH])\{([A-Za-z])\}/g,
        (m, a1, l1, a2, l2, a3, l3) => ((l1 || l2 || l3) + ACCENT[a1 || a2 || a3]).normalize("NFC"))
      .replace(/\\(?:mathrm|mathbf|mathit|mathsf|mathcal|mathbb|mathfrak|textrm|textit|textbf|textsc|text|emph|operatorname|rm|it|bf)\s*\{([^{}]*)\}/g, "$1")
      .replace(/\\(?:ket)\{([^{}]*)\}/g, "|$1⟩").replace(/\\(?:bra)\{([^{}]*)\}/g, "⟨$1|")
      .replace(/\\([%&_#$])/g, "$1")
      .replace(/\\([A-Za-z]+)/g, (m, c) => (c in SYM ? SYM[c] : c))
      .replace(/\$\$?/g, "")
      .replace(/([_^])\{([^{}]*)\}/g, "$1$2")
      .replace(/\\[,;:! ]/g, " ")
      .replace(/~/g, " ")
      .replace(/\s+/g, " ").trim();
  }

  // ---------- dates ----------
  const MONTHS = { jan: 1, feb: 2, mar: 3, apr: 4, may: 5, jun: 6, jul: 7, aug: 8, sep: 9, oct: 10, nov: 11, dec: 12 };
  const pad = (n) => String(n).padStart(2, "0");
  /** "Fri, 21 Aug 2026 00:00:00 -0400" or "Friday, 13 November 2026" to "2026-08-21". The written date, no time zone shift. */
  function dayOf(s) {
    const m = /(\d{1,2})\s+([A-Za-z]{3})[A-Za-z]*\.?\s+(\d{4})/.exec(String(s || ""));
    const mo = m && MONTHS[m[2].toLowerCase()];
    return mo ? `${m[3]}-${pad(mo)}-${pad(+m[1])}` : "";
  }

  const ID_RE = /(\d{4}\.\d{4,5})(?:v\d+)?/;
  const TYPE_RANK = { new: 0, cross: 1, replace: 2 };
  const typeOf = (s) => {
    const t = String(s || "").toLowerCase();
    return t.startsWith("replace") || t.startsWith("repl") ? "replace" : t.startsWith("cross") ? "cross" : t === "new" || t.startsWith("new") ? "new" : "";
  };
  const splitAuthors = (s) => clean(s).split(/\s*,\s*|\s+and\s+/).map((a) => a.trim()).filter(Boolean);
  const emptyCounts = () => ({ new: 0, cross: 0, replace: 0 });
  const countItems = (items) => items.reduce((c, p) => (c[p.type]++, c), emptyCounts());

  // ---------- RSS ----------
  /** Parses an arXiv RSS feed. An empty feed (weekend, holiday) returns no items, not an error. */
  function parseRss(xml) {
    xml = String(xml || "");
    if (!/<rss[\s>]/i.test(xml) || !/<channel[\s>]/i.test(xml)) throw formatError("not an arXiv RSS feed");
    const head = tag(xml, "channel").replace(/<item[\s>][\s\S]*<\/item>/i, "");
    const items = [];
    let date = "";
    for (const it of tags(xml, "item")) {
      const idm = ID_RE.exec(clean(tag(it, "link"))) || ID_RE.exec(clean(tag(it, "guid")));
      if (!idm) continue;
      const desc = clean(tag(it, "description"));
      const ab = desc.search(/Abstract:/i);
      const type = typeOf(clean(tag(it, "arxiv:announce_type"))) || typeOf((/Announce Type:\s*([a-z-]+)/i.exec(desc) || [])[1]);
      if (!type) continue;
      items.push({
        id: idm[1],
        title: tex(clean(tag(it, "title"))),
        authors: splitAuthors(tag(it, "dc:creator")),
        abstract: tex(ab >= 0 ? desc.slice(ab + 9) : desc),
        cats: tags(it, "category").map(clean).filter(Boolean),
        type,
        jref: !!clean(tag(it, "arxiv:journal_reference")),
      });
      if (!date) date = dayOf(tag(it, "pubDate"));
    }
    return finish(items, date || dayOf(tag(head, "pubDate")), null);
  }

  // ---------- HTML /list/<cat>/new ----------
  /** Parses an arXiv "new" listing page. Section totals come from the headers, so a page cut at show=500 still counts right. */
  function parseHtml(html) {
    html = String(html || "");
    const start = html.search(/<dl id=['"]articles['"]/);
    if (start < 0) throw formatError("not an arXiv listing page");
    const date = dayOf((/Showing new listings for ([^<]+)/i.exec(html) || [])[1]);
    const sections = [...html.matchAll(/<h3>\s*([^<(]+?)\s*\(showing\s+(?:first\s+)?\d+\s+of\s+(\d+)\s+entries\s*\)/gi)]
      .map((m) => ({ at: m.index, type: typeOf(m[1]), total: +m[2] }))
      .filter((s) => s.type);
    const totals = emptyCounts();
    for (const s of sections) totals[s.type] += s.total;

    const items = [];
    for (const m of html.slice(start).matchAll(/<dt>([\s\S]*?)<\/dt>\s*<dd>([\s\S]*?)<\/dd>/g)) {
      const at = start + m.index;
      const sec = sections.filter((s) => s.at < at).pop();
      if (!sec) continue;
      const idm = /id="(\d{4}\.\d{4,5})"/.exec(m[1]) || /\/abs\/(\d{4}\.\d{4,5})/.exec(m[1]);
      if (!idm) continue;
      const dd = m[2];
      const title = clean((/<div class=['"]list-title[^'"]*['"][^>]*>([\s\S]*?)<\/div>/.exec(dd) || [])[1]).replace(/^Title:\s*/i, "");
      const authorsHtml = (/<div class=['"]list-authors['"][^>]*>([\s\S]*?)<\/div>/.exec(dd) || [])[1] || "";
      const subjects = clean((/<div class=['"]list-subjects['"][^>]*>([\s\S]*?)<\/div>/.exec(dd) || [])[1]);
      items.push({
        id: idm[1],
        title: tex(title),
        authors: tags(authorsHtml, "a").map(clean).filter(Boolean),
        abstract: tex(clean((/<p class=['"]mathjax['"]>([\s\S]*?)<\/p>/.exec(dd) || [])[1])),
        cats: [...subjects.matchAll(/\(([a-z-]+(?:\.[A-Za-z-]+)?)\)/g)].map((c) => c[1]),
        type: sec.type,
        jref: /class=['"]list-journal-ref['"]/.test(dd),
      });
    }
    return finish(items, date, sections.length ? totals : null);
  }

  /** De-duplicates by id (a paper in two merged categories keeps its best type) and fills in counts. */
  function finish(items, date, totals) {
    const byId = new Map();
    for (const p of items) {
      const had = byId.get(p.id);
      if (!had) { byId.set(p.id, p); continue; }
      if (TYPE_RANK[p.type] < TYPE_RANK[had.type]) had.type = p.type;
      for (const c of p.cats) if (!had.cats.includes(c)) had.cats.push(c);
      had.jref = had.jref || p.jref;
    }
    const list = [...byId.values()];
    const counts = countItems(list);
    if (totals) for (const k of Object.keys(counts)) counts[k] = Math.max(counts[k], totals[k]);
    return { date, items: list, counts };
  }

  /** Merges listings of several categories (the HTML fallback fetches one page per category). */
  function merge(listings) {
    const all = [], extra = emptyCounts();
    let date = "";
    for (const l of listings) {
      all.push(...l.items.map((p) => ({ ...p, cats: [...p.cats] })));
      const own = countItems(l.items);
      for (const k of Object.keys(extra)) extra[k] += Math.max(0, l.counts[k] - own[k]); // entries past show=500
      if (l.date > date) date = l.date;
    }
    const out = finish(all, date, null);
    for (const k of Object.keys(extra)) out.counts[k] += extra[k];
    return out;
  }

  /**
   * Daily totals (new + cross-lists) of the last five announcements, from https://arxiv.org/list/<cat>/pastweek?show=25:
   * the page links each day at its offset into the week ("recent?skip=162" for the second day), so a day's total is the
   * next day's offset minus its own, and the last day runs to "Total of N entries". Oldest first: [{ d, n }].
   */
  function parsePastWeek(html) {
    html = String(html || "");
    const days = [...html.matchAll(/href="\/list\/[^"?]+\/recent\?skip=(\d+)[^"]*"[^>]*>\s*([^<]+?)\s*<\/a>/g)]
      .map((m) => ({ skip: +m[1], d: dayOf(m[2]) }))
      .filter((x) => x.d);
    const total = +((/Total of (\d+) entries/.exec(html) || [])[1] || 0);
    if (!days.length || !total) throw formatError("not an arXiv past-week page");
    days.sort((a, b) => a.skip - b.skip);
    const out = days.map((x, i) => ({ d: x.d, n: (i + 1 < days.length ? days[i + 1].skip : total) - x.skip }));
    return out.filter((x) => x.n >= 0).sort((a, b) => (a.d < b.d ? -1 : 1));
  }

  function formatError(msg) { const e = new Error(msg); e.format = true; return e; }

  /** Category value to a safe RSS/listing path: "quant-ph", "cs.AI+cs.LG". Empty for anything else. */
  function cleanCats(s) {
    const parts = String(s || "").split(/[\s,+]+/).map((p) => p.trim()).filter(Boolean);
    if (!parts.length || parts.length > 6 || !parts.every((p) => /^[a-z][a-z-]{1,15}(\.[A-Za-z][A-Za-z-]{0,15})?$/i.test(p))) return "";
    return [...new Set(parts)].join("+");
  }

  const api = { parseRss, parseHtml, parsePastWeek, merge, tex, dayOf, cleanCats, clean };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  root.ArxivFeed = api;
})(typeof globalThis !== "undefined" ? globalThis : this);
