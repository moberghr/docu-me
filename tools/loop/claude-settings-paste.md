# The `.claude/settings.json` format-on-edit paste (blocker `paste-format-on-edit-hook`, opened iter133)

**For Mirko: one paste, nothing waits on it, and it fixes automation this repo has claimed to have
since the MTK bootstrap on 2026-07-24 and has never once run.** Replace the whole of
`.claude/settings.json` with the single fenced `json` block below. The loop cannot write `.claude/`
(path-based guard above the permission layer, `blockersArchive.settled.claude-dir-writes`) and
deliberately does not route around it with an allowlisted `python3`, because the file it would be
editing is the one that governs its own behaviour.

## What is broken

`.claude/settings.json` declares a formatter that runs after every edit:

```json
"command": "bash \"$CLAUDE_PLUGIN_ROOT/hooks/format-on-edit.sh\""
```

It has never worked, for two independent reasons, both measured at iter133 by
`python3 .mtk/paths-133/probe-project-settings-hook.py` (three child sessions) and
`python3 .mtk/paths-133/probe-hook-env.py`:

| Question | Measured answer |
|---|---|
| Does a hook in `.claude/settings.json` fire at all while this workspace is untrusted? | **Yes.** The untrusted state gates only `permissions.allow`, not `hooks`. This closes the lead iter131 left open. |
| What does `$CLAUDE_PLUGIN_ROOT` expand to there? | **The empty string.** The CLI sets it only for hooks that come *from a plugin*; a project settings file has no plugin. |
| So what does the command run? | `bash "/hooks/format-on-edit.sh"` → **exit 127, "No such file or directory"**. |
| Does that script exist anywhere? | **No.** `.claude/settings.json:20` was the only reference to it in the whole tree; there is no `.claude/hooks/` directory. It was never generated. |
| Does `$CLAUDE_PROJECT_DIR` work instead? | **Yes** — measured as `/Users/mirkobudimir/Dev/docu-me`. |
| Why did 133 iterations not notice? | **A failing PostToolUse hook is invisible to the agent.** The event carried `exit_code=127, outcome='error'`, yet no user/assistant turn mentioned it and the session's `result.is_error` was `False`. |

That last row is the reason this sat unnoticed for two days of continuous running, and it is the
generalisable lesson: a hook cannot be verified by waiting for it to complain.

## What the paste changes

**One string.** The `permissions` block is byte-identical — same three `allow` entries, same two
`deny` entries, nothing added. `python3 .mtk/paths-133/validate-claude-settings-paste.py` asserts
exactly that before you paste anything, and it fails if the block grants so much as one extra entry.

The hook now points at `tools/hooks/format-on-edit.py`, which is **committed, reviewable, and
tested** (`python3 .mtk/paths-133/probe-format-on-edit-script.py`, 5/5): it reformats a mangled `.cs`
file back to byte-identical committed formatting in ~7.5 s, and no-ops in ~0.0 s for Markdown, for a
missing path, for a payload with no `file_path`, and for a file outside the repo. It always exits 0
and prints nothing — a formatter must not be able to fail the tool call it tidies up after, and
(per the table above) a non-zero exit would not reach the agent anyway.

## The cost, measured, because it is the part worth your judgement

| Command | Wall time, one file | Covers |
|---|---|---|
| `dotnet format <slnx> --include <file>` | **~7.0 s** | whitespace + style + analyzer fixes |
| `dotnet format whitespace <slnx> --include <file>` | ~1.9 s | whitespace only |

The hook uses the **full** run, because the loop's own gate is `dotnet format --verify-no-changes`
(also the full check), so the cheap subcommand would leave diffs that gate still fails on. The scope
is **`.cs` only**, so the majority of this loop's writes (Markdown, JSON, YAML) cost nothing. What
you buy for ~7 s per C# edit is the removal of the build-fails-on-a-whitespace-analyzer cycle —
iter132 hand-fixed an SA1515 that this hook would have absorbed.

**If you would rather not pay that, the honest alternative is to delete the hook** rather than leave
a dead one in a committed file: keep the same block but drop the whole `"hooks"` key. Say which you
prefer in `tools/loop/state.json → decisions.formatOnEditHook` (`"fix"` or `"delete"`); either answer
closes this and the loop will stop carrying the ask. Leaving it exactly as it is today is the one
option with nothing to recommend it, since the repo then keeps advertising a formatter it does not
have.

## After you paste

`.claude/settings.json` is tracked by git, so the change needs committing — **the loop will commit
it** on its next iteration (it cannot write the file, but `git add` is not blocked; iter89 committed
`.claude/references/` edits the same way). It will also re-run the two probes above as the in-situ
confirmation and record the result.

---

```json
{
  "permissions": {
    "allow": [
      "Bash(dotnet build:*)",
      "Bash(dotnet test:*)",
      "Bash(dotnet format:*)"
    ],
    "deny": [
      "Read(**/appsettings.Production.json)",
      "Bash(dotnet publish:*)"
    ]
  },
  "hooks": {
    "PostToolUse": [
      {
        "matcher": "Edit|Write",
        "hooks": [
          {
            "type": "command",
            "command": "python3 \"$CLAUDE_PROJECT_DIR/tools/hooks/format-on-edit.py\""
          }
        ]
      }
    ]
  }
}
```

---

**One more thing this measurement turned up, and it is not yours to fix.** The same event streams show
a plugin hook failing on every session start and every prompt:
`/Users/mirkobudimir/.claude/plugins/cache/claude-code-warp/warp/2.0.0/scripts/warp-notify.sh: line
21: /dev/tty: Device not configured`. It exits 0, so nothing breaks, but a notifier writing to
`/dev/tty` cannot work in a headless loop — the same shape as the dead `PushNotification` channel
(iter126, iter131). Recorded here only so it is not rediscovered as a mystery; it lives in a plugin
cache outside this repo.
