#!/usr/bin/env python3
"""iter166 — prove check_method_notes_stubs BOTH ways.

A check that has never been shown to fail is a claim, not a guard. This harness plants one defect
per direction and grades whether THAT check named it.

PREDICTIONS ARE WRITTEN BEFORE THE FIRST RUN (iter164's rule: a cell whose result surprises you is
either a find or a broken cell, and only the prediction tells you which). Each cell's `predict`
field below was filled in before any of this was executed.

GRADING SLICES STDOUT BY THE CHECK'S OWN SECTION HEADER (iter164's rule: "the checker exited
non-zero" attributes nothing when main() runs eight other checks that may fire for their own correct
reasons). A cell that makes the script exit 1 while THIS check stays silent grades WRONG-CHECK, not
CAUGHT. The header is located before anything is graded, so a renamed header fails loudly instead of
grading everything green.

Verdicts:
  CAUGHT       - this check printed a BROKEN line naming the planted defect
  MISSED       - the tree is broken and this check said nothing
  WRONG-CHECK  - the run failed, but a sibling check covered for this one
  OK-IGNORED   - the control cell, which must NOT fail

Run: python3 tools/loop/mutate-method-notes-check.py   (restores every file it touches)
"""

import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))

NOTES = os.path.join(REPO, "tools/loop/method-notes.md")
GEN3 = os.path.join(REPO, "tools/loop/method-notes-archive-3.md")
CHECKER = os.path.join(REPO, "tools/loop/check-state-size.py")

# The check's own section header. Located before grading; a rename fails the run.
# Spelled in two halves so this file never carries the `<->` sequence in a shell-quotable
# argument (iter164: this Bash tool reads it as a zsh numeric-range glob).
HEADER_PREFIX = "method-notes.md stubs "
HEADER_SUFFIX = " their archived bodies (iter166):"

ROTATED_HEADING = "## Proving a vacuity judgement instead of writing one (iter164)"


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
    start = -1
    for i, line in enumerate(out.splitlines()):
        if HEADER_PREFIX in line and HEADER_SUFFIX in line:
            start = i
            break
    if start < 0:
        return None
    block = []
    for line in out.splitlines()[start + 1:]:
        # the next check's section header, or the final verdict block
        if line and not line.startswith(" "):
            break
        block.append(line)
    return "\n".join(block)


# --- the mutations, each returning a dict of {path: mutated_text} --------------------------


def mut_orphan_stub():
    """Rename the heading in gen3 so the live stub points at nothing."""
    text = read(GEN3)
    assert text.count(ROTATED_HEADING) == 1, "anchor must match exactly once"
    return {GEN3: text.replace(
        ROTATED_HEADING, "## Proving a vacuity judgement instead of writing one (iter164) [renamed]"
    )}


def mut_orphan_body():
    """Delete a stub from the live file, leaving its body archived and uncited."""
    text = read(NOTES)
    assert text.count(ROTATED_HEADING) == 1, "anchor must match exactly once"
    start = text.index(ROTATED_HEADING)
    end = text.index("\n## ", start + 1)
    return {NOTES: text[:start] + text[end + 1:]}


def mut_undeclared_generation():
    """Drop gen3 from ARCHIVE_FILES while leaving it in METHOD_NOTES_GENERATIONS."""
    text = read(CHECKER)
    anchor = ('    "method-notes-archive-3.md": "method notes, generation 3 - iter166 onwards;'
              ' opened by heading.",\n')
    assert text.count(anchor) == 1, "anchor must match exactly once"
    return {CHECKER: text.replace(anchor, "")}


