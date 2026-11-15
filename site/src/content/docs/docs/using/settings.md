---
title: Settings tour
description: A page-by-page tour of Umbra's Settings window, with keyboard shortcuts and where each setting is stored.
sidebar: { order: 1 }
---

Open Settings from the tray icon, from the Start or app menu, or by launching Umbra a second time (a second launch opens Settings in the running copy). The window reopens with its size, maximized state, zoom and last page (and on Windows, its monitor and position; on Linux, the compositor places the window).

## Pages

| Page | What it does |
|---|---|
| **General** | Start with Windows, refresh interval, animations, appearance (corner radius, border, accent color or the Windows accent, interface font and size, light text, reduce transparency) and advanced options (log level, developer mode, GPU, startup delay, proxy). |
| **My widgets** | Turn each widget on or off, edit its options, size and opacity, lock it, give it its own refresh interval, schedule when it shows, add another copy or reset its position. Also where you start edit mode and approve or revoke widget commands. |
| **Layouts** | Save the arrangement of your widgets under a name, then apply, rename or delete it. Layouts can switch automatically when you dock or undock. |
| **Browse** | The community catalog: install, update or remove widgets in one click. Each card shows the hosts, notifications, media and commands a widget asks for. |
| **Themes** | Install community themes and switch between them live. |
| **Hotkeys** | Global shortcuts for edit mode, refresh, show/hide, peek and Settings. |
| **Data & privacy** | What each widget can reach and store, recent blocked requests, backup and restore, reset, and safe mode. |
| **About** | Version, update status (with release notes once after an update), source link, and **Report a problem** (anonymous bug reports, with a link to a pre-filled GitHub issue). |

## Keyboard

- <kbd>Esc</kbd> closes Settings.
- <kbd>Ctrl</kbd>+<kbd>F</kbd> or <kbd>/</kbd> jumps to the Browse search.
- <kbd>Ctrl</kbd>+<kbd>1</kbd> to <kbd>Ctrl</kbd>+<kbd>7</kbd> switch pages.
- The arrow keys move through the sidebar and the refresh interval.

Switches and buttons have names for screen readers.

## Changes apply live

There is no Save button. Changing a setting updates widgets immediately, and `settings.json` in your data folder is written for you. Settings that need a restart say so: the proxy and hardware acceleration.

## The tray menu

The tray icon's tooltip shows the widget count and whether edit mode or safe mode is on. A dot on the icon means an Umbra update is ready. The menu opens Settings, toggles edit mode, opens the data folder, restarts Umbra, and offers **Undo** for a widget copy you just removed.

## The same settings from a script

Everything in Settings is also available from [`umbractl`](/umbra/docs/automate/cli/) and the [MCP server](/umbra/docs/automate/mcp/), for example `umbractl general --refresh 120` and `umbractl configure sp500 --scale 1.5`.
