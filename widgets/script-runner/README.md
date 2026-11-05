# Script Runner

Runs your own PowerShell, Python or Bash scripts (or any program) from the desktop and shows the exit code, duration,
last run time and output.

## Add a script

1. Open **Settings > My widgets > Script Runner** (or right-click the widget > Options).
2. Under **Your scripts**, press **Add script** and fill in:
   - **Name**: what the widget shows, e.g. *Backup photos*.
   - **Type**: PowerShell, Python, Bash or Program.
   - **Run**: a **script file** (type the full path or press **Browse...**) or **Write code** right there (PowerShell and
     Python: as many lines as you like; Bash: one line, run with `bash -c`).
   - **Arguments** (optional): separated by spaces, `"quotes"` around one with spaces.
   - **Time limit** (1-120 s, default 60) and a **Description** (optional, shown under the name).
3. Press **Add script**. It appears in the widget right away; press **Run**.

Saving is your approval of exactly that command (the row shows the command line Umbra runs). Edit or delete a script
from the same list. If its program isn't installed yet (say Python isn't on `PATH`), the script is saved as
*Not approved*; install the program, then press **Approve**.

Notes:

- What runs: `powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File <script> <args>`, `python <script> <args>`,
  `bash <script> <args>` or `<program> <args>`. Never a shell line.
- Code you write in Settings is saved by Umbra in `%APPDATA%\Umbra\scripts\script-runner\` and run as a file.
- Editing a script file you picked does not need a new approval; changing the script in Settings saves (and approves) the new one.
- Scripts run in `%APPDATA%\Umbra\data\run\script-runner`, with a time limit and output cut at 1 MB (flagged).
- Your scripts are stored apart from the widget (`%APPDATA%\Umbra\scripts.json`), so updating Script Runner keeps them.
- The three examples (`system-info`, `disk-free`, `python-version`) come with the widget; each asks for your approval
  on its first run.

## Options

Automatic runs: set **Script to run automatically** to a script's name (or an example's id) and **Run it every
(minutes)**; optionally **run it when the widget starts**. Your own scripts are approved when you save them, so they
run on their own straight away; an example needs one click on **Run** and your approval first. **Show the last output
line big** turns the last stdout line into a headline for one-number dashboards.

## For widget authors

This widget sets `permissions.userScripts` and lists what it can run with `umbra.commands()`. See
"The user's own scripts" in `docs/widget-sdk.md`.
