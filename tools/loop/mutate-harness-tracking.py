#!/usr/bin/env python3
"""Prove check_harness_tracking's red branches fire - including the two that only exist because a
mutation FIXTURE is not a git repository.

ITER168. The check asserts that every harness guarding this loop's tooling is tracked by git, which
is the property iter167 could measure and not assert: all seven lived in gitignored `.mtk/`, so they
EXISTED for one machine and for nobody else, and check #10 called every citation green.

METHOD, and it is mutate-citation-check.py's idiom rather than mutate-size-check.py's: the checker is
imported and `check_harness_tracking` is called with REPO, LOOP_DIR, HARNESSES, HARNESS_RUNNER,
CITATION_SOURCES and SCRATCH_CITATION_CEILING pointed at a FIXTURE tree in a temp directory. The live
repo is never touched and no cell can pass by accident of the real tree's shape.

TWO CELLS EXIST FOR ONE DISTINCTION THE CHECK MAKES DELIBERATELY: a tree with no `.git` is a fixture,
where tracked-ness is unknowable and the other facts must still fire; a tree WITH `.git` that git
cannot read is broken, and must fail. If those two ever collapse into each other, every mutation
harness in tools/loop/ silently stops asserting tracked-ness - so `fixture-still-asserts` and
`git-broken` are the cells that keep this check honest inside the fixtures of its own siblings.

THE REFUSAL CELL ASKS THE ITER165 QUESTION, NOT THE ITER164 ONE: it empties HARNESSES *and* plants an
independent ratchet breach in the same fixture, then demands BOTH messages. A refusal that `return`ed
would print "asserted nothing" and skip the plant, which is the defect iter165 found in two checks
that had looked correct for four iterations.

Run: python3 tools/loop/mutate-harness-tracking.py   (touches nothing outside a temp directory)
"""

import contextlib
import importlib.util
import io
import json
import os
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
CHECKER = os.path.join(REPO, "tools", "loop", "check-state-size.py")

# What the fixture declares. Deliberately NOT the live HARNESSES: a cell must fail because of the
# mutation it made, not because the real tree happens to agree with it.
FIXTURE_HARNESSES = {
    "mutate-alpha.py": "the alpha contract (3/3)",
    "mutate-beta.py": "the beta contract (4/4)",
}

RUNNER_TEMPLATE = '''#!/usr/bin/env python3
"""A fixture runner. Only STEPS is read."""
STEPS = {steps!r}
'''


