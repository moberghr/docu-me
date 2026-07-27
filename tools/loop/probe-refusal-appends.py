#!/usr/bin/env python3
"""iter166 — does the vacuity refusal APPEND, or does it skip the findings?

iter165's finding was that two refusals ended in `return problems` where the others append, so a
run printed "nothing to check" and silently skipped the defects the other directions still owned.
check_method_notes_stubs was written in the appending shape. THAT IS A CLAIM UNTIL A CELL FIRES IT.

The cell: reword every stub's provenance so the stub population empties. The refusal must fire AND
directions (2) and (4) must still name their defects in the same run - 25 orphaned bodies and 25
unparseable stub-shaped sections. If only the refusal appears, the shape is wrong.

Restores the file it touches. Run: python3 tools/loop/probe-refusal-appends.py
"""

import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
NOTES = os.path.join(REPO, "tools/loop/method-notes.md")
CHECKER = os.path.join(REPO, "tools/loop/check-state-size.py")

HEADER_PREFIX = "method-notes.md stubs "
HEADER_SUFFIX = " their archived bodies (iter166):"


def block_of(out):
    lines = out.splitlines()
    start = next((i for i, l in enumerate(lines)
                  if HEADER_PREFIX in l and HEADER_SUFFIX in l), -1)
    assert start >= 0, "the check's section header is missing from the output"
    body = []
    for line in lines[start + 1:]:
        if line and not line.startswith(" "):
            break
        body.append(line)
    return "\n".join(body)


def main():
    with open(NOTES, encoding="utf-8") as handle:
        original = handle.read()

    mutated = original
    for spelling in ("**MOVED to `", "**MOVED ON to `", "*Moved to `"):
        mutated = mutated.replace(spelling, "**RELOCATED INTO `")
    assert mutated != original, "the mutation matched nothing"

    try:
        with open(NOTES, "w", encoding="utf-8") as handle:
            handle.write(mutated)
        proc = subprocess.run([sys.executable, CHECKER], cwd=REPO,
                              capture_output=True, text=True)
        block = block_of(proc.stdout + proc.stderr)
    finally:
        with open(NOTES, "w", encoding="utf-8") as handle:
            handle.write(original)

    refusal = "nothing to check" in block
    orphan_bodies = block.count("ORPHAN BODY")
    unparsed = block.count("counted as a LIVE BODY")

    print(block[:400] + ("\n  ..." if len(block) > 400 else ""))
    print()
    print(f"  vacuity refusal fired ......... {refusal}")
    print(f"  ORPHAN BODY findings .......... {orphan_bodies}")
    print(f"  unparseable-stub findings ..... {unparsed}")
    print()

    if not refusal:
        print("FAIL: the refusal did not fire, so this cell proved nothing.")
        return 1
    if orphan_bodies == 0 or unparsed == 0:
        print("FAIL: the refusal fired and the other directions went SILENT - that is the")
        print("`return problems` shape iter165 found, not the appending one.")
        return 1
    print(f"OK: the refusal APPENDS. One run printed 'nothing to check' AND named")
    print(f"{orphan_bodies} orphaned bodies and {unparsed} unparseable stubs. A refusal that")
    print("returned would have printed the first line and skipped the other two directions.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
