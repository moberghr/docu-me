#!/usr/bin/env python3
"""Report whether the files the iteration protocol READS still fit through the Read tool.

Step 1 of tools/loop/ITERATION-PROMPT.md reads THREE things, not one: `tools/loop/state.json`,
`GATES.md`, and PLAN.md §14. That Read has TWO ceilings and they bite in a different order:

  * a ~256 KB BYTE ceiling, which makes the Read FAIL outright (no content at all). state.json
    crossed it at iter127, when the `done` log had reached 397 KB.
  * a 25,000-TOKEN cap, which makes the Read TRUNCATE. state.json crossed this one long before
    the byte ceiling and nobody measured it: at iter128 the file was 74,115 bytes / 31,303
    tokens, so step 1 returned lines 1-43 of 92 and dropped `decisions`, `doneCount`,
    `doneArchive`, `doneRecent` and `spikes`.

WHY THIS CHECKS MORE THAN state.json (iter129). iter128 built this script for the one file it had
just fixed and stopped there, so two of step 1's three files were measured by nothing. Neither is
close to the cap today - that is the point of knowing rather than assuming, and `headroom` below
says how much room each one has left. PLAN.md is the one to watch: every outstanding decision in
`state.json -> decisions` asks for a PLAN.md edit.

    python3 tools/loop/check-state-size.py

CALIBRATION, AND HOW TO REDO IT. There is no tokenizer on this machine (no tiktoken, no
anthropic, no transformers - checked at iter129). The Read tool IS the only one available, because
its truncation notice reports the file's exact total: "PARTIAL view - showing lines 1-462 of 1500
total (68900 tokens, cap 25000)". So the notice is explicit and machine-readable, NOT silent -
iter128's "truncation looks like success" was about an agent skimming past it, not about the tool
hiding it. To re-measure: build a file over the cap but under 256 KB, Read it whole, and divide.

  * JSON, measured iter128:     74,115 bytes -> 31,303 tokens = 2.368 B/tok
  * Markdown, measured iter129: 179,382 bytes -> 68,900 tokens = 2.604 B/tok
    (.mtk/paths-129/calibrate-read-tokens.py rebuilds that file from real repo prose)

Both constants below are rounded DOWN from the measurement, so the estimate OVER-states tokens and
the check trips slightly early rather than slightly late.
"""

import json
import os
import sys

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
LOOP_DIR = os.path.join(REPO, "tools", "loop")
STATE = os.path.join(LOOP_DIR, "state.json")

READ_TOKEN_CAP = 25_000
READ_BYTE_CEILING = 256 * 1024

BYTES_PER_TOKEN_JSON = 2.3
BYTES_PER_TOKEN_MARKDOWN = 2.5

# Headroom matters more than fitting. An iteration that appends 5 KB of prose to `phase` and
# `nextAction` must not be the one that re-breaks step 1, so the budget leaves room for several.
TARGET_TOKENS = 20_000

# What step 1 of the protocol actually opens. All three must survive one Read each.
STEP1_PATHS = [
    ("tools/loop/state.json", "step 1: orient"),
    ("GATES.md", "step 1: human gates"),
    ("PLAN.md", "step 1: milestone map (SS14) + every decisions.* edit target"),
]

# Auto-loaded by Claude Code on every iteration rather than Read by step 1. They spend the same
# context window, so they are reported, but they are not the thing that truncates.
ALWAYS_LOADED = [
    "CLAUDE.md",
    "tools/loop/ITERATION-PROMPT.md",
]


def bytes_per_token(path):
    if os.path.splitext(path)[1] in (".json", ".jsonl"):
        return BYTES_PER_TOKEN_JSON
    return BYTES_PER_TOKEN_MARKDOWN


def est_tokens(n_bytes, path):
    return int(n_bytes / bytes_per_token(path))


def status_for(n_bytes, tokens):
    if n_bytes > READ_BYTE_CEILING:
        return "OVER BYTE CEILING - Read FAILS"
    if tokens > READ_TOKEN_CAP:
        return "OVER CAP - Read TRUNCATES"
    if tokens > TARGET_TOKENS:
        return "over budget - shrink it"
    return "ok"


def headroom(path, tokens):
    """Bytes of the same kind of prose this file could still gain before it truncates."""
    return int((READ_TOKEN_CAP - tokens) * bytes_per_token(path))


def report(rel, note):
    path = os.path.join(REPO, rel)
    n_bytes = os.path.getsize(path)
    with open(path, encoding="utf-8") as handle:
        lines = sum(1 for _ in handle)
    tokens = est_tokens(n_bytes, rel)
    state = status_for(n_bytes, tokens)
    print(f"  {rel:<26} {n_bytes:>8,} B  {lines:>5} lines  ~{tokens:>6,} tok  {state}")
    print(f"  {'':<26} headroom ~{headroom(rel, tokens) / 1024:,.0f} KB before it truncates   ({note})")
    return tokens, state


def main():
    failures = []

    print(f"STEP-1 READ PATH (cap {READ_TOKEN_CAP:,} tok / {READ_BYTE_CEILING // 1024} KB per Read,")
    print(f"                  budget {TARGET_TOKENS:,} tok):")
    orientation = 0
    for rel, note in STEP1_PATHS:
        tokens, state = report(rel, note)
        orientation += tokens
        if state != "ok":
            failures.append(rel)
    print(f"\n  orienting costs ~{orientation:,} tokens across the three Reads.")

    with open(STATE, encoding="utf-8") as handle:
        doc = json.loads(handle.read())
    print("\nstate.json largest fields (bytes of JSON):")
    for n_bytes, key in sorted(((len(json.dumps(v)), k) for k, v in doc.items()), reverse=True)[:6]:
        print(f"  {n_bytes:>7,}  {key}")

    print("\nalways loaded by Claude Code (spends context, does not truncate step 1):")
    for rel in ALWAYS_LOADED:
        n_bytes = os.path.getsize(os.path.join(REPO, rel))
        print(f"  {rel:<32} {n_bytes:>7,} B  ~{est_tokens(n_bytes, rel):>6,} tok")

    print("\nother files under tools/loop/ (NOT on the step-1 path, but a Read of one truncates at")
    print("the same cap - read those with grep/tail/offset instead):")
    for name in sorted(os.listdir(LOOP_DIR)):
        path = os.path.join(LOOP_DIR, name)
        if name == "state.json" or not os.path.isfile(path):
            continue
        if os.path.splitext(name)[1] not in (".json", ".jsonl", ".md"):
            continue
        n_bytes = os.path.getsize(path)
        tokens = est_tokens(n_bytes, name)
        print(f"  {n_bytes:>8,}  ~{tokens:>7,} tok  {name}  {status_for(n_bytes, tokens)}")

    if failures:
        print(f"\nFAIL: {', '.join(failures)} past budget. For state.json, move an append-heavy field")
        print("to an archive file and leave a short pointer behind - see `readMe` and `doneArchive`.")
        print("For GATES.md or PLAN.md, the content is the spec: archive settled sections, never drop them.")
        return 1
    print("\nOK: all three step-1 Reads return their whole file.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
