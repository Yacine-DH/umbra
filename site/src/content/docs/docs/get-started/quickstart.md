---
title: Quickstart
description: From a fresh install to an arranged desktop in five minutes, plus your first command-line and widget experiments.
sidebar: { order: 3 }
---

This walks you from a fresh [install](/umbra/docs/get-started/install/) to a desktop you've arranged yourself.

## 1. Add widgets

Click the Umbra icon in the tray (the notification area next to the clock), or open Umbra from the Start menu, to open **Settings**. Go to **Browse** and click **Install** on a few widgets: Clock, Weather and To Do are a good start. Each card lists exactly what the widget can reach (for example `api.open-meteo.com`, or **No network access**), so you know what you're installing.

New widgets appear on your desktop at once, each in its default position.

## 2. Arrange them

Open the tray menu and choose **Edit mode** (or turn it on from **My widgets**). Every unlocked widget gets an outline and a corner handle:

- **Drag** a widget to move it. Guide lines show when it lines up with an edge, a center or a neighbour. Hold <kbd>Shift</kbd> while dragging to skip snapping.
- **Drag the corner** (or hold <kbd>Ctrl</kbd> and scroll) to resize. Widgets zoom like a web page, so text stays sharp.
- **Click a widget** to select it, then nudge it with the arrow keys: 1 px, or 10 px with <kbd>Shift</kbd>.

A small **Editing widgets · Snap · Done** pill appears at the top of each monitor that has widgets. Click **Done** (or press <kbd>Esc</kbd> on a clicked widget) to leave edit mode. See [Arranging widgets](/umbra/docs/using/arranging-widgets/) for everything edit mode can do.

## 3. Make them yours

In **My widgets**, each widget has its own card: change its options (a city, a currency, a time format), its size, its opacity, and when it should be visible. Under **General** you can set the accent color, corner radius and fonts, and **Themes** switches the whole look in one click. All changes apply live.

## 4. Peek and show the desktop

Press <kbd>Win</kbd>+<kbd>D</kbd>: your widgets stay where they are. To bring them above your apps for a few seconds without moving anything, use **Peek**. Assign hotkeys for it, for edit mode, for refresh and for show/hide in **Settings → Hotkeys** ([details](/umbra/docs/using/hotkeys-peek-layouts/)).

## 5. Try the command line

The installer puts `umbractl` on your `PATH`. Every command prints JSON, and starts Umbra in the background if it isn't running:

```powershell
umbractl list                          # your widgets, options and positions
umbractl install sp500                 # install from the catalog
umbractl set sp500 symbol ^IXIC        # follow the Nasdaq instead; applies instantly
umbractl configure sp500 --scale 1.5   # 150 % size
umbractl capture sp500 shot.png        # a PNG of just that widget
```

The [command reference](/umbra/docs/automate/cli/) lists every command.

## 6. Build your own

```powershell
umbractl new my-widget
```

This creates a working widget in `%APPDATA%\Umbra\widgets\my-widget` and shows it on your desktop. Edit its files, run `umbractl reload`, and follow [Your first widget](/umbra/docs/widgets/first-widget/).

:::tip
If something looks wrong, **Settings → About → Report a problem** tells the developer anonymously, and **safe mode** turns community widgets off without deleting anything. See [Troubleshooting](/umbra/docs/using/troubleshooting/).
:::
