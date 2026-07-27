#!/usr/bin/env python3
"""iter170 — prove check_prose_constants BOTH ways.

The check pairs a NUMBER written in prose with the constant it copies. Its defect had been live for
32 iterations before anything looked for it: iter138 lowered BYTES_PER_TOKEN_MARKDOWN from 2.5 to
2.4, and method-notes.md's preamble went on presenting iter129's blend average (2.604) as the
measured pair, undated, in the file every iteration reads before writing a probe. iter170 read it,
used it, and computed 4,674 B of headroom where the checker computes 594 B.

PREDICTIONS ARE WRITTEN BEFORE THE FIRST RUN (iter164's rule). Each cell's predicted verdict and
phrase below were filled in before any of this was executed.

GRADING SLICES STDOUT BY THIS CHECK'S OWN SECTION HEADER (iter164), because main() runs eleven
other checks that may fire for their own correct reasons - notably `check_calibration`, which shares
these constants. A cell that makes the script exit 1 while THIS check stays silent grades
WRONG-CHECK, not CAUGHT.

THIS HARNESS'S GRADING IS PROVEN NON-VACUOUS BY A REAL FAILURE, NOT A SELF-CHECK. iter169 had to
add a synthetic 3/3 self-check because its harness passed 8/8 on the first run, which says nothing
about whether a cell CAN report FAIL. This one graded 5/6 on its first complete run: the
`authority/dropped-from-population` cell was MISSED because it reworded two hand-listed ratios and a
third sat in the check's own comment. The cell was wrong, the check was right, and the prediction is
what distinguished them - so the BAD path is known to work because it fired.

Verdicts:
  CAUGHT       - this check printed a BROKEN line naming the planted defect
  MISSED       - the tree is broken and this check said nothing
  WRONG-CHECK  - the run failed, but a sibling check covered for this one
  OK-IGNORED   - the control cell, which must NOT fail

Run: python3 tools/loop/mutate-prose-constants.py   (restores every file it touches)
"""

import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))

NOTES = os.path.join(REPO, "tools/loop/method-notes.md")
GEN2 = os.path.join(REPO, "tools/loop/method-notes-archive-2.md")
CHECKER = os.path.join(REPO, "tools/loop/check-state-size.py")

TOUCHED = (NOTES, GEN2, CHECKER)

# This check's own section header, spelled in two halves so this file never carries the `<->`
# sequence in a shell-quotable argument (iter164: the Bash tool reads it as a zsh numeric-range
# glob). Located before anything is graded, so a renamed header fails loudly.
HEADER_PREFIX = "prose copies of a bytes-per-token constant "
HEADER_SUFFIX = " the constant (iter170):"


def read(path):
    with open(path, encoding="utf-8") as handle:
        return handle.read()


def write(path, text):
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(text)


def run_checker():
    proc = subprocess.run(
        [sys.executable, CHECKER], cwd=REPO, capture_output=True, text=True,
    )
    return proc.returncode, proc.stdout + proc.stderr


def slice_block(out):
    """Return only this check's section of the output, or None if its header is absent."""
    lines = out.splitlines()
    start = -1
    for i, line in enumerate(lines):
        if HEADER_PREFIX in line and HEADER_SUFFIX in line:
            start = i
            break
    if start < 0:
        return None
    block = []
    for line in lines[start + 1:]:
        if line and not line.startswith(" "):
            break
        block.append(line)
    return "\n".join(block)


# --- the mutations, each returning a dict of {path: mutated_text} --------------------------


def mut_stale_copy():
    """Re-plant the exact defect iter170 found: an undated, superseded ratio in the live file.

    This is the cell that matters, because it is not hypothetical - it is the sentence that stood
    in method-notes.md's preamble from iter129 to iter170 and that this iteration acted on.
    """
    text = read(NOTES)
    anchor = "  * WHEN A HARNESS CRASHES, CASES AFTER THE CRASH DID NOT RUN."
    assert text.count(anchor) == 1, f"anchor must match once, matched {text.count(anchor)}"
    planted = ("  * MEASURING BYTES PER TOKEN: build a file over the cap, Read it whole, divide.\n"
               "    Measured: markdown 2.604 B/tok, state.json's JSON 2.368.\n")
    return {NOTES: text.replace(anchor, planted + anchor)}


def mut_attribution_stripped():
    """Strip the iteration marker off a legitimately dated ratio in an archive.

    The archives are FULL of ratios and every one of them is fine, because each names the iteration
    that measured it. Remove that marker and the same sentence becomes a figure presented as
    current - which is the whole distinction this check draws.
    """
    text = read(GEN2)
    anchor = "and it is how iter129"
    assert text.count(anchor) == 1, f"anchor must match once, matched {text.count(anchor)}"
    return {GEN2: text.replace(anchor, "and it is how an early pass")}


def mut_constant_moved():
    """Lower the markdown constant and leave the prose quoting the old one.

    This replays iter138 exactly: the constant moves, nothing updates the sentences that explain
    it. 2.35 is chosen deliberately BELOW the densest measured markdown ratio (2.447) so
    `check_calibration` stays green and this cell cannot pass on a sibling's failure.
    """
    text = read(CHECKER)
    anchor = "BYTES_PER_TOKEN_MARKDOWN = 2.4"
    assert text.count(anchor) == 1, f"anchor must match once, matched {text.count(anchor)}"
    return {CHECKER: text.replace(anchor, "BYTES_PER_TOKEN_MARKDOWN = 2.35")}


