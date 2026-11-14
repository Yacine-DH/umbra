// A small, local learner: it nudges the ranking towards papers like the ones you save and open, and away from the ones you
// hide. Pure functions (no DOM, no umbra), so Node can test them (tests/widgets/arxiv-digest.test.mjs). Nothing leaves the PC.
//
// Features of a paper: words and word pairs of its title and abstract (stopwords dropped, plurals folded) and its authors'
// surnames. Each signal (save +2, open +1, hide -1; undoing one applies the opposite) adds signal x rate x rarity to the
// paper's features, spread over them so long abstracts don't count more. Rarity is measured on the current day's listing,
// so words in every paper ("quantum", "state") learn almost nothing. Old signals fade (half-life 30 days), and only the
// strongest weights are kept, which keeps the model small (well under the 1 MB widget store).
//
// The boost is capped at about one title hit of the query, so the query you typed always matters most, and it stays at
// zero until a few signals exist.
(function (root) {
  "use strict";

  const norm = (root.ArxivQuery || (typeof require === "function" ? require("./query.js") : null)).norm;

  const RATE = 0.5;
  const HALF_LIFE = 30 * 86400000;
  const KEEP = 1500;
  const MIN_SIGNALS = 5;
  const CAP = 3;          // max boost, about one title hit of a weight-1 term
  const SCALE = 0.3;      // raw score that gives ~76 % of the cap
  const MAX_FEATURES = 400;
  const HINT_RARITY = 0.6; // a single word shows as a hint only if at most ~1 in 12 of today's papers use it

  const STOP = new Set((
    "about above after again against all also although among and any are around as at based be been before being below " +
    "between both but by can could did do does done due during each either et al etc even for from further had has have " +
    "here how however if in into is it its itself just less like made make many may more most much must near new no nor " +
    "not now of off often on once one only onto or other our out over own per present presented propose proposed provide " +
    "provides recent recently result results same several shall she should show shown shows since so some such than that " +
    "the their them then there these they this those though three through thus to too two under until up upon us use used " +
    "uses using very via was way we well were what when where whether which while who whose why will with within without " +
    "work would yet paper study studies demonstrate demonstrates find found first second approach approaches method methods " +
    "obtain obtained different various particular general case cases given known allow allows also however further specific " +
    "far assume assumed increase decrease substantial help add complementary significant significantly novel important key " +
    "able enable enables achieve achieves high low large small thereby therefore indeed generally typically respectively " +
    "whereas order terms term analysis analyze investigate investigated consider considered introduce introduced develop " +
    "developed including include includes compared compare leading lead leads possible arbitrary standard simple"
  ).split(" "));

  const stem = (w) => w.length > 4 && w.endsWith("ies") ? w.slice(0, -3) + "y"
    : w.length > 4 && w.endsWith("s") && !w.endsWith("ss") && !w.endsWith("us") && !w.endsWith("is") ? w.slice(0, -1) : w;

  function words(text) {
    return norm(text).split(" ").filter((w) => w.length >= 3 && !STOP.has(w) && !/^\d/.test(w)).map(stem);
  }

  /** The features of a paper, as a Set of strings ("collapse", "quantum gravity", "@penrose"). */
  const cache = new WeakMap();
  function features(p) {
    let f = cache.get(p);
    if (f) return f;
    f = new Set();
    for (const part of [p.title, p.abstract]) {
      const w = words(part);
      for (let i = 0; i < w.length && f.size < MAX_FEATURES; i++) {
        f.add(w[i]);
        if (i + 1 < w.length && w[i] !== w[i + 1]) f.add(w[i] + " " + w[i + 1]);
      }
    }
    for (const a of (p.authors || []).slice(0, 20)) {
      const last = norm(a).split(" ").pop();
      if (last && last.length > 1) f.add("@" + last);
    }
    cache.set(p, f);
    return f;
  }

  /**
   * Rarity of each feature in a listing, 0 (in every paper) .. 1. A feature only one paper has today (often an odd word
   * pair of that abstract) counts half, since it says little about the next paper. Returns a lookup function.
   */
  function rarity(items) {
    const df = new Map();
    for (const p of items) for (const f of features(p)) df.set(f, (df.get(f) || 0) + 1);
    const n = items.length;
    const top = Math.log(n + 1) || 1;
    return (f) => {
      const d = df.get(f) || 0;
      return Math.max(0, Math.log((n + 1) / (d + 1)) / top) * (d <= 1 ? 0.5 : 1);
    };
  }

  // w: feature weights; c: how many liked papers (saved or opened) had the feature, which picks the "learned:" hint;
  // l: liked signals so far; n: all signals so far; t: time of the last update (for the fade).
  const empty = () => ({ v: 1, w: {}, c: {}, l: 0, n: 0, t: 0 });

  function decay(m, now) {
    if (m.t && now > m.t) {
      const k = Math.pow(0.5, (now - m.t) / HALF_LIFE);
      if (k < 0.999) for (const f in m.w) m.w[f] *= k;
    }
    m.t = Math.max(m.t || 0, now);
  }

  function prune(m) {
    for (const f of Object.keys(m.w)) if (Math.abs(m.w[f]) < 1e-4) delete m.w[f];
    const left = Object.keys(m.w);
    if (left.length > KEEP) {
      left.sort((a, b) => Math.abs(m.w[b]) - Math.abs(m.w[a]));
      for (const f of left.slice(KEEP)) delete m.w[f];
    }
    for (const f of Object.keys(m.c)) if (!(f in m.w) || m.c[f] <= 0) delete m.c[f];
  }

  /**
   * Applies one signal (+2 save, +1 open, -1 hide) for paper `p`; `undo` applies the opposite of an earlier one (unsave,
   * unhide) and takes it out of the signal count. Mutates and returns the model.
   */
  function update(m, p, signal, rare, now, undo) {
    const liked = signal > 0;
    if (undo) signal = -signal;
    m = m && m.w ? m : empty();
    m.c = m.c || {};
    decay(m, now || Date.now());
    const f = features(p);
    const spread = Math.sqrt(f.size) || 1;
    for (const x of f) {
      const r = rare ? rare(x) : 1;
      if (r <= 0) continue;
      m.w[x] = (m.w[x] || 0) + (RATE * signal * r) / spread;
      if (liked) m.c[x] = (m.c[x] || 0) + (undo ? -1 : 1);
    }
    if (liked) m.l = Math.max(0, (m.l || 0) + (undo ? -1 : 1));
    m.n = Math.max(0, (m.n || 0) + (undo ? -1 : 1));
    prune(m);
    return m;
  }

  const show = (f) => (f[0] === "@" ? f.slice(1).replace(/^./, (c) => c.toUpperCase()) : f);

  /**
   * { boost, why: [up to 2 learned features that explain it] } for paper `p`. `rare` (from rarity() of today's listing)
   * picks hints that are distinctive today, so "gravitational decoherence" wins over "state".
   */
  function boost(m, p, rare) {
    if (!m || !m.w || (m.n || 0) < MIN_SIGNALS) return { boost: 0, why: [] };
    const f = features(p);
    const shared = Math.max(2, Math.ceil(0.3 * (m.l || 0)));
    let s = 0;
    const parts = [];
    for (const x of f) {
      const w = m.w[x];
      if (!w) continue;
      s += w;
      // A hint is a feature a good share of the liked papers have (an interest, not a quirk of one abstract) and that
      // says something: a word pair, an author, or a single word that few of today's papers use.
      const c = m.c?.[x] || 0;
      if (w <= 0 || c < shared) continue;
      const r = rare ? rare(x) : 1;
      const pair = x.includes(" ") || x[0] === "@";
      if (!pair && r < HINT_RARITY) continue;
      parts.push([x, w * r * (pair ? 1.5 : 1)]);
    }
    s /= Math.sqrt(f.size) || 1;
    const b = CAP * Math.tanh(s / SCALE);
    parts.sort((a, b2) => b2[1] - a[1] || b2[0].length - a[0].length);
    // Skip a word already inside a chosen pair, so the hint reads "wave function" rather than "wave function, wave".
    const why = [];
    for (const [x] of parts) {
      if (why.length >= 2) break;
      if (why.some((y) => y.includes(x) || x.includes(y))) continue;
      why.push(x);
    }
    return { boost: b, why: b > 0.3 ? why.map(show) : [] };
  }

  const api = { features, rarity, update, boost, empty, MIN_SIGNALS, CAP };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  root.ArxivLearn = api;
})(typeof globalThis !== "undefined" ? globalThis : this);
