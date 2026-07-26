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

mkdir -p "$LOG_DIR"

# Keep the Mac awake for the lifetime of the loop.
if [ -z "${DOCUME_LOOP_CAFFEINATED:-}" ] && command -v caffeinate >/dev/null 2>&1; then
  export DOCUME_LOOP_CAFFEINATED=1
  exec caffeinate -is "$0" "$@"
fi

log() { printf '[%s] %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$1" | tee -a "$MAIN_LOG"; }
notify() { osascript -e "display notification \"$1\" with title \"DocuMe Loop\"" >/dev/null 2>&1 || true; }

if [ -z "${DOCUME_CONFLUENCE_EMAIL:-}" ] || [ -z "${DOCUME_CONFLUENCE_TOKEN:-}" ]; then
  log "WARN: DOCUME_CONFLUENCE_EMAIL / DOCUME_CONFLUENCE_TOKEN not exported — fine until M2, required for Confluence milestones."
fi

backoff=300      # seconds; doubles on consecutive failures, capped at 1h
iter=0
resume_sid=""    # non-empty => previous iteration died mid-work; resume it

log "Loop started (pid $$)."
notify "Build loop started"

while true; do
  if [ -f "$STOP_FILE" ]; then
    log "STOP file found — exiting cleanly."
    notify "Loop stopped (STOP file)"
    exit 0
  fi

  iter=$((iter + 1))
  ts=$(date '+%Y%m%d-%H%M%S')
  iter_log="$LOG_DIR/iter-$(printf '%04d' "$iter")-$ts.log"
  model=$(current_model)

  if [ -n "$resume_sid" ]; then
    log "Iteration $iter: resuming interrupted session $resume_sid [model: ${model:-cli-default}]"
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
    log "Iteration $iter: fresh session $sid [model: ${model:-cli-default}]"
    out=$(claude -p "$(cat "$PROMPT_FILE")" \
      --session-id "$sid" \
      --permission-mode acceptEdits --settings "$SETTINGS" \
      ${model:+--model "$model"} 2>&1)
    code=$?
  fi

  printf '%s\n' "$out" > "$iter_log"
  status=$(printf '%s\n' "$out" | grep -Eo 'LOOP-STATUS:[[:space:]]*[A-Z-]+[^"]*' | tail -1)
  log "Iteration $iter finished (exit $code) — ${status:-no status line}"

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
