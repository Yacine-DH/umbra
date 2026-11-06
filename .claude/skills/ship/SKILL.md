---
name: ship
description: Commit and push the current issue or task, open a PR, wait for CI, then hand it to a fresh-context reviewer agent that merges it if everything is okay. Use for "ship this", "commit and PR this", "turn this into a PR and get it reviewed and merged".
---

# Ship the current task

The user has authorised this skill to merge: a PR the reviewer approves merges without asking again. The author (you) never
reviews or merges your own PR.

Without write access (`gh repo view AmineChr54/umbra --json viewerPermission`), you are a contributor: work from a fork as
`CLAUDE.md` describes, stop once CI is green and report the PR URL. The maintainer reviews and merges.

## 1. Commit and push

1. Run `git status` and `git diff`. Stage only the files of this task, by name. If unrelated changes are in the tree, leave
   them unstaged and mention them in the final report. If there's nothing to ship, say so and stop.
2. On `main`, create a branch first (`fix/<topic>`, `feat/<topic>`, `chore/<topic>`, `docs/<topic>`).
3. If `widgets/` or `themes/` changed: bump the widget's `version` and run `node scripts/catalog.mjs`, because CI fails on stale
   index files. Run the local checks that apply to the change (`dotnet build Umbra.slnx -warnaserror`, `dotnet test Umbra.slnx`).
4. Commit with the attribution line from the session reminder and push with `git push -u origin HEAD`.

## 2. Open the PR

If the branch already has an open PR, push to it and go to step 3. Otherwise run `gh pr create --base main` with:

- A specific title, and the `type:` and `area:` labels that fit.
- A body with `Closes #N` (if there's an issue), `## Summary` and `## Testing`. Under Testing, say what you ran and what you didn't.
- One line starting `Needs human check:` for each thing a person must try (drag, real hardware). The `release-checklist` skill collects these lines.
- The PR attribution line from the session reminder at the end.

## 3. Wait for CI

CI takes about 2 minutes. Start the wait with the Bash tool and `run_in_background`, then end your turn until it notifies you.
The delay is there because checks don't exist until a moment after the push:

```bash
sleep 30; gh pr checks <N> --watch --interval 20
```

If a check failed, read it with `gh run view <run-id> --log-failed`, fix the cause, push and wait again. Don't start the
reviewer on a red PR. After 3 failed attempts, stop and report the failure to the user.

## 4. Hand off to a fresh reviewer

Get the SHA with `gh pr view <N> --json headRefOid`, then start the `pr-reviewer` agent with the Agent tool, without `fork`, so it
starts with no context from this session. If that agent type isn't listed yet, use `general-purpose` and tell it to read
`.claude/agents/pr-reviewer.md` and follow it.

Keep the prompt to this, so that your reasoning doesn't colour the review:

```
Review and, if everything is okay, merge PR #<N> on AmineChr54/umbra. Head SHA: <sha>.
```

The review runs in the background, so end your turn until it reports.

## 5. Act on the verdict

- **MERGED:** run `git switch main; git pull --ff-only` (skip this in a worktree) and go to step 6.
- **CHANGES:** fix each finding, or leave a PR comment saying why it's wrong. Push, repeat step 3, then send the same reviewer the new
  SHA with SendMessage. A new agent would re-read everything for no gain, since this one never saw your reasoning either. After 3 review rounds,
  stop and report the open findings to the user.
- **BEHIND** (conflict, or the merge was refused because `main` moved): rebase on `origin/main`, `git push --force-with-lease`,
  repeat step 3, then send the reviewer the new SHA.
- **HOLD:** the PR needs the maintainer. Don't merge it yourself, leave it open and pass on the reviewer's reason.

## 6. Report

Give the PR URL, the outcome, any `Needs human check:` lines, the nits the reviewer left and any unrelated changes you left uncommitted.
