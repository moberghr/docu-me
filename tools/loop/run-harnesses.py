#!/usr/bin/env python3
"""Every harness iter163's changes are answerable to, on real exit codes.

iter163 hardened three soft branches in the loop's own tooling (check_gate_pointers' walk assertion,
run-suite.py's `verdict`, deny-history-rewrite.py's payload branch). Two of those files already had
a guard, and iter162's lesson was that a guard nobody re-runs rots silently - so this runs the new
cells AND the pre-existing guards on the same files, and reports each one's real exit code.

  mutate-soft-flags.py         35/35  iter163's four hardened branches, plus family 4: every check
                                      that was judged non-vacuous IN PROSE (iter164) or that DECLARES
                                      a refusal (iter165) now has a cell that empties its population
                                      and demands its own red - and, for the two refusals that used
                                      to `return`, that the refusal does not skip the directions
                                      whose population was still intact
  paths-130 force-push guard   25/25  the hook's original contract, unchanged by the payload fix
  paths-129 size-check guard    5/5   check-state-size.py's five original red branches still fire
  check-state-size.py            0    the live tree passes all eight checks

Run: python3 tools/loop/run-harnesses.py
"""

import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))

STEPS = [
    ("iter163 hardened branches + iter164/165 vacuity cells", "tools/loop/mutate-soft-flags.py",
     "35/35"),
    ("iter130 force-push guard", "tools/loop/mutate-force-push-guard.py", "25/25"),
    ("iter129 size-check red branches", "tools/loop/mutate-size-check.py", "5/5"),
    # iter166: the method-notes stub layer, the one stub/body split iters 159-161 did not cover.
    ("iter166 method-notes stub pairing", "tools/loop/mutate-method-notes-check.py", "7/7"),
    ("iter166 the refusal appends, not returns", "tools/loop/probe-refusal-appends.py", "exit 0"),
    # iter167: citation resolution - the first check of a dimension outside the declared-set idiom.
    ("iter167 orientation-layer citation resolution",
     "tools/loop/mutate-citation-check.py", "12/12"),
    # iter168: tracked-ness - the property a citation that RESOLVES still does not have.
    ("iter168 harness tracking", "tools/loop/mutate-harness-tracking.py", "12/12"),
    ("the checker itself, live tree", "tools/loop/check-state-size.py", "exit 0"),
]


def main():
    failures = []
    for name, rel, expected in STEPS:
        proc = subprocess.run(
            [sys.executable, os.path.join(REPO, rel)], cwd=REPO, capture_output=True, text=True
        )
        blob = proc.stdout + proc.stderr
        tail = [line for line in blob.strip().splitlines() if line.strip()][-1:]
        print(f"\n=== {name}  ({rel})")
        print(f"    expected {expected}; exit {proc.returncode}")
        for line in tail:
            print(f"    | {line}")
        if proc.returncode != 0:
            failures.append(name)

    print("\n" + "=" * 78)
    print(f"{len(STEPS) - len(failures)}/{len(STEPS)} harnesses green")
    for name in failures:
        print(f"  FAILED: {name}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