def load_checker():
    spec = importlib.util.spec_from_file_location("chk_harness_fixture", CHECKER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def build_fixture(root, harnesses=None, steps=None, runner_body=None, scratch_citations=0):
    """A tree with tools/loop/, a runner, and a citation source. No .git unless a cell adds one."""
    harnesses = FIXTURE_HARNESSES if harnesses is None else harnesses
    loop_dir = os.path.join(root, "tools", "loop")
    os.makedirs(loop_dir, exist_ok=True)

    for name in harnesses:
        with open(os.path.join(loop_dir, name), "w", encoding="utf-8") as handle:
            handle.write("# fixture harness\n")

    if steps is None:
        steps = [(f"fixture {name}", f"tools/loop/{name}", expected)
                 for name, expected in harnesses.items()]
    body = RUNNER_TEMPLATE.format(steps=steps) if runner_body is None else runner_body
    with open(os.path.join(loop_dir, "run-harnesses.py"), "w", encoding="utf-8") as handle:
        handle.write(body)

    # The citation source fact (5) counts over. `.mtk/` paths need not exist: the ratchet counts what
    # the prose CITES, which is the whole point - a citation is an instruction whether or not it
    # resolves, and resolution is check #10's job.
    cites = [f".mtk/paths-{200 + i}/probe-{i}.py" for i in range(scratch_citations)]
    with open(os.path.join(loop_dir, "state.json"), "w", encoding="utf-8") as handle:
        json.dump({"nextAction": " ".join(cites) or "nothing cited"}, handle, indent=2)
    return "tools/loop/state.json"


def git_init(root, add=True):
    subprocess.run(["git", "init", "-q", root], check=True, capture_output=True)
    if add:
        subprocess.run(["git", "-C", root, "add", "tools/loop"], check=True, capture_output=True)


def run_check(module, root, source_rel, harnesses=None, ceiling=99):
    saved = (module.REPO, module.LOOP_DIR, module.HARNESSES, module.HARNESS_RUNNER,
             module.CITATION_SOURCES, module.SCRATCH_CITATION_CEILING)
    module.REPO = root
    module.LOOP_DIR = os.path.join(root, "tools", "loop")
    module.HARNESSES = FIXTURE_HARNESSES if harnesses is None else harnesses
    module.HARNESS_RUNNER = "run-harnesses.py"
    module.CITATION_SOURCES = (source_rel,)
    module.SCRATCH_CITATION_CEILING = ceiling
    buffer = io.StringIO()
    try:
        with contextlib.redirect_stdout(buffer):
            problems = module.check_harness_tracking()
    finally:
        (module.REPO, module.LOOP_DIR, module.HARNESSES, module.HARNESS_RUNNER,
         module.CITATION_SOURCES, module.SCRATCH_CITATION_CEILING) = saved
    return problems, buffer.getvalue()


def cell(name, mutate, expect_red, anchors, harnesses=None, ceiling=99, also_absent=()):
    """Build the fixture, let `mutate` change it, and require the verdict the cell predicts.

    `anchors` are substrings the failure must contain; `also_absent` are substrings it must NOT -
    that is how the refusal cell proves it appended rather than returned.
    """
    module = load_checker()
    with tempfile.TemporaryDirectory() as root:
        source_rel = build_fixture(root, harnesses=harnesses)
        state = {"harnesses": harnesses, "ceiling": ceiling, "source_rel": source_rel}
        mutate(root, state)
        problems, out = run_check(module, root, state["source_rel"],
                                 harnesses=state["harnesses"], ceiling=state["ceiling"])
        blob = " ".join(problems) + out
        if expect_red and not problems:
            return f"FAIL[{name}]: expected a red and got none. Output:\n{out}"
        if not expect_red and problems:
            return f"FAIL[{name}]: expected green, got {len(problems)} problem(s): {problems}"
        for anchor in anchors:
            if anchor not in blob:
                return f"FAIL[{name}]: never said {anchor!r}. Output:\n{blob[:900]}"
        for anchor in also_absent:
            if anchor in blob:
                return f"FAIL[{name}]: said {anchor!r}, which this cell requires it NOT to"
        return f"ok[{name}]: {len(problems)} problem(s), all predicted anchors present"


# --------------------------------------------------------------------------- mutations
def m_baseline(root, state):
    git_init(root)


def m_missing_on_disk(root, state):
    git_init(root)
    os.remove(os.path.join(root, "tools", "loop", "mutate-beta.py"))


def m_untracked(root, state):
    git_init(root)
    subprocess.run(["git", "-C", root, "rm", "-q", "--cached", "tools/loop/mutate-beta.py"],
                   check=True, capture_output=True)


def m_untracked_runner(root, state):
    """The runner itself. Facts (1) and (2) only need it READABLE, so an untracked one passes both
    while taking every harness it calls out of a clone's reach - which the live tree showed at iter168
    the first time this check ran, because `git add` had not happened yet."""
    git_init(root)
    subprocess.run(["git", "-C", root, "rm", "-q", "--cached", "tools/loop/run-harnesses.py"],
                   check=True, capture_output=True)


def m_runner_runs_undeclared(root, state):
    steps = [("fixture alpha", "tools/loop/mutate-alpha.py", "3/3"),
             ("fixture beta", "tools/loop/mutate-beta.py", "4/4"),
             ("a harness nobody declared", "tools/loop/mutate-gamma.py", "9/9")]
    build_fixture(root, steps=steps)
    git_init(root)


def m_declared_not_run(root, state):
    steps = [("fixture alpha", "tools/loop/mutate-alpha.py", "3/3")]
    build_fixture(root, steps=steps)
    git_init(root)


def m_step_without_expected(root, state):
    steps = [("fixture alpha", "tools/loop/mutate-alpha.py", "3/3"),
             ("fixture beta", "tools/loop/mutate-beta.py", "   ")]
    build_fixture(root, steps=steps)
    git_init(root)


def m_runner_will_not_import(root, state):
    build_fixture(root, runner_body="STEPS = [ this is not python\n")
    git_init(root)


def m_fixture_mode_still_asserts(root, state):
    """No .git at all: tracked-ness is unknowable, and fact (1) must still fire."""
    os.remove(os.path.join(root, "tools", "loop", "mutate-beta.py"))


def m_git_broken(root, state):
    """`.git` exists but is not a work tree - the case that must NOT read as a fixture."""
    os.makedirs(os.path.join(root, ".git"), exist_ok=True)
    with open(os.path.join(root, ".git", "not-a-repo"), "w", encoding="utf-8") as handle:
        handle.write("decoy\n")


def m_ratchet(root, state):
    state["source_rel"] = build_fixture(root, scratch_citations=5)
    state["ceiling"] = 3
    git_init(root)


def m_refusal_and_a_plant(root, state):
    """Empty the declaration AND plant a ratchet breach: the refusal must report both."""
    state["harnesses"] = {}
    state["source_rel"] = build_fixture(root, scratch_citations=4)
    state["ceiling"] = 2
    git_init(root)


def main():
    results = [
        # The control. Without it, a check that failed on everything would "pass" all nine reds.
        cell("baseline-green", m_baseline, False, ["every declared harness is on disk"]),
        cell("missing-on-disk", m_missing_on_disk, True,
             ["mutate-beta.py but it is not on disk", "the beta contract"]),
        cell("untracked", m_untracked, True,
             ["NOT tracked by git", "a clone gets the citation and not"]),
        cell("untracked-runner", m_untracked_runner, True,
             ["run-harnesses.py is on disk but NOT tracked",
              "it is the re-run command for all"]),
        cell("runner-runs-undeclared", m_runner_runs_undeclared, True,
             ["runs tools/loop/mutate-gamma.py but HARNESSES does not declare it"]),
        cell("declared-but-never-run", m_declared_not_run, True,
             ["never runs it", "a guard nobody re-runs"]),
        cell("step-without-expected", m_step_without_expected, True,
             ["declares no expected result"]),
        cell("runner-will-not-import", m_runner_will_not_import, True,
             ["could not be imported", "cannot pair anything until it imports"]),
        # The two halves of the fixture/broken distinction.
        cell("fixture-still-asserts", m_fixture_mode_still_asserts, True,
             ["tracked-ness: fixture", "is not on disk"],
             also_absent=["tracked-ness could not be established"]),
        cell("git-broken-is-not-a-fixture", m_git_broken, True,
             ["tracked-ness could not be established",
              "must not read like a scratch fixture"]),
        cell("scratch-ratchet", m_ratchet, True, ["past the 3 standing at iter168"]),
        # iter165's question: does the refusal SKIP the facts whose population was intact?
        cell("refusal-appends-and-still-ratchets", m_refusal_and_a_plant, True,
             ["HARNESSES is empty", "past the 2 standing at iter168"]),
    ]

    for line in results:
        print(line)
    bad = [r for r in results if r.startswith("FAIL")]
    print(f"\n{len(results) - len(bad)}/{len(results)} cells")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
