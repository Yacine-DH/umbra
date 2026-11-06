# Script Runner

Runs your own PowerShell, Python or Bash scripts (or any program) from the desktop. Scripts that finish show their exit
code, duration and output; scripts that keep running (an SSH tunnel, a dev server, a file watcher) get Start and Stop and
live output.

## Add a script

1. Open **Settings > My widgets > Script Runner** (or right-click the widget > Options).
2. Under **Your scripts**, press **Add script** and fill in:
   - **Example** (optional): start from *System info*, *Free disk space*, *Python version* or *Ping every 5 seconds*.
     It only fills the form; once saved it is your script, and you can edit or delete it like any other.
   - **Name**: what the widget shows, e.g. *Backup photos*.
   - **Type**: PowerShell, Python, Bash or Program.
   - **How it runs**: *Runs and finishes* (a report, a backup) or *Keeps running* (a tunnel, a server).
   - **Run**: a **script file** (type the full path or press **Browse...**; a Windows shortcut `.lnk` works too and fills in
     its file, arguments and Start in) or **Write code** right there (PowerShell and Python: as many lines as you like;
     Bash: one line, run with `bash -c`).
   - **Arguments** (optional): separated by spaces, `"quotes"` around one with spaces.
   - **Start in** (optional): the folder it runs in. Picking a file fills in its folder.
   - For a script that finishes: a **time limit** (1-120 s, default 60). For one that keeps running: **Start
     automatically** (whenever the widget is on, so also after you sign in) and **Restart if it stops**.
   - **Description** (optional, shown under the name).
3. Press **Add script**. It appears in the widget right away.

Saving is your approval of exactly that command (the row shows the command line Umbra runs). If its program isn't
installed yet (say Python isn't on `PATH`), the script is saved as *Not approved*; install the program, then press **Approve**.

## Use it

- **Run** a script; while it runs the output appears live and the button becomes **Stop**.
- A script that keeps running shows **Start** / **Stop**, a green *Running · 2 h 14 m*, and its newest output lines.
  Stop it here, on its row in Settings, or from the tray menu (*1 script running > Stop ...*). It also stops when you turn
  the widget off, edit or delete the script, or quit Umbra.
- **Copy** copies the output. **Earlier runs** lists the last runs of the selected script (when, exit code, duration).
- **Notify me** (options): a Windows toast when a script fails or a running one stops on its own (default), on every
  finish, or never. Also switch on *Allow notifications* for this widget in Settings.

Notes:

- What runs: `powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File <script> <args>`, `python <script> <args>`,
  `bash <script> <args>` or `<program> <args>`. Never a shell line, and with no console window: a script that asks for
  input (a password prompt) waits forever, so use key-based logins for tunnels.
- Code you write in Settings is saved by Umbra in `%APPDATA%\Umbra\scripts\script-runner\` and run as a file.
- Without Start in, scripts run in `%APPDATA%\Umbra\data\run\script-runner`. Output of a script that finishes is cut at
  1 MB (flagged); a script that keeps running keeps its last 64 KB.
- Your scripts are stored apart from the widget (`%APPDATA%\Umbra\scripts.json`), so updating Script Runner keeps them.
- Live output, Stop and scripts that keep running need Umbra 1.12 or later; on an older Umbra the widget works as before.

## Options

Automatic runs: set **Script to run automatically** to a script's name and **Run it every (minutes)**; optionally **run it
when the widget starts**. A script that keeps running uses its own *Start automatically* switch instead. **Show the last
output line big** turns the last stdout line into a headline for one-number dashboards.

## For widget authors

This widget sets `permissions.userScripts` and lists what it can run with `umbra.commands()`; it uses `umbra.stop()` and
`umbra.commandOutput()` for Stop and live output. See "The user's own scripts" in `docs/widget-sdk.md`.
