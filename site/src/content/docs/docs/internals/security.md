---
title: Security model
description: The invariants Umbra enforces for widgets, community installs, commands, themes, automation and updates, and why each exists.
sidebar: { order: 4 }
---

Umbra runs code written by other people on your desktop, so its security rules are part of the design, not an afterthought. These are the invariants. **Never weaken one without an issue and review.** To report a vulnerability, read [SECURITY.md](https://github.com/AmineChr54/umbra/blob/main/SECURITY.md).

## The widget sandbox

- A widget page can only reach `umbra.local`, `community.umbra.local`, `data:`, `blob:` and **HTTPS hosts in its `permissions.hosts`**. Everything else gets a 403 from `WebResourceRequested` in `WebHost.InitAsync`.
- `fetchJson` goes through `WidgetManifest.AllowsUrl`: HTTPS plus a declared host.
- Top-level navigation off the virtual hosts is cancelled and links open in the user's browser.

## Input and focus

- Widget windows **never activate themselves**. They get keyboard focus only right after a physical mouse press on them (`WidgetWindow.TakeKeyboardFocus`), so a widget can't pull focus while you type elsewhere.
- A widget **can't resize or zoom itself**. The page's `resize` and `zoom` messages are honoured only in edit mode, for an unlocked widget, with a physical mouse press (resize) or <kbd>Ctrl</kbd> held (zoom) over that window.

## Files

Community and agent-written files: relative forward-slash paths only, no `..`, allowed extensions only, 5 MB total, id equals folder. Everything is staged in a temp folder and validated **before** `Commit` (`Community.cs`). Built-in widgets (the generic `<Content>` mechanism, none shipped today) can't be removed or shadowed by community or agent ids.

## The one exception: commands

The one exception to "a widget cannot run code" is `permissions.commands` (`WidgetCommands.cs`, `CommandService.cs`, `CommandRunner.cs`):

- A widget runs only a command it **declared**: exact executable plus an argv array, **no shell**. Parameters are `${name}` whole-argument placeholders, validated and passed as single arguments, never a free command line.
- **Every command needs the user's approval**, stored per widget id and command hash in `approvals.json`. A changed command is asked again.
- Only a **host-drawn prompt** (after a physical click on the widget, rate-limited) or the Settings button approves. **No control command, `umbractl` command or MCP tool may approve**: `approvals` is read-only.
- Runs are as the user, in the widget's data directory, with a scrubbed environment, a timeout (max 120 s) and a 1 MB output cap, and never while the widget is disabled. Built-in widgets get no automatic trust.

See [Running local commands](/umbra/docs/widgets/local-commands/) for the rules a widget author sees.

## Updates never grant access silently

`Community.PrepareAsync` diffs the downloaded manifest against the installed one (`PermissionDiff`: hosts, system, media, notifications, commands), and `PendingInstall.Require` refuses until the user accepts. Settings asks with a host-drawn box (`SettingsWindow.ConfirmNewAccess`), `umbractl install` needs `--accept-permissions`, and the MCP server strips that argument. Any new install path (for example "Update all") must go through `PrepareAsync`/`InstallAsync` with `acceptPermissions` false by default.

## Themes are data

Token names come from `Theme.Allowed`; values must match the color and length patterns (mirrored in `scripts/catalog.mjs` and `schemas/theme.schema.json`), and catalog tokens are re-validated before Settings draws them. A theme can't contain code, `url()` or any other way to load something.

## Automation

The control pipe uses `PipeOptions.CurrentUserOnly`. **Never add a network listener.** Control commands may check for updates but never install one.

## Self-update

`Updates.cs` runs in the host only. The installer must be an **HTTPS** download from `github.com/AmineChr54/umbra/releases/download/` and named `UmbraSetup-<release version>.exe`; it's verified before it runs.

## Privacy

No account and no tracking. The network traffic Umbra itself makes is the daily GitHub release check, the catalog (and its install counts) when Browse is open, an anonymous +1 per fresh catalog install (`InstallStats`: the widget id only, off in Data & privacy), and requests widgets make to hosts they declared. Backups never include command approvals, and diagnostics contain no option values, proxy address or widget data.

## Related

[Sandbox and permissions](/umbra/docs/widgets/sandbox/) · [WebView2 host](/umbra/docs/internals/webview-host/) · [Updates](/umbra/docs/using/updates/)
