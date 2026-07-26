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
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
LOOP_DIR = os.path.join(REPO, "tools", "loop")
STATE = os.path.join(LOOP_DIR, "state.json")
ARCHIVE = os.path.join(LOOP_DIR, "done-archive.jsonl")

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


def iteration_of(entry):
    """Which iteration does this archive entry belong to? None if it does not say.

    THE ARCHIVE HAS TWO ENTRY SHAPES and neither is wrong - `doneArchive.format` allows both.
    Measured at iter136 over all 136 lines: 107 entries are STRINGS that name themselves in a
    leading `iterNNN`, and 29 are OBJECTS that carry `{"iteration": NNN, "slice": ...}` instead.
    Anything that reads the archive has to handle both, and until iter136 nothing did.
    """
    if isinstance(entry, str):
        match = re.match(r"\s*iter(\d+)", entry)
        return int(match.group(1)) if match else None
    if isinstance(entry, dict):
        value = entry.get("iteration")
        return value if isinstance(value, int) else None
    return None


def check_done_archive(doc):
    """Is done-archive.jsonl still the complete, authoritative log state.json says it is?

    ADDED ITER133, after finding it was not. `doneArchive.note` calls the archive authoritative and
    `doneRecent` a convenience tail, and `howToAppend` tells every iteration to trim `doneRecent` down
    to the newest entry - so an entry that never reached the archive is destroyed by the next
    iteration performing the documented ritual. That had already happened: iter132's 3,416-char record
    existed ONLY in `doneRecent`, the archive's final line was a bare JSON string (no `n`) duplicating
    n=132, and `doneCount` counted that malformed line so the total looked right.

    EXTENDED ITER136 with the three checks that shape misses. The iter133 checks are all about the
    FILE - valid JSON, contiguous `n`, a matching count - and a file can satisfy every one of them
    while an iteration's record is simply absent, because `n` is a LINE INDEX and not an iteration
    number. It has not equalled the iteration number since line 50: iter48 has two entries, so
    `n = iteration + 1` for all 87 lines after it, and `doneCount` 136 against iteration 135 only
    looks aligned. What is checked now is ATTRIBUTION (every entry says which iteration it is),
    COVERAGE (no iteration in the range is missing), and the HEAD (the newest entry is the iteration
    state.json says it is on) - the last of which is the one that fires if an iteration bumps its
    counter without appending, which is how iter132's record was lost.

    Checked here rather than in a script of its own because this is the script `readMe` requires after
    every edit to state.json, which is exactly when the invariant can break.
    """
    problems = []
    with open(ARCHIVE, encoding="utf-8") as handle:
        lines = handle.read().splitlines()

    parsed = []
    for number, line in enumerate(lines, start=1):
        try:
            obj = json.loads(line)
        except json.JSONDecodeError as exc:
            problems.append(f"line {number} is not valid JSON ({exc.msg})")
            continue
        if not isinstance(obj, dict) or "n" not in obj or "entry" not in obj:
            kind = type(obj).__name__
            problems.append(f'line {number} is a bare {kind}, not the declared {{"n","entry"}} object')
            continue
        parsed.append((number, obj))

    for index, (number, obj) in enumerate(parsed, start=1):
        if obj["n"] != index:
            problems.append(f"line {number} has n={obj['n']}, expected {index} (n must be 1..N, contiguous)")
            break

    count = doc.get("doneCount")
    if count != len(lines):
        problems.append(f"doneCount is {count} but the archive has {len(lines)} lines")

    # The load-bearing one: nothing may sit in doneRecent that the archive does not already hold.
    entries = {obj["entry"] for _, obj in parsed if isinstance(obj["entry"], str)}
    for recent in doc.get("doneRecent", []):
        if isinstance(recent, str) and recent not in entries:
            head = recent[:60].replace("\n", " ")
            problems.append(f"doneRecent entry NOT in the archive, so trimming it would destroy it: {head!r}...")

    # ITER136 (1/3) ATTRIBUTION. An entry that names no iteration is a record nobody can look up
    # again, and every check above passes on one.
    attributed = {}
    for number, obj in parsed:
        which = iteration_of(obj["entry"])
        if which is None:
            problems.append(f"line {number} (n={obj['n']}) does not name its iteration in either shape")
            continue
        attributed.setdefault(which, []).append(number)

    # ITER136 (2/3) COVERAGE. `n` being contiguous says nothing about this: it counts lines.
    if attributed:
        gaps = [i for i in range(1, max(attributed) + 1) if i not in attributed]
        if gaps:
            problems.append(f"no archive entry for iteration(s) {gaps} - a record is missing, not just miscounted")

    # ITER136 (3/3) HEAD. Bump the counter without appending and this is the only check that fires.
    # Duplicates are legitimate (iter48 logged two slices), so coverage tolerates them; the head does not.
    current = doc.get("iteration")
    newest = max(attributed) if attributed else None
    if isinstance(current, int) and newest is not None and newest != current:
        problems.append(
            f"state.json is on iteration {current} but the newest archived entry is iter{newest}"
            " - append the entry, or fix whichever number is wrong"
        )

    print("\ndone-archive.jsonl integrity:")
    print(f"  {len(lines)} lines, {len(parsed)} well-formed, doneCount {count}")
    shapes = sum(1 for _, obj in parsed if isinstance(obj["entry"], dict))
    print(f"  {len(parsed) - shapes} string entries + {shapes} object entries, covering {len(attributed)} iterations")
    print(f"  newest archived: iter{newest}; state.json iteration: {current}")
    for problem in problems:
        print(f"  BROKEN: {problem}")
    if not problems:
        print("  OK: well-formed, n contiguous, doneRecent archived, every iteration present and attributed.")
    return problems


