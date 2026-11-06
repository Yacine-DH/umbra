---
title: Running local commands
description: How a widget declares and runs a local program, the strict rules around it, and how the user's approval works.
sidebar: { order: 7 }
---

Most widgets never need this. A widget that must start a program (a launcher, `claude -p "Hello"`, a script runner) declares each command in `widget.json`, and the page can only name a declared **id**. This is the one place a widget runs code outside the sandbox, so it is tightly constrained.

## Declare it

```json
"permissions": {
  "commands": [
    { "id": "wake", "run": ["claude", "-p", "Hello"], "description": "Starts the Claude usage window" },
    { "id": "ask",  "run": ["claude", "-p", "${prompt}", "--model", "${model}"],
      "params": { "prompt": { "maxLength": 300 }, "model": { "choices": ["haiku", "sonnet"] } },
      "timeoutSeconds": 90 }
  ]
}
```

## Run it

```js
const r = await umbra.run("wake");   // { exitCode, stdout, stderr, timedOut, truncated, durationMs }
await umbra.run("ask", { prompt: "Say hi", model: "haiku" });
```

## The rules

These are checked when the widget loads, by `scripts/catalog.mjs`, and on install. A violation stops the widget from loading.

- `run` is `[executable, ...arguments]`, **never a shell line**. The executable is a plain program name found on `PATH` (`claude`, `python`) or an absolute local path (`C:\Tools\app.exe`); no relative paths, no UNC. The page can't change it.
- Each argument is a literal, or exactly `"${name}"` for a declared parameter. A parameter can't be part of a longer string (`"--model=${m}"` is rejected), and the executable can't be a parameter.
- A parameter is a string, always supplied, at most `maxLength` characters (default 200, max 1000), without control characters, and **must not start with `-` or `/`** unless it's one of the fixed `choices`.
- If the executable is an interpreter (`cmd`, `powershell`, `pwsh`, `wscript`, `cscript`, `mshta`, `node`, `python`, `py`, `bash`, `sh`, `wsl`, `rundll32`, `regsvr32`, `msiexec`, or a `.ps1`/`.bat`/`.cmd`/`.vbs`/`.js` file), every parameter needs fixed `choices`; free text is refused when the widget loads. A parameter reaches the program as one single argument, so spaces and quotes in it can't add arguments.
- `.cmd`/`.bat` files only start for commands without parameters. `timeoutSeconds` is 1 to 120 (default 60).

## How it runs

The program runs as you, with no window, in `%APPDATA%\Umbra\data\run\<widget id>`, with a scrubbed environment (system variables and `PATH` only: no tokens, no `UMBRA_*`). It and its descendants run in a job object and are killed on timeout or when Umbra exits. At most 1 MB of stdout and of stderr is kept (`truncated` tells you). Calls fail while the widget is disabled, and a running command stops when the widget closes.

## The user approves every command

The first run shows an Umbra prompt with the **exact command line**. The approval is saved for this widget and this command, and is void as soon as the command changes or the resolved program file (its path or SHA-256) differs.

- A prompt only appears right after the user clicked the widget (so a button works; a background timer doesn't), at most every 20 seconds, and not after three refusals. It closes as a refusal after 2 minutes.
- Without an approval, a call rejects with a message you should show ("Approve it in Settings > My widgets").
- Users approve and revoke each command in **Settings → My widgets**, and Browse flags widgets that declare commands.
- **Automation can't approve.** `umbractl approvals` and the MCP `list_command_approvals` are read-only, and backups never contain approvals.

:::caution
Declaring a command makes your widget need a human review. Prefer fixed `choices` over free-text parameters, and keep descriptions honest: they're shown to the user.
:::

`widgets/script-runner` is a worked example of the user's own scripts (below).

## The user's own scripts

A widget that runs whatever the user wants sets `"userScripts": true` in `permissions`. Its card in **Settings > My widgets** then gets a **Your scripts** section where the user adds, edits and deletes scripts (PowerShell, Python, Bash or a program; a file, or code typed right there). The widget lists them with `umbra.commands()`, re-reads them in `umbra.onCommandsChange`, and runs them by id with `umbra.run`.

- Umbra builds the command (`powershell ... -File <script>`, `python <script>`, `bash <script>`, `<program>`), never a shell line, and the row in Settings shows it.
- Saving in Settings is the approval. Only Settings can add, change, delete or approve a script; no `umbractl` or MCP tool can, and the widget's page only sees ids and names.
- Scripts are kept outside the widget folder, so updating the widget keeps them. The same timeout, output cap and environment rules apply.
- An optional **Start in** folder sets where the script runs. Picking a Windows shortcut (`.lnk`) with Browse fills the file, arguments and Start in from it.
- **Keeps running** (a tunnel, a server, a watcher) is a choice in the editor: no time limit, only the last 64 KB of output kept, and it stops when the user presses Stop (widget, Settings or tray), when the widget closes, when the script is edited or deleted, and with Umbra. Optional: start automatically while the widget is on, and restart if it stops (5 tries with growing waits). Only the user's own scripts can keep running, never a command declared in `widget.json`.
- **Run as administrator** (scripts that run and finish only) is a switch in the editor. Each run shows Windows' own UAC prompt, only starts right after a click on the widget, returns its output like any other run (through a small PowerShell helper that Windows elevates), and is approved only in Settings. `widget.json` can't ask for it.
- `umbra.stop(id)` stops a run or a script that keeps running; `umbra.commandOutput(id)` returns what it printed so far, for live output.
