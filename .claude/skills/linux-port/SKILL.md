---
name: linux-port
description: Drive the Umbra Linux port (epic #172) from an Ubuntu GNOME machine - pick the next ready Linux issue in order, hand it to agents in worktrees, review, test on Linux and open PRs from a fork. Meant to run as "/loop 2m /linux-port". Use for "work on the Linux issues", "continue the Linux port".
---

# Linux port loop

You are working on the Linux port of Umbra: **epic #172** on `AmineChr54/umbra`. Read the epic once at the start
(`gh issue view 172 -R AmineChr54/umbra`). It holds the design (repo layout, dependencies, the Windows → Linux mapping,
the test harness) and the ordered task list. The maintainer already did #173–#177 (the `linux/phase-0` PR). Your issues
are **#178 → #200, in that order**. You run on a **real Ubuntu GNOME desktop** that belongs to a person, so the safety
rules below are not optional.

This skill runs every 2 minutes (`/loop 2m /linux-port`). Each tick must be **short and idempotent**: when nothing changed,
do one or two cheap checks and end the tick. Don't re-read the codebase on every tick; agents do the heavy lifting.

## State
- GitHub is the source of truth: issue state, "I'm taking this" comments, your open PRs and their checks.
- Keep your own notes in `~/.local/state/umbra-linux/loop.md` (running agents: issue, agent name, branch, worktree; PRs
  opened; blockers). Read it at the start of every tick and update it at the end. Never commit it.
- Human checks you can't do yourself go into `~/.local/state/umbra-linux/human-checks.md` (see step 6).

## First tick only: prerequisites
Check, don't install: `dotnet --version` (10.x), `pkg-config --modversion gtk4 webkitgtk-6.0`, `pkg-config --exists gtk4-layer-shell-0`,
`gcc --version`, `node --version` (22+), `gh auth status`, and that the working copy is a fork clone with
`upstream` = AmineChr54/umbra. If something is missing, **stop the tick** and tell the person exactly what to run
(the apt line is in the epic's "Getting started on Ubuntu"); suggest typing `! <command>` so the output lands in the
session. Never run `sudo` yourself.

If the `linux/phase-0` PR is not merged yet, base work on `upstream/linux/phase-0`; once it's merged, rebase onto `upstream/main`.

## Every tick
1. **Sync** (cheap): `git fetch upstream -q`; `gh pr list -R AmineChr54/umbra --author @me --state all --json number,title,state,headRefName,statusCheckRollup --limit 50`.
2. **Harvest finished agents** (their completion notifications arrive on their own; never poll their transcripts):
   - The agent reports a PR → run the `host-reviewer` agent on that PR's diff for anything touching `src/`, `settings/`,
     `shared/` or `linux/`. Send the findings back to the **same** implementing agent with SendMessage to fix. Don't start a new agent.
   - CI red on one of your PRs (the `local-ci` status in `gh pr checks N`, which the maintainer's `scripts/ci-local.ps1` posts; GitHub
     Actions is off) → run `powershell -File scripts/ci-local.ps1` on that branch yourself (needs Docker) and send the failing job's log
     (`dist/ci/<sha7>/<job>.log`) to the agent that owns it.
   - The maintainer asked for changes in a review → same: route them to that PR's agent (or a fresh agent if that one is gone,
     with the PR number and the review comments).
   - You never merge; the maintainer does.
3. **Pick ready issues**, at most **3 agents running at once**:
   - Go down the epic's task list. An issue is **ready** when it is open, has no assignee, has no open PR from you, and every
     "Depends on #N" is either closed or has an open PR from you (then **stack**: branch from that PR's branch and write
     "Stacked on #PR, review that first" at the top of the new PR).
   - Don't run two agents that edit the same files at once. In particular the first host skeleton (#179) goes alone. After it,
     these can run in parallel: #180 (sandbox), #181 (control + capture), #182 (X11 pin), #185 (crash recovery), #189–#193 (integrations).
   - Before starting an issue, comment `I'm taking this` on it (`gh issue comment N -R AmineChr54/umbra --body "I'm taking this"`).
4. **Launch** with the Agent tool, one issue per agent (two small ones in the same files may share), `isolation: "worktree"`,
   in one message when you start several. Use `subagent_type`: `general-purpose` for host work, `widget-builder` for #195,
   `docs-writer` for #199. Every agent prompt contains:
   - `Implement issue #N on AmineChr54/umbra (gh issue view N). Read CLAUDE.md and epic #172 first.`
   - The base branch to start from (upstream main, `upstream/linux/phase-0`, or the stacked PR's branch) and the new branch name `linux/<N>-<slug>`.
   - The safety rules below, word for word.
   - "Done means: `dotnet build Umbra.Linux.slnf -warnaserror` at 0 warnings, `dotnet test Umbra.Linux.slnf` green, the issue's
     acceptance boxes checked with evidence (commands run, `umbractl capture` PNGs you looked at), Windows untouched or still
     building (CI checks it). Push to the fork and open the PR with `gh pr create --repo AmineChr54/umbra`, body with
     `Closes #N`, what you verified, and a **Human check** section for anything you couldn't verify."
   - The commit and PR attribution lines from your session's system reminder.
   - "Be economical: read only what you need. Final report: 5 lines max (PR URL, what was done, what needs a human check)."
5. **Write the tally** to `loop.md`: running, in review, done, blocked. Print one line to the person only when something changed.
6. **Human checks**: append each PR's Human check items to `human-checks.md` with the PR number. When new items arrive, send one
   short `PushNotification` ("3 new Linux checks need you: see ~/.local/state/umbra-linux/human-checks.md"). Never mark a human
   check as done yourself, and never fake one.
7. **Stop** when every issue #178–#200 is closed or has an open PR from you and no agent is running: cancel this loop
   (CronList, then CronDelete on the `/linux-port` job), send a `PushNotification` with the summary, and print the PR list
   plus the open human checks.

## Safety rules (paste into every agent prompt)
- This is a person's real desktop. **Never take desktop or full-screen screenshots.** Inspect Umbra only with
  `umbractl capture <widget|settings:page> out.png`, which captures only that web view. Compositor screenshots are allowed only inside a
  throwaway nested or headless session you started yourself (`dbus-run-session -- gnome-shell --nested --wayland`,
  `kwin_wayland --virtual`, `xvfb-run`).
- Run development builds with a throwaway profile, `UMBRA_DATA_DIR=$(mktemp -d)`. Never touch `~/.local/share/umbra` of
  an installed Umbra. Only kill processes you started.
- No `sudo`, no system-wide installs, no changes to GNOME settings or extensions outside a nested session. If something needs
  that, stop and report it as a blocker.
- Security invariants in CLAUDE.md apply unchanged on Linux (sandbox, `fetchJson` allow-list, navigation lock, command approvals,
  focus gating, current-user-only control socket, no network listener). Never weaken one to make a test pass. If parity looks
  impossible, stop and write it up in the PR for the maintainer.
- Keep Windows building: don't change Windows behaviour unless the issue says so. CI runs the Windows build and smoke test on every PR.
- Only the NuGet packages named in the epic (`GirCore.Gtk-4.0`, `GirCore.WebKit-6.0`). Anything else needs a reason in the PR.
