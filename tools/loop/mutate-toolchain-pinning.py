#!/usr/bin/env python3
"""Prove every red branch of ToolchainPinningTests fires, including its vacuity refusal.

ITER169. The test class it guards was written because CI run 30283057704 went red on code nobody had
touched: the runner resolved SDK 10.0.302, this machine has 10.0.100, and `rollForward: latestFeature`
plus `AnalysisLevel: latest-recommended` plus warnings-as-errors turns a newer feature band into a
build error with no local signal. A class asserting that has to be mutation-checked like any other, or
it is one more thing that looks like coverage.

METHOD, AND IT DIFFERS FROM EVERY SIBLING IN tools/loop/ - SAID PLAINLY BECAUSE THE DIFFERENCE IS A
RISK. The others import check-state-size.py and point it at a FIXTURE tree, so the live repo is never
touched. That is not available here: the assertions live in a compiled test whose RepoRoot walks up
from its own assembly to find DocuMe.slnx, so it reads the real tree by construction and cannot be
redirected at one in /tmp. This harness therefore mutates the working tree in place and restores it.

WHAT KEEPS THAT HONEST, because "it restores afterwards" is what every destructive script claims:
  * it REFUSES TO START if any file it would touch is dirty in git - a harness must never be the
    reason someone's uncommitted edit disappears
  * every cell restores in a `finally`, so a failing assertion cannot leave a mutation behind
  * after the last cell it re-reads every target and compares BYTES against the snapshot, and then
    asks git the same question a second way. Both must agree before this script exits 0.

The cells are one per red branch, plus the two halves of the tripwire (rollForward and AnalysisLevel
fail it independently), plus an isolation check: a floating install in a TEMPLATE must not trip the
fact that is about this repository's OWN ci.yml, or the two facts are really one.

AND THEN IT CHECKS ITSELF. This harness passed 8/8 on its first run, which in this tree is a reason
to look rather than to celebrate - iter166's probe fabricated 18 findings before it found zero, and a
`cell()` that could not report FAIL would print exactly the 8/8 that was printed. `self_check` below
hands cell() three claims that must fail: a green run declared red, a substring the message does not
contain, and an also_absent that is present. It runs every time, because the honesty of a harness is
the thing most worth re-running.

Run: python3 tools/loop/mutate-toolchain-pinning.py    (~1.5 min; restores the tree before it exits)
"""

import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))

CI = os.path.join(".github", "workflows", "ci.yml")
RELEASE = os.path.join(".github", "workflows", "release.yml")
FEEDBACK = os.path.join("templates", "workflows", "docs-feedback.yml")
REFRESH = os.path.join("templates", "workflows", "docs-refresh.yml")
SYNC = os.path.join("templates", "workflows", "docs-sync.yml")
GLOBAL_JSON = "global.json"
PROPS = "Directory.Build.props"

# Every file a cell below writes to. The snapshot, the dirty-tree refusal and the final byte
# comparison all read exactly this list, so adding a cell that touches a new file and forgetting to
# declare it here is caught by the restore check rather than by a future `git diff`.
TARGETS = [CI, RELEASE, FEEDBACK, REFRESH, SYNC, GLOBAL_JSON, PROPS]

FILTER = "/*/*/ToolchainPinningTests/*"

_snapshot = {}


def read(rel):
    with open(os.path.join(REPO, rel), "rb") as handle:
        return handle.read()


def write(rel, data):
    with open(os.path.join(REPO, rel), "wb") as handle:
        handle.write(data)


def replace(rel, old, new):
    """Exactly one occurrence of `old` becomes `new`, or the cell is a lie about what it mutated."""
    text = _snapshot[rel].decode("utf-8")
    if text.count(old) != 1:
        raise AssertionError(
            f"{rel} contains {text.count(old)} occurrences of {old!r}, expected exactly 1 - the"
            " tree moved under this harness and the cell would be mutating something else"
        )
    write(rel, text.replace(old, new).encode("utf-8"))


def drop_lines(rel, needle):
    text = _snapshot[rel].decode("utf-8")
    kept = [line for line in text.splitlines(keepends=True) if needle not in line]
    if len(kept) == len(text.splitlines(keepends=True)):
        raise AssertionError(f"{rel} has no line containing {needle!r}; nothing would be removed")
    write(rel, "".join(kept).encode("utf-8"))


def append_line(rel, line):
    write(rel, _snapshot[rel] + line.encode("utf-8"))


# ---------------------------------------------------------------- the mutations, one per red branch


def m_rollforward_pinned():
    replace(GLOBAL_JSON, '"rollForward": "latestFeature"', '"rollForward": "disable"')


def m_analysis_level_pinned():
    replace(PROPS, "<AnalysisLevel>latest-recommended</AnalysisLevel>",
            "<AnalysisLevel>10.0-recommended</AnalysisLevel>")


def m_ci_pin_goes_latest():
    replace(CI, "@anthropic-ai/claude-code@2.1.219", "@anthropic-ai/claude-code@latest")


def m_undeclared_float_in_a_template():
    # A template that carries no global install today and is not in FloatingByDesign, so this trips
    # the undeclared branch and nothing else.
    append_line(SYNC, "        run: npm install -g some-linter@latest\n")


def m_stale_declaration():
    # Someone pins the install and leaves the declaration behind: the entry now exempts nothing.
    replace(REFRESH, "@anthropic-ai/claude-code@latest", "@anthropic-ai/claude-code@2.1.219")


