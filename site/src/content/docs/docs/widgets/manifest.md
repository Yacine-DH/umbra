---
title: widget.json
description: Every field of a widget manifest, option types, permissions and the rules Umbra validates.
sidebar: { order: 2 }
---

Every widget has a `widget.json` in its folder. Umbra validates it when the widget loads, when it's installed, and in CI for catalog widgets (`node scripts/catalog.mjs`). Add the `$schema` line and your editor validates and completes it as you type:

```json
"$schema": "https://raw.githubusercontent.com/AmineChr54/umbra/main/schemas/widget.schema.json"
```

## Example

```json
{
  "$schema": "https://raw.githubusercontent.com/AmineChr54/umbra/main/schemas/widget.schema.json",
  "id": "weather",
  "name": "Weather",
  "description": "Current temperature and conditions for one city.",
  "author": "your-github-name",
  "version": "1.0.0",
  "entry": "index.html",
  "preview": "preview.png",
  "width": 300,
  "height": 160,
  "defaultPosition": { "x": 24, "y": 420 },
  "permissions": { "hosts": ["api.open-meteo.com"] },
  "options": {
    "city":     { "label": "City", "type": "text", "default": "Berlin", "hint": "Shown under the field" },
    "units":    { "label": "Units", "type": "select", "default": "c",
                  "choices": [{ "value": "c", "label": "Celsius" }, { "value": "f", "label": "Fahrenheit" }] },
    "decimals": { "label": "Decimals", "type": "number", "default": 0, "min": 0, "max": 2, "step": 1 },
    "compact":  { "label": "Compact mode", "type": "toggle", "default": false }
  }
}
```

## Fields

| Field | Rules |
|---|---|
| `id` | Equals the folder name. 2 to 40 characters: lowercase letters, digits and dashes, starting with a letter or digit (`^[a-z0-9][a-z0-9-]{1,39}$`). |
| `name`, `description`, `author` | Required, non-empty. |
| `version` | Required, like `1.2.3`. **Bump it on every change**: it's how users get an Update button. |
| `changelog` | Optional, up to 500 characters: what changed. Shown when an update asks for new access and carried in the catalog index. |
| `entry` | The page to open. Default `index.html`. |
| `preview` | Screenshot for Browse. Recommended: a 2x capture on a black background. |
| `icon` | Optional tab icon for [tab groups](/umbra/docs/using/copies-and-tab-groups/): a 64×64 PNG, light grey on transparent. |
| `width`, `height` | Integers 80 to 1200, in **logical** pixels. Umbra scales them for the display's DPI and the user's size setting. |
| `defaultPosition` | `{ "x", "y" }` from the top left of the primary work area, in logical pixels. Omit to start at the top right. |
| `themeable` | Default `true`. `false` keeps your own look and ignores the user's [theme](/umbra/docs/using/appearance/). |
| `options` | The settings form in **My widgets**, in this order. See below. |
| `permissions` | What the widget may reach. See below. |

## Options

| `type` | Extra fields | Value |
|---|---|---|
| `text` | `hint` | string |
| `number` | `min`, `max`, `step` | number |
| `select` | `choices`: a list of `{ "value", "label" }` (required) | the chosen `value` |
| `toggle` | none | boolean |

Every option has a `label` and a `default`. Read values in the page from `umbra.options`; they update live through `umbra.onSettings`. Users (and scripts) can change them with `umbractl set <id> <option> <value>`, which types the value by the schema.

## Permissions

| Field | Meaning |
|---|---|
| `hosts` | Every HTTPS hostname the widget may reach. **All other requests are blocked** (fetch, XHR, images, scripts, fonts). Use `[]` for offline widgets. |
| `notifications` | `true` to use `umbra.notify`. The user must also switch "Allow notifications" on for the widget in Settings (off by default). |
| `media` | `"read"` (`umbra.media.nowPlaying`) and/or `"control"` (`umbra.media.control`). |
| `system` | Any of `"cpu"`, `"memory"`, `"network"`, `"gpu"`, `"disk"`, for `umbra.system()` (`gpu` and `disk` need an Umbra newer than 1.14.0). |
| `commands` | Local programs the widget may start with `umbra.run`. Every command needs your approval. See [Running local commands](/umbra/docs/widgets/local-commands/). |

Browse shows every permission on the widget's card, and an update that adds any of them needs the user's approval. See [Sandbox and permissions](/umbra/docs/widgets/sandbox/).

## Files

A widget folder may contain `.html .js .mjs .css .svg .png .jpg .webp .gif .woff .woff2 .ttf .otf .json .txt .md` files, 5 MB in total. File paths must be relative with forward slashes and no `..`. See [widget.json schema](/umbra/docs/reference/widget-schema/) for the machine-readable definition, and the [SDK reference](/umbra/docs/widgets/sdk-reference/) for the full details in one page.
