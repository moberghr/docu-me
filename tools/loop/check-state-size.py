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

import importlib.util
import json
import os
import re
import subprocess
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

# How `check_stub_bodies` reads a decision stub's status, and it is deliberately not
# `startswith("OPEN")` any more. The marker sits at the very start of the stub, optionally wrapped in
# markdown emphasis - state.json bolds for emphasis on nearly every line, and `**OPEN**` is one
# keystroke from `OPEN`. iter164 measured that the bare prefix test goes blind on the emphasised form
# and takes direction (3) with it, silently: seven open decisions could lose their bodies and this
# script still exited 0 (cell stub-bodies/open-marker-bolded-bodies-gone).
DECISION_OPEN = re.compile(r"[*_\s]*OPEN\b")
# The same drift, caught rather than absorbed: an OPEN word in the stub's opening clause that the
# pattern above did NOT accept as the status marker. `\bOPEN\b` cannot match inside REOPEN - there is
# no word boundary there - which is what keeps `formatOnEditHook`'s "DO NOT REOPEN" from reading as
# an open decision. The window is the opening clause because that is where every marker in this field
# sits; the longest today is `mermaidDialectGap`'s "OPEN, MIRKO'S, iter113 -" at 24 characters.
DECISION_MARKER = re.compile(r"\bOPEN\b")
DECISION_MARKER_WINDOW = 48

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
#
# DELIBERATELY REPORTED WITHOUT A STATUS COLUMN, and reviewed at iter163 when the sweep for
# report-without-failing branches reached this block. Neither file goes through the Read tool at all
# - CLAUDE.md is injected by the CLI and ITERATION-PROMPT.md is `cat`'d into the prompt by
# docume-loop.sh:117 - so the 25,000-token cap does not apply to either, and printing `status_for`
# beside them would invent a defect rather than report one. Bytes and tokens with no verdict is the
# honest shape here: it is a context-cost readout, not a check.
ALWAYS_LOADED = [
    "CLAUDE.md",
    "tools/loop/ITERATION-PROMPT.md",
]

# Files under tools/loop/ that are ARCHIVES: opened on purpose by key, heading or date with
# grep/tail/offset, never Read whole, and therefore exempt from the token budget every other file
# here is held to. Each entry states WHY, because an exemption without a reason is how a file that
# IS read whole quietly becomes unreadable - which is exactly what happened to method-notes.md.
#
# DECLARED, NEVER INFERRED FROM THE FILENAME (iter161's lesson, applied before the fact): a rule like
# "anything matching *-archive.* is exempt" would let the next file exempt itself by being named
# well, and the whole point of this check is that a file cannot opt out of being readable by accident.
# Anything under tools/loop/ that is NOT listed here must fit in one Read, budget included.
ARCHIVE_FILES = {
    "done-archive.jsonl": "one line per iteration; read with `--find`, never whole (doneArchive.howToRead).",
    "blockers-archive.jsonl": "settled blocker bodies, one per line; opened by key.",
    "decisions-archive.json": "full decision bodies; opened by the key a `decisions` stub names.",
    "gates-archive.json": "the verbose pre-iter128 gate mirrors; GATES.md is the copy you read.",
    "spikes-archive.json": "settled spike findings; opened by spike name.",
    "handoff-archive.md": "session handoffs; opened by date when one is needed.",
    "method-notes-archive.md": "method notes, generation 1 - full and frozen at iter162; opened by heading.",
    "method-notes-archive-2.md": "method notes, generation 2 - iter162 onwards; opened by heading.",
    "method-notes-archive-3.md": "method notes, generation 3 - iter166 onwards; opened by heading.",
}

# The method-note generations, in order. DECLARED, for ARCHIVE_FILES' reason: a rule like
# "anything matching method-notes-archive*" would let a generation 4 exempt itself by being named
# well. check_method_notes_stubs asserts every name here is ALSO in ARCHIVE_FILES, which is how
# `nextAction`'s standing instruction - "a generation 3 must be DECLARED in ARCHIVE_FILES in the
# same change that creates it" - stops being prose an iteration can forget and starts being a
# failing check.
METHOD_NOTES_GENERATIONS = (
    "method-notes-archive.md",
    "method-notes-archive-2.md",
    "method-notes-archive-3.md",
)

# The canonical provenance sentence a method-notes.md stub must carry, in the THREE spellings that
# are actually in the tree. All three are live and a reader must handle all three - the same trap
# done-archive.jsonl's two entry shapes set (`doneArchive.format`).
#
# ITER166 WROTE THIS REGEX WRONG TWICE, and both misses fabricated findings rather than missing
# them: `[Mm]oved` does not match the all-caps "MOVED", which read 18 stubs as live bodies and
# then reported their perfectly good archived bodies as 18 orphans; case-insensitive "moved to"
# still missed "MOVED ON to", the one section rotated twice, whose wording differs precisely
# because its history does. Direction (4) below exists so a FOURTH spelling fails loudly instead
# of being silently absorbed into the live-body count.
METHOD_NOTES_MOVED_RE = re.compile(
    r"moved(?:\s+on)?\s+to\s+`([^`]+)`\s+at\s+(iter\d+)",
    re.IGNORECASE,
)

# ---------------------------------------------------------------------------
# CITATION RESOLUTION (iter167, check #10)
#
# What a cold session is told to OPEN and RUN. Every check through iter166 pairs a stub with a body
# inside a DECLARED set; none of them ever asked whether the ~85 `path/to/thing.ext` tokens in the
# orientation layer name files that exist. A dangling one sends the next iteration at a ghost.
CITATION_SOURCES = (
    "tools/loop/state.json",
    "GATES.md",
    "tools/loop/method-notes.md",
    "tools/loop/blockers-open.json",
    "tools/loop/decisions-archive.json",
)

# Citations that are ABSENT ON PURPOSE. DECLARED, never inferred - for ARCHIVE_FILES' reason, and
# for a sharper one here: three of these four are absences the orientation layer is deliberately
# TELLING you about ("deliberately absent", "do NOT recreate"), so a blunt "every citation must
# resolve" rule would report the tree's clearest documentation as four defects.
CITATION_KNOWN_ABSENT = {
    "tools/hooks/format-on-edit.py":
        "DELETED at iter155 (20c043a) when decisions.formatOnEditHook was answered 'delete'. Cited"
        " in state.json and GATES.md precisely to say DO NOT RECREATE IT. If this ever resolves,"
        " someone recreated it - that is the failure, not this declaration.",
    "cases/mermaid.md":
        "Deliberately absent from the golden corpus: beautiful-mermaid 1.1.3 rejects `graph TD;`"
        " and `pie`, and a page fails as a unit (gate-m2's caveat, decisions.mermaidDialectGap)."
        " Prose shorthand - the corpus is flat in tests/golden/, there is no cases/ directory.",
    "_meta/feedback/inbox/":
        "A CONSUMER-repo path. `docume sync --comments` writes it into the repo being documented;"
        " DocuMe's own tree has no _meta/. Absent here by design, at every commit.",
    "20-reference/conversion.md":
        "Shorthand for docs/wiki/20-reference/conversion.md, which exists and is cited in full in"
        " the same file. The suffix form is prose, not a second file.",
}

# ---------------------------------------------------------------------------
# HARNESS TRACKING (iter168, check #11)
#
# THE DIMENSION CHECK #10 COULD NOT REACH. iter167 measured that 26 of the orientation layer's 81
# resolving citations point into `.mtk/`, which is gitignored (.gitignore:7) - and one of them was
# `nextAction`'s "the ONE command that re-checks everything iters 162-167 touched". Those citations
# RESOLVED, so check #10 was green; they resolved on one machine only, so the loop's entire
# regression harness was one `rm -rf .mtk` or one clone away from vanishing with no error message.
# Existence is not the same property as availability, and only the second one survives a handoff.
#
# Each entry says what the file guards and what it reports when it is green, so a failure here can
# name the cost of the missing file rather than just its path. DECLARED, never inferred from a
# `mutate-*`/`probe-*` name shape, for ARCHIVE_FILES' reason - and paired BOTH WAYS with the runner
# below, because a harness the runner never calls is the rot iter162 named: a guard nobody re-runs.
HARNESSES = {
    "mutate-soft-flags.py": "iter163's four hardened branches plus iter164/165's vacuity cells (35/35)",
    "mutate-force-push-guard.py": "the force-push hook, both directions (25/25)",
    "mutate-size-check.py": "this checker's five original red branches, and the fixture recipe the"
                            " other harnesses import (5/5)",
    "mutate-method-notes-check.py": "method-notes.md's stub/body pairing (7/7)",
    "probe-refusal-appends.py": "that a vacuity refusal appends rather than returns (exit 0)",
    "mutate-citation-check.py": "check #10's four directions and its refusal (12/12)",
    "mutate-harness-tracking.py": "this check's own red branches, fixture-vs-broken-git included"
                                  " (12/12)",
    # iter169, and the one harness here that mutates the LIVE tree rather than a fixture - the class
    # it guards reads the real repo by construction (its RepoRoot walks up to DocuMe.slnx). It
    # refuses to start on a dirty tree and verifies the restore two ways before exiting.
    "mutate-toolchain-pinning.py": "ToolchainPinningTests' red branches: the SDK-band tripwire, the"
                                   " floating-install declaration both ways, and its vacuity"
                                   " refusal (8/8, plus a 3/3 self-check that its own cells can"
                                   " report FAIL)",
    # iter170, and the first guard here whose defect was already LIVE when the check was written -
    # every earlier one was green on arrival. Its stale-copy cell replants the exact sentence that
    # stood in method-notes.md from iter129 to iter170, which is why this file is a declared fixture
    # above rather than swept prose.
    "mutate-prose-constants.py": "check_prose_constants' red branches: a stale copy, a stripped"
                                 " attribution, a moved constant, a blinded sweep, and its vacuity"
                                 " refusal (6/6)",
    # NOT a guard - the guarded thing. The runner calls it last as the live-tree control, and it is
    # held to the same tracked-ness as the rest, so listing it here keeps the pairing exact instead
    # of needing a second declaration for the one exception.
    "check-state-size.py": "the checker itself on the live tree, run last as the control (exit 0)",
}

