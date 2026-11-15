---
title: Troubleshooting
description: Find out why a widget misbehaves, read logs, use safe mode and diagnostics, and understand what Umbra does when a widget crashes.
sidebar: { order: 9 }
---

## Start here

1. **Is the widget enabled and visible?** Check **My widgets**: enabled, not hidden, and not limited by a time range or "only on AC power". The tray tooltip shows if edit mode or safe mode is on. Widgets also hide while a fullscreen app is in front if that option is on.
2. **Read the log.** `umbractl logs 100` prints the last lines (widget load errors, blocked network requests, crashes). The file is `%APPDATA%\Umbra\umbra.log`, and **Data & privacy → Open log** opens it. For more detail, set **General → Log level** to *verbose* (`umbractl general --log-level verbose`).
3. **Check blocked requests.** If a widget shows no data, **Data & privacy** lists requests it made to hosts it didn't declare. A widget can only reach HTTPS hosts listed in its `widget.json`.
4. **Try safe mode.** `umbractl safe-mode on` (or `Umbra.exe --safe`) closes all community widgets without deleting anything. If the problem goes away, turn widgets back on one at a time.

## Reporting a bug

**Settings → About → Report a problem** sends the developer what you write, anonymously. For a GitHub issue, `umbractl diagnostics` produces a Markdown block with the Umbra version, Windows build, WebView2 version, monitors with DPI, GPU and proxy state, enabled widgets with versions and the last 30 log lines with your user paths hidden. It contains no option values, no proxy address and no widget data. Paste it into an [issue](https://github.com/AmineChr54/umbra/issues/new/choose); the GitHub link in the Report a problem box pre-fills your version.

## Common problems

**A widget stopped working.** If a widget's renderer exits unexpectedly, Umbra reloads it automatically, up to 3 times in 10 minutes. After that you see a host-drawn **"This widget stopped working"** page with a **Reload** button instead of a blank window. A hung renderer is only logged. If the whole WebView2 browser process exits, Umbra recreates it. Data & privacy shows each widget's crash count. See [Crash recovery](/umbra/docs/internals/crash-recovery/).

**A hotkey does nothing.** `umbractl hotkeys` shows each hotkey's status. `failed` includes the reason, most often that another app already owns that combination.

**Umbra says it's already running.** Only one instance runs per user (a portable copy has its own). A second launch opens Settings in the running copy instead. If you want to run from source, quit the installed Umbra first.

**WebView2 is missing.** Umbra needs the WebView2 runtime, which ships with current Windows. On startup Umbra checks for it and tells you if it's missing; install the Evergreen runtime from Microsoft.

**Widgets show "Offline".** The little status dot means the last fetches failed. Check your network or proxy (**General → Advanced → Proxy**).

**Everything is broken after a change.** **Data & privacy → Reset everything** returns Umbra to a clean state, or restore a [backup](/umbra/docs/using/data-privacy-backups/).

## For widget developers

`umbractl reload` re-scans widget folders after editing, `umbractl capture <id> out.png` shows exactly what the widget renders, and starting Umbra with `UMBRA_SIMULATE_OFFLINE=1` makes every host fetch fail so you can test the offline state. See [Testing with capture](/umbra/docs/widgets/testing/).
