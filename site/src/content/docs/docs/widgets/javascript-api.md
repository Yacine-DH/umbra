---
title: JavaScript API
description: The window.umbra object by task - settings, fetching, saving data, notifications, media, system readings, formatting and utilities.
sidebar: { order: 4 }
---

`umbra.js` adds one global, `window.umbra`. This page groups it by what you want to do. The one-table reference is in the [SDK reference](/umbra/docs/widgets/sdk-reference/#javascript-api-windowumbra).

## Know your settings

| API | Description |
|---|---|
| `umbra.ready` | Promise that resolves when the host has sent this widget's settings. |
| `umbra.options` | This widget's option values, e.g. `{ city: "Berlin", units: "c" }`. |
| `umbra.settings` | `{ refreshSeconds, animations, locked, scale, locale, hour12, editMode, paused, energySaver, options }`, plus `accent` and `themeCss`. |
| `umbra.widget` | `{ id, instance, name }`: the widget (the folder) and this copy. |
| `umbra.onSettings(fn)` | Called with the new settings whenever the user changes anything. |
| `umbra.every(fn)` | Calls `fn` once ready, then every `refreshSeconds`. See [Page and lifecycle](/umbra/docs/widgets/page-and-lifecycle/). |
| `umbra.onTimeChanged(fn)` | Runs when the system clock or time zone changes. |

## Fetch data

| API | Description |
|---|---|
| `umbra.fetchJson(url)` | `GET` JSON through the host: no CORS issues, 25 s timeout. Rejects unless the URL is HTTPS and its host is in `permissions.hosts`. |
| `umbra.fetchText(url)` | Like `fetchJson` but resolves the body as a string (up to 2 MB), for Markdown, CSV or HTML. |
| `umbra.status` | Data freshness (`state`, `lastOk`, `failures`, `ok()`, `failed()`, `onChange()`). See [Freshness and offline](/umbra/docs/widgets/offline/). |

Plain `fetch()` also works for declared hosts if the server allows CORS; `umbra.fetchJson` always works.

## Save data

```js
const tasks = await umbra.store.get("tasks", []);
tasks.push({ title: "Read chapter 3" });
await umbra.store.set("tasks", tasks);
umbra.store.onChange(async () => render(await umbra.store.get("tasks", [])));
```

| API | Description |
|---|---|
| `umbra.store.get(key, fallback?)` | The saved value for `key` (any JSON), or `fallback`. |
| `umbra.store.set(key, value)` | Saves a JSON value. Rejects when the widget's data would pass 1 MB. |
| `umbra.store.remove(key)`, `umbra.store.keys()` | Delete a key; list the keys. |
| `umbra.store.onChange(fn)` | `fn(key)` when the data was changed from outside (`umbractl data`, MCP). `key` is `null` when everything was cleared. |

Use options for what the user picks, and `umbra.store` for what the widget creates. The store is private to the widget, survives restarts and updates, and lives in `%APPDATA%\Umbra\data\<id>.json`. Don't use `localStorage`: every widget shares one origin.

## Talk to the user and the system

| API | Needs | Description |
|---|---|---|
| `umbra.notify({ title, body?, url?, tag?, actions? })` | `permissions.notifications` and the user's switch | A real Windows toast. Up to 2 `actions`, `tag` replaces an earlier toast, 5 per 10 minutes per widget. A toast `url` must be HTTPS on a declared host. |
| `umbra.onNotificationAction(fn)` | | `fn({ id, tag })` when a toast button is clicked, while the widget is open. |
| `umbra.media.nowPlaying()` | `media: "read"` | The current Windows media session (`title`, `artist`, `album`, `app`, `status`, `playing`, `art?`…) or `null`. |
| `umbra.media.control(cmd)` | `media: "control"` | `play`, `pause`, `toggle`, `next`, `previous`. Needs a physical click on the widget in the last 2 s. |
| `umbra.system()` | `permissions.system` | `cpu.percent`, `memory.{totalBytes,usedBytes,percent}`, `network.{downBytesPerSec,upBytesPerSec}`, `gpu.{percent,memoryUsedBytes}`, `disk.{activePercent,readBytesPerSec,writeBytesPerSec,drives}`. Call from `umbra.every` or a short timer of your own. |
| `umbra.run(id, params?)` | `permissions.commands` | Runs a declared, user-approved command. See [Running local commands](/umbra/docs/widgets/local-commands/). |

## Format for the user's locale

| API | Description |
|---|---|
| `umbra.locale` | The user's Windows region format as a BCP-47 tag (`"de-DE"`). Pass it to `Intl.*` and `toLocale*String`. |
| `umbra.hour12` | `true` when Windows uses a 12-hour clock. Pass it as `hour12`, and let an option override it if you offer one. |
| `umbra.fmt(n, decimals = 2)` | `7744.06` → `"7,744.06"`, or `"7.744,06"` when Windows is set to German. |

## Utilities

| API | Description |
|---|---|
| `umbra.rendered()` | Call once the first real frame is drawn; screenshots wait for it. |
| `umbra.tween(from, to, ms, onFrame)` | Ease-out-cubic number animation; jumps to `to` when motion is off or the window is hidden. |
| `umbra.openUrl(url)` | Opens an http(s) link in the user's browser. Links never navigate the widget. |
| `umbra.requestFocus()` | Ask for the keyboard, only honoured right after a mouse press on the widget. |

:::tip
Widgets that must run on an older installed Umbra than the SDK they were written against should guard newer calls, for example `umbra.status?.failed(err)`.
:::
