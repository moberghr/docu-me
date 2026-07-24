# DocuMe autonomous build loop

An external driver that runs headless Claude Code sessions in a `while true` loop until DocuMe
(PLAN.md) is fully implemented and verified. Each session executes MTK milestone work, verifies
with `dotnet build`/`dotnet test`, commits, and hands off through `state.json`. The driver — not
the AI — owns resilience: usage-limit lockouts, crashes, and turn caps all resolve to
backoff-then-resume of the interrupted session.

## Start

```bash
# once: credentials (needed from M2 onward)
export DOCUME_CONFLUENCE_EMAIL="you@moberg.hr"
export DOCUME_CONFLUENCE_TOKEN="…"

./tools/loop/docume-loop.sh
```

The script re-execs itself under `caffeinate -is`, so the Mac won't sleep while it runs.
Run it inside `tmux`/`screen` (or `nohup … &`) if you want to close the terminal.

Optional env: `DOCUME_LOOP_MODEL` (model override for the headless sessions).

## Stop / pause

```bash
touch tools/loop/STOP     # clean stop at the next loop pass
rm tools/loop/STOP        # allow it to run again
```

Ctrl-C works too (kills the current session mid-flight; state.json + git keep it recoverable).

## Watch

```bash
tail -f tools/loop/logs/loop.log        # one line per iteration
cat tools/loop/state.json               # milestone, phase, next action, blockers
ls tools/loop/logs/                     # full transcript per iteration
```

macOS notifications fire on: limit hit, gate opened, blocked, done.

## Human gates

See `GATES.md`. The loop appends a checkbox when it reaches a gate and keeps working on anything
independent. Tick the box; the loop notices within ~30 minutes. Production AUR publishing is
double-locked: the M7 gate checkbox **and** `productionAllowed: true` in `state.json`.

## How resilience works

| Event | Behavior |
|---|---|
| Usage limit / rate limit | Exit captured → notify → back off (5 min → doubling → 1 h cap) → `claude --resume <session>` continues mid-thought |
| Crash / API error | Same backoff + resume path |
| Mac sleep | Prevented by caffeinate |
| Blocked on Mirko | Notification + re-check every 30 min (`state.json → blockers`) |
| All §15 criteria met | Session prints `LOOP-STATUS: DONE`, driver exits |

## Remote control (steer the loop from your phone)

The headless workers can't be attached directly, but the loop is steered entirely through files —
so a Remote Control hub running next to it gives you full control from claude.ai/code or the
Claude mobile app:

```bash
tmux new -s docume
./tools/loop/docume-loop.sh                          # pane 1: the loop
claude remote-control --name docume-hub              # pane 2: remote-control hub
```

From your phone you can then spawn a session in this repo and: tick GATES.md checkboxes, edit
state.json (e.g. set `productionAllowed`), extend the allowlist, inspect `tools/loop/logs/`,
or `touch tools/loop/STOP`. When an iteration ends BLOCKED, loop.log prints the exact
`claude remote-control --session-id <id>` command to reopen that session with its full context.
For hands-on intervention, prefer `touch tools/loop/STOP` first so the loop idles while you work,
then remove STOP to resume.

## Permissions model

Sessions run `--permission-mode acceptEdits` with the allowlist in `loop-settings.json`
(dotnet, git, gh, node/npm/npx, docume, file utilities, web). Anything outside it is denied;
the session records `needs-allowlist: <cmd>` in state.json and reports BLOCKED so you can extend
the list deliberately. Force-push and `.env` reads are explicitly denied.