def m_pin_instruction_deleted():
    replace(FEEDBACK, "# Pin this to an exact version", "# Set this to an exact version")


def m_no_installs_at_all():
    for rel in (CI, FEEDBACK, REFRESH):
        drop_lines(rel, "npm install -g")


def m_control():
    """No mutation. The live tree must be green, or every red above proves nothing."""


# ------------------------------------------------------------------------------------------ running


def run_tests():
    result = subprocess.run(
        ["dotnet", "test", "--solution", "DocuMe.slnx", "--no-build", "--filter-query", FILTER],
        cwd=REPO, capture_output=True, text=True, check=False,
    )
    return result.returncode, result.stdout + result.stderr


def cell(name, mutate, expect_red, expect=(), also_absent=()):
    try:
        mutate()
        code, output = run_tests()
    finally:
        for rel in TARGETS:
            write(rel, _snapshot[rel])

    red = code != 0
    problems = []

    if red != expect_red:
        problems.append(f"expected {'red' if expect_red else 'green'}, got exit {code}")
    for needle in expect:
        if needle not in output:
            problems.append(f"message missing {needle!r}")
    for needle in also_absent:
        if needle in output:
            problems.append(f"message should NOT mention {needle!r} - the two facts are not isolated")

    if problems:
        return f"FAIL  {name}: " + "; ".join(problems)
    return f"ok    {name}"


def refuse_on_a_dirty_tree():
    result = subprocess.run(
        ["git", "status", "--porcelain", "--"] + TARGETS,
        cwd=REPO, capture_output=True, text=True, check=False,
    )
    if result.returncode != 0:
        return f"git could not report on the targets: {result.stderr.strip()}"
    if result.stdout.strip():
        return (
            "these files this harness mutates are already modified:\n"
            + result.stdout.rstrip()
            + "\nCommit or stash them first. Restoring from the snapshot would silently overwrite"
            " whatever is in them now."
        )
    return None


def restored():
    """Both answers must agree: the bytes are back, AND git sees no change."""
    problems = [rel for rel in TARGETS if read(rel) != _snapshot[rel]]
    result = subprocess.run(
        ["git", "status", "--porcelain", "--"] + TARGETS,
        cwd=REPO, capture_output=True, text=True, check=False,
    )
    if result.stdout.strip():
        problems.append("git still reports: " + result.stdout.strip().replace("\n", ", "))
    return problems


def self_check():
    """Three claims cell() must reject. If any is accepted, every ok above is worthless."""
    return [
        ("green-declared-red", cell("x", m_control, True)),
        ("substring-not-in-message",
         cell("x", m_rollforward_pinned, True, ["a phrase this message never contains"])),
        ("also-absent-is-present",
         cell("x", m_ci_pin_goes_latest, True,
              also_absent=["unattended drift with nobody to notice it"])),
    ]


def main():
    refusal = refuse_on_a_dirty_tree()
    if refusal:
        print(f"REFUSING TO RUN: {refusal}")
        return 2

    for rel in TARGETS:
        _snapshot[rel] = read(rel)

    print("ToolchainPinningTests, one cell per red branch (iter169):\n")

    results = [
        # The tripwire on the open decision. Two cells because either half settles it on its own.
        cell("rollforward-pinned", m_rollforward_pinned, True,
             ["decisions.analyzerBandDrift", "delete this test"]),
        cell("analysis-level-pinned", m_analysis_level_pinned, True,
             ["decisions.analyzerBandDrift"]),
        # This repository's own CI.
        cell("ci-pin-goes-latest", m_ci_pin_goes_latest, True,
             ["unattended drift with nobody to notice it"]),
        # The declaration, both ways.
        cell("undeclared-float-in-a-template", m_undeclared_float_in_a_template, True,
             ["declare it in FloatingByDesign"],
             also_absent=["unattended drift with nobody to notice it"]),
        cell("stale-declaration", m_stale_declaration, True,
             ["A stale declaration exempts nothing"]),
        # The reason the declaration gives has to still be in the file it describes.
        cell("pin-instruction-deleted", m_pin_instruction_deleted, True,
             ["lines above the step mentions"]),
        # The vacuity refusal: an empty scan must fail rather than pass three facts by having nothing
        # to filter.
        cell("no-installs-found-at-all", m_no_installs_at_all, True,
             ["It is not evidence that", "npm install -g"]),
        cell("control-live-tree-is-green", m_control, False),
    ]

    for line in results:
        print(line)

    print("\ncan cell() report FAIL at all? three claims it must reject:")
    dishonest = []
    for name, line in self_check():
        if line.startswith("FAIL"):
            print(f"ok    {name}: rejected -> {line[6:].strip()}")
            continue
        dishonest.append(name)
        print(f"BROKEN {name}: cell() ACCEPTED a claim it should have rejected -> {line}")

    leftovers = restored()
    if leftovers:
        print("\nRESTORE FAILED - THE WORKING TREE IS STILL MUTATED:")
        for problem in leftovers:
            print(f"  {problem}")
        print("Recover with `git checkout --` on the files above.")
        return 2

    bad = [line for line in results if line.startswith("FAIL")]
    print(
        f"\n{len(results) - len(bad)}/{len(results)} cells, {3 - len(dishonest)}/3 self-check;"
        " working tree restored and verified twice"
    )
    return 1 if bad or dishonest else 0


if __name__ == "__main__":
    sys.exit(main())
