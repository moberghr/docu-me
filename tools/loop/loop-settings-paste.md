# Paste: wire the force-push guard into `tools/loop/loop-settings.json`

**One paste, no drafting. It closes a measured hole in rule §8.2's enforcement and grants the loop
nothing.** Replace the entire contents of `tools/loop/loop-settings.json` with the block below.

## What this fixes (measured at iter130, nothing was pushed to find it)

Rule §8.2 in `.claude/rules/git-workflow.md` reads **"[ENFORCED — loop-settings deny + protocol]"**.
The enforcement half was these two lines in that file:

    "Bash(git push --force:*)"
    "Bash(git push -f:*)"

Those patterns match whole tokens **from the start of the command**, so they only cover the spelling
where the flag comes first. Probed with a remote name that does not exist, so no probe could reach
GitHub or push anything:

| command | result |
|---|---|
| `git push --force <remote> HEAD` | **denied** |
| `git push -f <remote> HEAD` | **denied** |
| `git push <remote> HEAD --force` | **allowed** ← the hole |
| `git push <remote> HEAD -f` | **allowed** ← the hole |
| `git push --force-with-lease <remote> HEAD` | **allowed** (a different token, so no match) |
| `git tag --list` | denied (read-only, and denied — see the note at the bottom) |

With `origin main` in place of the nonexistent remote, the third row would have rewritten
`origin/main`'s history, which §8.2 says never happens and which cannot be undone. **No deny list
can close this**: a remote and a refspec of any name sit between `git push` and the flag, so there
is no prefix to match on. A `PreToolUse` hook can, because it sees the whole command string.

The hook is `tools/loop/hooks/deny-history-rewrite.py` (already committed, and inert until this
paste wires it). It refuses `git push` when a force flag appears **anywhere** in the argv, including
`--force-with-lease`, `--force-if-includes`, `--mirror`, short clusters like `-uf`, a `+refspec`
force that carries no flag at all, global options before the subcommand (`git -c … push … --force`),
and a force push in the second half of a `&&` chain. Proven both directions, 25/25, by
`python3 .mtk/paths-130/mutate-force-push-guard.py` — 13 must-block cases (the first three are the
rows measured as allowed above), 10 must-allow cases covering the commands this loop runs every
iteration, one documented over-block, and one non-Bash tool that must pass through untouched.

## Why the loop is not doing this itself

`Edit` on `tools/loop/loop-settings.json` was **refused** under `--permission-mode acceptEdits`,
while a `Write` of a brand-new file under `tools/loop/hooks/` succeeded in the same session — so the
guard is specific to the settings file, not to the directory. That guard is correct and the loop
should not route around it with an allowlisted `python3`, even though this particular change only
*narrows* authority: anything that can add a `PreToolUse` hook to its own settings can also add one
that auto-approves. Same reasoning that has kept `paste-rule-8-2a` in your hands for 50-odd
iterations. `.mtk/paths-130/validate-settings-paste.py` asserts the block below adds **no** `allow`
entry, drops none, and keeps every existing `deny`.

## The paste

```json
{
  "permissions": {
    "allow": [
      "Bash(dotnet:*)",
      "Bash(git:*)",
      "Bash(gh:*)",
      "Bash(claude:*)",
      "Bash(node:*)",
      "Bash(npm:*)",
      "Bash(npx:*)",
      "Bash(python3:*)",
      "Bash(jq:*)",
      "Bash(docume:*)",
      "Bash(curl:*)",
      "Bash(mkdir:*)",
      "Bash(cp:*)",
      "Bash(mv:*)",
      "Bash(touch:*)",
      "Bash(ls:*)",
      "Bash(cat:*)",
      "Bash(head:*)",
      "Bash(tail:*)",
      "Bash(grep:*)",
      "Bash(rg:*)",
      "Bash(sed:*)",
      "Bash(awk:*)",
      "Bash(tr:*)",
      "Bash(cut:*)",
      "Bash(sort:*)",
      "Bash(uniq:*)",
      "Bash(wc:*)",
      "Bash(diff:*)",
      "Bash(xargs:*)",
      "Bash(basename:*)",
      "Bash(dirname:*)",
      "Bash(printf:*)",
      "Bash(echo:*)",
      "Bash(find:*)",
      "Bash(date:*)",
      "Bash(which:*)",
      "Bash(chmod:*)",
      "Bash(unzip:*)",
      "Bash(tar:*)",
      "Write(.claude/**)",
      "Edit(.claude/**)",
      "WebSearch",
      "WebFetch",
      "PushNotification"
    ],
    "deny": [
      "Bash(git push --force:*)",
      "Bash(git push -f:*)",
      "Bash(git push --force-with-lease:*)",
      "Bash(git push --force-if-includes:*)",
      "Bash(git push --mirror:*)",
      "Bash(git tag:*)",
      "Bash(git push --tags:*)",
      "Read(./.env)",
      "Read(.env.*)"
    ]
  },
  "hooks": {
    "PreToolUse": [
      {
        "matcher": "Bash",
        "hooks": [
          {
            "type": "command",
            "command": "python3 \"$CLAUDE_PROJECT_DIR/tools/loop/hooks/deny-history-rewrite.py\""
          }
        ]
      }
    ]
  }
}
```

Changes against the current file, and there are no others: three `deny` entries added
(`--force-with-lease`, `--force-if-includes`, `--mirror` — the flag-first spellings a deny list
*can* express, so the guard survives even if the hook is ever unwired), and the `hooks` block. The
`Write(.claude/**)` / `Edit(.claude/**)` allow entries are left exactly as they are; they have been
dead since iter68 and removing them is a separate question, not this paste's business.

## After you paste

Nothing to run — the next iteration picks it up when `docume-loop.sh` starts a session with
`--settings`. **The next iteration's own check is one command**, and it is written into
`state.json → nextAction`:

    git push deny-probe-nonexistent-remote HEAD --force

Before the paste that command is permitted and fails on the bad remote name. After it, the hook
refuses it and prints the §8.2 message. That is a falsifiable before/after, which is the only way
the loop can tell whether hooks are honoured from a `--settings` file at all — a thing nothing in
this repo has ever established.

## One thing this paste does NOT fix, because it would widen authority

`"Bash(git tag:*)"` denies **every** `git tag` subcommand, including read-only `git tag --list`. So
the loop cannot confirm whether a `v0.1.0` tag exists, which is `gate-m6-first-release` step 1's
subject matter. Narrowing that to `Bash(git tag -a:*)`/`Bash(git tag -d:*)` would hand the loop a
capability it does not have today, and that is your call, not the loop's. Say the word in
`state.json → decisions.pushPolicy` (same neighbourhood) and it stays denied until then.