def find_iteration(wanted):
    """`grep -n 'iterNNN'` is WRONG for 27 of 135 iterations. This is the lookup that works.

    MEASURED AT ITER136 by running the command `doneArchive.howToRead` actually prescribes, once per
    iteration, against the lines that genuinely own each one (.mtk/paths-136/probe-archive-lookup.py):
    it found NOTHING for 3 of them (69, 84, 108 - object-shaped entries spell it `"iteration": 69`,
    which no `iter69` pattern matches) and, for 24 more, found ONLY lines belonging to some OTHER
    entry. That second half is the dangerous one, because it looks like a hit: ask it for iter113 and
    it hands back lines 135 and 136, which are iter134's and iter135's records mentioning iter113 in
    prose. The loop's entries cite each other constantly, so prose mentions outnumber self-namings -
    65 of 135 iterations get at least one match that is not their own entry.
    """
    with open(ARCHIVE, encoding="utf-8") as handle:
        lines = handle.read().splitlines()

    owners, mentions = [], []
    for number, line in enumerate(lines, start=1):
        try:
            obj = json.loads(line)
        except json.JSONDecodeError:
            continue
        if not isinstance(obj, dict) or "entry" not in obj:
            continue
        if iteration_of(obj["entry"]) == wanted:
            owners.append((number, obj))
        elif f"iter{wanted}" in json.dumps(obj["entry"]):
            mentions.append(number)

    if not owners:
        print(f"no archive entry for iter{wanted}.")
    for number, obj in owners:
        entry = obj["entry"]
        body = entry if isinstance(entry, str) else json.dumps(entry, indent=2, ensure_ascii=False)
        print(f"--- iter{wanted}: line {number}, n={obj['n']} ---")
        print(body)
    if mentions:
        print(f"\n({len(mentions)} OTHER entries mention iter{wanted} in prose: lines {mentions}."
              " A bare grep returns these too, and they are not this iteration's record.)")
    return 0 if owners else 1


def main():
    if len(sys.argv) == 3 and sys.argv[1] == "--find" and sys.argv[2].isdigit():
        return find_iteration(int(sys.argv[2]))
    if len(sys.argv) > 1:
        print("usage: check-state-size.py [--find <iteration>]")
        return 2

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

    archive_problems = check_done_archive(doc)

    if failures:
        print(f"\nFAIL: {', '.join(failures)} past budget. For state.json, move an append-heavy field")
        print("to an archive file and leave a short pointer behind - see `readMe` and `doneArchive`.")
        print("For GATES.md or PLAN.md, the content is the spec: archive settled sections, never drop them.")
        return 1
    if archive_problems:
        print("\nFAIL: done-archive.jsonl is not the complete log state.json claims. Repair it BEFORE")
        print("trimming doneRecent - an entry held only there is destroyed by the next iteration.")
        return 1
    print("\nOK: all three step-1 Reads return their whole file, and the done archive is intact.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
