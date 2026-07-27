#!/usr/bin/env bash
# DocuMe autonomous build loop.
# Repeatedly runs headless Claude sessions that advance PLAN.md milestone work via MTK,
# surviving usage-limit lockouts (backoff + resume), machine sleep (caffeinate), and crashes.
#
# Start:   ./tools/loop/docume-loop.sh
# Stop:    touch tools/loop/STOP   (or Ctrl-C)
# Watch:   tail -f tools/loop/logs/loop.log ; cat tools/loop/state.json
set -u

cd "$(cd "$(dirname "$0")/../.." && pwd)" || exit 1

LOOP_DIR="tools/loop"
LOG_DIR="$LOOP_DIR/logs"
PROMPT_FILE="$LOOP_DIR/ITERATION-PROMPT.md"
SETTINGS="$LOOP_DIR/loop-settings.json"
STOP_FILE="$LOOP_DIR/STOP"
MODEL_FILE="$LOOP_DIR/MODEL"
MAIN_LOG="$LOG_DIR/loop.log"
STATE_FILE="$LOOP_DIR/state.json"

# Model for the headless workers, re-read every iteration so it can be changed live:
#   tools/loop/MODEL file (an alias like `opus`/`fable`/`sonnet`, or a full model id)
#   → else $DOCUME_LOOP_MODEL → else the CLI default.
# Reads the first non-blank, non-comment line. A file that is missing, empty, or only
# blanks/comments falls through to the env var — `-s` alone treated a newline-only file as a
# value and skipped straight to the CLI default, and an unfiltered `tr` folded a trailing
# comment into the id (`claude-opus-5` + `# note` → `claude-opus-5#note`, which every
# iteration would then fail on).
current_model() {
  local m=""
  if [ -f "$MODEL_FILE" ]; then
    m=$(grep -vE '^[[:space:]]*(#|$)' "$MODEL_FILE" | head -1 | tr -d '[:space:]')
  fi
  printf '%s' "${m:-${DOCUME_LOOP_MODEL:-}}"
}

# THE LOOP'S ITERATION NUMBER IS state.json's, NOT THIS DRIVER'S (iter137). `pass_n` below counts
# attempts of THIS process and resets to 1 whenever the script restarts; `iteration` in state.json is
# written by the agent at the end of an iteration and only ever increments. Measured at iter137 from
# this loop's own log: 161 completed passes had produced 136 iterations, because each of the 25
# usage-limit deaths burns a pass number and writes no bookkeeping. The two therefore drift apart IN
# STEPS - 0, 4, 9, 13, 17, 21, 25 across this run - so 133 of 136 iterations had a log file named
# after a number that was not theirs, and no constant correction could recover the mapping. The three
# restarts in loop.log contributed nothing to that drift: both of the reset runs were killed before a
# pass finished. A pass works on `iteration + 1`; name its log that, and log both numbers so neither
# one pretends to be the other.
state_iteration() {
  python3 -c 'import json,sys
try:
    v = json.load(open(sys.argv[1]))["iteration"]
    print(v if isinstance(v, int) else "")
except Exception:
    print("")' "$STATE_FILE" 2>/dev/null
}

mkdir -p "$LOG_DIR"

# Keep the Mac awake for the lifetime of the loop.
if [ -z "${DOCUME_LOOP_CAFFEINATED:-}" ] && command -v caffeinate >/dev/null 2>&1; then
  export DOCUME_LOOP_CAFFEINATED=1
  exec caffeinate -is "$0" "$@"
fi

