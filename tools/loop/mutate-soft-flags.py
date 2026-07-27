#!/usr/bin/env python3
"""Prove the three branches iter163 hardened actually fail now, and that nothing else moved.

WHY (iter163). iter162 found that `check-state-size.py` had printed `OVER CAP - Read TRUNCATES` and
exited 0 for ~23 iterations, and drew the lesson: A CHECK THAT REPORTS A DEFECT AND EXITS 0 TRAINS
ITS READERS TO SKIM. It then fixed only the instance it was pointed at. This iteration swept the
rest of the loop's own tooling and hardened three branches; every one of them needs its red proven,
because a branch that has never fired is not a guard (iter119).

WHAT IS UNDER TEST, one family per fix:

  1. check_gate_pointers' WALK ASSERTION (check-state-size.py). That check has resolved NOTHING
     since it shipped at iter151 - the population has been empty from its first run - while printing
     "OK: every pointer resolves...". An empty population is not a failure, so the fix is a walk
     assertion that tells "nobody writes that prose form" apart from "the parser broke", plus an
     honest VACUOUS conclusion. Cells: the honest label appears at HEAD; a broken section scan and a
     broken checkbox scan each turn it red.
  2. run-suite.py's `verdict`. It used to read `red = returncode != 0`, so a run that exited 0
     without a parseable summary printed `total=?` beside the word PASS. Now an unaccounted-for run
     is red, and so is one that ran fewer tests than the suite has. Cells are pure-function, no
     dotnet needed.
  3. deny-history-rewrite.py's PAYLOAD branch. It used to print to stderr and `return 0`, calling
     itself loud; iter163 measured that nothing but exit 2 is audible in the driver's invocation, so
     it now fails closed. tools/loop/mutate-force-push-guard.py had no case for it at all -
     25/25 said nothing about this branch - so its cells live here, and that harness is re-run
     unchanged as the regression proof.
  4. ADDED ITER164: THE VACUITY JUDGEMENTS THEMSELVES. iter163 fixed the one check of eight that
     printed a verdict over an empty population, and judged four others "non-vacuous by
     construction" - in prose, on the grounds that their reverse direction fires when a population
     empties. That is exactly the kind of claim iter163 had just disproved, so family 4 empties each
     of those four populations in a scratch tree and demands the check go red FOR ITS OWN REASON.
     Anything that stays green, or that is only saved by a sibling check firing, is a real find. See
     `vacuity_cell` for the verdict vocabulary this adds (WRONG-CHECK vs MISSED vs WRONG-BRANCH).
  5. ADDED ITER165, and it finishes the family two ways. FIRST, the four checks that DECLARE a
     vacuity refusal - check_settled_bodies, check_gates_archive, check_read_whole_files,
     check_calibration - had never had one of those refusals executed by anything. A declared
     refusal is prose until a cell fires it, which is iter164's own argument one level in. All five
     new cells came back CAUGHT, so that half is a green measurement and not a find; predictions for
     every one were written first, in .mtk/paths-165/predictions.md.
     SECOND, AND THIS ONE WAS A DEFECT: two of those refusals `return` from the branch, and their
     populations are not interdependent. Emptying the one that IS vacuous therefore skipped the
     directions that still had theirs while printing "nothing to check" - measured with a planted
     orphan gate body and a deleted archived blocker body, both of which went unnamed. Fixed by
     appending the refusal rather than returning on it. `masking_cell` tells MASKED from CAUGHT, and
     both cells were MASKED before the fix and CAUGHT after.

VERDICTS ARE KEPT DISTINCT (iter161/iter158): CAUGHT, MISSED, WRONG-CHECK, CRASH, and GREEN /
REGRESSION for the must-stay-green controls. Every expectation is anchored on THE CHECK'S OWN
MESSAGE TEXT, never on the mutated file's or key's name - the size table prints every filename on
every run, green included (iter162), and one mutation can legitimately trip two checks (iter161).

THE LIVE TREE IS NEVER WRITTEN. Family 1 builds a scratch copy per cell with
`tempfile.TemporaryDirectory` and copies the tools/loop DIRECTORY rather than a hand-written file
list (iter162: a hand-enumerated fixture rots the moment the thing it guards gains a dependency).
Families 2 and 3 import or drive a script and touch no files at all.

Run: python3 tools/loop/mutate-soft-flags.py
"""

from __future__ import annotations

