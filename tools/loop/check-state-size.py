#!/usr/bin/env python3
"""Report whether the loop's bookkeeping files still fit through the Read tool.

Step 1 of tools/loop/ITERATION-PROMPT.md is "Read tools/loop/state.json". That Read has TWO
ceilings and they bite in a different order:

  * a ~256 KB BYTE ceiling, which makes the Read FAIL outright (no content at all). state.json
    crossed it at iter127, when the `done` log had reached 397 KB.
  * a 25,000-TOKEN cap, which makes the Read TRUNCATE. state.json crossed this one long before
    the byte ceiling and nobody measured it: at iter128 the file was 74,115 bytes / 31,303
    tokens, so step 1 returned lines 1-43 of 92 and silently dropped `decisions`, `doneCount`,
    `doneArchive`, `doneRecent` and `spikes`.

Truncation is the worse failure of the two, because it looks like success. Run this after every
edit to state.json; it exits non-zero when the file no longer fits.

    python3 tools/loop/check-state-size.py
"""

import json
import os
import sys

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
LOOP_DIR = os.path.join(REPO, "tools", "loop")
STATE = os.path.join(LOOP_DIR, "state.json")

# The Read tool's cap, in tokens. Measured at iter128: 74,115 bytes of this file's prose came
# back as 31,303 tokens, i.e. 2.368 bytes/token. 2.3 is the conservative rounding (it
# OVER-estimates the token count, so the check trips slightly early rather than slightly late).
READ_TOKEN_CAP = 25_000
BYTES_PER_TOKEN = 2.3

# Headroom matters more than fitting. An iteration that appends 5 KB of prose to `phase` and
# `nextAction` must not be the one that re-breaks step 1, so the budget leaves room for several.
TARGET_TOKENS = 20_000


def est_tokens(n_bytes):
    return int(n_bytes / BYTES_PER_TOKEN)


def status_for(tokens):
    if tokens > READ_TOKEN_CAP:
        return "OVER CAP - Read TRUNCATES"
    if tokens > TARGET_TOKENS:
        return "over budget - shrink it"
    return "ok"


def main():
    size = os.path.getsize(STATE)
    with open(STATE, encoding="utf-8") as handle:
        text = handle.read()
    doc = json.loads(text)

    tokens = est_tokens(size)
    print(f"state.json  {size:,} bytes  {text.count(chr(10)) + 1} lines  ~{tokens:,} tokens")
    print(f"            budget {TARGET_TOKENS:,} tokens, hard cap {READ_TOKEN_CAP:,} -> {status_for(tokens)}")

    print("\nlargest fields (bytes of JSON):")
    fields = sorted(((len(json.dumps(v)), k) for k, v in doc.items()), reverse=True)
    for n_bytes, key in fields[:6]:
        print(f"  {n_bytes:>7,}  {key}")

    print("\nother files under tools/loop/ (archives are NOT on the step-1 path, but a Read of one")
    print("still truncates at the same cap - read them with grep/tail/offset instead):")
    for name in sorted(os.listdir(LOOP_DIR)):
        path = os.path.join(LOOP_DIR, name)
        if name == "state.json" or not os.path.isfile(path):
            continue
        if os.path.splitext(name)[1] not in (".json", ".jsonl", ".md"):
            continue
        n_bytes = os.path.getsize(path)
        print(f"  {n_bytes:>7,}  ~{est_tokens(n_bytes):>6,} tok  {name}  {status_for(est_tokens(n_bytes))}")

    if tokens > TARGET_TOKENS:
        print("\nFAIL: state.json is past its budget. Move an append-heavy field to an archive file")
        print("and leave a short pointer behind - see `readMe` and `doneArchive` in state.json.")
        return 1
    print("\nOK: step 1 of the iteration protocol returns the whole file.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
