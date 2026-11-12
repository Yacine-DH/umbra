---
title: FAQ
description: Short answers about how Umbra works, what it can access, and how it compares to other desktop widget tools.
sidebar: { order: 4 }
---

## Does Umbra work with Win+D?

Yes. Each widget window is owned by the desktop shell and kept at the bottom of the window stack, so <kbd>Win</kbd>+<kbd>D</kbd> leaves your widgets visible and every app covers them. See [Desktop pinning](/umbra/docs/internals/desktop-pinning/).

## Will a widget steal focus while I type?

No. Widget windows never activate themselves. A widget gets the keyboard only right after you press the mouse on it (for example to type in a text field). A widget can't pull focus on its own.

## Is it safe to install community widgets?

Each widget runs in a sandbox. It can load only Umbra's own virtual hosts and the HTTPS hosts it declares in its `widget.json`; every other request is blocked by the app. An update that asks for new access (more hosts, notifications, media, commands) waits for your approval, and a widget can start a local program only if it declared that exact command and you approved it. Every catalog widget is also reviewed in a pull request. Details: [Sandbox and permissions](/umbra/docs/widgets/sandbox/) and the [security model](/umbra/docs/internals/security/).

## Does Umbra collect data?

No account and no tracking. Settings, widget data and logs stay in `%APPDATA%\Umbra` on your PC. The only network traffic Umbra itself makes is a daily check for a new release on GitHub, the widget catalog and its install counts when you open **Browse**, one anonymous +1 for a catalog widget when you install it (just the widget's id; turn it off in **Data & privacy**), and the requests widgets make to hosts they declare. **Settings → Data & privacy** shows what each widget can reach and store.

## Why are the windows opaque black, not transparent?

On Windows, translucent layered windows lose ClearType, so text gets blurrier. Umbra's windows are opaque true black with DWM-drawn rounded corners and a hairline border, which keeps full text quality. On an OLED display black pixels are off, so each card still looks like it floats on your wallpaper.

## How is this different from Rainmeter?

Rainmeter skins are described in its own format and drawn on translucent windows. Umbra widgets are ordinary web pages (HTML, CSS, JavaScript) in WebView2, so anything you can build for the web works, with a small SDK for refresh, options, saved data, notifications and media. The original Rainmeter skin of this project is still in [`extras/rainmeter`](https://github.com/AmineChr54/umbra/tree/main/extras/rainmeter).

## Does it use a lot of memory or battery?

Each widget is a WebView2 window. Umbra pauses widgets while the PC is locked or the display is off, stretches refresh intervals 4x on Windows Energy Saver (you can turn that off), and can hide widgets during fullscreen apps and games. Keep the number of widgets reasonable on low-end machines.

## Can I run two copies of one widget?

Yes: **My widgets → Add another copy**. Each copy has its own options, position, size and saved data, so you can show the S&P 500 and the Nasdaq side by side. See [Copies and tab groups](/umbra/docs/using/copies-and-tab-groups/).

## Where is my data?

In `%APPDATA%\Umbra`: `settings.json`, `widgets\` (installed widgets), `data\<id>.json` (what each widget saved), `themes\`, `trash\` and `umbra.log`. Portable copies keep all of it in a `data` folder next to `Umbra.exe`. Use **Data & privacy → Export backup** (or `umbractl backup <file>`) to save it.

## How do I update Umbra and its widgets?

Umbra checks GitHub daily, downloads updates in the background and shows a dot on the tray icon when one is ready; install it from the tray menu or **Settings → About**. Widget updates appear on **Browse**. See [Updates](/umbra/docs/using/updates/).

## Does it run on macOS or Linux?

Not yet. Umbra needs Windows 10 version 1809 or later, or Windows 11. A Linux version is in progress, see [Linux port](/umbra/docs/internals/linux-port/).

## Can an AI agent build widgets for me?

Yes. Run `claude mcp add umbra -- umbractl mcp` (or point any MCP client at `umbractl mcp`) and ask for the widget you want. The agent reads the authoring guide, writes the widget, takes a screenshot of just that widget and fixes what it sees. See [MCP server](/umbra/docs/automate/mcp/).

## I found a bug. What do I do?

Open **Settings → About → Copy diagnostics**, then use the issue link on the same page (it pre-fills your version) and paste the diagnostics. For security problems, read [SECURITY.md](https://github.com/AmineChr54/umbra/blob/main/SECURITY.md) instead.
