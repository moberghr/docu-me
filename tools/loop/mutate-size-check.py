#!/usr/bin/env python3
"""Prove tools/loop/check-state-size.py's red branches actually fire.

iter119's lesson, on a different checker: "the checker's own red branches were proven once and
asserted by nothing." iter129 widened this checker from one guarded file to three, so the FAIL path
now has three ways in and none of them had been exercised.

Method: copy the step-1 read path into a scratch tree, grow ONE file past a ceiling, run the
checker against the copy, and require a non-zero exit that NAMES that file. The live repo is never
touched. Green baseline is asserted first, so a checker that fails on everything cannot pass this.

REPAIRED AT ITER162, AND THE REASON IS THE LESSON. This harness was 0/5 at HEAD, every case reading
"baseline copy is not green (exit 1), harness is invalid" - so the guard on the loop's own checker had
proved NOTHING since roughly iter136. Nothing was wrong with the cases. `NEEDED` was a hand-written
list of six files, and each check added after iter129 (`check_done_archive` at iter136, then iters
159/160/161) reads a file that list never gained, so the checker died on a FileNotFoundError inside
the scratch tree. **A harness that enumerates its fixture by hand rots every time the thing it
guards grows a dependency.** The fix is to copy the DIRECTORY (minus logs/, which no check in main()
reads) instead of naming files, so the next check added needs no edit here. Its own baseline
assertion is what kept this honest: it reported invalid rather than passing vacuously.
"""

import os
import shutil
import subprocess
import sys
import tempfile

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
CHECKER = "tools/loop/check-state-size.py"
# Root files the checker reads. Everything under tools/loop/ comes over wholesale - see the iter162
# note above; do not go back to enumerating it.
ROOT_FILES = ["GATES.md", "PLAN.md", "CLAUDE.md"]
SKIP_DIRS = shutil.ignore_patterns("logs", "__pycache__")

CAP_TOKENS = 25_000
BUDGET_TOKENS = 20_000


def build_tree(root):
    for rel in ROOT_FILES:
        dest = os.path.join(root, rel)
        os.makedirs(os.path.dirname(dest) or root, exist_ok=True)
        shutil.copy2(os.path.join(REPO, rel), dest)
    shutil.copytree(os.path.join(REPO, "tools", "loop"),
                    os.path.join(root, "tools", "loop"), ignore=SKIP_DIRS)
    mirror_rest(root)


def mirror_rest(root):
    """SYMLINK everything else in the repo into the scratch tree.

    ITER167, AND IT IS THE ITER162 LESSON RECURRING ONE LEVEL UP. That repair replaced a hand-written
    list of six files with "copy the whole tools/loop directory, so the next check added needs no
    edit here" - and it held for exactly as long as every check read only tools/loop. iter167's
    `check_citation_resolution` resolves the paths the orientation layer cites, which point at src/,
    tests/, docs/, plugin/, .github/ and .mtk/, so this harness went 0/5 again with the same
    "baseline copy is not green" message and for the same underlying reason: THE FIXTURE WAS STILL
    ENUMERATED, just at a coarser grain.

    Symlinks rather than copies: the mutation cases only ever grow the three step-1 files, which are
    real copies above, so nothing here is written through. A link costs nothing and the fixture is
    now the whole tree by construction - the next check can read anywhere with no edit here.
    """
    for name in os.listdir(REPO):
        if name in (".git", "tools") or os.path.exists(os.path.join(root, name)):
            continue
        os.symlink(os.path.join(REPO, name), os.path.join(root, name))
    for name in os.listdir(os.path.join(REPO, "tools")):
        if name == "loop":
            continue
        os.symlink(os.path.join(REPO, "tools", name), os.path.join(root, "tools", name))


def run(root):
    proc = subprocess.run([sys.executable, os.path.join(root, CHECKER)],
                          capture_output=True, text=True)
    return proc.returncode, proc.stdout + proc.stderr


