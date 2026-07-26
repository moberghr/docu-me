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

## Model

`tools/loop/MODEL` holds the model for the headless workers. It is re-read **every iteration**, so
changing it takes effect on the next pass with no restart:

```bash
echo claude-opus-5 > tools/loop/MODEL     # takes effect next iteration
```

Each iteration logs the model it used. Blank lines and `#` comments in the file are ignored and the
first real line wins; a file that is missing, empty, or all blanks/comments falls through to
`$DOCUME_LOOP_MODEL`, then to the CLI default. All fourteen cases of that chain are asserted by
`.mtk/paths-132/probe-current-model.py`, which lifts `current_model()` out of the script rather than
reimplementing it.

Changing the MODEL *file* needs no restart. Changing `docume-loop.sh` itself does: the running
driver already has the script parsed, so edits to it apply from the next `./tools/loop/docume-loop.sh`.

**Aliases and explicit ids both work; ids are still the safer choice for an unattended loop.**
Aliases (`opus`/`fable`/`sonnet`) track the latest model of a line. They have lagged: on CLI 2.1.218
(2026-07-24) `opus` resolved to `claude-opus-4-8` while `claude-opus-5` was live. **That lag is gone
as of CLI 2.1.219 (re-verified 2026-07-26): `opus` → `claude-opus-5`, `sonnet` → `claude-sonnet-5`.**
An id still pins what you audited; an alias can move under you between iterations.

Verify a model change with the CLI's own accounting rather than asking the model what it is (models
self-report unreliably). `modelUsage` names the model that actually served the request:

```bash
claude -p hi --model opus --output-format json < /dev/null \
  | python3 -c 'import json,sys; print(list(json.load(sys.stdin)["modelUsage"]))'
```

Keep `2>&1` out of that pipeline. The CLI writes warnings to stderr — this workspace currently emits
an untrusted-workspace warning on every run (see the `settings-corrections` item in `GATES.md`) — and
folding stderr into stdout puts that text ahead of the JSON and breaks the parse.

## Stop / pause

```bash
touch tools/loop/STOP     # clean stop at the next loop pass
rm tools/loop/STOP        # allow it to run again
```

Ctrl-C works too (kills the current session mid-flight; state.json + git keep it recoverable).

## Watch

```bash
tail -f tools/loop/logs/loop.log        # one line per driver pass
cat tools/loop/state.json               # milestone, phase, next action, blockers
ls tools/loop/logs/                     # full transcript per pass
```

**Two numbers, and they are not the same number.** `state.json -> iteration` is the loop's history
counter, written by the agent; the driver's `pass_n` counts attempts of the current
`docume-loop.sh` process. A usage-limit death burns a pass and writes no iteration, so the two drift
apart in steps — 25 apart by iter137, when the loop's own logs were still named after the pass
counter alone and 133 of 136 iterations sat in a file numbered for a different one. Transcripts are
now `iter-<iteration>-pass<n>-<ts>.log`, so `ls logs/iter-0137-*` shows every attempt at iteration
137. For anything logged **before** iter137, ask instead of guessing — the offset is not constant:

```bash
python3 tools/loop/check-state-size.py --find 113   # that iteration's record + its transcript path
```

Notifications — two channels:
- **Phone push** (Claude mobile app): sessions send a PushNotification when they open a gate, block
  on a question, or finish. The message contains the actual question and where to put the answer
  (state.json field / GATES.md checkbox); answer it by spawning a hub session from your phone.
  Pushes are auto-skipped when you're actively at the keyboard (they'd be redundant).
- **macOS notification center** (Mac-local): all of the above plus driver-level events. Usage-limit
  hits can only be Mac-local — during a lockout no Claude call can succeed, so nothing can push.
  (If you want limit alerts on your phone too, an ntfy.sh curl in the driver is the way — ask.)

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