def mut_fourth_spelling():
    """Reword one stub's provenance into a spelling MOVED_RE does not know.

    THIS IS THE CELL THAT MATTERS. It is the exact failure that bit iter166 twice while writing
    the probe: an unparsed stub is silently reclassified as a live body, and its archived body
    then reports as an orphan from the other direction. Direction (4) exists to name it as what
    it is - a stub that does not parse - instead of letting the run blame the archive.
    """
    text = read(NOTES)
    # The bare provenance string is NOT unique - two stubs cite generation 3 at iter166 - so the
    # anchor carries the sentence that follows it in iter164's stub only. This assertion fired for
    # real the moment the second rotation landed, which is the iter165 lesson working: a no-op
    # replace leaves the tree healthy and grades MISSED, i.e. a fabricated find.
    anchor = ("**MOVED to `tools/loop/method-notes-archive-3.md` at iter166**, verbatim and"
              " round-trip asserted,\n    into the generation this rotation opened.")
    assert text.count(anchor) == 1, f"anchor must match exactly once, matched {text.count(anchor)}"
    return {NOTES: text.replace(
        anchor, "**RELOCATED INTO `tools/loop/method-notes-archive-3.md`, iter166.**"
    )}


def mut_vacuity():
    """Reword EVERY stub's provenance so the whole population empties.

    Tests the iter165 shape directly: the refusal must APPEND, so the run prints both "nothing to
    check" AND the defects the other directions still own. A refusal that returned would print the
    first and skip the rest.
    """
    text = read(NOTES)
    for spelling in ("**MOVED to `", "**MOVED ON to `", "*Moved to `"):
        text = text.replace(spelling, "**RELOCATED INTO `")
    return {NOTES: text}


def mut_small_body():
    """Gut an archived section so the round trip it promises did not happen."""
    text = read(GEN3)
    assert text.count(ROTATED_HEADING) == 1, "anchor must match exactly once"
    start = text.index(ROTATED_HEADING)
    return {GEN3: text[:start] + ROTATED_HEADING + "\n\n  * (gutted)\n"}


def mut_control():
    """No mutation. The only proof that the cells above fail for their own reason."""
    return {}


CELLS = [
    # (name, mutation, predicted verdict, phrase the block must contain)
    ("control/unmutated", mut_control, "OK-IGNORED", None),
    ("orphan-stub/heading-renamed-in-archive", mut_orphan_stub, "CAUGHT", "ORPHAN STUB"),
    ("orphan-body/stub-deleted-from-live-file", mut_orphan_body, "CAUGHT", "ORPHAN BODY"),
    ("generation/dropped-from-ARCHIVE_FILES", mut_undeclared_generation, "CAUGHT", "ARCHIVE_FILES"),
    ("spelling/fourth-provenance-wording", mut_fourth_spelling, "CAUGHT", "counted as a LIVE BODY"),
    ("vacuity/every-stub-reworded", mut_vacuity, "CAUGHT", "nothing to check"),
    ("round-trip/archived-body-gutted", mut_small_body, "CAUGHT", "the stub promises"),
]


def main():
    originals = {p: read(p) for p in (NOTES, GEN3, CHECKER)}

    # The un-mutated tree must be green BEFORE anything is graded (iter162: assert the control
    # first, or every CAUGHT is measured against an already-broken baseline).
    code, out = run_checker()
    block = slice_block(out)
    if block is None:
        print("BROKEN HARNESS: the check's section header is not in the output. Renamed?")
        return 1
    if "BROKEN:" in block or code != 0:
        print(f"BROKEN BASELINE: checker exit={code} and this check's block is not clean.")
        print(block)
        return 1
    print(f"baseline: checker exit 0, this check's block clean\n")

    results = []
    try:
        for name, mutate, predicted, phrase in CELLS:
            for path, text in mutate().items():
                write(path, text)
            code, out = run_checker()
            block = slice_block(out)
            for path, text in originals.items():
                write(path, text)

            if block is None:
                verdict = "BROKEN-HARNESS"
            elif not mutate():           # control cell
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
            results.append((name, predicted, verdict, ok))
            flag = "  " if ok else "!!"
            print(f"{flag} {verdict:<20} (predicted {predicted:<10}) {name}")
    finally:
        for path, text in originals.items():
            write(path, text)

    # The tree must be exactly as it was found.
    for path, text in originals.items():
        assert read(path) == text, f"restore failed for {path}"

    passed = sum(1 for *_, ok in results if ok)
    print(f"\n{passed}/{len(results)} cells matched their prediction")
    code, out = run_checker()
    print(f"tree restored: checker exit {code}")
    return 0 if passed == len(results) and code == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