def mut_authority_dropped():
    """Reword the two ratios in the DEFINING file so it falls out of the population.

    Direction (2). If PROSE_CONSTANT_RE stops matching the canonical spelling the sweep goes blind
    and every other file passes for the wrong reason - the silent-reclassification trap that cost
    iter166 two wrong regexes. Rewording the authority's own mentions is the cheapest way to
    simulate that without touching the pattern.

    REWORDS EVERY OCCURRENCE, NOT A HAND-LISTED TWO. The first version of this cell named two
    anchors and asserted each matched once - both assertions passed, and the cell still graded
    MISSED, because a THIRD ratio sits in this check's own explanatory comment. Fact (2) cannot
    fire while ANY ratio remains in the file, so a partial mutation leaves the tree healthy and
    grades the check as broken. Hence the sweep, plus an assertion that none survives.
    """
    text = read(CHECKER)
    # The pattern LITERAL is `(\\d+\\.\\d+)\\s*B/tok`, with no digits before `B/tok`, so this
    # substitution cannot damage the regex it is simulating the rot of.
    mutated, count = re.subn(r"(\d+\.\d+)(\s*)B/tok", r"\1\2bytes per token", text)
    assert count >= 3, f"expected at least 3 ratios in the authority, reworded {count}"
    assert not re.search(r"(\d+\.\d+)\s*B/tok", mutated), "a ratio survived; fact (2) cannot fire"
    return {CHECKER: mutated}


def mut_vacuity():
    """Break the pattern so the population empties entirely.

    Tests the iter165 shape directly: the refusal must APPEND, so the run has to print BOTH
    "nothing to check" AND direction (2)'s and (3)'s findings, which still have their full
    populations. A refusal that returned would print the first and skip the rest.
    """
    text = read(CHECKER)
    anchor = 'PROSE_CONSTANT_RE = re.compile(r"(\\d+\\.\\d+)\\s*B/tok")'
    assert text.count(anchor) == 1, f"anchor must match once, matched {text.count(anchor)}"
    return {CHECKER: text.replace(
        anchor, 'PROSE_CONSTANT_RE = re.compile(r"(\\d+\\.\\d+)\\s*B/tokZZ")'
    )}


def mut_control():
    """No mutation. The only proof that the cells above fail for their own reason."""
    return {}


CELLS = [
    # (name, mutation, predicted verdict, phrase the block must contain)
    ("control/unmutated", mut_control, "OK-IGNORED", None),
    ("stale-copy/undated-superseded-ratio", mut_stale_copy, "CAUGHT", "neither a live"),
    ("attribution/iter-marker-stripped", mut_attribution_stripped, "CAUGHT", "neither a live"),
    ("constant-moved/prose-not-updated", mut_constant_moved, "CAUGHT", "quotes it"),
    ("authority/dropped-from-population", mut_authority_dropped, "CAUGHT", "no ratio in"),
    ("vacuity/pattern-broken", mut_vacuity, "CAUGHT", "nothing to check"),
]


def main():
    originals = {p: read(p) for p in TOUCHED}

    # The un-mutated tree must be green BEFORE anything is graded (iter162: assert the control
    # first, or every CAUGHT is measured against an already-broken baseline).
    code, out = run_checker()
    block = slice_block(out)
    if block is None:
        print("BROKEN HARNESS: this check's section header is not in the output. Renamed?")
        return 1
    if "BROKEN:" in block or code != 0:
        print(f"BROKEN BASELINE: checker exit={code} and this check's block is not clean.")
        print(block)
        return 1
    print("baseline: checker exit 0, this check's block clean\n")

    results = []
    try:
        for name, mutate, predicted, phrase in CELLS:
            mutation = mutate()
            for path, text in mutation.items():
                write(path, text)
            code, out = run_checker()
            block = slice_block(out)
            for path, text in originals.items():
                write(path, text)

            if block is None:
                verdict = "BROKEN-HARNESS"
            elif not mutation:
                verdict = "OK-IGNORED" if code == 0 and "BROKEN:" not in block else "CONTROL-FAILED"
            elif "BROKEN:" in block and (phrase is None or phrase in block):
                verdict = "CAUGHT"
            elif "BROKEN:" in block:
                verdict = "CAUGHT-WRONG-REASON"
            elif code != 0:
                verdict = "WRONG-CHECK"
            else:
                verdict = "MISSED"

            ok = verdict == predicted
            print(f"  {'OK ' if ok else 'BAD'}  {name}: predicted {predicted}, got {verdict}")
            results.append(ok)
    finally:
        # Restore unconditionally, then prove it (iter160: `git checkout --` is unusable once the
        # increment has dirtied these files, so restore from the in-memory snapshot and verify).
        # A `return` here would swallow an exception from the loop above, so the verdict is
        # recorded and acted on AFTER the block.
        unrestored = []
        for path, text in originals.items():
            write(path, text)
        for path, text in originals.items():
            if read(path) != text:
                unrestored.append(path)

    if unrestored:
        for path in unrestored:
            print(f"\nBROKEN: {path} was not restored")
        return 1

    code, out = run_checker()
    if code != 0 or "BROKEN:" in (slice_block(out) or "BROKEN:"):
        print("\nBROKEN: the checker is not green again after restoring")
        return 1

    print(f"\n{sum(results)}/{len(results)} cells as predicted; tree restored and checker green")
    return 0 if all(results) else 1


if __name__ == "__main__":
    sys.exit(main())
