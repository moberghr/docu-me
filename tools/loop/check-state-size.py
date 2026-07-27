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

Every measurement taken that way is recorded in MEASURED below, and `check_calibration` asserts the
constants stay conservative against all of them. Add a row when you measure a new file; do not
adjust a constant without one.

WHAT ITER138 CORRECTED, BECAUSE THE REASONING HERE WAS WRONG. This docstring used to end:

    "Both constants below are rounded DOWN from the measurement, so the estimate OVER-states
     tokens and the check trips slightly early rather than slightly late."

Rounding the constant down does make the estimate conservative - but only against the ONE file the
constant was measured on. `tokens = bytes / constant`, so the estimate over-states tokens only while
the constant is BELOW the file's true ratio. A denser file has a LOWER ratio, and once a real file
sits under the constant the estimate UNDER-states it and the check trips LATE, which is the exact
failure the script exists to prevent.

That was not hypothetical. Markdown was calibrated at iter129 on a CONCATENATION of seven repo files
(2.604 B/tok) and the constant set to 2.5. iter138 measured two individual files and they straddle
it: PLAN.md is sparser at 2.646, but `tools/loop/handoff-archive.md` is DENSER at 2.447 - so its
32,783 real tokens were being estimated at 32,080. Identifier-heavy prose (type names, XML
fragments, paths) tokenizes worse than spec prose, and a blend cannot show that. THE RULE THAT
REPLACES THE OLD ONE: pin each constant at or below the DENSEST file ever measured of that kind,
never at the average.
"""

import json
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
LOOP_DIR = os.path.join(REPO, "tools", "loop")
STATE = os.path.join(LOOP_DIR, "state.json")
ARCHIVE = os.path.join(LOOP_DIR, "done-archive.jsonl")
GATES_MD = os.path.join(REPO, "GATES.md")
LOG_DIR = os.path.join(LOOP_DIR, "logs")
LOOP_LOG = os.path.join(LOG_DIR, "loop.log")

# The three shapes a gate heading takes in GATES.md. Anchored to the start of the line AND to the
# bold id, because gate bodies cite other gate ids constantly - a bare id search matches prose in
# six other gates. `- [ ]`/`- [x]` is a live gate, `~~id~~` one closed without action, and a bold
# bullet with no box one in the "Anticipated" section that is not open yet.
GATE_CHECKBOX = re.compile(r"^- \[([ x])\] \*\*([a-z0-9-]+)\*\*")
GATE_STRUCK = re.compile(r"^- ~~\*\*([a-z0-9-]+)\*\*~~")
GATE_ANTICIPATED = re.compile(r"^- \*\*([a-z0-9-]+)\*\* +[-—]")

# `gates` keys that are not gates: the pointer naming GATES.md as the authoritative copy.
NOT_A_GATE = frozenset({"authoritative"})

# A gate body sending the reader to another GATES.md section for work that waits there. iter151
# found three of these outliving the work they named, and `under "<Title>"` was the exact form all
# three used, so that is what this resolves. A correction note quoting a retired pointer writes the
# title in BACKTICKS instead - that is deliberate, and it is what keeps history from tripping the
# check that retired it.
SECTION_POINTER = re.compile(r'under (?:the )?"([^"\n]+)"')
SECTION_HEADING = re.compile(r"^## +(.+?)\s*$")
ANY_CHECKBOX = re.compile(r"^\s*- \[([ x])\]")

READ_TOKEN_CAP = 25_000
READ_BYTE_CEILING = 256 * 1024

BYTES_PER_TOKEN_JSON = 2.3
# 2.4, not iter129's 2.5: handoff-archive.md measures 2.447 B/tok, so 2.5 under-stated it. See the
# docstring's iter138 note - the constant belongs at or below the densest file measured, not the mean.
BYTES_PER_TOKEN_MARKDOWN = 2.4

# EVERY bytes-per-token measurement this loop has taken through the Read tool's truncation notice.
# (kind, what was measured, bytes, tokens reported by the notice). `check_calibration` fails if a
# constant above drifts over the smallest ratio here, which is what keeps the estimate conservative
# instead of merely well-intentioned. To add a row: python3 .mtk/paths-138/calibrate-file.py <file>,
# Read the scratch file it writes, then record its bytes and the notice's token total.
MEASURED = [
    ("markdown", "iter129 blend of 7 repo files (.mtk/paths-129/calib.md)", 179_382, 68_900),
    ("markdown", "iter138 PLAN.md x3", 134_931, 50_996),
    ("markdown", "iter138 tools/loop/handoff-archive.md", 80_200, 32_783),
    ("json", "iter128 tools/loop/state.json", 74_115, 31_303),
]

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


def check_calibration():
    """Is every bytes-per-token constant still conservative against every file ever measured?

    ADDED ITER138, after finding the docstring's safety argument inverted (see the module docstring).
    A constant ABOVE a real file's true ratio makes `est_tokens` under-state that file, so the check
    waves through something the Read tool will truncate - the one outcome this script exists to stop.
    Being a check rather than a comment is the point: iter129's constant was optimistic for nine
    iterations and nothing could notice, because the only record of the measurement was prose.
    """
    problems = []
    constants = {"markdown": BYTES_PER_TOKEN_MARKDOWN, "json": BYTES_PER_TOKEN_JSON}

    print("\nbytes-per-token calibration (measured through the Read tool's truncation notice):")
    for kind, what, n_bytes, tokens in MEASURED:
        ratio = n_bytes / tokens
        constant = constants[kind]
        margin = ratio - constant
        flag = "ok" if margin >= 0 else "CONSTANT IS OPTIMISTIC"
        print(f"  {kind:<8} {ratio:.4f} B/tok  (constant {constant}, margin {margin:+.4f})  {flag}  {what}")
        if margin < 0:
            understated = int(n_bytes / ratio) - int(n_bytes / constant)
            problems.append(
                f"{kind} constant {constant} exceeds the {ratio:.4f} B/tok measured on {what},"
                f" so its {tokens:,} tokens estimate as {int(n_bytes / constant):,}"
                f" - under by {understated:,}. Lower the constant to at or below {ratio:.4f}."
            )

    for kind, constant in constants.items():
        ratios = [b / t for k, _, b, t in MEASURED if k == kind]
        if not ratios:
            problems.append(f"no measurement recorded for {kind} - the {constant} constant is unfounded")
            continue
        print(f"  {kind:<8} densest measured {min(ratios):.4f}; constant {constant} leaves"
              f" {min(ratios) - constant:+.4f} of margin")

    for problem in problems:
        print(f"  BROKEN: {problem}")
    if not problems:
        print("  OK: every constant sits at or below the densest file measured of its kind.")
    return problems


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


def scan_gates_md(text):
    """Every gate id GATES.md declares, split by the shape of its heading."""
    boxes, struck, anticipated = {}, [], []
    for line in text.splitlines():
        match = GATE_CHECKBOX.match(line)
        if match:
            boxes[match.group(2)] = match.group(1) == "x"
            continue
        match = GATE_STRUCK.match(line)
        if match:
            struck.append(match.group(1))
            continue
        match = GATE_ANTICIPATED.match(line)
        if match:
            anticipated.append(match.group(1))
    return boxes, struck, anticipated


def check_gate_mirror(doc):
    """Is `state.json -> gates` still the complete mirror of GATES.md that rule §9.7 requires?

    ADDED ITER144, after finding it was not. Rule §9.7 says "human gates live in GATES.md as
    `- [ ]` checkboxes mirrored into state", and `gates.authoritative` restates it - "this block is
    the status mirror rule §9.7 asks for". Nothing checked it, and `paste-rule-8-2a` had been
    unmirrored since iter75: 69 iterations, every one of which wrote this file.

    WHY EYEBALLING NEVER CAUGHT IT, which is the reason this is a check and not a correction.
    GATES.md carried 11 checkboxes and `gates` carried 11 keys - the counts MATCHED, because one
    mirror key belongs to `gate-m1-aurservices-files`, whose heading is struck through rather than
    a checkbox. Only a set comparison sees it. A grep does not help either: `paste-rule-8-2a`
    appears in state.json four times over, in `nextAction`'s list of side items and in a `blockers`
    key spelled `rule-8-2a`, so searching for the id returns hits from a block that is not the
    mirror. Same family as iter136's archive lookup and iter143's regex - a confident wrong answer
    that reads as a clean result.

    THE THIRD CHECK IS THE ONE WITH A FUTURE. Directions (1) and (2) catch a gate going missing;
    (3) catches the mirror's STATUS going stale, which is what happens on the day Mirko finally
    ticks a box - the loop's whole orientation depends on `nextAction`/`gates` agreeing with
    GATES.md about what is still open, and that disagreement has no other detector.

    Checked here for `check_done_archive`'s reason: this is the script `readMe` requires after
    every edit to state.json AND to GATES.md, which is exactly when the invariant can break.
    """
    problems = []
    with open(GATES_MD, encoding="utf-8") as handle:
        boxes, struck, anticipated = scan_gates_md(handle.read())

    mirror = {k: v for k, v in doc.get("gates", {}).items() if k not in NOT_A_GATE}
    known = set(boxes) | set(struck) | set(anticipated)

    # (1) THE MIRROR RULE ITSELF. Every checkbox needs a line in `gates`.
    for gate in boxes:
        if gate not in mirror:
            problems.append(
                f"GATES.md has a checkbox for {gate!r} and `gates` has no key for it"
                " - rule §9.7 says every checkbox is mirrored"
            )

    # (2) THE REVERSE. A key for a gate GATES.md no longer carries is a status nobody maintains.
    for key in mirror:
        if key not in known:
            problems.append(f"`gates` has a key {key!r} that matches no gate heading in GATES.md")

    # (3) STATUS DRIFT. Every open gate's mirror says PENDING and no closed one does; that
    # convention is the only machine-readable status in a free-prose field, so it is worth holding.
    for gate, is_ticked in boxes.items():
        body = mirror.get(gate)
        if not isinstance(body, str):
            continue
        if not is_ticked and "PENDING" not in body:
            problems.append(
                f"{gate!r} is unticked in GATES.md but its mirror does not say PENDING"
                " - an open gate that reads as settled is how the loop skips work it owes"
            )
        if is_ticked and "PENDING" in body:
            problems.append(f"{gate!r} is ticked in GATES.md but its mirror still says PENDING")

    open_count = sum(1 for ticked in boxes.values() if not ticked)
    print("\nGATES.md <-> state.json gate mirror (rule §9.7):")
    print(f"  {len(boxes)} checkboxes ({open_count} open, {len(boxes) - open_count} ticked),"
          f" {len(struck)} struck, {len(anticipated)} anticipated")
    print(f"  {len(mirror)} mirror keys in `gates` (excluding {', '.join(sorted(NOT_A_GATE))})")
    for problem in problems:
        print(f"  BROKEN: {problem}")
    if not problems:
        print("  OK: every checkbox mirrored, no orphan keys, no status drift.")
    return problems


def gates_sections(text):
    """Every `## ` section of GATES.md -> the lines beneath it, in order."""
    sections, title = {}, None
    for line in text.splitlines():
        match = SECTION_HEADING.match(line)
        if match:
            title = match.group(1)
            sections.setdefault(title, [])
            continue
        if title is not None:
            sections[title].append(line)
    return sections


def gate_bodies(text):
    """Every checkbox gate -> (is_ticked, its own lines). A body ends at the next gate or `## `.

    Continuation lines in this file are indented, so the three gate-heading patterns and the section
    heading are the only things that can close one; a nested `  - **item**` bullet cannot.
    """
    bodies, current = {}, None
    for line in text.splitlines():
        match = GATE_CHECKBOX.match(line)
        if match:
            current = match.group(2)
            bodies[current] = (match.group(1) == "x", [line])
            continue
        if SECTION_HEADING.match(line) or GATE_STRUCK.match(line) or GATE_ANTICIPATED.match(line):
            current = None
            continue
        if current is not None:
            bodies[current][1].append(line)
    return bodies


def check_gate_pointers():
    """Does every "the work is under <section>" pointer in an OPEN gate still point at open work?

    ADDED ITER151, after finding three that did not. gate-m2, gate-m3 and gate-m4 each opened their
    action list with "(1) the three sandbox items under <Setup Mirko must do before M2> below" - and
    those three items were finished at iter112, the publish they gated ran at iter113, and the
    section itself was later drained into gates-archive.json and relabelled "ALL FIVE DONE,
    ARCHIVED", leaving it with no checkbox at all. So for 38 iterations the two cheapest gates in
    the file opened by asking Mirko for setup he had already done, and gate-m3 - one label and a
    tick - read as step 3 of 4. `state.json -> gates` had it right all along; the authoritative file
    was the stale one.

    WHY NOTHING CAUGHT IT. iter150 found this same class in gate-m6's closing line and named the
    general gap: every rule in .claude/rules/ has a structural check, but the orientation prose that
    POINTS AT WORK has none. `check_gate_mirror` above cannot see it - it compares gate status, and
    all three gates really are open with mirrors that really do say PENDING. What was stale sat
    INSIDE an open gate, in its list of steps, where no set comparison reaches.

    WHAT THIS COVERS AND WHAT IT DOES NOT, said plainly because the limit is the point. It resolves
    one prose form - `under "<Title>"`, the one all three defects used - to a `## ` heading, and asks
    whether that section still carries an unticked box. It catches a pointer into a settled or
    drained section, and a pointer whose title matches no heading at all. It does NOT read a
    paraphrase ("see the setup section below"), and it cannot know whether a step spelled out inline
    is already done. A backstop for the shape that broke, not a proof that the prose is true.
    """
    problems = []
    with open(GATES_MD, encoding="utf-8") as handle:
        text = handle.read()
    sections = gates_sections(text)
    bodies = gate_bodies(text)

    pointers = 0
    for gate, (is_ticked, lines) in sorted(bodies.items()):
        if is_ticked:
            continue  # a settled gate's steps are history; only an open one still instructs.
        for title in SECTION_POINTER.findall("\n".join(lines)):
            pointers += 1
            matched = [name for name in sections if name.startswith(title)]
            if not matched:
                problems.append(
                    f"{gate!r} sends the reader to a section {title!r} that GATES.md does not have"
                )
                continue
            open_boxes = sum(
                1 for name in matched for line in sections[name]
                if (found := ANY_CHECKBOX.match(line)) and found.group(1) == " "
            )
            if open_boxes == 0:
                problems.append(
                    f"{gate!r} is open and points at {matched[0]!r} for work, but that section has"
                    " no unticked checkbox left - the step it names is already done"
                )

    print("\nopen gates pointing at other GATES.md sections for work (iter151):")
    open_gates = sum(1 for ticked, _ in bodies.values() if not ticked)
    print(f"  {len(sections)} sections, {open_gates} open gates, {pointers} section pointers")
    for problem in problems:
        print(f"  BROKEN: {problem}")
    if not problems:
        print("  OK: every pointer resolves to a section that still has outstanding work.")
    return problems


def check_stub_bodies(doc):
    """Does every one-line stub in `blockers` and `decisions` still have the body it points at?

    ADDED ITER159, and the case for it is `check_gate_mirror` above: `gates` was the FIRST of
    state.json's stub-plus-body splits to get a checker, and it got one only after
    `paste-rule-8-2a` sat unmirrored from iter75 to iter144. `blockers` and `decisions` are the
    same shape and were the last two without one. Both spell the contract out themselves:

      * blockers-open.json's `note` - "Full text of the OPEN blockers in tools/loop/state.json
        ... state.json -> blockers holds a one-line stub per key."
      * decisions-archive.json's `why` - "state.json keeps a one-line stub per key; this file is
        the full text", and `blockers._archive` prescribes the edit for adding one: "a short
        kebab-case key here with a ONE-LINE stub ending in a pointer, and the full body in
        blockers-open.json under the same key. TO SETTLE ONE: delete the key from both".

    A THREE-PLACE EDIT PRESCRIBED IN PROSE AND CHECKED BY NOTHING is the half-edit this catches:
    add a blocker and skip the body, and the stub's own "Body: tools/loop/blockers-open.json"
    sends the next iteration to a key that is not there; settle one in state.json only, and the
    orphan body left behind is a settled blocker still reading as open in the file an iteration
    opens to ACT.

    WHAT THIS DELIBERATELY DOES NOT CHECK, because the archive disclaims it. decisions-archive's
    `authoritative` says "The ANSWER lives in state.json -> decisions.<key>, not here ... if the
    two ever disagree, state.json wins and this is stale." So a body still posing a question Mirko
    has since answered is CORRECT BY DESIGN, and direction (3) below only asks for a body while
    the stub itself still says OPEN. The day Mirko answers a decision this check stays green with
    no edit - unlike the gate mirror, where a tick is exactly what the loop must go update.
    """
    problems = []
    with open(os.path.join(LOOP_DIR, "blockers-open.json"), encoding="utf-8") as handle:
        open_bodies = json.loads(handle.read()).get("blockers", {})
    with open(os.path.join(LOOP_DIR, "decisions-archive.json"), encoding="utf-8") as handle:
        decision_bodies = json.loads(handle.read()).get("decisions", {})

    stubs = {k: v for k, v in doc.get("blockers", {}).items() if not k.startswith("_")}
    decisions = {k: v for k, v in doc.get("decisions", {}).items() if not k.startswith("_")}

    # (1) EVERY BLOCKER STUB HAS A BODY. `blockers` is the orientation copy and every entry there
    # is open by definition, so this direction has no exemption.
    for key in stubs:
        if key not in open_bodies:
            problems.append(
                f"`blockers` has a stub {key!r} with no body in blockers-open.json"
                " - the stub's own pointer sends the next iteration to a key that is not there"
            )

    # (2) THE REVERSE. A body for a blocker `blockers` no longer lists is a settled blocker that
    # still reads as open in the file `_archive` tells the reader to open in order to ACT.
    for key in open_bodies:
        if key not in stubs:
            problems.append(
                f"blockers-open.json has a body {key!r} that `blockers` does not list"
                " - settling one means deleting the key from BOTH files"
            )

    # (3) OPEN DECISIONS HAVE A BODY. Only the open ones: see the docstring: an answered stub needs
    # no question behind it, and `compositeAction`/`formatOnEditHook` are exactly that shape today.
    for key, stub in decisions.items():
        if not isinstance(stub, str) or not stub.startswith("OPEN"):
            continue
        if key not in decision_bodies:
            problems.append(
                f"decision {key!r} still says OPEN and has no body in decisions-archive.json"
                " - `nextAction` sends the reader there for every open decision's full body"
            )

    # (4) NO ORPHAN DECISION BODIES. Same failure as (2) and the same one paste-rule-8-2a had:
    # a key that exists in one place and is invisible in the place orientation actually reads.
    for key in decision_bodies:
        if key not in decisions:
            problems.append(
                f"decisions-archive.json has a body {key!r} that `decisions` has no stub for"
                " - a decision nobody orienting can see"
            )

    # (5) A SETTLED BLOCKER STAYS SETTLED. `blockersArchive.settled` exists so no iteration
    # re-adds one ("append a one-line verdict ... so it is never re-added"); a key in both places
    # at once means that tombstone failed at the only job it has.
    settled = doc.get("blockersArchive", {}).get("settled", {})
    for key in settled:
        for where, block in (("`blockers`", stubs), ("blockers-open.json", open_bodies)):
            if key in block:
                problems.append(
                    f"{key!r} is recorded settled in blockersArchive.settled but is open again in"
                    f" {where} - re-adding a settled blocker is what that tombstone prevents"
                )

    open_decisions = sum(1 for v in decisions.values() if isinstance(v, str) and v.startswith("OPEN"))
    print("\nstate.json stubs <-> their archived bodies (iter159):")
    print(f"  {len(stubs)} blocker stubs / {len(open_bodies)} bodies in blockers-open.json,"
          f" {len(settled)} settled tombstones")
    print(f"  {len(decisions)} decision stubs ({open_decisions} OPEN)"
          f" / {len(decision_bodies)} bodies in decisions-archive.json")
    for problem in problems:
        print(f"  BROKEN: {problem}")
    if not problems:
        print("  OK: every stub resolves to a body, no orphan bodies, no settled blocker reopened.")
    return problems


def transcript_for(wanted):
    """Which `logs/iter-*.log` holds iteration `wanted`'s transcript. NOT `iter-<wanted>-*` before 137.

    MEASURED AT ITER137 from loop.log: the driver's own counter counted ATTEMPTS, so each of the 25
    usage-limit deaths burned a number that no iteration ever owned, and 133 of the first 136
    iterations were filed under someone else's. The drift is NOT a constant either - it steps 0, 4, 9,
    13, 17, 21, 25 - so subtracting 25 is right for the tail and wrong for 109 of the 133. From
    iter137 the driver names the file `iter-NNNN-passNNNN-<ts>.log` after state.json's iteration, so
    the new shape is looked up directly and only the historic ones need counting.

    Counting rule for the old shape: iteration i is the i-th pass that exited 0. A non-zero exit
    finished no iteration (it died and got resumed), which is exactly why the two numbers parted.
    """
    if not os.path.exists(LOOP_LOG):
        return None, "no logs/loop.log on this machine"

    with open(LOOP_LOG, encoding="utf-8", errors="replace") as handle:
        lines = handle.read().splitlines()

    iteration, owner_pass, stated = 0, None, None
    for line in lines:
        match = re.search(r"Iteration (\d+|\?)(?: \(pass (\d+)\))? finished \(exit (\d+)\)", line)
        if not match:
            continue
        said, pass_n, code = match.group(1), match.group(2), int(match.group(3))
        if pass_n and said.isdigit():
            # iter137 onward: the driver states the iteration, so believe it rather than counting.
            if int(said) == wanted and code == 0:
                stated = int(pass_n)
            continue
        if code == 0:
            # In the old shape the number printed IS the driver's pass counter, so `said` is the
            # filename's number and the running count is the iteration. Reading the pass number off
            # the count instead resolved every iteration to itself - a confident wrong answer that
            # only a spot-check against a known pair (iter4 lives in iter-0008-*) exposed.
            iteration += 1
            if iteration == wanted:
                owner_pass = int(said)

    if stated is not None:
        pattern = f"iter-{wanted:04d}-pass{stated:04d}-"
    elif owner_pass is not None:
        pattern = f"iter-{owner_pass:04d}-"
    else:
        return None, f"loop.log records no completed pass for iteration {wanted}"

    found = sorted(name for name in os.listdir(LOG_DIR) if name.startswith(pattern)) if os.path.isdir(LOG_DIR) else []
    note = ""
    if stated is None and owner_pass != wanted:
        note = f" (filed under the driver's pass number {owner_pass}, not {wanted})"
    if not found:
        return None, f"expected logs/{pattern}*.log{note}, but no such file is on disk"
    return found, f"logs/{found[0]}{note}"


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

    _, where = transcript_for(wanted)
    print(f"\ntranscript: {where}")
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

    calibration_problems = check_calibration()
    archive_problems = check_done_archive(doc)
    mirror_problems = check_gate_mirror(doc)
    pointer_problems = check_gate_pointers()
    stub_problems = check_stub_bodies(doc)

    if calibration_problems:
        print("\nFAIL: a bytes-per-token constant is optimistic, so every estimate and headroom")
        print("figure above under-states the truth. Lower the constant in this file - do NOT drop the")
        print("measurement, which is the evidence. See the docstring's iter138 note.")
        return 1
    if failures:
        print(f"\nFAIL: {', '.join(failures)} past budget. For state.json, move an append-heavy field")
        print("to an archive file and leave a short pointer behind - see `readMe` and `doneArchive`.")
        print("For GATES.md or PLAN.md, the content is the spec: archive settled sections, never drop them.")
        return 1
    if archive_problems:
        print("\nFAIL: done-archive.jsonl is not the complete log state.json claims. Repair it BEFORE")
        print("trimming doneRecent - an entry held only there is destroyed by the next iteration.")
        return 1
    if mirror_problems:
        print("\nFAIL: `state.json -> gates` is not the mirror of GATES.md rule §9.7 requires. Add the")
        print("missing line (one line: whose call it is, what to do, where the full text lives), drop the")
        print("orphan, or correct the status - GATES.md is authoritative, so it is the mirror that moves.")
        return 1
    if pointer_problems:
        print("\nFAIL: an OPEN gate tells Mirko to do work that is already done, or points at a")
        print("section that is not there. Rewrite that gate's steps to name only what is left - the")
        print("cost of this one is measured in gates that look expensive and therefore stay unticked.")
        return 1
    if stub_problems:
        print("\nFAIL: a stub in `blockers`/`decisions` and its archived body disagree about what")
        print("exists. Adding one writes BOTH places; settling a blocker deletes the key from BOTH and")
        print("appends a verdict to blockersArchive.settled. Never drop a body to make this pass.")
        return 1
    print("\nOK: all three step-1 Reads return their whole file, the done archive is intact, every")
    print("GATES.md checkbox is mirrored, no open gate points at work that is finished, and every")
    print("blocker/decision stub resolves to its archived body.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
