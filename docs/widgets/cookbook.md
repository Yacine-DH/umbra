# Widget cookbook

Short recipes, each pointing at a real widget in [`widgets/`](../../widgets) that does it. Open the file, copy the part you need.
API details are in the [reference](../widget-sdk.md); a first walk-through is the [tutorial](tutorial.md).

| Recipe | Real widget |
|---|---|
| [Fetch JSON from an API](#fetch-json-from-an-api) | [weather](../../widgets/weather), [fx-rates](../../widgets/fx-rates), [crypto](../../widgets/crypto) |
| [Options and the Settings form](#options-and-the-settings-form) | [prayer-times](../../widgets/prayer-times), [internships](../../widgets/internships) |
| [Timers and persistence](#timers-and-persistence) | [pomodoro](../../widgets/pomodoro) |
| [Notifications](#notifications) | [pomodoro](../../widgets/pomodoro), [prayer-times](../../widgets/prayer-times) |
| [Local commands](#local-commands) | [script-runner](../../widgets/script-runner) |
| [Media](#media) | [now-playing](../../widgets/now-playing) |
| [Freshness and offline](#freshness-and-offline) | [sp500](../../widgets/sp500) |
| [Games without a framework](#games-without-a-framework) | [minesweeper](../../widgets/minesweeper), [solitaire](../../widgets/solitaire) |
| [Animations and tokens](#animations-and-tokens) | [sp500](../../widgets/sp500), [crypto](../../widgets/crypto), [clock](../../widgets/clock) |
| [Performance and energy saver](#performance-and-energy-saver) | [pomodoro](../../widgets/pomodoro), [weather](../../widgets/weather) |

## Fetch JSON from an API

Declare every host, then call `umbra.fetchJson` from `umbra.every`, which runs once on start and then every refresh interval
(and after sleep, a returning network and "Refresh all").

```json
"permissions": { "hosts": ["api.open-meteo.com", "geocoding-api.open-meteo.com"] }
```
```js
umbra.every(async () => {
  const data = await umbra.fetchJson(`https://api.open-meteo.com/v1/forecast?latitude=${lat}&longitude=${lon}&current=temperature_2m`);
  render(data);
});
```

`fetchJson` has no CORS problems, a 25 s timeout and rejects for any URL that is not HTTPS on a declared host.
See [`weather`](../../widgets/weather/widget.json) (two hosts), [`fx-rates`](../../widgets/fx-rates/widget.json) (one host,
`api.frankfurter.dev`) and [`crypto`](../../widgets/crypto/widget.json) (`api.coingecko.com`). Declare only hosts you call:
Browse shows them to the user and an update that adds one asks again.

## Options and the Settings form

Anything the user picks goes in `options` in `widget.json`; Umbra draws the form under **My widgets**, in the order you write
the keys, and delivers the values as `umbra.options`. Types: `text`, `number`, `select` (with `choices`), `toggle`.

```json
"options": {
  "timeFormat": { "label": "Time format", "type": "select", "default": "auto",
    "choices": [{ "value": "auto", "label": "Follow Windows" }, { "value": "24", "label": "24-hour" }] },
  "maxRows": { "advanced": true, "label": "Rows to show", "type": "number", "default": 7, "min": 3, "max": 12, "step": 1 }
}
```

[`internships`](../../widgets/internships/widget.json) shows `advanced: true` options (folded away until asked for) and
numeric ranges; [`prayer-times`](../../widgets/prayer-times/widget.json) shows option `group`s (its "Time adjustments" section folds the related
options together). Re-render from `umbra.onSettings(fn)` when the user changes one. Data the widget creates itself (not a setting) belongs in
`umbra.store`.

## Timers and persistence

Timers that must survive a restart store absolute timestamps, not "seconds left". [`pomodoro`](../../widgets/pomodoro/session.js)
keeps its session in `umbra.store`, recomputes the remaining time from the clock on every tick, and re-renders on
`umbra.onTimeChanged` so a clock correction or time zone change cannot corrupt it.

```js
await umbra.store.set("session", { running: true, endsAt: Date.now() + 25 * 60_000 });
const s = await umbra.store.get("session", null);
const left = s ? Math.max(0, s.endsAt - Date.now()) : 0;
umbra.store.onChange(async () => render(await umbra.store.get("session", null))); // umbractl data / MCP edits
```

Store limit: 1 MB per widget copy. Never use `localStorage` (all widgets share one origin).

## Notifications

Declare `"permissions": { "notifications": true }`; the user also switches notifications on for the widget in Settings.

```js
umbra.notify({ title: "Focus session done", body: "Take 5", tag: "pomodoro-phase" }).catch(() => {});
// Optional buttons (up to 2): actions: [{ id: "break", label: "Start break" }], answered here:
umbra.onNotificationAction(({ id }) => { if (id === "break") startBreak(); });
```

`tag` makes a later toast replace this one. At most 5 toasts per 10 minutes per widget; the call rejects when the user's switch is off, so
catch it. [`pomodoro`](../../widgets/pomodoro/pomodoro.js) announces the end of a phase; [`prayer-times`](../../widgets/prayer-times/prayer-times.js)
announces each prayer. Neither uses buttons; the button API is in the [reference](../widget-sdk.md#javascript-api-windowumbra).

## Local commands

The one place a widget can run code, and it is locked down: the widget declares the exact executable and argument array, every
command needs the user's approval (a host-drawn prompt after a click on the widget, or Settings), parameters are whole-argument
`${name}` placeholders, and output is capped.

```json
"permissions": { "commands": [{
  "id": "system-info",
  "run": ["powershell", "-NoProfile", "-NonInteractive", "-Command", "Get-CimInstance Win32_OperatingSystem | Select-Object -Expand Caption"],
  "timeoutSeconds": 30 }] }
```
```js
const r = await umbra.run("system-info"); // { exitCode, stdout, stderr, timedOut, truncated, durationMs }
```

[`script-runner`](../../widgets/script-runner/widget.json) declares several. An interpreter (`powershell`, `python`, `bash`...)
with a parameter needs fixed `choices` for it; `umbractl validate` tells you. An agent or `umbractl` can never approve a command.

To let the user add their own scripts in Settings instead, add `"userScripts": true` to `permissions` and list what can
run with `umbra.commands()` (re-read it in `umbra.onCommandsChange`), as `script-runner` does. See
[The user's own scripts](../widget-sdk.md#the-users-own-scripts).

## Media

```json
"permissions": { "media": ["read", "control"] }
```
```js
const np = await umbra.media.nowPlaying(); // { title, artist, album, app, playing, art? } or null
button.onclick = () => umbra.media.control("toggle"); // needs "control" and a real click on the widget
```

See [`now-playing`](../../widgets/now-playing). `art` is a data URL, omitted when big. Poll with `umbra.every`; it already pauses while
the PC is locked.

## Freshness and offline

The SDK tracks every `fetchJson` and sets `<html data-stale>` when data is old or the network is down. Add the status dot and dim
your numbers:

```html
<footer><umbra-status></umbra-status></footer>
```
```css
html[data-stale] .price { opacity: .55; }
```

[`sp500`](../../widgets/sp500/sp500.js) also calls `umbra.status?.failed(err)` for a response that parsed but had no usable data.
Test it with `UMBRA_SIMULATE_OFFLINE=1`. Details in the [reference](../widget-sdk.md#freshness-and-offline).

## Games without a framework

[`minesweeper`](../../widgets/minesweeper) and [`solitaire`](../../widgets/solitaire) are plain DOM and CSS (no canvas, no library).
Both keep the rules in separate pure files (`engine.js`, `klondike.js`, `cards.js`) that the repo's tests load under Node
(`tests/widgets/*.test.mjs`), and keep the UI in a thin file. Save the game with `umbra.store` so a restart resumes it. Widgets are
locked unless the user is arranging them, so mouse clicks reach your page; put `data-no-drag` on buttons that need plain clicks.

## Animations and tokens

Use the design tokens from `umbra.css` (`--bg`, `--surface-1`, `--text`, `--up`, `--down`, `--accent`, radii) so themes work, and
`calc(200ms * var(--motion-scale, 1))` for durations. Count numbers up with `umbra.tween(from, to, ms, onFrame)` as
[`sp500`](../../widgets/sp500/sp500.js) does; it jumps straight to the end when motion is off. Use `umbra.fmt(n)` for locale-correct numbers.
Do not hard-code colors your theme cannot change, and keep text at 11px or larger (`umbractl validate` warns).

## Performance and energy saver

- Use `umbra.every` instead of your own `setInterval` for data: Umbra stretches it 4x on Energy Saver (when the user allows it),
  pauses it while the PC is locked or the display is off, and runs it again on unlock.
- A page timer (a clock, a countdown) should stop when `umbra.settings.paused` is true, as [`pomodoro`](../../widgets/pomodoro/pomodoro.js) does.
- Draw only what changed; avoid full-widget re-renders every second.
- Keep the widget small: 5 MB total, and remember it runs once per copy the user adds.