HARNESS_RUNNER = "run-harnesses.py"

# The orientation layer's remaining `.mtk/` citations, counted at iter168 after the seven harnesses
# and the rotation engine moved into tracked space, and iter167's resolved finding left `nextAction`:
# 26 -> 21. A RATCHET, NOT A TARGET. Every one of the 23 is provenance for
# a measurement already taken ("39 dialects, measured here"), so losing them costs history, not
# capability, and demanding zero would fail today for no gain. What must not happen is the number
# GROWING - that is a new re-runnable thing written into scratch, which is the defect this check
# exists to stop. Lower it freely; raising it is a decision to be made out loud, in the same change.
SCRATCH_CITATION_CEILING = 21

# ---------------------------------------------------------------------------
# PROSE COPIES OF A CONSTANT (iter170, check #12)
#
# `check_calibration` guards BYTES_PER_TOKEN_* where they are DEFINED - it fails when one drifts
# optimistic against the MEASURED table. Nothing guarded the copies of them written into PROSE
# somewhere else, and iter170 found one that had been wrong since iter138 lowered the constant:
# method-notes.md's preamble said "Measured: markdown 2.604 B/tok, state.json's JSON 2.368" with no
# date and no supersession marker, in the file every iteration is ordered to read BEFORE writing a
# probe. 2.604 is iter129's blend-of-seven-files AVERAGE - the methodology this script's own
# docstring names as the one that was replaced ("pin each constant at or below the DENSEST file ever
# measured of that kind, never at the average"). The live pair is 2.4/2.3.
#
# THE COST WAS PAID, NOT HYPOTHESISED: iter170 read that bullet, used 2.604 in its first measurement
# script, and computed 4,674 B of method-notes.md headroom where this checker computes 594 B - an 8x
# over-statement of the one number that decides whether the file can take another note. The preamble
# bullet four rows above it warns "two copies of one rule is how the iter127 wording went stale".
#
# THE INVARIANT: a bytes-per-token ratio in prose is either (a) one of the live constants, or (b)
# attributed to the iteration that measured it, which is what makes it readable as dated history
# instead of as the value to compute with. An unattributed ratio that is not the live constant is a
# trap, and it is invisible from the definition site by construction.
#
# The population is swept, NOT declared per-file: the exemption direction is the dangerous one here
# (a new orientation file could opt out by not being listed), and inferring broadly can only ADD
# coverage. Only the exclusions are declared, with reasons.
PROSE_CONSTANT_RE = re.compile(r"(\d+\.\d+)\s*B/tok")
PROSE_CONSTANT_ATTRIBUTION = re.compile(r"iter\d+")
# Prose wraps, so a ratio's attribution routinely sits a line or two above it. Two lines of lead-in
# is the wrapped-paragraph scale; a larger window starts absorbing a NEIGHBOURING paragraph's
# `iterNNN` and calling an unattributed number attributed, which is the false-negative direction.
PROSE_CONSTANT_CONTEXT_LINES = 2
PROSE_CONSTANT_EXTS = (".md", ".py", ".json", ".jsonl")
PROSE_CONSTANT_EXCLUDED = {
    "logs": "per-iteration transcripts; they QUOTE the orientation layer, so a stale constant there"
            " is a historical record of the defect, not a live instruction.",
}
# A MUTATION HARNESS MUST CARRY THE DEFECT IT PLANTS. mutate-prose-constants.py replants the exact
# undated ratio this check exists to catch, so sweeping it fails the harness on its own payload -
# measured on the harness's first run. The payload cannot be attributed without neutering the cell.
#
# EXEMPTED VIA AN EXISTING DECLARED LIST, NOT A NAME SHAPE (iter161's rule, and the reason this is
# not `mutate-*`): a `mutate-*` glob would let a future orientation file opt out by being named
# well. HARNESSES is already declared here AND paired both ways with the runner by
# check_harness_tracking, so a new harness earns this exemption only by being declared as one.
# The authority is subtracted back in - it is in HARNESSES as the live-tree control, and fact (2)
# needs it in the population.
PROSE_CONSTANT_FIXTURES = "declared in HARNESSES: a harness's payload is a fixture, not prose"
# The file that DEFINES the constants must stay in the population. If the regex ever stops matching
# it, the sweep has gone blind and every other file passes for the wrong reason.
PROSE_CONSTANT_AUTHORITY = "check-state-size.py"

