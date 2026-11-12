---
title: Data, privacy and backups
description: What Umbra stores and sends, the Data and privacy page, backup and restore, the trash, and safe mode.
sidebar: { order: 7 }
---

## What Umbra stores

Everything lives in `%APPDATA%\Umbra` (or the `data` folder next to `Umbra.exe` in a [portable copy](/umbra/docs/internals/portable-mode/)). The environment variable `UMBRA_DATA_DIR` overrides the location.

| Path | Contents |
|---|---|
| `settings.json` | Settings, per-widget options, positions, layouts, hotkeys, active theme |
| `widgets\<id>\` | Installed community and agent-written widgets |
| `data\<id>.json` | What a widget saved with `umbra.store` (1 MB cap per widget) |
| `themes\<id>\` | Installed themes |
| `approvals.json` | Which widget commands you approved |
| `trash\<id>-<timestamp>\` | Widgets removed in Settings, kept 7 days for **Undo** |
| `umbra.log` | The log |

## What Umbra sends

Umbra has no account and doesn't track you. Its own network traffic is limited to:

1. a daily check for a new release on GitHub (turn it off with `umbractl general --updates off`),
2. the widget and theme catalog when you open **Browse** or **Themes**, plus the install counts Browse shows,
3. one anonymous +1 for a catalog widget when you install it from the catalog: the request is just the widget's id, with no
   user or machine id, no version and nothing about your PC, and updates or file installs aren't counted. The Umbra relay
   keeps only the total per widget. Turn it off with **Count my widget installs** in **Data & privacy**; that also stops
   Browse from loading the counts,
4. requests that widgets make to hosts they declared in their `widget.json`.

This website counts page views the same way: the page's path and the referring site's name, no cookie, no id, and
nothing at all when your browser sends Do Not Track or Global Privacy Control.

A widget cannot reach anything it didn't declare; the app blocks the rest. See [Sandbox and permissions](/umbra/docs/widgets/sandbox/).

## The Data & privacy page

**Settings → Data & privacy** shows, for every widget:

- the hosts it can reach and the local commands it declares (and whether you approved them),
- how much data it has saved, with a **Delete data** button,
- recent blocked requests (a request to a host the widget didn't declare),
- how many times its renderer crashed.

It also has **Export** and **Import backup**, **Open data folder** and **Open log**, **Reset everything**, and **safe mode**. The same read-only report is `umbractl privacy [id]`.

## Backup and restore

```powershell
umbractl backup C:\Backups\umbra.json     # settings, layouts, theme, hotkeys and widget data
umbractl restore C:\Backups\umbra.json    # validated before anything is replaced
```

A backup **never includes command approvals**, so after a restore you approve widget commands again. The trash is not part of backups either.

## Removing widgets

- **Settings → Remove** moves the widget folder, its data and its settings (for every copy) to the trash for 7 days, with an **Undo** button on the toast. The trash is purged at startup after 7 days. (Undo does not bring back the working folders of `permissions.commands` runs.)
- `umbractl remove <id>` and the MCP `remove_widget` delete a widget and its data for good, with no undo.

## Safe mode

Safe mode turns community widgets off without deleting anything, so you can find a misbehaving one. Turn it on from **Data & privacy**, with `umbractl safe-mode on`, or start Umbra with `Umbra.exe --safe`. It isn't saved: the next normal start restores everything.
