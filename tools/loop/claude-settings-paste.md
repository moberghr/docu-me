# The `.claude/settings.json` paste (blocker `paste-format-on-edit-hook`, opened iter133, **answered "delete" iter155**)

**THE CHOICE IS SETTLED — DO NOT RE-READ THIS FILE AS A DECISION.** On 2026-07-27 you answered
`tools/loop/state.json → decisions.formatOnEditHook` with `"delete"`. This file used to present two
options and lead with the *fix*; it now carries exactly one thing, the paste that deletes the hook.
The loop did its half at iter155 (`tools/hooks/format-on-edit.py` is deleted, commit below).

**What is left is one paste and nothing waits on it.** Replace the whole of `.claude/settings.json`
with the fenced `json` block at the end of this file. The loop cannot write `.claude/` (a path-based
guard above the permission layer, `blockersArchive.settled.claude-dir-writes`; re-confirmed when the
interactive session was refused the same edit) and deliberately does not route around it with an
allowlisted `python3`, because the file it would be editing is the one that governs its own behaviour.

## What the paste changes: it drops the `hooks` key, and nothing else

The `permissions` block is **byte-identical** — same three `allow` entries, same two `deny` entries,
nothing added, nothing reordered. The only difference from what is on disk today is that the whole
`"hooks"` key is gone. That is the entire diff.

## Why the hook is being deleted rather than fixed

Not because it was hard to fix — the fix was built and proven — but because the ~7.0 s per C# edit was
not worth buying, and a dead hook in a committed file is worse than no hook. What was measured at
iter133 (`python3 .mtk/paths-133/probe-project-settings-hook.py`, three child sessions, plus
`probe-hook-env.py`) stays on the record, because the last row generalises well past this hook:

| Question | Measured answer |
|---|---|
| Does a hook in `.claude/settings.json` fire at all while this workspace is untrusted? | **Yes.** The untrusted state gates only `permissions.allow`, not `hooks`. This closed the lead iter131 left open. |
| What does `$CLAUDE_PLUGIN_ROOT` expand to there? | **The empty string.** The CLI sets it only for hooks that come *from a plugin*; a project settings file has no plugin. |
| So what did the declared command run? | `bash "/hooks/format-on-edit.sh"` → **exit 127, "No such file or directory"**, after every Edit and Write since 2026-07-24. |
| Did that script exist anywhere? | **No.** `.claude/settings.json:20` was the only reference to it in the whole tree; there is no `.claude/hooks/` directory. It was never generated. |
| Does `$CLAUDE_PROJECT_DIR` work instead? | **Yes** — measured as `/Users/mirkobudimir/Dev/docu-me`. (Moot now; recorded because it is the non-obvious half.) |
| Why did 133 iterations not notice? | **A failing PostToolUse hook is invisible to the agent.** The event carried `exit_code=127, outcome='error'`, yet no user/assistant turn mentioned it and the session's `result.is_error` was `False`. |

That last row is why this sat unnoticed for two days of continuous running, and it is the durable
lesson: **a hook cannot be verified by waiting for it to complain.** It is mirrored in
`tools/loop/method-notes.md`, which is where a future iteration will actually reach it.

The rejected alternative, recorded so it is not rediscovered as an untried idea: point the command at
a committed `python3` script instead of the phantom `.sh`. That was built and tested 5/5, and proven
end to end 3/3 against a child session that really did re-mangle and get re-formatted. It worked. It
cost `dotnet format <slnx> --include <file>` ≈ **7.0 s** per `.cs` edit (the `whitespace` subcommand
is ~1.9 s but leaves diffs that the loop's own `--verify-no-changes` gate still fails on). You judged
that not worth it, which is the whole content of the "delete" answer.

## After you paste

`.claude/settings.json` is tracked by git, so the change needs committing — **the loop will commit it**
on its next iteration (it cannot write the file, but `git add` is not blocked; iter89 committed
`.claude/references/` edits the same way). It will then tick `paste-format-on-edit-hook` and this file
and its gate item both go to the archive.

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