log() { printf '[%s] %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$1" | tee -a "$MAIN_LOG"; }
# The `|| true` swallows an osascript failure ON PURPOSE (reviewed iter163): a notification that
# cannot be delivered must never take the loop down, and nothing downstream reads its result. It is
# also unverifiable from in here — GATES.md records that the call exits 0 while Notification
# Center's DB is TCC-blocked to the loop, so nobody can prove from this process that it arrived.
notify() { osascript -e "display notification \"$1\" with title \"DocuMe Loop\"" >/dev/null 2>&1 || true; }

if [ -z "${DOCUME_CONFLUENCE_EMAIL:-}" ] || [ -z "${DOCUME_CONFLUENCE_TOKEN:-}" ]; then
  log "WARN: DOCUME_CONFLUENCE_EMAIL / DOCUME_CONFLUENCE_TOKEN not exported — fine until M2, required for Confluence milestones."
fi

backoff=300      # seconds; doubles on consecutive failures, capped at 1h
pass_n=0         # attempts of THIS process; NOT the loop's iteration number (state_iteration)
resume_sid=""    # non-empty => previous iteration died mid-work; resume it

log "Loop started (pid $$)."
notify "Build loop started"

while true; do
  if [ -f "$STOP_FILE" ]; then
    log "STOP file found — exiting cleanly."
    notify "Loop stopped (STOP file)"
    exit 0
  fi

  pass_n=$((pass_n + 1))
  ts=$(date '+%Y%m%d-%H%M%S')
  state_iter=$(state_iteration)
  if [ -n "$state_iter" ]; then
    next_iter=$((state_iter + 1))
    iter_label=$(printf 'iter-%04d' "$next_iter")
  else
    next_iter="?"
    iter_label="iter-unknown"
  fi
  iter_log="$LOG_DIR/$iter_label-pass$(printf '%04d' "$pass_n")-$ts.log"
  model=$(current_model)

  if [ -n "$resume_sid" ]; then
    log "Iteration $next_iter (pass $pass_n): resuming interrupted session $resume_sid [model: ${model:-cli-default}]"
    out=$(claude --resume "$resume_sid" \
      -p "You were interrupted mid-iteration (likely a usage limit). Re-read tools/loop/state.json, verify what actually completed (git status, build), and continue the iteration protocol exactly where you left off." \
      --permission-mode acceptEdits --settings "$SETTINGS" \
      ${model:+--model "$model"} 2>&1)
    code=$?
    # A session that never got created can't be resumed — fall back to a fresh one.
    if [ $code -ne 0 ] && printf '%s' "$out" | grep -qiE 'no conversation|not found|invalid session'; then
      log "Resume impossible (session never persisted) — will start fresh."
      resume_sid=""
      printf '%s\n' "$out" > "$iter_log"
      continue
    fi
    sid="$resume_sid"
  else
    sid=$(uuidgen | tr '[:upper:]' '[:lower:]')
    log "Iteration $next_iter (pass $pass_n): fresh session $sid [model: ${model:-cli-default}]"
    out=$(claude -p "$(cat "$PROMPT_FILE")" \
      --session-id "$sid" \
      --permission-mode acceptEdits --settings "$SETTINGS" \
      ${model:+--model "$model"} 2>&1)
    code=$?
  fi

  printf '%s\n' "$out" > "$iter_log"
  status=$(printf '%s\n' "$out" | grep -Eo 'LOOP-STATUS:[[:space:]]*[A-Z-]+[^"]*' | tail -1)
  log "Iteration $next_iter (pass $pass_n) finished (exit $code) — ${status:-no status line}"

  # "no status line" IS A PROTOCOL VIOLATION REPORTED WITHOUT CONSEQUENCE, AND THAT IS DELIBERATE
  # (reviewed iter163, whose increment was a sweep for exactly this shape elsewhere in tools/loop).
  # The protocol requires exactly one LOOP-STATUS line as the last line; an iteration that exits 0
  # without one falls through the `case` below to `*)` and is treated as CONTINUE. Kept advisory on
  # purpose: this is a supervisor whose job is to still be running tomorrow, the full transcript is
  # already on disk above (so no evidence is lost, unlike the cases that sweep did fix), and the
  # alternative — treating it as a failure — would resume a session that had in fact finished. The
  # honest cost is that a malformed ending is indistinguishable from CONTINUE in the log line.

  if [ $code -ne 0 ]; then
    if printf '%s' "$out" | grep -qiE 'usage limit|rate limit|limit reached|overloaded|too many requests'; then
      log "Usage/rate limit hit. Waiting ${backoff}s, then resuming session."
      notify "Usage limit hit — resuming after backoff"
    else
      log "Iteration failed (see $iter_log). Waiting ${backoff}s, then resuming session."
    fi
    resume_sid="$sid"
    sleep "$backoff"
    backoff=$((backoff * 2))
    [ "$backoff" -gt 3600 ] && backoff=3600
    continue
  fi

  backoff=300
  resume_sid=""

  case "$status" in
    *DONE*)
      log "LOOP-STATUS: DONE — all PLAN.md §15 criteria satisfied. Exiting."
      notify "DocuMe build DONE 🎉"
      exit 0
      ;;
    *BLOCKED*)
      log "Blocked — needs Mirko (see tools/loop/state.json blockers). Re-checking in 30 min."
      log "Remote: attach to this exact session with 'claude remote-control --session-id $sid', or spawn a fresh one from claude.ai/code."
      notify "Loop BLOCKED — check tools/loop/state.json"
      sleep 1800
      ;;
    *WAITING-GATE*)
      log "Only gated work remains — waiting on GATES.md. Re-checking in 30 min."
      log "Remote: tick the gate via a session spawned from claude.ai/code (needs 'claude remote-control' hub running), session-id was $sid."
      notify "Waiting on a human gate — see GATES.md"
      sleep 1800
      ;;
    *)
      sleep 30
      ;;
  esac
done