_SEG = r"[A-Za-z0-9_.@+-]+"
_CITE_RE = re.compile(rf"(?<![A-Za-z0-9_/:.-])((?:{_SEG})?(?:/{_SEG})+/?)")
_CITE_DELIMS = re.compile(r"[\s`'\"()\[\]{},;<>*]+")
_CITE_SCHEME = re.compile(r"[A-Za-z][A-Za-z0-9+.-]*://")
_CITE_EXT = re.compile(r"\.[A-Za-z0-9]{1,6}$")
_CITE_LINE = re.compile(r":\d+(?:-\d+)?$")


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

    # (0) NOT VACUOUS (iter164), and this one needed measuring rather than reasoning about. Every
    # check below is either per-entry or a comparison against a count the same edit can "repair", so
    # an EMPTY archive satisfies all of them at once: doneCount 0 agrees with 0 lines, `doneRecent`
    # is already empty, and COVERAGE and HEAD both skip themselves because `attributed` is empty -
    # so the HEAD check that exists to catch ONE missing record cannot fire when EVERY record is
    # missing. Measured: truncating the file and setting doneCount to 0 made this whole script exit 0
    # (tools/loop/mutate-soft-flags.py, cell done-archive/emptied-with-count).
    if not parsed:
        problems.append(
            "done-archive.jsonl holds no well-formed entries at all, so every check below is vacuous"
            " - doneCount agrees with zero lines and the COVERAGE and HEAD checks no-op when nothing"
            " is attributed. This archive only grows (doneArchive.howToAppend: every iteration"
            " appends one line), so empty is never a legitimate state - recover it with"
            " `git log -p -- tools/loop/done-archive.jsonl`, never re-create it empty"
        )

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

    # (0) NOT VACUOUS (iter164). All three directions below are per-item loops over `boxes` and
    # `mirror`, so emptying BOTH satisfies every one of them. Measured: draining `gates` down to its
    # pointer AND breaking GATES.md's checkbox shape left this check silent while two SIBLING checks
    # went red for their own reasons (cell gate-mirror/both-sides-drained) - the tree stayed
    # protected and this check did not, and a net that only holds while a different net holds is
    # decoration (iter146). Neither population empties on the event the loop is waiting for: ticking
    # every box leaves ten `- [x]` gates in `boxes`, and `gates` keeps its mirror key either way.
    if not boxes or not mirror:
        problems.append(
            f"parsed {len(boxes)} checkboxes out of GATES.md against {len(mirror)} keys in `gates` -"
            " with either at zero this check compares nothing, so what is wrong is the scan or the"
            " field name (GATE_CHECKBOX / `gates`), not the file"
        )

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

    ITER163: THIS CHECK HAS RESOLVED NOTHING SINCE THE DAY IT SHIPPED, AND SAID "OK" EVERY TIME.
    Found by the soft-flag sweep, and it is the mirror image of iter162's finding rather than a
    repeat: that one printed a defect and exited 0, this one printed a VERDICT IT HAD NOT REACHED.
    iter151 repaired the three stale pointers by rewriting the gates that carried them, which
    removed the last `under "<Title>"` in the file - so the population has been EMPTY from the first
    run, and `OK: every pointer resolves to a section that still has outstanding work` was true only
    in the way "every unicorn in this room is blue" is true. An empty population is NOT a failure
    here (a green that demands somebody write more pointer prose would be absurd), so the fix is
    twofold and only the first half has teeth: the WALK is asserted to have parsed something, which
    is what tells "vacuous because nobody writes that form" apart from "vacuous because the parser
    broke"; and the conclusion now says which of the two happened instead of claiming a resolution.
    """
    problems = []
    with open(GATES_MD, encoding="utf-8") as handle:
        text = handle.read()
    sections = gates_sections(text)
    bodies = gate_bodies(text)

    # (0) THE WALK PARSED SOMETHING. Every resolution below iterates `bodies` and looks up
    # `sections`, so a scan that returned neither reports OK over an empty tree - and both come from
    # regexes over a hand-edited markdown file, which is exactly the input that changes shape.
    # GATES.md always has `## ` sections and always has open gates, so zero of either is this scan
    # breaking, not the file emptying. This is the one vacuity in this check that IS a failure.
    if not sections or not bodies:
        problems.append(
            f"parsed {len(sections)} `## ` sections and {len(bodies)} gate bodies out of GATES.md -"
            " with either at zero every resolution below is vacuous, so this is the scan that is"
            " broken (GATE_CHECKBOX / SECTION_HEADING), not the file"
        )

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
    if not problems and pointers:
        print("  OK: every pointer resolves to a section that still has outstanding work.")
    if not problems and not pointers:
        # NOT "OK". Nothing was resolved, so a conclusion about resolutions would be a claim this
        # run did not earn - and the walk assertion above is what licenses calling this harmless.
        print(f"  VACUOUS, and not a failure: no gate uses the `under \"<Title>\"` form, so nothing"
              " was resolved this run.")
        print(f"      The walk is sound ({len(sections)} sections, {len(bodies)} gate bodies), so the"
              " population is genuinely empty - iter151")
        print("      emptied it by rewriting the three gates that carried the defect. Demanding a"
              " pointer exist would be absurd,")
        print("      so this stays exit 0; what changed at iter163 is that it no longer reports a"
              " verdict it never reached.")
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

    ITER164: SCOPING DIRECTION (3) TO A PROSE PREFIX WAS ITSELF A VACUITY, AND A ONE-KEYSTROKE ONE.
    iter159 knew the risk and put the guard in the wrong place - its harness read the printed
    `(7 OPEN)` back and refused a `(0 OPEN)` run, so the assertion lived in a scratch script nothing
    re-ran rather than in the check. Measured here: rewriting the seven stubs' markers as `**OPEN**`,
    which is this file's own house style, and deleting every decision body left this script exiting 0
    over seven orphaned questions. The prefix test is now emphasis-tolerant (DECISION_OPEN) and the
    drift it used to absorb is a reported defect of its own, while a genuine zero-OPEN run stays green
    and merely says so - because that is the day Mirko answers the last decision.
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

    # (0) NOT VACUOUS (iter164) - and ONLY for `decisions`, which is the whole subtlety. Directions
    # (3) and (4) are keyed lookups over `decisions` and its bodies, so draining both satisfies both:
    # measured, that made this script exit 0 (cell stub-bodies/decision-pair-drained). `decisions`
    # only grows, because an ANSWERED decision stays as a tombstone - `compositeAction` has read
    # "floating-v1" since iter75 and `formatOnEditHook` says do not reopen - so empty means the field
    # was renamed or the read is wrong. NOT ASSERTED FOR `blockers`/blockers-open.json, and not for
    # the count of OPEN decisions either: both of those legitimately reach zero on the day the last
    # blocker settles or the last decision is answered, and a check must never fire on the event the
    # loop is waiting for (iter159). Emptiness is a defect for one of these populations, not three.
    if not decisions:
        problems.append(
            "`decisions` has no stubs at all, so directions (3) and (4) below compare nothing"
            " - that field only grows (an answered decision stays as a tombstone), so empty means"
            " the field was renamed or this read is wrong, not that every decision is settled"
        )

    # (3) OPEN DECISIONS HAVE A BODY. Only the open ones: see the docstring: an answered stub needs
    # no question behind it, and `compositeAction`/`formatOnEditHook` are exactly that shape today.
    for key, stub in decisions.items():
        if not isinstance(stub, str):
            continue
        if not DECISION_OPEN.match(stub):
            # The marker drifted out of this check's reach rather than the decision being answered.
            if DECISION_MARKER.search(stub[:DECISION_MARKER_WINDOW]):
                problems.append(
                    f"decision {key!r} carries an OPEN marker in its opening clause that direction"
                    " (3) cannot read, so it is being skipped as though it were answered - the"
                    " marker must start the stub (emphasis is fine: `**OPEN**` matches, `still OPEN`"
                    " does not). This is how a population empties without anybody editing a check"
                )
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

    open_decisions = sum(
        1 for v in decisions.values() if isinstance(v, str) and DECISION_OPEN.match(v)
    )
    print("\nstate.json stubs <-> their archived bodies (iter159):")
    print(f"  {len(stubs)} blocker stubs / {len(open_bodies)} bodies in blockers-open.json,"
          f" {len(settled)} settled tombstones")
    print(f"  {len(decisions)} decision stubs ({open_decisions} OPEN)"
          f" / {len(decision_bodies)} bodies in decisions-archive.json")
    if not open_decisions and decisions:
        # iter163's rule, applied to this check's one legitimately-emptiable population: say that
        # direction (3) resolved nothing rather than letting the OK line below imply it resolved
        # something. Zero OPEN decisions is what Mirko answering the last one looks like.
        print("  NOTE: no decision stub reads as OPEN, so direction (3) resolved nothing this run"
              " - either all are answered, or a marker drifted (see DECISION_OPEN).")
    for problem in problems:
        print(f"  BROKEN: {problem}")
    if not problems:
        print("  OK: every stub resolves to a body, no orphan bodies, no settled blocker reopened.")
    return problems


def check_settled_bodies(doc):
    """Does every SETTLED tombstone still have the body it was archived from?

    ADDED ITER160, and it exists because iter159's enumeration was one short. That iteration asked
    "which siblings of an already-proven defect were never checked", found state.json's
    stub-plus-archived-body splits, counted THREE (`gates`, `blockers`, `decisions`) and checked all
    three - but it enumerated stub -> OPEN body pairs. There is a FOURTH pair of the same family on
    the settled side: a one-line verdict in `blockersArchive.settled` whose body belongs in
    blockers-archive.jsonl, and a name in `spikesArchive.settled` whose body belongs in
    spikes-archive.json.

    AND THE FOURTH PAIR WAS ALREADY BROKEN WHEN THE CHECK FOR THE OTHER THREE SHIPPED. iter155
    settled `format-on-edit-hook` in three places - the state.json stub, blockers-open.json, and a
    verdict in `blockersArchive.settled` - and appended nothing to blockers-archive.jsonl, so its
    1806-byte body existed only in commit 20c043a's parent. Five settled tombstones, four bodies.
    iter160 recovered it verbatim (.mtk/paths-160/migrate-settled-bodies.py, entry round-trip
    asserted) and this check is what stops the next settle from doing the same thing.

    SETTLING A BLOCKER IS THEREFORE A FOUR-PLACE EDIT, not the three `blockers._archive` used to
    prescribe: delete the key from `blockers` AND blockers-open.json, append a verdict to
    `blockersArchive.settled`, AND append the body here. check_stub_bodies covers the first three;
    this covers the fourth, which is the only one whose omission DESTROYS something.

    ANTI-VACUITY, which is method note (b) and cost iter159 an iteration to learn: both sides are
    keyed lookups, so a broken filter or a renamed field would report OK over an empty set forever.
    Both settled sets are asserted non-empty before anything is compared.
    """
    problems = []
    with open(os.path.join(LOOP_DIR, "blockers-archive.jsonl"), encoding="utf-8") as handle:
        records = [json.loads(line) for line in handle if line.strip()]
    with open(os.path.join(LOOP_DIR, "spikes-archive.json"), encoding="utf-8") as handle:
        spike_bodies = json.loads(handle.read())

    settled = doc.get("blockersArchive", {}).get("settled", {})
    spikes = doc.get("spikesArchive", {}).get("settled", [])

    # (0) NOT VACUOUS. Every comparison below is "key in <collection>"; an empty left-hand side
    # passes all of them silently, which is the exact failure mode this guard exists to refuse.
    #
    # IT NO LONGER RETURNS (iter165). It used to print this and stop, and the three populations are
    # NOT interdependent: draining `spikesArchive.settled` makes direction (4) vacuous and leaves the
    # blocker side - 5 tombstones against 5 bodies - fully assertable. Measured: with the spike names
    # drained AND one keyed body deleted, this check printed "nothing to check" and said nothing about
    # the missing body, which is the only defect in the tree that destroys text (cell
    # settled-bodies/spikes-drained-hides-missing-body). None of the directions below produces a
    # falsehood over an empty population either - "every tombstone is missing its body" is exactly
    # what a truncated archive means - so the refusal is now context on top of the findings, not
    # instead of them.
    if not settled or not spikes or not records:
        problems.append(
            f"nothing to check - {len(settled)} blocker tombstones, {len(spikes)} spike names,"
            f" {len(records)} archived blocker bodies. A vacuous pass is not a pass"
        )

    # (1) EVERY ARCHIVED BODY IS KEYED. Without this the pairing below cannot be made at all - the
    # four pre-iter160 lines carried only `n` and `archivedAt`, which is why nothing had checked it.
    keyed = {}
    for record in records:
        key = record.get("key")
        if not key:
            problems.append(
                f"blockers-archive.jsonl line n={record.get('n')!r} has no `key`"
                " - an unkeyed body cannot be paired with the tombstone it belongs to"
            )
            continue
        keyed[key] = record

    # (2) EVERY TOMBSTONE HAS ITS BODY. The one that destroys content: the verdict is one line, and
    # once blockers-open.json drops the key the full text is nowhere but git history.
    for key in settled:
        if key not in keyed:
            problems.append(
                f"{key!r} is recorded settled in blockersArchive.settled but has no body in"
                " blockers-archive.jsonl - settling a blocker archives the body, it does not"
                " discard it (see the iter155/format-on-edit-hook precedent)"
            )

    # (3) THE REVERSE. A body with no tombstone is a settled blocker nothing prevents re-adding.
    for key in keyed:
        if key not in settled:
            problems.append(
                f"blockers-archive.jsonl has a body {key!r} with no verdict in"
                " blockersArchive.settled - the tombstone is what stops it being re-opened"
            )

    # (4) THE SPIKE SIBLING, both directions. Same shape, and green today - this is a standing
    # guard, not a repair. PLAN.md §13 owns the spike CONTRACT; this only holds the archive to the
    # names state.json claims are in it.
    for name in spikes:
        if name not in spike_bodies:
            problems.append(
                f"spikesArchive.settled names {name!r} with no body in spikes-archive.json"
            )
    for name in spike_bodies:
        if name not in spikes:
            problems.append(
                f"spikes-archive.json has a body {name!r} that spikesArchive.settled does not name"
            )

    print("\nsettled tombstones <-> their archived bodies (iter160):")
    print(f"  {len(settled)} blocker verdicts / {len(records)} bodies in blockers-archive.jsonl"
          f" ({len(keyed)} keyed)")
    print(f"  {len(spikes)} spike names / {len(spike_bodies)} bodies in spikes-archive.json")
    for problem in problems:
        print(f"  BROKEN: {problem}")
    if not problems:
        print("  OK: every settled blocker and spike resolves to an archived body, no orphans.")
    return problems


GATES_ARCHIVE_NON_GATE_KEYS = {
    "trimmed-iter145":
        "iter145's own migration note - names the four side-item mirrors it moved here verbatim.",
    "settled-ci-findings-iter134-135":
        "a settled GATES.md section's body, not a gate: no checkbox was ever keyed for it.",
    "settled-setup-before-m2":
        "same shape - the pre-M2 sandbox setup section, settled at iter152 and archived whole.",
    "settled-notification-correction-iter139":
        "GATES.md's header parenthetical, not a gate: iter139's measurement that no notification"
        " fires when a gate opens. Archived at iter163 to buy back GATES.md budget, verbatim.",
}


def check_gates_archive(doc):
    """Does `gates`'s SECOND body pair up? (gates-archive.json, not GATES.md.)

    ADDED ITER161, by applying iter160's own transferable lesson one level further down. That lesson
    was "when an earlier iteration closed a family, check whether its enumeration skipped a
    lifecycle half". iter159 enumerated state.json's stub-plus-archived-body splits, counted three -
    `gates`, `blockers`, `decisions` - and recorded all three as checked, treating `gates` as covered
    by check_gate_mirror. THAT WAS TRUE OF ONE OF ITS BODIES. `gates` is the only split in this file
    with TWO: GATES.md, which is authoritative and live and has been paired since iter144, and
    tools/loop/gates-archive.json, which five stubs cite by name ("Long mirror:
    tools/loop/gates-archive.json (trimmed iter145)") and which nothing has ever paired. The
    enumeration did not skip a lifecycle half this time; it skipped a SECOND BODY on the same half.

    NOTHING IS LOST TODAY - this is a standing guard, not a repair, and iter161 says so plainly
    rather than dressing a green measurement up as a find. All five citations resolve, and the
    archive's twelve gate-shaped keys are exactly the twelve live `gates` keys. The one drift found
    was cosmetic and is recorded in the archive's own `trimmed-iter145` note: iter155 rewrote the
    `paste-format-on-edit-hook` stub when Mirko answered "delete" and the rewrite dropped that
    stub's pointer back here, so the note claims four trimmed mirrors while three still cite it. No
    text was destroyed - direction (1) is what would have caught it if any had been.

    WHAT THIS DELIBERATELY DOES NOT CHECK, for the same reason check_stub_bodies does not:
    `gatesArchive.note` disclaims the whole file ("Read GATES.md instead. This archive exists so
    nothing was discarded, not because it is the place to look"). A body that has gone stale against
    a gate Mirko has since moved is therefore CORRECT BY DESIGN. The archived
    `paste-format-on-edit-hook` body still describes the hook as a thing to FIX, which the answered
    decision has since overtaken; that is history behaving like history, not a failure.

    NON-GATE KEYS ARE DECLARED, NOT INFERRED. A regex over key shape would silently absorb a real
    gate whose mirror got mis-keyed - which is the failure this exists to catch - so the three meta
    keys are named in GATES_ARCHIVE_NON_GATE_KEYS above with a reason each, and direction (3) fails
    on a declaration that has gone stale in either direction.
    """
    problems = []
    with open(os.path.join(LOOP_DIR, "gates-archive.json"), encoding="utf-8") as handle:
        bodies = json.loads(handle.read())

    gates = {k: v for k, v in doc.get("gates", {}).items() if k != "authoritative"}
    citing = {k: v for k, v in gates.items() if "gates-archive.json" in str(v)}

    # (0) NOT VACUOUS, method note (b). Both directions are "key in <collection>" lookups, so an
    # empty citing set or an empty archive would report OK forever over nothing at all.
    #
    # IT NO LONGER RETURNS (iter165), for check_settled_bodies' reason. `citing` is derived by
    # SUBSTRING, so it empties the moment stubs get reworded - which iter155 already did to one of
    # them - while directions (1) and (3) still have their full populations: 12 gate-shaped bodies and
    # 4 declared keys. Measured: citations dropped AND an undeclared gate-shaped body planted, and
    # this check printed "nothing to check" while direction (1)'s orphan went unnamed (cell
    # gates-archive/citations-dropped-hides-orphan).
    if not citing or not bodies:
        problems.append(
            f"nothing to check - {len(citing)} gate stubs cite the archive, {len(bodies)} bodies"
            " in it. A vacuous pass is not a pass"
        )

    # (1) NO ORPHAN BODY. A gate-shaped key here that `gates` does not list is either a gate removed
    # without its mirror or a mis-keyed mirror - and a mis-keyed mirror is a pointer to nothing from
    # the other end, which is iter160's exact failure shape.
    for key in bodies:
        if key in gates or key in GATES_ARCHIVE_NON_GATE_KEYS:
            continue
        problems.append(
            f"gates-archive.json holds a body {key!r} that `gates` does not list and"
            " GATES_ARCHIVE_NON_GATE_KEYS does not declare - either it is a mis-keyed gate mirror,"
            " or it is deliberately not a gate and belongs in that declaration with a reason"
        )

    # (2) NO DANGLING CITATION. The stub tells the next iteration its long mirror is in this file;
    # if the key is not there, that pointer sends them to nothing.
    for key in citing:
        if key not in bodies:
            problems.append(
                f"gate {key!r} cites gates-archive.json as its long mirror but no body is keyed for"
                " it there - the pointer sends the next iteration to a key that is not there"
            )

    # (3) THE DECLARATION STAYS HONEST, both ways. A declared name that has vanished from the
    # archive is dead weight; one that is ALSO a live gate would exempt that gate from (1) forever.
    for key in GATES_ARCHIVE_NON_GATE_KEYS:
        if key not in bodies:
            problems.append(
                f"GATES_ARCHIVE_NON_GATE_KEYS declares {key!r} but gates-archive.json has no such"
                " key - drop the stale declaration, do not leave it exempting nothing"
            )
        if key in gates:
            problems.append(
                f"{key!r} is declared a non-gate key but `gates` lists it as a gate - that"
                " declaration would exempt a real gate mirror from the orphan check above"
            )

    gate_shaped = [k for k in bodies if k not in GATES_ARCHIVE_NON_GATE_KEYS]
    print("\n`gates` <-> gates-archive.json, its second body (iter161):")
    print(f"  {len(gate_shaped)} gate-shaped bodies / {len(gates)} live gates,"
          f" {len(GATES_ARCHIVE_NON_GATE_KEYS)} declared non-gate keys")
    print(f"  {len(citing)} stubs cite the archive as their long mirror")
    for problem in problems:
        print(f"  BROKEN: {problem}")
    if not problems:
        print("  OK: every archived gate body is a live gate or a declared non-gate key, and every"
              " citation resolves.")
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

    EVERY FAILURE HERE IS ADVISORY, ON PURPOSE (reviewed iter163's sweep). This returns a REASON
    string rather than raising, and `find_iteration` exits 0 on a missing transcript as long as the
    archive entry was found - because `tools/loop/logs/` is gitignored (.gitignore:1), so on a fresh
    clone or another machine there are no transcripts at all and the archive entry is the deliverable.
    The reason is spelled out in the returned string ("no such file is on disk") instead of a bare
    status word, which is what keeps this from being the kind of flag a reader learns to skim past.
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


def check_read_whole_files():
    """Does every tools/loop file that an iteration READS WHOLE still fit in one Read?

    ADDED ITER162, and it is a soft flag becoming a hard failure. This script has printed
    "OVER CAP - Read TRUNCATES" next to `method-notes.md` since iter139 and exited 0 anyway, so for
    ~23 iterations the protocol told every iteration to read a file whose newest notes a Read was
    silently dropping, and every iteration saw the flag and moved on. THE LESSON IS NOT ABOUT THAT
    FILE: a check that reports a defect without failing is a check that trains its readers to skim.
    Step 1's three files have failed this script since iter129; the rest of tools/loop had a
    printout. Now the printout has the same teeth.

    Three facts, and a vacuity refusal:
      (1) every file NOT declared an archive fits inside TARGET_TOKENS, with the remedy named
      (2) every declared archive still exists, so the declaration cannot rot into exempting nothing
      (3) an archive is exempt from the budget but still reported, because "you only grep it" is a
          claim about how it is read, not permission for it to be unreadable
    The refusal: if the scan classified no read-whole files at all, something is wrong with the walk
    and a green result would mean nothing.
    """
    problems = []
    read_whole, archives = [], []

    for name in sorted(os.listdir(LOOP_DIR)):
        path = os.path.join(LOOP_DIR, name)
        if name == "state.json" or not os.path.isfile(path):
            continue
        if os.path.splitext(name)[1] not in (".json", ".jsonl", ".md"):
            continue
        n_bytes = os.path.getsize(path)
        row = (name, n_bytes, est_tokens(n_bytes, name))
        (archives if name in ARCHIVE_FILES else read_whole).append(row)

    print("\nother files under tools/loop/ (NOT on the step-1 path). READ-WHOLE ones are held to the")
    print("same budget as step 1's three; archives are exempt by declaration and read with grep/offset:")
    for name, n_bytes, tokens in read_whole:
        print(f"  read-whole  {n_bytes:>8,}  ~{tokens:>7,} tok  {name}  {status_for(n_bytes, tokens)}")
    for name, n_bytes, tokens in archives:
        print(f"  archive     {n_bytes:>8,}  ~{tokens:>7,} tok  {name}  {status_for(n_bytes, tokens)}")

    # (1) THE READ-WHOLE CLASS FITS. Naming the remedy in the failure is the point: iter162's own
    # rotation is the worked example, and it is cheaper than an iteration rediscovering the shape.
    for name, n_bytes, tokens in read_whole:
        if tokens > TARGET_TOKENS:
            problems.append(
                f"{name} is {n_bytes:,} B / ~{tokens:,} tok, past the {TARGET_TOKENS:,}-token budget"
                " every read-whole file here must fit. Rotate its oldest settled sections into an"
                " archive VERBATIM, assert the round trip before rewriting the live file, and leave"
                " the heading plus its headlines behind (recipe:"
                " tools/loop/rotate-method-notes.py). If it is genuinely never read whole,"
                " declare it in ARCHIVE_FILES with the reason instead"
            )

    # (2) THE DECLARATION STAYS HONEST. A declared name that is not on disk exempts nothing and
    # hides a rename - the same failure shape iter161 guarded on gates-archive.json's key list.
    for name in ARCHIVE_FILES:
        if not os.path.isfile(os.path.join(LOOP_DIR, name)):
            problems.append(
                f"ARCHIVE_FILES declares {name!r} exempt but tools/loop/{name} is not on disk -"
                " drop the stale declaration or fix the name; a typo here silently exempts nothing"
            )

    # THE VACUITY REFUSAL. Every failure above is per-file, so a walk that finds no read-whole files
    # passes with flying colours and asserts nothing whatsoever.
    if not read_whole:
        problems.append(
            "the scan classified ZERO read-whole files under tools/loop/, so this check asserted"
            " nothing - the walk or the declaration is wrong, not the tree"
        )

    print("\nread-whole vs archive under tools/loop/ (iter162):")
    print(f"  {len(read_whole)} read-whole files, budget {TARGET_TOKENS:,} tok each;"
          f" {len(archives)} declared archives, exempt")
    print(f"  largest read-whole: "
          f"{max(read_whole, key=lambda r: r[2])[0] if read_whole else 'none'}"
          f" (~{max(r[2] for r in read_whole) if read_whole else 0:,} tok)")
    for problem in problems:
        print(f"  BROKEN: {problem}")
    if not problems:
        print("  OK: every read-whole file fits in one Read with budget to spare, and every declared")
        print("      archive exemption still names a file that exists.")
    return problems


def cite_strip_line(tok):
    """`src/X.cs:48` -> `src/X.cs`. Citations carry :line and :line-range."""
    return _CITE_LINE.sub("", tok)


def cite_normalise(tok):
    """The ONE place a prefix or suffix is trimmed, so both extractions trim identically.

    TWO BUGS LIVED HERE DURING ITER167'S MEASUREMENT, both of the fabricating kind:
      - stripping punctuation from BOTH ends turned `.mtk/x.py` into `mtk/x.py`: 43 invented misses
      - a sentence-ending period is INSIDE the segment class, so `...surface.py.` and
        `tests/golden/.claude/.` failed the extension test and were dropped silently
    """
    tok = tok.strip()
    while tok.startswith("./"):
        tok = tok[2:]
    while tok.endswith(".") and not tok.endswith("/."):
        tok = tok[:-1]
    if tok.endswith("/."):
        tok = tok[:-1]
    return tok


def cite_plausible(tok):
    """Is this a repo-relative path, or prose that happens to contain a slash?"""
    if not tok or tok.startswith(("http", "//")):
        return False
    if _CITE_SCHEME.search(tok):
        return False
    # `~/.claude.json` and `../../x` lose their root to the segment class; not repo-relative, and
    # resolving them against REPO would invent a verdict.
    if tok.startswith("/") or tok.startswith(".."):
        return False
    if "<" in tok or ">" in tok or "{" in tok:
        return False
    # `${CLAUDE_PLUGIN_ROOT}/hooks/x.sh`: the brace is outside the class, so the match starts after it.
    head = tok.split("/")[0]
    if head.isupper() and "_" in head:
        return False
    if "/" not in tok:
        return False
    # A bare top-level directory (`src/`, `.claude/`) names a region, not a thing to open.
    if len([s for s in cite_strip_line(tok).split("/") if s]) < 2:
        return False
    # `state.json/GATES.md` is prose disjunction: a real path carries no extension in a NON-FINAL
    # segment. Excluding it beats letting the pattern invent a file and report it missing.
    stem = cite_strip_line(tok).rstrip("/").split("/")[:-1]
    if any(_CITE_EXT.search(seg) and not seg.startswith(".") for seg in stem):
        return False
    if tok.endswith("/"):
        return True
    return bool(_CITE_EXT.search(cite_strip_line(tok)))


def cite_source_text(path):
    """A .json source is PARSED, not slurped: scanning raw bytes makes the `\\n` escape inside a
    string literal part of the next token, and `\\n.mtk/x.py` resolves against nothing."""
    with open(path, encoding="utf-8") as handle:
        if not path.endswith(".json"):
            return handle.read()
        doc = json.load(handle)

    chunks = []

    def walk(node):
        if isinstance(node, str):
            chunks.append(node)
        elif isinstance(node, dict):
            for key, value in node.items():
                chunks.append(key)
                walk(value)
        elif isinstance(node, list):
            for value in node:
                walk(value)

    walk(doc)
    return "\n".join(chunks)


def cite_extract_pattern(text):
    """Extraction A: one compiled pattern over the whole text."""
    out = set()
    for match in _CITE_RE.finditer(text):
        before = text[max(0, match.start() - 8):match.start()]
        if "://" in before[-4:] or before.endswith(":/"):
            continue
        tok = cite_normalise(match.group(1))
        if cite_plausible(tok):
            out.add(tok)
    return out


def cite_extract_tokens(text):
    """Extraction B: tokenise on delimiters FIRST, filter second. Never sees _CITE_RE."""
    out = set()
    for raw in _CITE_DELIMS.split(text):
        tok = raw.rstrip(".,:;")
        if raw.endswith("/"):
            tok = raw
        base = re.match(rf"^((?:{_SEG})?(?:/{_SEG})+/?)", tok)
        if not base:
            continue
        tok = cite_normalise(base.group(1))
        if cite_plausible(tok):
            out.add(tok)
    return out


def cite_resolves(tok):
    clean = cite_strip_line(tok)
    full = os.path.join(REPO, clean.rstrip("/"))
    if clean.endswith("/"):
        return os.path.isdir(full)
    return os.path.exists(full)


def check_citation_resolution():
    """Does every path the orientation layer cites as an ACTION exist on disk?

    ADDED ITER167, and it is a different dimension from every check above it. Those pair a stub with
    a body inside a set this file DECLARES (ARCHIVE_FILES, METHOD_NOTES_GENERATIONS,
    gates-archive's keys). None of them looks at the ~85 ordinary `path/to/thing.ext` tokens that
    make up the orientation layer's actual instructions - "run this probe", "the exact text is in
    that file", "strike the entry off this test". A cold session follows those literally.

    NOTHING IS BROKEN TODAY: 85 citations, 81 resolve, 4 are absent on purpose and declared above.
    Green, and said plainly. The value is standing - state.json's prose is rewritten every single
    iteration, and a citation typed into it is never checked by anything else.

    THE FIRST FACT IS ABOUT THE INSTRUMENT, NOT THE TREE, and that is deliberate. iter166's lesson
    was that an enumeration keyed on a phrase must be checked against an INDEPENDENT count of the
    same population before its findings are believed; iter167 then reproduced that failure three
    times inside the probe written to apply it - 43 fabricated findings, then 4, then 2, every one
    confident and specific. So the agreement of two independent extractions is not a note in a
    docstring here, it is fact (1), and a disagreement FAILS: when the instrument is wrong the
    other three facts are noise, and a green from them would be worse than no check at all.

    Four facts, and a vacuity refusal:
      (1) two independent extractions agree on the population, or nothing below is believable
      (2) every citation resolves, or is declared absent-on-purpose with a reason
      (3) every declared absence is STILL ABSENT - a declaration that starts resolving is stale,
          and for format-on-edit.py that specific direction IS the thing worth catching
      (4) every declared absence is STILL CITED - an exemption nobody invokes exempts nothing, the
          same rot ARCHIVE_FILES' existence check guards against

    THE REFUSAL APPENDS RATHER THAN RETURNS (iter165's shape): facts (3) and (4) have their own
    population - the four declarations - and a returning refusal would skip them in the same run
    that the extraction went quiet.
    """
    problems = []
    print("\norientation-layer citations <-> the working tree (iter167):")

    per_source, pattern_all, token_all = {}, set(), set()
    for rel in CITATION_SOURCES:
        path = os.path.join(REPO, rel)
        if not os.path.isfile(path):
            problems.append(
                f"CITATION_SOURCES names {rel!r} but it is not on disk - a source that vanished"
                " takes its whole citation population with it, silently"
            )
            continue
        try:
            text = cite_source_text(path)
        except (ValueError, UnicodeDecodeError) as exc:
            problems.append(f"{rel} could not be parsed for citations: {exc}")
            continue
        by_pattern, by_token = cite_extract_pattern(text), cite_extract_tokens(text)
        per_source[rel] = by_pattern | by_token
        pattern_all |= by_pattern
        token_all |= by_token

    # (1) THE INSTRUMENT AGREES WITH ITSELF.
    only_pattern, only_token = sorted(pattern_all - token_all), sorted(token_all - pattern_all)
    if only_pattern or only_token:
        problems.append(
            f"the two extractions disagree on {len(only_pattern) + len(only_token)} citation(s) -"
            f" pattern-only {only_pattern[:6]}, token-only {only_token[:6]}. Until they agree the"
            " resolution figures below are noise: fix the shared cite_normalise/cite_plausible"
            " rules, never silence one extraction to match the other"
        )

    population = pattern_all | token_all
    declared = set(CITATION_KNOWN_ABSENT)
    missing = sorted(t for t in population if not cite_resolves(t) and t not in declared)
    resolving = sorted(t for t in population if cite_resolves(t))

    print(f"  {len(population)} unique citations across {len(per_source)} sources;"
          f" {len(resolving)} resolve, {len(declared)} declared absent-on-purpose")

    # (2) EVERY CITATION RESOLVES OR IS DECLARED.
    for tok in missing:
        where = sorted(rel for rel, toks in per_source.items() if tok in toks)
        problems.append(
            f"{tok!r} is cited in {', '.join(where)} but is not on disk. Fix the path, or - if it"
            " is absent ON PURPOSE - declare it in CITATION_KNOWN_ABSENT with the reason, in the"
            " same change. Do not delete the sentence that cites it to make this pass"
        )

    # (3) AND (4) THE DECLARATION STAYS HONEST IN BOTH DIRECTIONS.
    for tok, reason in sorted(CITATION_KNOWN_ABSENT.items()):
        if not reason.strip():
            problems.append(f"CITATION_KNOWN_ABSENT[{tok!r}] has no reason - a silent exemption")
        if cite_resolves(tok):
            problems.append(
                f"CITATION_KNOWN_ABSENT declares {tok!r} absent on purpose, but it EXISTS now."
                " Either it was recreated against a recorded decision (for"
                " tools/hooks/format-on-edit.py that is exactly the event worth catching - see"
                " decisions.formatOnEditHook, answered 'delete'), or the declaration is stale and"
                " belongs deleted"
            )
        if tok not in population:
            problems.append(
                f"CITATION_KNOWN_ABSENT declares {tok!r} but nothing in the orientation layer cites"
                " it any more, so the exemption covers nothing. Delete the entry - an exemption"
                " kept past its citation is how a list rots into exempting a future typo"
            )

    # THE VACUITY REFUSAL. Every failure above is per-citation, so an extraction that went quiet -
    # a reworded source, a JSON that stopped parsing - passes clean while asserting nothing.
    if len(population) < 20:
        problems.append(
            f"only {len(population)} citations were extracted from {len(CITATION_SOURCES)} sources,"
            " which is far below the ~85 that have always been there - the extraction is broken,"
            " not the tree, and a green result here would mean nothing"
        )

    for problem in problems:
        print(f"  BROKEN: {problem}")
    if not problems:
        print("  OK: both extractions agree on the population, every citation resolves or is")
        print("      declared absent-on-purpose, and every declaration is still absent and still cited.")
    return problems


def harness_runner_steps():
    """The (basename, expected) pairs tools/loop/run-harnesses.py will really execute.

    IMPORTED, NOT PATTERN-MATCHED, and that is the iter167 lesson applied rather than repeated. That
    iteration's fix for a fabricating extraction was a second, independent extraction; the better fix
    where it is available is NO extraction - importing the runner returns the list it will actually
    iterate, so there is no pattern to get wrong. The module guards its work behind __main__, so the
    import executes nothing.
    """
    path = os.path.join(LOOP_DIR, HARNESS_RUNNER)
    if not os.path.isfile(path):
        return None, f"tools/loop/{HARNESS_RUNNER} is not on disk"
    try:
        spec = importlib.util.spec_from_file_location("loop_harness_runner", path)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        steps = module.STEPS
    except Exception as exc:  # a runner that will not import runs nothing, which is the failure
        return None, f"tools/loop/{HARNESS_RUNNER} could not be imported: {exc!r}"
    return [(os.path.basename(rel), expected) for _, rel, expected in steps], None


def harness_git_mode():
    """Can tracked-ness be asserted against THIS tree? -> ('repo' | 'fixture' | 'broken', detail)

    Three answers, and the middle one is why this is a function rather than one subprocess call.
      repo     REPO/.git is present and git answers - fact (3) below is asserted.
      fixture  no .git at all: a mutation harness's temp tree, where tracked-ness is UNKNOWABLE
               rather than false. Printing a verdict this tree cannot support would invent a defect,
               which is the reasoning iter163 recorded for ALWAYS_LOADED's missing status column. The
               other facts still assert here, so a fixture run is not a vacuous pass.
      broken   .git EXISTS but git could not answer. That is a FAILURE, not a fixture: in the live
               repo fact (3) must always be reached, and "git went missing" must never read the same
               as "this is a scratch copy".
    """
    if not os.path.isdir(os.path.join(REPO, ".git")):
        return "fixture", "no .git in this tree, so nothing here can be tracked by anything"
    try:
        proc = subprocess.run(["git", "-C", REPO, "rev-parse", "--is-inside-work-tree"],
                              capture_output=True, text=True, timeout=30)
    except (OSError, subprocess.SubprocessError) as exc:
        return "broken", f"{REPO}/.git exists but git could not be run: {exc!r}"
    if proc.returncode != 0 or proc.stdout.strip() != "true":
        return "broken", (f"{REPO}/.git exists but `git rev-parse --is-inside-work-tree` said"
                          f" {proc.stdout.strip()!r} (exit {proc.returncode})")
    return "repo", "git work tree"


def harness_is_tracked(name):
    proc = subprocess.run(
        ["git", "-C", REPO, "ls-files", "--error-unmatch", "--", f"tools/loop/{name}"],
        capture_output=True, text=True, timeout=30,
    )
    return proc.returncode == 0


def check_harness_tracking():
    """Do the loop's own regression harnesses survive a clone?

    ADDED ITER168, and it is the dimension check #10 measured but could not assert. Check #10 asks
    whether a cited path EXISTS; every one of the seven harnesses did, so it was green - and all
    seven lived in gitignored `.mtk/`, where they existed for this machine and no other. `nextAction`
    called one of them the one command that re-checks everything iters 162-167 touched. A clone got
    that command and not the file, with no error message anywhere.

    EXISTENCE AND AVAILABILITY ARE DIFFERENT PROPERTIES and only the second one survives a handoff.
    This check asserts the second.

    Five facts, and a vacuity refusal:
      (1) every declared harness is on disk
      (2) the declaration and the runner's STEPS name the same set BOTH WAYS - a harness the runner
          never calls is iter162's rot (a guard nobody re-runs), and a step naming an undeclared file
          is a harness whose tracked-ness nothing here asserts
      (3) every declared harness is TRACKED - asserted wherever a git work tree exists, and honestly
          reported as unknowable in a mutation fixture (see harness_git_mode)
      (4) the runner's expected result for each harness is recorded, so a step cannot quietly become
          a no-op that "passes" by printing nothing
      (5) the orientation layer's remaining `.mtk/` citations do not GROW past SCRATCH_CITATION_CEILING

    THE REFUSAL APPENDS RATHER THAN RETURNS (iter165's shape): facts (3) and (5) have populations of
    their own, and a returning refusal would skip them in the same run that the declaration emptied.
    """
    problems = []
    print("\nthe loop's own harnesses <-> git (iter168):")

    declared = set(HARNESSES)
    steps, runner_error = harness_runner_steps()
    if runner_error:
        problems.append(
            f"{runner_error}. That file IS the re-run command `nextAction` sends every cold session"
            " to, so this check cannot pair anything until it imports"
        )
    mode, detail = harness_git_mode()

    # (1) ON DISK.
    on_disk = sorted(n for n in declared if os.path.isfile(os.path.join(LOOP_DIR, n)))
    for name in sorted(declared - set(on_disk)):
        problems.append(
            f"HARNESSES declares tools/loop/{name} but it is not on disk - it guards"
            f" {HARNESSES[name]}, and that guard is now asserting nothing. Restore it"
            " (`git log --diff-filter=D --name-only -- tools/loop/`) or delete the declaration"
        )

    # (2) THE DECLARATION AND THE RUNNER AGREE, BOTH WAYS.
    if steps is not None:
        run_names = {name for name, _ in steps}
        for name in sorted(run_names - declared):
            problems.append(
                f"tools/loop/{HARNESS_RUNNER} runs tools/loop/{name} but HARNESSES does not declare"
                " it, so nothing here asserts it is tracked - the exact hole iter168 closed. Declare"
                " it with what it guards, in this change"
            )
        for name in sorted(declared - run_names):
            problems.append(
                f"HARNESSES declares {name} but {HARNESS_RUNNER} never runs it, so it is a guard"
                " nobody re-runs (iter162's rot). Add it to STEPS with its expected result, or drop"
                " the declaration if the thing it guarded is gone"
            )
        # (4) EVERY STEP STATES WHAT GREEN LOOKS LIKE.
        for name, expected in steps:
            if not str(expected).strip():
                problems.append(
                    f"{HARNESS_RUNNER}'s step for {name} declares no expected result, so a harness"
                    " that silently stopped asserting anything would still read as green"
                )

    # (3) TRACKED - THE PROPERTY THAT HOLDS ON SOMEONE ELSE'S MACHINE.
    if mode == "broken":
        problems.append(
            f"tracked-ness could not be established: {detail}. In the live repo this fact must always"
            " be reached; a git that cannot answer must not read like a scratch fixture"
        )
    # THE RUNNER IS IN THIS POPULATION, and it was not until the live tree showed why: facts (1) and
    # (2) only need it to be READABLE, so an untracked runner passes both while taking every harness
    # it calls out of a clone's reach with it.
    tracked_population = {name: HARNESSES[name] for name in on_disk}
    if steps is not None:
        tracked_population[HARNESS_RUNNER] = (
            f"nothing itself - it is the re-run command for all {len(steps)} steps above"
        )
    untracked = []
    if mode == "repo":
        untracked = sorted(n for n in tracked_population if not harness_is_tracked(n))
    for name in untracked:
        problems.append(
            f"tools/loop/{name} is on disk but NOT tracked by git, so it guards"
            f" {tracked_population[name]} on this machine only. `git add` it in this change - a clone"
            " gets the citation and not the file, which is exactly how iter167 found the whole"
            " harness set living in .mtk/"
        )

    # (5) THE SCRATCH RATCHET. Reuses check #10's population, so the two checks cannot disagree
    # about what the orientation layer cites.
    scratch = set()
    for rel in CITATION_SOURCES:
        path = os.path.join(REPO, rel)
        if not os.path.isfile(path):
            continue
        try:
            text = cite_source_text(path)
        except (ValueError, UnicodeDecodeError):
            continue
        for tok in cite_extract_pattern(text) | cite_extract_tokens(text):
            if tok.startswith(".mtk/"):
                scratch.add(tok)
    if len(scratch) > SCRATCH_CITATION_CEILING:
        fresh = sorted(scratch)
        problems.append(
            f"the orientation layer now cites {len(scratch)} paths under gitignored .mtk/, past the"
            f" {SCRATCH_CITATION_CEILING} standing at iter168: {fresh[:8]}. If the new one is"
            " RE-RUNNABLE, move it into tools/loop/ and retarget the citation in the same change -"
            " depth is preserved, so no path arithmetic inside it needs editing. If it is provenance"
            " for a measurement already taken, raise the ceiling here and say which citation it is"
        )

    print(f"  {len(on_disk)}/{len(declared)} declared harnesses on disk;"
          f" {len(steps) if steps else 0} runner steps; tracked-ness: {mode} ({detail})")
    if mode == "repo":
        print(f"  {len(tracked_population) - len(untracked)}/{len(tracked_population)} tracked by git"
              " (harnesses plus the runner) - these survive a clone, which is the whole point")
    print(f"  {len(scratch)} orientation citations still point into gitignored .mtk/"
          f" (ceiling {SCRATCH_CITATION_CEILING}, all provenance)")

    # THE VACUITY REFUSAL. Every failure above is per-harness or per-step, so an emptied declaration
    # passes clean while asserting nothing at all - and this check's whole subject is guards that
    # quietly stopped guarding.
    if not declared:
        problems.append(
            "HARNESSES is empty, so this check asserted nothing - the declaration is wrong, not the"
            " tree. A check about guards nobody re-runs must not become one"
        )

    for problem in problems:
        print(f"  BROKEN: {problem}")
    if not problems:
        print("  OK: every declared harness is on disk, paired with the runner both ways, states its")
        print("      expected result, and is tracked by git, so a clone gets the guards too.")
    return problems


def markdown_sections(path):
    """{heading: body} for every '## ' section. Shared by check_method_notes_stubs."""
    out, current, buf = {}, None, []
    with open(path, encoding="utf-8") as handle:
        for line in handle.read().splitlines():
            if line.startswith("## "):
                if current is not None:
                    out[current] = "\n".join(buf)
                current, buf = line[3:].strip(), []
            elif current is not None:
                buf.append(line)
    if current is not None:
        out[current] = "\n".join(buf)
    return out


def check_method_notes_stubs():
    """Does every method-notes.md stub resolve to the archived body it claims?

    ADDED ITER166, and it is the document-side twin of the three splits iters 159/160/161 closed
    inside state.json. Those paired `blockers`/`decisions` stubs with their archived bodies, settled
    tombstones with theirs, and `gates` with its second body. method-notes.md has the SAME shape and
    was the one instance nobody had paired: 24 of its 26 sections are stubs, each carrying a sentence
    of the form "MOVED to `<archive>` at iterNNN, verbatim and round-trip asserted", and until now
    the only thing guarding them was check_read_whole_files asserting the archive FILES exist. That a
    file exists says nothing about whether it still holds the section a stub promises is in it.

    NOTHING IS BROKEN TODAY - 24/24 resolve, both directions, and iter166 says so plainly rather
    than dressing a green measurement up as a find. The value is standing: this file is rotated
    every few iterations by hand, the rotation is a destructive write to the live file, and the
    "verbatim, round-trip asserted" claim in each stub was asserted ONCE, at rotation time, by the
    script doing the rotating. Nothing re-checked it afterwards.

    Four facts, and a vacuity refusal:
      (1) every stub resolves to a real, non-trivial section in the generation it cites
      (2) every archived section is cited by a stub - an uncited body is a lesson that has fallen
          out of the read path entirely, which is invisible from the live file by construction
      (3) every declared generation is also declared in ARCHIVE_FILES, so a generation 3 cannot be
          created without the exemption that makes it legal
      (4) a section that LOOKS like a stub but whose provenance does not parse is a failure, not a
          live body. This is the direction that cost iter166 two wrong regexes: an unmatched stub is
          silently reclassified as a live body, and its archived body then reports as an orphan.

    THE REFUSAL APPENDS RATHER THAN RETURNS (iter165's shape, and it matters here for iter165's
    exact reason): the stub population is derived by REGEX, so it empties the moment the stub layer
    is reworded, while directions (2) and (3) still have their full populations - 24 archived
    sections and 3 declared generations. A refusal that returned would print "nothing to check" and
    skip an orphaned body in the same run.
    """
    problems = []
    notes = os.path.join(LOOP_DIR, "method-notes.md")
    if not os.path.isfile(notes):
        print("\nmethod-notes.md stubs <-> their archived bodies (iter166):")
        print("  BROKEN: tools/loop/method-notes.md is not on disk")
        return ["tools/loop/method-notes.md is not on disk"]

    sections = markdown_sections(notes)
    generations = {}
    for name in METHOD_NOTES_GENERATIONS:
        path = os.path.join(LOOP_DIR, name)
        generations[f"tools/loop/{name}"] = markdown_sections(path) if os.path.isfile(path) else None

    stubs, live_bodies = [], []
    for heading, body in sections.items():
        match = METHOD_NOTES_MOVED_RE.search(body)
        if match:
            stubs.append((heading, match.group(1), match.group(2)))
            continue
        live_bodies.append((heading, body))

    # (1) EVERY STUB RESOLVES. A missing section means a rotation that dropped it, or a heading
    # edited on one side only - and the stub is the only thing that knows the body ever existed.
    resolved = 0
    for heading, dest, when in stubs:
        if dest not in generations:
            problems.append(
                f"stub {heading!r} cites {dest!r} ({when}), which is not a declared method-note"
                " generation - add it to METHOD_NOTES_GENERATIONS and ARCHIVE_FILES, or fix the path"
            )
            continue
        if generations[dest] is None:
            problems.append(
                f"stub {heading!r} cites {dest!r} ({when}), declared but not on disk"
            )
            continue
        body = generations[dest].get(heading)
        if body is None:
            problems.append(
                f"ORPHAN STUB {heading!r} claims it was moved to {dest} at {when}, but that file has"
                " no section with that heading. Recover it with"
                f" `git log -S '{heading[:40]}' -- tools/loop/`; never delete the stub to pass"
            )
            continue
        if len(body.strip()) < 200:
            problems.append(
                f"stub {heading!r} resolves in {dest} but the section is only"
                f" {len(body.strip()):,} B - the stub promises a full body was preserved there"
            )
            continue
        resolved += 1

    # (2) NO ORPHAN BODY. Invisible from method-notes.md by construction: nothing in the live file
    # mentions a lesson that lost its pointer, so only this direction can find one.
    cited = {(heading, dest) for heading, dest, _ in stubs}
    archived_total = 0
    for dest, secs in generations.items():
        if secs is None:
            continue
        archived_total += len(secs)
        for heading in secs:
            if (heading, dest) not in cited:
                problems.append(
                    f"ORPHAN BODY {heading!r} lives in {dest} but no stub in method-notes.md points"
                    " at it - a lesson that has fallen out of the read path entirely. Add the stub"
                    " back (heading + destination + its headlines), do not delete the body"
                )

    # (3) A GENERATION CANNOT EXIST WITHOUT ITS EXEMPTION. `nextAction` has carried this as prose
    # since iter162; here it fails a run instead.
    for name in METHOD_NOTES_GENERATIONS:
        if name not in ARCHIVE_FILES:
            problems.append(
                f"{name!r} is a declared method-note generation but is NOT in ARCHIVE_FILES, so it is"
                " held to the read-whole token budget it exists to escape - declare it in the same"
                " change that creates it"
            )

    # (4) A STUB THAT DOES NOT PARSE IS NOT A LIVE BODY. Without this, a fourth spelling of the
    # provenance sentence is absorbed silently and takes its archived body down as an orphan with it.
    for heading, body in live_bodies:
        named = [dest for dest in generations if dest in body]
        if named:
            problems.append(
                f"section {heading!r} names {named[0]} but carries no parseable"
                " \"MOVED to `<archive>` at iterNNN\" sentence, so it counted as a LIVE BODY and its"
                " archived section will report as an orphan. Fix the wording to the canonical form"
                " (or widen METHOD_NOTES_MOVED_RE) - do not leave it half-classified"
            )

    # THE VACUITY REFUSAL, and it APPENDS. See the docstring.
    if not stubs:
        problems.append(
            f"nothing to check - 0 of {len(sections)} sections in method-notes.md parsed as a stub."
            " The stub layer was reworded out from under METHOD_NOTES_MOVED_RE, or the file no longer"
            " has one. A vacuous pass is not a pass"
        )

    print("\nmethod-notes.md stubs <-> their archived bodies (iter166):")
    print(f"  {len(sections)} sections = {len(stubs)} stubs + {len(live_bodies)} live bodies")
    print(f"  {resolved} resolved against {archived_total} archived sections in"
          f" {len(METHOD_NOTES_GENERATIONS)} declared generations")
    for problem in problems:
        print(f"  BROKEN: {problem}")
    if not problems:
        print("  OK: every stub resolves to a non-trivial archived body, every archived body is")
        print("      cited by a stub, and every generation is declared in ARCHIVE_FILES.")
    return problems


def check_prose_constants():
    """Does every bytes-per-token ratio written in prose agree with the constant it copies?

    ADDED ITER170, and it is the constant-side twin of every pairing check iters 159-168 built.
    Those paired a stub with its archived body, a gate with its mirror, a citation with the file it
    names, a harness with the runner. This one pairs a NUMBER in prose with the number in code, and
    it is the first of the family whose defect had already been live for 32 iterations: iter138
    lowered BYTES_PER_TOKEN_MARKDOWN from 2.5 to 2.4 and nothing looked for the prose copies of the
    old figure. method-notes.md's preamble still presented iter129's superseded blend average as the
    measured pair, undated, in the file every iteration reads before writing a probe.

    WHY THIS IS NOT check_calibration's JOB. That check asks whether the constants are conservative
    against the MEASURED table - it guards the definition. A prose copy is invisible to it: the
    number is not a constant, it is a sentence, and the sentence is what an agent reads and uses.
    iter170 used it, and got 4,674 B of headroom where this file computes 594 B.

    THREE FACTS, AND A VACUITY REFUSAL:
      (1) every ratio in prose is a live constant, or is attributed to the iteration that measured
          it. Attribution is what separates dated history (legitimate, and the archives are full of
          it) from a figure presented as the one to compute with.
      (2) the file that DEFINES the constants is still in the population. If the regex stops matching
          the canonical `N.NNN B/tok` spelling, the sweep goes blind and every file passes for the
          wrong reason - the same silent-reclassification trap that cost iter166 two wrong regexes.
      (3) every live constant is quoted SOMEWHERE in the orientation layer, so lowering one cannot
          leave the read path with no correct copy at all.

    THE REFUSAL APPENDS RATHER THAN RETURNS (iter165). The population is derived by regex over a
    swept tree, so it empties on a reworded spelling - and fact (2) has to be able to say so in the
    same run rather than being skipped by an early return.
    """
    problems = []
    live = {
        f"{BYTES_PER_TOKEN_MARKDOWN:g}": "BYTES_PER_TOKEN_MARKDOWN",
        f"{BYTES_PER_TOKEN_JSON:g}": "BYTES_PER_TOKEN_JSON",
    }

    fixtures = set(HARNESSES) - {PROSE_CONSTANT_AUTHORITY}
    targets = []
    for root, dirs, files in os.walk(LOOP_DIR):
        dirs[:] = [d for d in dirs if d not in PROSE_CONSTANT_EXCLUDED]
        for name in sorted(files):
            if name.endswith(PROSE_CONSTANT_EXTS) and name not in fixtures:
                targets.append(os.path.join(root, name))
    for rel in ("GATES.md", "PLAN.md", "CLAUDE.md"):
        path = os.path.join(REPO, rel)
        if os.path.isfile(path):
            targets.append(path)

    found = []
    for path in sorted(set(targets)):
        try:
            lines = open(path, encoding="utf-8").read().splitlines()
        except (OSError, UnicodeDecodeError):
            continue
        for n, line in enumerate(lines):
            for match in PROSE_CONSTANT_RE.finditer(line):
                start = max(0, n - PROSE_CONSTANT_CONTEXT_LINES)
                context = "\n".join(lines[start:n + 1])
                found.append({
                    "rel": os.path.relpath(path, REPO),
                    "line": n + 1,
                    "value": match.group(1),
                    "attributed": bool(PROSE_CONSTANT_ATTRIBUTION.search(context)),
                })

    # (1) EVERY RATIO IS AUTHORITATIVE OR ATTRIBUTED.
    for hit in found:
        if hit["value"] in live or hit["attributed"]:
            continue
        problems.append(
            f"{hit['rel']}:{hit['line']} states {hit['value']} B/tok, which is neither a live"
            f" constant ({', '.join(sorted(live))}) nor attributed to the iteration that measured"
            " it. Either name that iteration so it reads as dated history, or replace the figure"
            " with a pointer to check-state-size.py - never leave a superseded constant in prose an"
            " iteration is told to read before it measures anything"
        )

    # (2) THE AUTHORITY IS STILL IN THE POPULATION - otherwise the regex has rotted.
    if not any(hit["rel"].endswith(PROSE_CONSTANT_AUTHORITY) for hit in found):
        problems.append(
            f"the sweep found no ratio in {PROSE_CONSTANT_AUTHORITY}, which defines both constants."
            " PROSE_CONSTANT_RE no longer matches the canonical `N.NNN B/tok` spelling, so this"
            " check is blind and its green verdict means nothing"
        )

    # (3) EVERY LIVE CONSTANT IS QUOTED SOMEWHERE, so the read path keeps a correct copy.
    quoted = {hit["value"] for hit in found}
    for value, name in sorted(live.items()):
        if value not in quoted:
            problems.append(
                f"{name} is {value}, and no file in the orientation layer quotes it. Lowering a"
                " constant without updating the prose that explains it is exactly how iter138's"
                " change left 2.604 standing for 32 iterations"
            )

    # THE VACUITY REFUSAL, and it APPENDS.
    if not found:
        problems.append(
            f"nothing to check - 0 ratios matched across {len(targets)} swept files. A vacuous pass"
            " is not a pass"
        )

    print("\nprose copies of a bytes-per-token constant <-> the constant (iter170):")
    print(f"  {len(found)} ratios across {len(targets)} swept files;"
          f" live constants {', '.join(sorted(live))}")
    print(f"  {sum(1 for h in found if h['value'] in live)} are a live constant,"
          f" {sum(1 for h in found if h['value'] not in live and h['attributed'])} are dated history")
    for problem in problems:
        print(f"  BROKEN: {problem}")
    if not problems:
        print("  OK: every ratio in prose is a live constant or is attributed to the iteration that")
        print("      measured it, the defining file is still in the population, and every live")
        print("      constant is quoted somewhere an iteration reads.")
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

    read_whole_problems = check_read_whole_files()
    calibration_problems = check_calibration()
    archive_problems = check_done_archive(doc)
    mirror_problems = check_gate_mirror(doc)
    pointer_problems = check_gate_pointers()
    stub_problems = check_stub_bodies(doc)
    settled_problems = check_settled_bodies(doc)
    archive_mirror_problems = check_gates_archive(doc)
    method_notes_problems = check_method_notes_stubs()
    citation_problems = check_citation_resolution()
    harness_problems = check_harness_tracking()
    prose_constant_problems = check_prose_constants()

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
    if settled_problems:
        print("\nFAIL: a settled tombstone and its archived body disagree. SETTLING A BLOCKER IS A")
        print("FOUR-PLACE EDIT: delete the key from `blockers` AND blockers-open.json, append the")
        print("verdict to blockersArchive.settled, AND append the body to blockers-archive.jsonl.")
        print("The fourth is the only one whose omission destroys content - recover it from git")
        print("(`git log -S <key> -- tools/loop/blockers-open.json`), never delete a verdict to pass.")
        return 1
    if read_whole_problems:
        print("\nFAIL: a tools/loop file an iteration READS WHOLE no longer fits in one Read, or an")
        print("archive exemption has gone stale. This branch used to be a printed flag that exited 0,")
        print("and ~23 iterations read the flag and moved on while method-notes.md silently lost its")
        print("newest notes to truncation. Rotate, do not delete, and do not declare a file an archive")
        print("to silence this unless it is genuinely only ever opened by key, heading or date.")
        return 1
    if archive_mirror_problems:
        print("\nFAIL: `gates` and gates-archive.json disagree about what has a long mirror. `gates`")
        print("is the one split with TWO bodies - GATES.md is authoritative and live, this archive is")
        print("the trimmed verbose mirror - so fix the KEY, never delete a body: recover it with")
        print("`git log -S <key> -- tools/loop/gates-archive.json`. If a key is deliberately not a")
        print("gate, declare it in GATES_ARCHIVE_NON_GATE_KEYS with the reason, do not rename it away.")
        return 1
    if method_notes_problems:
        print("\nFAIL: method-notes.md and its archive generations disagree about what was rotated.")
        print("Rotating a section is a THREE-PLACE edit: append the body to the generation VERBATIM,")
        print("assert the round trip BEFORE the destructive write to method-notes.md, and leave the")
        print("heading plus its headlines behind as a stub. The stub is the only thing that knows the")
        print("body exists - recover a lost one with `git log -S <heading> -- tools/loop/`, and never")
        print("delete a stub or a body to make this pass. Recipe: tools/loop/rotate-method-notes.py.")
        return 1
    if citation_problems:
        print("\nFAIL: the orientation layer cites a path that is not there, or its absent-on-purpose")
        print("list has rotted. These citations ARE the instructions - a cold session runs and opens")
        print("them literally - so fix the path, or declare a deliberate absence in")
        print("CITATION_KNOWN_ABSENT with its reason in the SAME change. If the failure is the two")
        print("extractions disagreeing, fix that FIRST and ignore everything else this check said:")
        print("a mis-matching extractor does not miss findings quietly, it invents them (iter167).")
        return 1
    if harness_problems:
        print("\nFAIL: a harness that guards this loop's own tooling is not on disk, not tracked by")
        print("git, or not paired with tools/loop/run-harnesses.py. A guard that exists on ONE machine")
        print("guards nothing a clone or a cold session can run - that is how iter167 found the whole")
        print("set living in gitignored .mtk/ while check #10 called every citation green. `git add`")
        print("the file, or declare it in HARNESSES and STEPS together; never drop a declaration to")
        print("make this pass, because the declaration is the only thing that knows the guard exists.")
        return 1
    if prose_constant_problems:
        print("\nFAIL: a bytes-per-token ratio in prose disagrees with the constant it copies, or the")
        print("sweep that finds them has gone blind. check_calibration guards the DEFINITION; this")
        print("guards the copies, which are what an agent actually reads and computes with. Fix the")
        print("prose - attribute the figure to the iteration that measured it, or replace it with a")
        print("pointer to this file's constants. Do NOT raise a constant to match a sentence, and do")
        print("not delete a MEASURED row: the measurements are the evidence, the prose is the copy.")
        return 1
    print("\nOK: all three step-1 Reads return their whole file, every read-whole file under")
    print("tools/loop/ fits in one too, the done archive is intact, every GATES.md checkbox is")
    print("mirrored, no open gate points at work that is finished, every blocker/decision stub")
    print("resolves to its archived body, every settled tombstone still has the body it was archived")
    print("from, `gates`'s long-mirror archive pairs both ways, every method-notes.md stub")
    print("resolves to the archived body it claims, every path the orientation layer cites")
    print("either exists or is declared absent on purpose, every harness that guards this")
    print("tooling is tracked by git rather than living in scratch, and every bytes-per-token")
    print("ratio written in prose is a live constant or dated history rather than a stale copy.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
