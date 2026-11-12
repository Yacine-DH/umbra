# Umbra relay

A small Cloudflare Worker plus one Durable Object class. It is the only server Umbra has. Two widgets use it, and it
also keeps Umbra's anonymous install counts and the website's page views ([Stats](#stats)):

- **Buddy** (`widgets/buddy`): pairs two people with a one-time code and shows whether the other one is online.
- **Hearth** (epic #207): a long-distance couples widget. The relay keeps the couple's names, together-since date,
  taps, drawing board and pet.

Widgets can only make HTTPS **GET** requests to hosts they declare (`umbra.fetchJson`), and that call throws away the
body of any non-2xx answer. So **every response is JSON with status 200**, and failures are `{ "error": "..." }` or
`{ "paired": false, ... }`. There are no WebSockets.

Files: `worker.js` (router, Buddy, the `Room` Durable Object class), `hearth.js` (all Hearth logic, plain functions
over a storage object and a clock), `wrangler.toml`. Tests: `node --test tests/relay/*.test.mjs` (the Worker runs
against an in-memory fake of Durable Object storage and a fake clock; no Cloudflare account needed).

Deployed host: `umbra-buddy-relay.umbra-server.workers.dev` (the Worker keeps its original name so Buddy and every
existing room keep working; see [Deploy](#deploy)).

## Pairing (shared by Buddy and Hearth)

| Call | Response |
|---|---|
| `GET /pair?new=1` | `{ code, room, me: "a", key, ttl: 600 }`. `room` is 32 hex characters, `key` 32 hex characters, `code` 8 characters (no `I L O 0 1`). The code is valid for `ttl` seconds and works once. |
| `GET /pair?code=ABCD2345` | `{ room, me: "b", key }` and the code is used up. Case and dashes are ignored. Errors: `{ error: "bad code" }`, `{ error: "unknown or expired code" }`. |

A widget stores `{ room, me, key }` and sends all three with every later call. The key identifies one side of one
room; there are no accounts. Hearth reuses this exactly: a Hearth room is a Buddy room that also has Hearth state,
created lazily by the first `/hearth/*` call.

## Buddy routes (unchanged)

`GET /beat?room=R&me=a|b&key=K&iv=60` returns `{ paired, peerSeen, peerIv, now }`, and `GET /unpair?room=R&me=a|b&key=K`
returns `{ ok }`. A bad room id or side is `{ error: "bad request" }`; a wrong key or unknown room is
`{ paired: false, now }`. Anything else under an unknown path is `404 { error: "not found" }`. (Only `/hearth/*`
promises never to answer with a non-2xx status.) `tests/relay/buddy.test.mjs` pins all of this.

## Hearth routes

All are `GET /hearth/<route>?room=R&me=a|b&key=K&...`, authenticated with the same `room`, `me` and `key` from `/pair`.
The URL must stay under 6,000 characters in total. Every response has status 200.

**Failure shapes (the same for every route):**

| Response | Meaning |
|---|---|
| `{ "error": "bad request" }` | `room` is not 32 hex characters, or `me` is not `a` or `b` |
| `{ "error": "<message>" }` | bad parameters for that route (messages are listed per route). Nothing was changed. |
| `{ "error": "not found" }` | unknown route, `{ "error": "GET only" }` for other methods, `{ "error": "too long" }` for a URL over 6,000 characters, `{ "error": "server error" }` for an unexpected failure |
| `{ "paired": false, "reason": "unknown", "now": ms }` | unknown room, deleted room, or wrong key (deliberately indistinguishable). **Forget the pairing.** |
| `{ "paired": false, "reason": "unpaired", "by": "you"\|"peer", "graceUntil": ms, "now": ms }` | someone called `unpair`; the room still exists until `graceUntil` (7 days). The side that stayed (`by: "peer"`) may call `reinvite`. |

A widget should treat a network failure or a non-JSON answer (for example Cloudflare's own quota errors) as
"offline, try later", an `error` as a bug or bad input, and only `paired: false` as "this pairing is over".
Success responses never contain `error` and never contain `paired: false`.

### Revisions (`rev`) and what `sync` returns

The room has one counter, `rev`, that goes up by one for every change anyone makes (a stroke, an undo, a tap, a
name...). `rev` starts at 1. Each part of the state remembers the rev it last changed at, and a stroke's id is the rev
it was created at.

`GET /hearth/sync?...&since=<rev>` answers:

- **Nothing changed** (`since` equals the room's `rev`): exactly `{"rev":42}`, about 10 bytes.
- **Otherwise**: `rev`, `now`, and only the sections that changed after `since`. `since` missing, `0`, not a number or
  larger than the room's rev means "send everything" (a first call; a room recreated after a deletion).

```jsonc
{
  "rev": 57,
  "now": 1780315200000,                       // server time in ms. Always present in a non-idle answer.
  "names": { "a": "Ada", "b": "Bo" },          // "" until set
  "togetherSince": "2024-02-14",               // "" until set. (The `set` parameter for it is `since`.)
  "petName": "Pip",                            // "" until set
  "pet": { ... },                              // see Pet below
  "taps": { "a": { "n": 3, "at": 1780315000000 }, "b": { "n": 5, "at": 1780314000000 } },
  "together": { "n": 1, "at": 1780314010000 }, // number of together moments so far, and the last one's time
  "board": {
    "reset": false,       // true: throw away your whole board and use `strokes` (see Board)
    "strokes": [ { "i": 55, "by": "a", "c": 3, "z": 2, "k": 0, "p": "100.200.10.5.-5.-15" } ],
    "removed": [ 51 ],    // ids undone since `since`
    "clear": null         // or { "by": "a", "at": ms, "until": ms } while that clear can still be undone
  }
}
```

Rules for clients:

- **Take `rev` only from `sync` responses.** Write responses deliberately carry no `rev`: after your own write a
  partner's change may sit between your last sync and your write, and adopting a newer rev would skip it. Your own
  strokes come back in the next sync; ignore ones whose `i` you already have (the `stroke` response gave you the ids).
- A section is absent when it did not change. Keep the last value of each section.
- `taps`: a partner tap happened when `taps.<peer>.n` is larger than you have seen. `together` works the same way
  (`together.n` went up: play the special moment).
- Time: use `now` from the answer to correct for your clock when you show `until`/`graceUntil`/`at` values.
- Sync does not carry presence. The 60 s idle poll described in the epic is the intended cadence, about 1.5 s while the
  board is being drawn on.

### Routes

| Route | Parameters | Success response | Errors |
|---|---|---|---|
| `sync` | `since=<rev>` | see above | |
| `stroke` | `s=<strokes>` (see Board) | `{ ok: true, ids: [55, 56] }`, one id per record, in order | `no strokes`, `too long`, `too many strokes`, `bad stroke`, `stroke too long` |
| `undo` | | `{ ok: true, removed: [55, 56] }`: the caller's own most recent stroke (all records of a split stroke) | `nothing to undo` |
| `clear` | | `{ ok: true, until: ms }`, or `{ ok: true, noop: true }` when the board is empty | |
| `unclear` | | `{ ok: true }`. Either side may restore the board within 60 s of the clear. | `nothing to restore`, `too late` |
| `tap` | | `{ ok: true, together: bool }`; `together: true` when the other side tapped within the window before this tap | |
| `care` | `action=feed\|water\|pet` | `{ ok: true, pet: {...} }` | `bad action`, `too soon` (plus `retryIn` seconds) |
| `set` | `names=A,B`, `since=YYYY-MM-DD`, `petName=...` (any combination, at least one) | `{ ok: true }` | `bad name`, `bad date`, `names needs two parts: a,b`, `nothing to set` |
| `unpair` | | `{ ok: true, graceUntil: ms }` | |
| `reinvite` | | `{ code, ttl: 600, rebinds: "a"\|"b" }` | |

Example URLs (replace the placeholders):

```
https://umbra-buddy-relay.umbra-server.workers.dev/pair?new=1
https://umbra-buddy-relay.umbra-server.workers.dev/pair?code=ABCD2345
https://umbra-buddy-relay.umbra-server.workers.dev/hearth/sync?room=<R>&me=a&key=<K>&since=57
https://umbra-buddy-relay.umbra-server.workers.dev/hearth/stroke?room=<R>&me=a&key=<K>&s=3.2.100.200.10.5.-5.-15_0.0.500.500
https://umbra-buddy-relay.umbra-server.workers.dev/hearth/care?room=<R>&me=b&key=<K>&action=feed
https://umbra-buddy-relay.umbra-server.workers.dev/hearth/set?room=<R>&me=a&key=<K>&names=Ada%2CBo&since=2024-02-14&petName=Pip
```

### Board

Strokes are small and quantised so a batch stays under about 4 KB of URL:

```
s       = record ("_" record)*                  at most 8 records, at most 4096 characters in total
record  = ["k"] colour "." size "." x "." y ("." dx "." dy)*
```

- `colour` is an index 0..15 and `size` an index 0..7 (the widget owns what they look like).
- `x` and `y` are integers 0..1000: the position as per mille of board width and height. The first point is absolute;
  every later point is a **delta** from the previous one, so `3.2.100.200.10.5.-5.-15` is colour 3, size 2, points
  (100,200), (110,205), (105,190). No point may leave 0..1000. A record with one point is a dot.
- At most 600 points per record. A longer stroke is sent as several records; mark every record after the first with a
  leading `k` ("continues the previous record of mine"), also across requests. `undo` removes a record chain as one stroke,
  and the first point of a `k` record is where the previous record ended.
- The separators are `.`, `-` and `_`, which `URLSearchParams` and `encodeURIComponent` leave alone. Build the
  parameter as plain text and let the URL encoder see it.

In a `sync`, each stroke is `{ i, by, c, z, k, p }`: `i` the id (ascending, oldest first), `by` the side that drew it,
`c`, `z`, `k` as above, and `p` the points as `"x.y.dx.dy..."`. Draw strokes in `i` order. To apply `board`:

1. `reset: true`: drop everything you have and use `strokes` (a first sync, after a clear or unclear, or when you fell
   far behind).
2. `reset: false`: remove the ids in `removed`, then add `strokes` you do not have yet.
3. `clear` is only for UI: while `until` is in the future, show "X cleared the board, Undo" (call `unclear`).

`clear` hides the board for everyone and keeps the strokes for 60 s so `unclear` can bring them back. The next `stroke`
after that window (or another `clear`) deletes them for good. A client that is behind by more than 64 undos is simply
sent a `reset`.

Caps: **300 stroke records and 20,000 points per room**. Past either, the oldest strokes are deleted. Clients that stay
open keep drawing what they already have; a fresh `sync` no longer contains the trimmed strokes.

### Pet

`pet` (in `sync`, and in the answer to `care`):

```jsonc
{ "food": 62, "water": 48, "love": 70,        // integers 10..100, computed from SERVER time
  "mood": "happy",                            // "happy" | "content" | "sad"   (there is no dead state)
  "level": 2, "xp": 7, "nextXp": 16,          // nextXp is null at the maximum level (50)
  "bond": { "day": "2026-06-01", "a": true, "b": false, "done": false, "streak": 3 },
  "decayPerHour": { "food": 1.25, "water": 1.875, "love": 0.9375 }, "min": 10 }
```

- New pets start at 70 on all three. Each stat falls linearly by `decayPerHour` and **stops at `min` (10)**. A pet that
  nobody feeds for a year is `sad` with 10/10/10 and recovers as soon as someone cares. Nothing a client sends can
  change the clock.
- An idle `sync` does not resend the pet, so a client that wants to show the decay between syncs computes
  `max(min, value - decayPerHour * hoursSinceNow)` from the values and the `now` it last received.
- `care?action=feed` adds 35 to `food`, `water` adds 35 to `water`, `pet` adds 25 to `love` (capped at 100), on top of the
  decayed value. Each side may repeat an action after 5 minutes (`{ error: "too soon", retryIn }`).
- `xp`: +1 for a care that was needed (the stat was under 90), +5 when the daily bond completes. `level = min(50, 1 + floor(sqrt(xp / 4)))`.
- `bond`: per UTC day. `a` and `b` say who cared today (any action); `done` is true when both did. `streak` counts
  consecutive UTC days with `done`; it reads 0 once a day was missed.
- The pet is "born" at the first Hearth call of a room.

### Names and dates

- `names=A,B` sets side `a`'s and side `b`'s name; either half may be empty to leave that side alone (`names=Ada,`).
  The comma is the separator, so URL-encode it (`%2C`) like any URL builder does.
- `petName=...` sets the pet name. Names are trimmed, repeated spaces collapse, 1 to 20 characters from letters, marks,
  digits, space, apostrophe, period and hyphen (any language; no emoji, no punctuation beyond those).
- `since=YYYY-MM-DD` must be a real calendar date from 1970-01-01 up to tomorrow (UTC). Do not confuse it with the
  revision parameter of `sync`: `sync?since=57` is a rev; `set?since=2024-02-14` is the together-since date, which
  comes back as `togetherSince`.
- If one value in a `set` call is invalid nothing in that call is applied.

### Taps

Every `tap` increments the caller's `taps.<me>.n`. When the caller taps and the **other** side's latest unmatched tap is
at most `TOGETHER_WINDOW_S` seconds old (default 120, configurable in `wrangler.toml`, clamped to 5..900), the answer
has `together: true`, `together.n` goes up for both sides, and both taps are used up (a third tap does not pair with an
old one). The side that tapped first learns about it from its next `sync`.

### Unpair and reinvite

- `unpair` (either side): the room is kept for **7 days** so the couple can pair again, then deleted with everything in
  it. During the grace period every route answers `paired: false, reason: "unpaired"` to both sides, except:
  `unpair` again (no change) and `reinvite` from the side that did **not** unpair.
- `reinvite` (the side that is still paired, or either side of a room that is not unpaired; for example when the other
  PC was lost or Umbra was reinstalled): returns a new one-time code. The code works exactly like a `/pair?code=` code
  (10 minutes, once) and gives `{ room, me, key }` for the **other** side (`rebinds` says which). The old key of that
  side stops working **immediately**, even if the code is never used, and an unpaired room becomes active again. Ask
  again if the code expired. A side cannot reinvite itself back after it unpaired, and a wrong key never gets a code.
- The new PC then calls `sync` without `since` to download the whole state.

## What is stored, and for how long

Per room (one Durable Object, `r:<room id>`): the two keys, the Buddy heartbeat times, and for Hearth one row `h`
(names, together-since date, pet name, pet stats and care times, tap counters and times, counters, the last 64 undone
stroke ids, the undo stacks, the clear window) plus one row per stroke (`s:<id>`: author, colour, size, points). Pairing
codes live in their own object for at most 10 minutes. There are no names of accounts, no emails, no IP addresses
and no location; names and dates exist only if the couple typed them.

| Data | Kept |
|---|---|
| Pairing code | 10 minutes, or until used |
| Room (Buddy and Hearth) | until **30 days** pass without a Buddy beat or any authenticated Hearth call, then everything is deleted. An idle `sync` refreshes the stamp at most once an hour |
| After a Hearth `unpair` | **7 days**, then everything is deleted (sooner than 30 days) |
| Strokes | newest 300 records / 20,000 points; a cleared board for 60 s |

Cloudflare itself keeps request logs (including URLs) per its own policy; the room key travels in the URL. Anyone with
a key can read and change that side's room, which is why the code is single use and keys are random 128-bit values.

## Request budget (Workers Free: 100,000 requests/day)

Every call is one Worker request **and** one Durable Object request (a `reinvite` or `/pair?new=1` is two), and Buddy
shares the same free quota. Rough load per couple:

| Source | Requests per day |
|---|---|
| Hearth idle poll, 60 s, per PC that is on and unlocked | 1,440 for 24 h, 720 for 12 h |
| Board open and being drawn on, ~1.5 s polling, per PC | about 2,400 per hour |
| Taps, care, names, strokes | a few dozen |
| Buddy beat, 60 s, per person | 1,440 for 24 h |

A typical couple (both PCs on 12 h, half an hour of drawing together) is about 1,440 + 2,400 = **3,800 a day**, so the
free plan carries roughly 25 couples, or fewer if Buddy is used a lot. These are estimates, not measurements. When the
quota runs out Cloudflare answers with errors until midnight UTC, and widgets then show "offline". If the relay gets
popular, the Workers Paid plan (about US$5 per month) includes 10 million requests per month. Storage use is
negligible (kilobytes per room, 300 strokes at most). An idle Hearth poll reads one small row and writes nothing, which
keeps it far from the Durable Object row limits.

## Stats

`stats.js`, in one Durable Object named `stats` (the same `Room` class, so no migration). Only counters: no ids, IP
addresses, user agents or versions are stored or derived. Every answer is status-200 JSON with
`access-control-allow-origin: *` (the website reads it from the browser).

| Call | Who | Response |
|---|---|---|
| `GET /stats/install?w=<widget id>` | the app, after a **fresh** catalog install while **Count my widget installs** is on (`InstallStats.cs`) | `{ ok }`, or `{ error: "bad widget id" }` |
| `GET /stats/view?p=<path>&r=<referrer host>` | the website, once per page (skipped with Do Not Track / Global Privacy Control) | `{ ok }`, or `{ error: "bad request" }` |
| `GET /stats/installs` | Settings > Browse and the website catalog | `{ widgets: { <id>: n }, since: "YYYY-MM-DD" }`, cached 10 minutes |
| `GET /stats/summary` with `Authorization: Bearer <STATS_TOKEN>` | the private dashboard (`site/src/pages/stats.astro`, `/umbra/stats/`) | `{ days: [{ day, i, v, r }], totals, since, now }` for the last 120 days; `{ error: "unauthorized" }` without the right token or with no secret set |

Stored: `t` = all-time installs per widget, and `d:<YYYY-MM-DD>` = that UTC day's installs per widget (`i`), views per
path (`v`) and referrer hosts (`r`), at most 500 widgets, 300 paths and 100 hosts a day (new keys past that are ignored).
Days older than 400 days are deleted when a new day starts. Counts are not authenticated, so they are a signal, not an
audit: anyone can add to them. Tests: `tests/relay/stats.test.mjs`.

## Deploy

One-time and per release, by a maintainer with the Cloudflare account (not done by CI or by the agents that edit this
code; they have no credentials):

```powershell
cd tools/umbra-relay
npx wrangler login         # once per machine
npx wrangler deploy        # prints https://umbra-buddy-relay.<subdomain>.workers.dev
```

- **Redeploying this version over the existing Worker** keeps the host, every existing Buddy room and the Durable Object
  class (`Room`); no migration is needed because Hearth added no class. Keep `name = "umbra-buddy-relay"` in
  `wrangler.toml`: another name creates a new Worker with an empty room namespace and a new host that every installed Buddy
  would have to be updated for.
- **Stats token** (once): `npx wrangler secret put STATS_TOKEN` and paste a long random string (for example the output of
  `node -e "console.log(crypto.randomUUID()+crypto.randomUUID())"`). Then open `https://aminechr54.github.io/umbra/stats/`
  and enter it; the page keeps it in that browser only. Without the secret the summary refuses everyone.
- Smoke test after deploying (read-only): `curl "https://<host>/hearth/sync?room=00000000000000000000000000000000&me=a&key=x"`
  should print `{"paired":false,"reason":"unknown","now":...}` with status 200, and an existing Buddy pair should keep
  its stars. `npx wrangler rollback` returns to the previous version.
- A fresh deployment on another account changes the host: put the new host in `private-widgets/buddy/main.js` (`RELAY`) and in
  `private-widgets/buddy/widget.json` (`permissions.hosts`), bump the widget version and reinstall it from the folder (see `private-widgets/README.md`; Buddy is not in the public catalog). The Hearth
  widget needs the same host in its own `permissions.hosts`.
- Run locally: `npx wrangler dev --port 8799`, then `curl "http://127.0.0.1:8799/pair?new=1"` (widgets need HTTPS, so
  this is for curl only).

### Rate limiting (recommended before publishing the Hearth widget)

Anyone can call `/pair?new=1`, which creates two Durable Objects, and the write routes, which write to storage. Add
Cloudflare rate-limiting rules so one client cannot burn the free quota:

- `/pair?new=1`: for example 10 requests per minute per IP.
- Write routes (`/hearth/stroke`, `undo`, `clear`, `unclear`, `tap`, `care`, `set`, `reinvite`): for example 120 requests
  per minute per IP (a drawing client sends about one `stroke` call per second).
- With the Worker on a **custom domain or route in your own zone**, create these in the dashboard under Security, WAF,
  Rate limiting rules, with an expression such as
  `(http.request.uri.path eq "/pair" and http.request.uri.query contains "new=1") or (starts_with(http.request.uri.path, "/hearth/") and http.request.uri.path ne "/hearth/sync")`
  (one rule per limit, since the two limits differ).
- A `*.workers.dev` host is not in your zone, so WAF rules cannot attach to it; use the Workers Rate Limiting binding
  (a `ratelimit` binding in `wrangler.toml`, checked in `worker.js` before the Durable Object call) or move the Worker to a custom domain.
  Neither is set up in this repository yet, and the binding syntax was not verified here.
