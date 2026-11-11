---
title: Sandbox and permissions
description: What a widget can and cannot do, and the permissions that widen it.
sidebar: { order: 8 }
---

Every widget runs in a WebView2 window with the host app watching its requests. The rules are the same for built-in, community and agent-written widgets.

## What a widget can do

- Load its own files from `community.umbra.local`, and Umbra's SDK and styles from `umbra.local`.
- Use `data:` and `blob:` URLs.
- Reach the HTTPS hosts listed in `permissions.hosts`, through `umbra.fetchJson` / `fetchText` (which avoids CORS) or plain `fetch` where the server allows it.
- Save data in its own `umbra.store` file (1 MB), and nothing else on disk.
- Open an http(s) link in your browser through `umbra.openUrl`.

## What a widget cannot do

- **Reach any other host.** Any other request, including images, scripts, fonts and XHR, gets an HTTP 403 from the host app. HTTP (non-TLS) is never allowed.
- **Navigate away or open windows.** Top-level navigation off the virtual hosts is cancelled, and links open in your browser.
- **Take focus.** It gets the keyboard only right after a physical mouse press on it.
- **Resize or zoom itself.** The page's resize and zoom messages are honoured only in edit mode, for an unlocked widget, with a physical mouse press (resize) or <kbd>Ctrl</kbd> held (zoom) over that window.
- **Use devtools or the browser context menu.** Right-click opens Umbra's own menu (Refresh, Options, Lock, Edit mode, Hide, Add another copy); a widget that calls `preventDefault()` on `contextmenu` keeps its own, and <kbd>Shift</kbd>+right-click always opens Umbra's. Browser zoom keys are off.
- **Touch the file system or the machine**, except through user-approved [commands](/umbra/docs/widgets/local-commands/).

## Permissions

| Permission | Lets the widget |
|---|---|
| `hosts` | Reach specific HTTPS hostnames |
| `notifications` | Show Windows toasts (and only if you switch notifications on for it) |
| `media` | Read (`"read"`) and/or control (`"control"`) the current media session |
| `system` | Read CPU, memory, network, GPU or disk totals |
| `commands` | Start specific local programs, each approved by you |

Browse shows every permission on each widget's card, and **Data & privacy** shows what each installed widget can reach and which requests were blocked.

## Updates can't widen it silently

An update to an installed catalog widget never grants new access on its own. Umbra compares the new manifest with the installed one (hosts, system, media, notifications, commands) and refuses until you accept. See [Updates](/umbra/docs/using/updates/#updates-that-ask-for-more). Files written by community installs and by agents are validated too: relative forward-slash paths only, no `..`, allowed extensions only, 5 MB, id equal to the folder, all staged in a temp folder and checked before they're committed.

## Why it matters when you build

- Declare **every** host you call, and no more: reviewers read this list.
- Prefer offline designs. `"hosts": []` is the easiest widget to approve.
- Don't ask for notifications, media, system or commands unless the widget's purpose needs it.

The deeper reasoning and the exact checks are in the [security model](/umbra/docs/internals/security/).
