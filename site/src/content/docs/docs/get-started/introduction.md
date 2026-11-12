---
title: Introduction
description: What Umbra is, how it works in one minute, and where to go next in these docs.
sidebar: { order: 1 }
---

Umbra pins small, sharp, true-black widgets to the Windows desktop. They sit under every window, never steal focus, stay visible with <kbd>Win</kbd>+<kbd>D</kbd> and start with Windows.

A widget is a folder with a `widget.json` and a web page (HTML, CSS and JavaScript). Umbra's host app, written in C# on .NET 10, opens each enabled widget in its own borderless WebView2 window and pins it to the desktop. The host knows nothing about any particular widget, so anyone can build one.

## Why it feels different

- **It stays where it belongs.** Widget windows are owned by the desktop shell and kept at the bottom of the window stack, so your apps always cover them.
- **It's sharp.** Windows are opaque true black with corners and a hairline border drawn by Windows itself, so text keeps full ClearType quality. On an OLED screen the black pixels are simply off, and each card seems to float on your wallpaper.
- **It's safe.** A widget can only reach the HTTPS hosts it declares. The host blocks everything else. An update that asks for new access waits for your OK, and a widget can start a local program only if it declared that exact command and you approved it.
- **It's private.** No account, no tracking. Settings, widget data and logs stay in `%APPDATA%\Umbra`; the one thing Umbra reports is an anonymous +1 when you install a catalog widget, and you can turn that off.
- **It's scriptable.** The `umbractl` command line and a built-in [MCP server](/umbra/docs/automate/mcp/) let scripts and AI agents arrange widgets, take screenshots and write new widgets.

## Where to go next

| You want to… | Read |
|---|---|
| Install Umbra and add your first widgets | [Install](/umbra/docs/get-started/install/), then [Quickstart](/umbra/docs/get-started/quickstart/) |
| Arrange, theme and tune your desktop | [Using Umbra](/umbra/docs/using/settings/) |
| Build a widget | [Your first widget](/umbra/docs/widgets/first-widget/) and the [SDK reference](/umbra/docs/widgets/sdk-reference/) |
| Script Umbra or connect an AI agent | [Automate](/umbra/docs/automate/overview/) |
| Work on the app itself | [Internals](/umbra/docs/internals/architecture/) |

:::tip[For AI agents]
Every page has a **View as Markdown** link, and [`llms.txt`](/umbra/llms.txt) lists them all. [`llms-full.txt`](/umbra/llms-full.txt) is the whole manual in one file.
:::

## Requirements

Windows 10 version 1809 or later, or Windows 11. Umbra uses the WebView2 runtime, which ships with current Windows; the installer tells you if it's missing. A Linux version is in progress ([#172](https://github.com/AmineChr54/umbra/issues/172)).

Umbra is free and open source under the MIT license.