import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
CHECKER = "tools/loop/check-state-size.py"
HOOK = os.path.join(REPO, "tools/loop/hooks/deny-history-rewrite.py")
ROOT_FILES = ["GATES.md", "PLAN.md", "CLAUDE.md"]
SKIP_DIRS = shutil.ignore_patterns("logs", "__pycache__")

# The one string only check_gate_pointers' walk assertion prints. Anchored here, once.
WALK_BROKEN = "every resolution below is vacuous"
VACUOUS_LABEL = "VACUOUS, and not a failure"


# --------------------------------------------------------------------------- family 1


def _mirror_rest(root):
    """IMPORTED, NOT COPIED, from tools/loop/mutate-size-check.py (iter146: reuse the repo's own
    definition; iter144: a mirror nobody diffs drifts). Both harnesses build the same scratch tree
    and both went red at iter167 for the same reason - the fixture stopped covering the checker's
    dependencies once `check_citation_resolution` began reading outside tools/loop. One definition
    means the NEXT widening fixes both."""
    import importlib.util

    recipe = os.path.join(REPO, "tools/loop/mutate-size-check.py")
    spec = importlib.util.spec_from_file_location("mutate129", recipe)
    if spec is None or spec.loader is None:
        raise SystemExit(f"could not load the iter129 fixture builder at {recipe}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    module.mirror_rest(root)


def build_tree(root):
    for rel in ROOT_FILES:
        shutil.copy2(os.path.join(REPO, rel), os.path.join(root, rel))
    shutil.copytree(
        os.path.join(REPO, "tools", "loop"),
        os.path.join(root, "tools", "loop"),
        ignore=SKIP_DIRS,
    )
    _mirror_rest(root)


def run_checker(root):
    proc = subprocess.run(
        [sys.executable, os.path.join(root, CHECKER)], capture_output=True, text=True
    )
    return proc.returncode, proc.stdout + proc.stderr


def rewrite(root, rel, transform):
    path = os.path.join(root, rel)
    with open(path, encoding="utf-8") as handle:
        text = handle.read()
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(transform(text))


def break_section_headings(text):
    """`## Open gates` -> `### Open gates`: SECTION_HEADING needs `## ` exactly, so sections -> 0."""
    return "\n".join(
        ("#" + line) if line.startswith("## ") else line for line in text.splitlines()
    )


def break_checkbox_shape(text):
    """`- [ ] **id**` -> `- ( ) **id**`: GATE_CHECKBOX stops matching, so gate bodies -> 0."""
    return text.replace("- [ ] **", "- ( ) **").replace("- [x] **", "- (x) **")


def gate_pointer_cell(name, transform):
    with tempfile.TemporaryDirectory() as root:
        build_tree(root)
        code, out = run_checker(root)
        if code != 0:
            return f"CRASH[{name}]: baseline scratch copy is not green (exit {code}); harness invalid"
        if VACUOUS_LABEL not in out:
            return (f"CRASH[{name}]: baseline never printed the honest vacuous label, so this cell "
                    "is not testing what it claims")
        rewrite(root, "GATES.md", transform)
        code, out = run_checker(root)
        if code == 0:
            return f"MISSED[{name}]: the scan parsed nothing and the checker still exited 0"
        if WALK_BROKEN not in out:
            return (f"WRONG-CHECK[{name}]: exit {code}, but check_gate_pointers' own walk message "
                    f"({WALK_BROKEN!r}) never printed - another check failed instead")
        return f"CAUGHT[{name}]: exit {code}, walk assertion fired"


def head_is_honest_cell():
    """The must-stay-green control: at HEAD the checker exits 0 AND says it resolved nothing."""
    code, out = run_checker(REPO)
    if code != 0:
        return f"REGRESSION[head-green]: the live tree's checker exits {code}, not 0"
    if VACUOUS_LABEL not in out:
        return "REGRESSION[head-green]: exit 0 but the vacuous label is absent"
    if "OK: every pointer resolves" in out:
        return ("REGRESSION[head-green]: still claiming 'every pointer resolves' over an empty "
                "population - the iter163 fix is not in effect")
    return "GREEN[head-green]: exit 0, and it reports resolving nothing instead of claiming an OK"


# --------------------------------------------------------------------------- family 2


def load_run_suite():
    """`run-suite.py` has a hyphen, so it cannot be imported by name."""
    spec = importlib.util.spec_from_file_location(
        "run_suite", os.path.join(REPO, "tools/loop/run-suite.py")
    )
    if spec is None or spec.loader is None:
        raise SystemExit("could not load tools/loop/run-suite.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def summary(total, failed=0, skipped=0):
    """The shape MTP prints above its own exit, which is what SUMMARY was written against."""
    return (
        f"Test run summary: {'Failed!' if failed else 'Passed!'}\n"
        f"  total: {total}\n  failed: {failed}\n  succeeded: {total - failed - skipped}\n"
        f"  skipped: {skipped}\n  duration: 34s\n"
    )


def verdict_cells(module):
    floor = module.EXPECTED_AT_LEAST
    cases = [
        ("green-full-suite", 0, summary(floor), False, ""),
        ("runner-said-red", 1, summary(floor, failed=1), True, ""),
        ("exit0-no-summary", 0, "Build succeeded.\n  0 Warning(s)\n", True, "no parseable"),
        ("exit0-short-of-floor", 0, summary(12), True, "short of the"),
        ("exit0-zero-tests", 0, summary(0), True, "short of the"),
        ("boundary-exactly-floor", 0, summary(floor), False, ""),
        ("boundary-one-short", 0, summary(floor - 1), True, "short of the"),
        # iter134's lesson, mechanised: a skip reports itself as success. `total` stays at the floor
        # so this cell can only be caught by the skip branch and not by the count branch.
        ("exit0-one-skipped", 0, summary(floor, skipped=1), True, "SKIPPED"),
        ("exit0-many-skipped", 0, summary(floor, skipped=37), True, "SKIPPED"),
    ]
    results = []
    for name, code, blob, want_red, want_reason in cases:
        try:
            red, _, reason = module.verdict(code, blob)
        except Exception as exc:  # noqa: BLE001 - a crash is its own verdict here
            results.append(f"CRASH[verdict/{name}]: {type(exc).__name__}: {exc}")
            continue
        if red != want_red:
            verb = "MISSED" if want_red else "REGRESSION"
            results.append(f"{verb}[verdict/{name}]: red={red}, expected {want_red}")
            continue
        if want_reason and want_reason not in reason:
            results.append(
                f"WRONG-CHECK[verdict/{name}]: red, but the reason never says {want_reason!r}"
                f" (said {reason[:60]!r})"
            )
            continue
        if not want_red and reason:
            results.append(f"REGRESSION[verdict/{name}]: green but carries a reason {reason[:40]!r}")
            continue
        tag = "CAUGHT" if want_red else "GREEN"
        results.append(f"{tag}[verdict/{name}]: red={red}")
    return results


# --------------------------------------------------------------------------- family 3


def run_hook(stdin_text):
    proc = subprocess.run(
        [sys.executable, HOOK], input=stdin_text, capture_output=True, text=True, timeout=20
    )
    return proc.returncode, proc.stderr


def hook_cells():
    valid_benign = json.dumps({"tool_name": "Bash", "tool_input": {"command": "git status -sb"}})
    valid_force = json.dumps(
        {"tool_name": "Bash", "tool_input": {"command": "git push origin main --force"}}
    )
    results = []

    for name, payload in (("malformed-payload", "this is not json"), ("empty-stdin", "")):
        code, err = run_hook(payload)
        if code != 2:
            results.append(
                f"MISSED[hook/{name}]: exit {code}, expected 2 - an unparseable payload means the"
                " hook inspected nothing, and exits 0/1 were measured inaudible at iter163"
            )
            continue
        if "could not be parsed as JSON" not in err:
            results.append(f"WRONG-CHECK[hook/{name}]: exit 2 but not for the payload reason")
            continue
        results.append(f"CAUGHT[hook/{name}]: exit 2, and it says why")

    code, _ = run_hook(valid_benign)
    results.append(
        f"GREEN[hook/valid-benign]: exit {code}"
        if code == 0
        else f"REGRESSION[hook/valid-benign]: exit {code} - fail-closed became a blanket block"
    )

    code, err = run_hook(valid_force)
    results.append(
        f"GREEN[hook/valid-force-push]: exit {code}, still blocked"
        if code == 2 and "8.2" in err
        else f"REGRESSION[hook/valid-force-push]: exit {code} - the original guard changed behaviour"
    )
    return results


# --------------------------------------------------------------------------- family 4


# EVERY per-check section header check-state-size.py prints, in the order main() runs the checks.
# Each anchor is a CONTIGUOUS substring of one printed line - iter163 learned that the hard way, when
# an anchor split across a `\n` inside a source string literal reported WRONG-CHECK against a hook
# that was behaving exactly as specified.
#
# WHY SECTIONS AT ALL, rather than grepping the whole output for a message: main() runs EVERY check
# and collects problems BEFORE it evaluates any FAIL banner, so one mutation legitimately turns two
# checks red (iter161). "The checker exited non-zero" therefore says nothing about the check under
# test. Slicing the output by header and asking whether THIS check printed a BROKEN line of its own
# is the only way to tell CAUGHT from a sibling covering for it - which is precisely the distinction
# family 4 exists to measure.
CHECK_SECTIONS = [
    ("check_read_whole_files", "read-whole vs archive under tools/loop/"),
    ("check_calibration", "bytes-per-token calibration"),
    ("check_done_archive", "done-archive.jsonl integrity:"),
    ("check_gate_mirror", "state.json gate mirror"),
    ("check_gate_pointers", "open gates pointing at other GATES.md sections"),
    ("check_stub_bodies", "state.json stubs <-> their archived bodies"),
    ("check_settled_bodies", "settled tombstones <-> their archived bodies"),
    ("check_gates_archive", "gates-archive.json, its second body"),
]


def sections_of(out):
    """Split the checker's stdout into one block per check. -> ({name: line index}, {name: lines})."""
    lines = out.splitlines()
    found = {}
    for name, anchor in CHECK_SECTIONS:
        for index, line in enumerate(lines):
            if anchor in line:
                found[name] = index
                break
    starts = sorted(found.values())
    blocks = {}
    for name, start in found.items():
        after = [s for s in starts if s > start]
        blocks[name] = lines[start + 1 : (after[0] if after else len(lines))]
    return found, blocks


def broken_checks(out):
    _, blocks = sections_of(out)
    return {
        name
        for name, block in blocks.items()
        if any(line.strip().startswith("BROKEN:") for line in block)
    }


def block_text(out, check):
    _, blocks = sections_of(out)
    return "\n".join(blocks.get(check, []))


def vacuity_cell(name, check, mutate, expect_red=True, anchor=None):
    """Empty `check`'s population in a scratch tree; does `check` itself still go red for it?

    THE QUESTION THIS ANSWERS, and why it is worth an iteration. Of check-state-size.py's eight
    checks, four declare an explicit vacuity refusal and one fails on a kind with no measurement.
    The other three plus check_gate_pointers' finding half were judged "non-vacuous by construction"
    IN PROSE, on the grounds that their reverse direction fires when a population empties (drain
    `gates` and 12 orphan mirror keys are left behind). iter163 is the iteration that proved a prose
    judgement about vacuity wrong, so each of those judgements gets a cell.

    A cell can therefore come back three interesting ways, and only one is a pass:
      CAUGHT       the check reported its OWN BROKEN line - the prose judgement was right.
      WRONG-CHECK  the checker went red, but a SIBLING check fired and this one said nothing. The
                   tree is protected; the check is not. Two nets over one rule must be proven
                   independent or one of them is decoration (iter146).
      MISSED       exit 0 over an emptied population - the vacuous pass the prose ruled out.
    """
    with tempfile.TemporaryDirectory() as root:
        build_tree(root)
        code, out = run_checker(root)
        if code != 0:
            return f"CRASH[{name}]: baseline scratch copy is not green (exit {code}); harness invalid"
        found, _ = sections_of(out)
        missing = [n for n, _ in CHECK_SECTIONS if n not in found]
        if missing:
            return (f"CRASH[{name}]: the output parser found no header for {missing} - it cannot"
                    " attribute a BROKEN line, so it must not grade this cell (iter163's own rule:"
                    " assert the walk, not the finding)")
        if check in broken_checks(out):
            return f"CRASH[{name}]: {check} is ALREADY red at baseline, so this cell proves nothing"

        try:
            mutate(root)
        except Exception as exc:  # noqa: BLE001 - a broken mutation must grade ONE cell, not kill 33
            return (f"CRASH[{name}]: the mutation itself failed ({exc}) - a mutation that does not"
                    " apply would come back MISSED and read as a find (iter165)")
        code, out = run_checker(root)
        fired = broken_checks(out)

        if not expect_red:
            if check in fired:
                return f"REGRESSION[{name}]: {check} invented a defect on a legitimate input"
            if code != 0:
                return (f"REGRESSION[{name}]: exit {code} on a legitimate input"
                        f" ({', '.join(sorted(fired)) or 'no check'} fired)")
            return f"GREEN[{name}]: exit 0, correctly - nothing is actually wrong here"

        if check not in fired:
            if code != 0:
                others = ", ".join(sorted(fired)) or "no check at all"
                return (f"WRONG-CHECK[{name}]: exit {code}, but {check} said NOTHING over its"
                        f" emptied population - what fired was {others}")
            return (f"MISSED[{name}]: the checker exited 0 with {check}'s population emptied"
                    " - a vacuous pass")
        if anchor and anchor not in block_text(out, check):
            return (f"WRONG-BRANCH[{name}]: {check} went red but not for {anchor!r} - some other"
                    " direction of the same check fired")
        # WHO ELSE FIRED, recorded even on a pass (iter165). WRONG-CHECK above catches the case where
        # a sibling covers for a SILENT check; this tells the weaker but still useful thing apart -
        # a refusal that is the tree's only detector for that emptying, versus one of several nets.
        others = sorted(fired - {check})
        also = f"; also fired: {', '.join(others)}" if others else "; sole detector"
        return f"CAUGHT[{name}]: exit {code}, {check} reported its own BROKEN{also}"


def masking_cell(name, check, mutate, refusal_anchor, masked_anchor):
    """Does `check`'s vacuity refusal SKIP the directions whose population was still intact?

    ADDED ITER165, one level in from `vacuity_cell` again. Two of the eight checks
    (check_settled_bodies, check_gates_archive) `return` from the refusal branch; the other six
    append and carry on. Where the emptied population is INDEPENDENT of the check's other
    directions, that early return turns one vacuous direction into a silent skip of the rest, while
    printing "nothing to check" - which is then false.

    So the mutation empties the independent population AND plants a defect that a SKIPPED direction
    owns. Both must appear in the check's own block:
      MASKED  only the refusal printed - the plant went unreported, the early return is the cause.
      CAUGHT  refusal AND the plant, i.e. the refusal costs nothing that was still assertable.
    """
    with tempfile.TemporaryDirectory() as root:
        build_tree(root)
        code, out = run_checker(root)
        if code != 0:
            return f"CRASH[{name}]: baseline scratch copy is not green (exit {code}); harness invalid"
        found, _ = sections_of(out)
        missing = [n for n, _ in CHECK_SECTIONS if n not in found]
        if missing:
            return f"CRASH[{name}]: the output parser found no header for {missing}"
        if check in broken_checks(out):
            return f"CRASH[{name}]: {check} is ALREADY red at baseline, so this cell proves nothing"

        try:
            mutate(root)
        except Exception as exc:  # noqa: BLE001
            return f"CRASH[{name}]: the mutation itself failed ({exc})"
        code, out = run_checker(root)
        block = block_text(out, check)

        if refusal_anchor not in block:
            return (f"CRASH[{name}]: {check} never printed its refusal ({refusal_anchor!r}), so this"
                    " cell cannot say anything about what the refusal skips")
        if masked_anchor not in block:
            return (f"MASKED[{name}]: {check} printed its vacuity refusal and NOTHING about"
                    f" {masked_anchor!r} - the early return skipped a direction whose population was"
                    " intact, and called it 'nothing to check'")
        return (f"CAUGHT[{name}]: exit {code}, {check} reported its refusal AND the independent"
                " defect the other direction owns")


def patch_state(root, transform):
    """Re-serialize state.json through json - a mutation itself until a control says otherwise."""
    path = os.path.join(root, "tools/loop/state.json")
    with open(path, encoding="utf-8") as handle:
        doc = json.load(handle)
    transform(doc)
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(doc, handle, indent=2, ensure_ascii=False)
        handle.write("\n")


def patch_source(root, old, new):
    """Mutate the SCRATCH COPY of check-state-size.py, asserting the anchor was there exactly once.

    ADDED ITER165 for the two checks whose population is not data. check_read_whole_files walks the
    tree and classifies against its own declared list, and check_calibration reads its own MEASURED
    table - so "empty that population" means the walk or the declaration, which is what both
    refusals say in their own words ("the walk or the declaration is wrong, not the tree").

    THE COUNT ASSERTION IS THE POINT: a replace that matched nothing would leave the tree healthy,
    the check would exit 0 for the correct reason, and `vacuity_cell` would grade it MISSED - a
    fabricated find. Same rule as the output parser's "assert the walk, not the finding".
    """
    path = os.path.join(root, CHECKER)
    with open(path, encoding="utf-8") as handle:
        text = handle.read()
    hits = text.count(old)
    if hits != 1:
        raise AssertionError(f"source anchor matched {hits} times, need exactly 1: {old!r}")
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(text.replace(old, new))


def patch_json(root, rel, transform):
    path = os.path.join(root, rel)
    with open(path, encoding="utf-8") as handle:
        doc = json.load(handle)
    transform(doc)
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(doc, handle, indent=2, ensure_ascii=False)


def keep_only_underscored(block):
    return {k: v for k, v in block.items() if k.startswith("_")}


def round_trip_only(root):
    patch_state(root, lambda doc: doc)


def empty_done_archive(root):
    with open(os.path.join(root, "tools/loop/done-archive.jsonl"), "w", encoding="utf-8"):
        pass


def empty_done_archive_and_count(root):
    """The realistic shape: truncate the log AND 'repair' the counter so the two agree."""
    empty_done_archive(root)
    patch_state(root, lambda doc: doc.update({"doneCount": 0}))


def drain_gates(root):
    patch_state(root, lambda doc: doc.update(
        {"gates": {"authoritative": doc["gates"]["authoritative"]}}))


def drain_gates_and_boxes(root):
    drain_gates(root)
    rewrite(root, "GATES.md", break_checkbox_shape)


def drain_blocker_stubs(root):
    patch_state(root, lambda doc: doc.update({"blockers": keep_only_underscored(doc["blockers"])}))


def drain_decision_pair(root):
    patch_state(root, lambda doc: doc.update({"decisions": keep_only_underscored(doc["decisions"])}))
    patch_json(root, "tools/loop/decisions-archive.json", lambda doc: doc.update({"decisions": {}}))


def bold_open_markers(root):
    """`OPEN, MIRKO'S, ...` -> `**OPEN**, MIRKO'S, ...`, which is this file's own house style.

    Direction (3) of check_stub_bodies keys on `stub.startswith("OPEN")`. Emphasising the marker is
    a one-character-class edit an iteration could make while tidying prose, and it silently empties
    that direction's population - the same shape as iter151 removing the last `under "<Title>"`.
    """
    def transform(doc):
        doc["decisions"] = {
            key: (f"**OPEN**{value[len('OPEN'):]}"
                  if isinstance(value, str) and value.startswith("OPEN") else value)
            for key, value in doc["decisions"].items()
        }
    patch_state(root, transform)


def bold_open_markers_and_drop_bodies(root):
    bold_open_markers(root)
    patch_json(root, "tools/loop/decisions-archive.json", lambda doc: doc.update({"decisions": {}}))


def unreadable_open_markers(root):
    """`OPEN, MIRKO'S, ...` -> `MIRKO'S CALL, still OPEN, ...`: the marker is there and unreadable.

    The bodies stay intact, so direction (3) has nothing to complain about and the ONLY branch that
    can fire is iter164's drift detector - the one that tells "this population emptied because the
    prose moved" apart from "Mirko answered the last decision", which must stay green.
    """
    def transform(doc):
        doc["decisions"] = {
            key: (f"MIRKO'S CALL, still OPEN{value[len('OPEN'):]}"
                  if isinstance(value, str) and value.startswith("OPEN") else value)
            for key, value in doc["decisions"].items()
        }
    patch_state(root, transform)


def drain_settled_tombstones(root):
    """`blockersArchive.settled` -> {}: the one-line verdicts condensed away as budget payback.

    Not hypothetical - iter162 condensed this very field for budget, and `blockersArchive.why` says
    it is kept "so no iteration would re-add them". Draining it leaves the five archived bodies
    paired with nothing, which is the population check_settled_bodies exists to compare.
    """
    patch_state(root, lambda doc: doc["blockersArchive"].update({"settled": {}}))


def drop_archive_citations(root):
    """Rewrite every gate stub's `gates-archive.json` pointer away, emptying `citing`.

    THE REALISTIC SHAPE, and it has already happened once: iter155 rewrote the
    paste-format-on-edit-hook stub when Mirko answered "delete" and the rewrite dropped that stub's
    pointer back to the archive - recorded in the archive's own `trimmed-iter145` note. This cell
    does to all five what one stub rewrite did to one, because `citing` is a SUBSTRING match and
    substring populations empty without anybody editing a check.
    """
    def transform(doc):
        doc["gates"] = {
            key: (value.replace("gates-archive.json", "the long-mirror archive")
                  if key != "authoritative" and isinstance(value, str) else value)
            for key, value in doc["gates"].items()
        }
    patch_state(root, transform)


def break_extension_filter(root):
    """The walk half: with no extensions to match, every file is skipped and both buckets go to 0."""
    patch_source(root, 'os.path.splitext(name)[1] not in (".json", ".jsonl", ".md")',
                 "os.path.splitext(name)[1] not in ()")


def declare_every_file_an_archive(root):
    """The declaration half: a classifier that exempts everything, which is iter161's stated fear.

    That comment refused to infer the exemption from a filename because "a rule like anything
    matching *-archive.* is exempt would let the next file exempt itself by being named well". This
    is that rule taken to its limit, so the refusal is asked to hold against the whole class going
    exempt rather than against one file doing it.
    """
    patch_source(root, "(archives if name in ARCHIVE_FILES else read_whole)",
                 "(archives if True else read_whole)")


def drop_json_calibration(root):
    """Delete the one `json` row of MEASURED, leaving BYTES_PER_TOKEN_JSON founded on nothing."""
    patch_source(root, '    ("json", "iter128 tools/loop/state.json", 74_115, 31_303),\n', "")


def drop_archive_citations_with_orphan_body(root):
    """Empty `citing` AND plant an orphan body, which direction (1) owns and (2) does not.

    `gate-m9-imaginary` is neither a live `gates` key nor a declared non-gate key, so direction (1)
    should name it. Whether it does while the refusal is also firing is the whole question.
    """
    drop_archive_citations(root)
    patch_json(root, "tools/loop/gates-archive.json", lambda doc: doc.update(
        {"gate-m9-imaginary": "a mis-keyed gate mirror - exactly what direction (1) exists for"}))


def drain_spike_names_with_broken_pairing(root):
    """Empty `spikes` (direction 4's population) AND delete a keyed blocker body (direction 2's).

    The blocker side of this check is untouched by the spike drain - 5 tombstones against what is
    now 4 bodies - so direction (2) has a real, nameable defect to report.
    """
    patch_state(root, lambda doc: doc["spikesArchive"].update({"settled": []}))
    path = os.path.join(root, "tools/loop/blockers-archive.jsonl")
    with open(path, encoding="utf-8") as handle:
        lines = [line for line in handle if line.strip()]
    kept = [line for line in lines if json.loads(line).get("key") != "composite-action"]
    if len(kept) != len(lines) - 1:
        raise AssertionError(f"expected to drop exactly one body, dropped {len(lines) - len(kept)}")
    with open(path, "w", encoding="utf-8") as handle:
        handle.writelines(kept)


def inject_pointer(title):
    """Give check_gate_pointers the population it has never had: one `under "<Title>"` in an open gate.

    Injected into gate-m3's body because that is one of the three gates iter151 actually found
    carrying a stale pointer, and the wording is that defect's, verbatim in shape.
    """
    needle = "- [ ] **gate-m3-approval-roundtrip**"

    def transform(text):
        lines = []
        for line in text.splitlines():
            lines.append(line)
            if line.startswith(needle):
                lines.append(f'  Step (1): the three sandbox items under "{title}" below.')
        return "\n".join(lines) + "\n"

    return lambda root: rewrite(root, "GATES.md", transform)


def vacuity_cells():
    return [
        # The control iter144's lesson demands: patch_state re-serializes the whole file, so prove
        # the round trip alone changes nothing before reading any cell built on it.
        vacuity_cell("state-json/round-trip-only", "check_done_archive", round_trip_only,
                     expect_red=False),

        # check_done_archive. One side of the count guard, then both sides moved together.
        vacuity_cell("done-archive/emptied-count-untouched", "check_done_archive",
                     empty_done_archive, anchor="doneCount is"),
        vacuity_cell("done-archive/emptied-with-count", "check_done_archive",
                     empty_done_archive_and_count,
                     anchor="holds no well-formed entries at all"),

        # check_gate_mirror. `gates` drained is the direction prose called self-evidently red; both
        # sides drained is the one nobody measured.
        vacuity_cell("gate-mirror/mirror-drained", "check_gate_mirror", drain_gates,
                     anchor="rule §9.7 says every checkbox is mirrored"),
        vacuity_cell("gate-mirror/both-sides-drained", "check_gate_mirror", drain_gates_and_boxes,
                     anchor="with either at zero this check compares nothing"),

        # check_stub_bodies. Blocker stubs drained leaves orphan bodies; the decision pair drained
        # together leaves nothing for directions (3) and (4) to look at.
        vacuity_cell("stub-bodies/blocker-stubs-drained", "check_stub_bodies", drain_blocker_stubs,
                     anchor="that `blockers` does not list"),
        vacuity_cell("stub-bodies/decision-pair-drained", "check_stub_bodies", drain_decision_pair,
                     anchor="has no stubs at all"),
        # Emphasising the marker is NOT a defect on its own - the bodies are all still there. This
        # cell is what proves a fix for the next one does not start inventing failures.
        vacuity_cell("stub-bodies/open-marker-bolded-only", "check_stub_bodies", bold_open_markers,
                     expect_red=False),
        vacuity_cell("stub-bodies/open-marker-bolded-bodies-gone", "check_stub_bodies",
                     bold_open_markers_and_drop_bodies, anchor="still says OPEN and has no body"),
        # The drift detector's own red branch. Its bodies are intact, so nothing else can fire.
        vacuity_cell("stub-bodies/open-marker-unreadable", "check_stub_bodies",
                     unreadable_open_markers, anchor="cannot read"),

        # check_gate_pointers' FINDING half. Its population has been empty since it shipped, so the
        # question here is the reverse of the others: give it one and prove the code is not dead.
        vacuity_cell("gate-pointers/pointer-to-missing-section", "check_gate_pointers",
                     inject_pointer("No Such Section In This File"),
                     anchor="that GATES.md does not have"),
        vacuity_cell("gate-pointers/pointer-to-settled-section", "check_gate_pointers",
                     inject_pointer("Setup Mirko must do before M2"),
                     anchor="no unticked checkbox left"),

        # ITER165: THE FOUR DECLARED REFUSALS THEMSELVES. iter164 proved that "non-vacuous by
        # construction" is prose until a cell fires it; a DECLARED refusal is the same claim one
        # level in - branch text that has never executed. Four checks carry one each, and none had
        # ever been run. Predictions for all five cells were written first, in
        # .mtk/paths-165/predictions.md, per iter164's own method note.
        vacuity_cell("settled-bodies/tombstones-drained", "check_settled_bodies",
                     drain_settled_tombstones, anchor="blocker tombstones"),
        vacuity_cell("gates-archive/citations-dropped", "check_gates_archive",
                     drop_archive_citations, anchor="gate stubs cite the archive"),
        # Two cells for one refusal, because its own wording names two causes - "the walk OR the
        # declaration is wrong, not the tree" - and one branch reached two ways is two claims.
        vacuity_cell("read-whole/walk-classifies-nothing", "check_read_whole_files",
                     break_extension_filter, anchor="classified ZERO read-whole files"),
        vacuity_cell("read-whole/everything-declared-archive", "check_read_whole_files",
                     declare_every_file_an_archive, anchor="classified ZERO read-whole files"),
        vacuity_cell("calibration/json-measurement-dropped", "check_calibration",
                     drop_json_calibration, anchor="no measurement recorded for json"),

        # ITER165, ONE LEVEL IN AGAIN: the two refusals that RETURN EARLY. Their populations are not
        # interdependent, so emptying the one that IS vacuous must not skip the directions that still
        # have theirs. Each cell empties the independent population and plants a defect a skipped
        # direction owns; MASKED means the refusal printed "nothing to check" over something.
        masking_cell("gates-archive/citations-dropped-hides-orphan", "check_gates_archive",
                     drop_archive_citations_with_orphan_body,
                     refusal_anchor="gate stubs cite the archive",
                     masked_anchor="that `gates` does not list"),
        masking_cell("settled-bodies/spikes-drained-hides-missing-body", "check_settled_bodies",
                     drain_spike_names_with_broken_pairing,
                     refusal_anchor="spike names",
                     masked_anchor="has no body in"),
    ]


# --------------------------------------------------------------------------- driver


def main():
    module = load_run_suite()
    print(f"run-suite.py floor under test: EXPECTED_AT_LEAST = {module.EXPECTED_AT_LEAST:,}")

    results = [
        head_is_honest_cell(),
        gate_pointer_cell("gates-md/no-section-headings", break_section_headings),
        gate_pointer_cell("gates-md/no-checkbox-gates", break_checkbox_shape),
    ]
    results.extend(verdict_cells(module))
    results.extend(hook_cells())
    results.extend(vacuity_cells())

    print()
    for line in results:
        print(f"  {line}")

    bad = [
        r for r in results
        if r.split("[")[0] in {"MISSED", "WRONG-CHECK", "WRONG-BRANCH", "MASKED", "CRASH",
                               "REGRESSION"}
    ]
    caught = [r for r in results if r.startswith("CAUGHT")]
    green = [r for r in results if r.startswith("GREEN")]
    print(f"\n{len(results) - len(bad)}/{len(results)} cells behaved as specified"
          f"  ({len(caught)} red branches fired, {len(green)} must-stay-green controls held)")
    if bad:
        print("RESULT: FAIL")
        return 1
    print("RESULT: PASS - each hardened branch fires, and none of them fires on the healthy tree")
    return 0


if __name__ == "__main__":
    sys.exit(main())