def grow_markdown(root, rel, target_bytes):
    """Append to EXACTLY target_bytes. The over-budget band is only 5,000 tokens wide, so a
    coarse filler chunk overshoots it into OVER CAP - which is how the first run of this harness
    failed, loudly, on its own arithmetic rather than on the checker."""
    path = os.path.join(root, rel)
    deficit = target_bytes - os.path.getsize(path)
    if deficit <= 0:
        raise AssertionError(f"{rel} is already {os.path.getsize(path)} B, cannot grow to {target_bytes}")
    with open(path, "a", encoding="utf-8") as handle:
        handle.write("\n" + "prose filler for the size checker. " * (deficit // 34))
    while os.path.getsize(path) < target_bytes:
        with open(path, "a", encoding="utf-8") as handle:
            handle.write("x")


def grow_state(root, target_bytes):
    """state.json must stay parseable - the checker json.loads it, so padding goes in a field.
    Re-serializing changes the size, so converge on the target instead of guessing once."""
    import json
    path = os.path.join(root, "tools/loop/state.json")
    doc = json.load(open(path, encoding="utf-8"))
    pad = max(0, target_bytes - os.path.getsize(path))
    actual = os.path.getsize(path)
    for _ in range(40):
        doc["mutationPadding"] = "x" * pad
        json.dump(doc, open(path, "w", encoding="utf-8"), indent=2)
        actual = os.path.getsize(path)
        if abs(actual - target_bytes) <= 8:
            return
        pad += target_bytes - actual
        if pad < 0:
            raise AssertionError(f"cannot shrink state.json to {target_bytes} B")
    raise AssertionError(f"state.json did not converge on {target_bytes} B (got {actual})")


def case(name, mutate, expect_names, expect_text):
    with tempfile.TemporaryDirectory() as root:
        build_tree(root)
        code, out = run(root)
        if code != 0:
            return f"FAIL[{name}]: baseline copy is not green (exit {code}), harness is invalid"
        mutate(root)
        code, out = run(root)
        if code == 0:
            return f"FAIL[{name}]: mutation did NOT trip the checker (exit 0)"
        if expect_names not in out:
            return f"FAIL[{name}]: exit {code} but output never names {expect_names!r}"
        if expect_text not in out:
            return f"FAIL[{name}]: exit {code} but never printed {expect_text!r}"
        return f"ok[{name}]: exit {code}, named {expect_names}, printed {expect_text!r}"


def main():
    # Aim at a token count, then convert to bytes with the SAME constant the checker uses, so each
    # case lands in the band it is testing. 22,000 tok sits inside the 20,000-25,000 budget band.
    md_over_cap = int((CAP_TOKENS + 3_000) * 2.5)
    md_over_budget = int(22_000 * 2.5)
    json_over_budget = int(22_000 * 2.3)

    results = [
        case("PLAN.md over cap",
             lambda r: grow_markdown(r, "PLAN.md", md_over_cap),
             "PLAN.md", "OVER CAP - Read TRUNCATES"),
        case("GATES.md over cap",
             lambda r: grow_markdown(r, "GATES.md", md_over_cap),
             "GATES.md", "OVER CAP - Read TRUNCATES"),
        case("GATES.md over budget only",
             lambda r: grow_markdown(r, "GATES.md", md_over_budget),
             "GATES.md", "over budget - shrink it"),
        case("state.json over budget only",
             lambda r: grow_state(r, json_over_budget),
             "tools/loop/state.json", "over budget - shrink it"),
        case("state.json past the BYTE ceiling",
             lambda r: grow_state(r, 300 * 1024),
             "tools/loop/state.json", "OVER BYTE CEILING - Read FAILS"),
    ]

    for line in results:
        print(line)
    bad = [r for r in results if r.startswith("FAIL")]
    print(f"\n{len(results) - len(bad)}/{len(results)} red branches fire correctly")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
