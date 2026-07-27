#!/usr/bin/env python3
"""Run the test suite so that a failure NAMES ITSELF.

WHY THIS EXISTS (iter154). The protocol's non-negotiable verification step is `dotnet build` +
`dotnet test`, and every iteration runs the suite as `dotnet test 2>&1 | tail -N`. That pipeline
keeps the summary and throws away the per-test failure lines above it, and the Microsoft Testing
Platform writes NO artifact unless asked (`TestResults/` is empty on this machine). So when the
suite goes red, the evidence is gone by the time anyone reads the number.

That is not hypothetical. It has now happened TWICE with the same shape - a single failure out of
~1,388 that passes on re-run and was never identified:
  * iteration 120: "the run immediately after the mutation harness reported failed:1 and the NAME
    WAS NOT CAPTURED; six later full runs were clean". It closed with a standing instruction "to
    capture the name in the same command" - written only into its done-archive entry, which
    `doneArchive.howToRead` tells every iteration NOT to read for orientation. It reached neither
    method-notes.md nor ITERATION-PROMPT.md, so nothing changed.
  * iteration 154: `dotnet test` -> `total: 1388, failed: 1`, first run of the session. The two
    runs after it were green. Same loss, 34 iterations later, for exactly the same reason.

This script closes that. It always leaves a full log and a TRX report on disk, and on red it
prints the failing test ids straight out of the TRX.

USAGE
  python3 tools/loop/run-suite.py              # one verification run (use this instead of `dotnet test`)
  python3 tools/loop/run-suite.py --repeat 20  # flake hunt: N runs, every failure named
  python3 tools/loop/run-suite.py --keep-green # keep artifacts from green runs too (default: drop them)

Exit code mirrors the suite: 0 green, non-zero red - and since iter163 it is also red when the
runner exits 0 without accounting for itself (no parseable summary, or fewer tests than the suite
has). See `verdict` below. Artifacts land in .mtk/suite-runs/ (gitignored).
"""

import argparse
import re
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
ARTIFACTS = REPO / ".mtk" / "suite-runs"
SUMMARY = re.compile(
    r"total:\s*(\d+).*?failed:\s*(\d+).*?succeeded:\s*(\d+).*?skipped:\s*(\d+)",
    re.S,
)
# MTP's own TRX namespace, and the plain-text fallback MTP prints above the summary.
TRX_NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def failing_from_trx(trx_path):
    """Failing test names out of the TRX. Authoritative: it survives the pipe."""
    if not trx_path.exists():
        return []
    try:
        root = ET.parse(trx_path).getroot()
    except ET.ParseError:
        return []
    names = []
    for result in root.iterfind(".//t:UnitTestResult", TRX_NS):
        if (result.get("outcome") or "").lower() != "failed":
            continue
        name = result.get("testName") or "(unnamed)"
        message = ""
        node = result.find(".//t:Message", TRX_NS)
        if node is not None and node.text:
            message = " ".join(node.text.split())[:400]
        names.append((name, message))
    return names


def failing_from_stdout(text):
    """Fallback for a run that died before the TRX was written."""
    names = []
    for line in text.splitlines():
        stripped = line.strip()
        if stripped.startswith("failed ") and len(stripped) > len("failed "):
            name = stripped[len("failed ") :].strip()
            if name not in names:
                names.append(name)
    return names


# The suite's size, and the ONE number `nextAction` has been asking every iteration to eyeball.
# A floor, not an equality: tests only get added here, so this trips when the count DROPS - a test
# project dropped from the solution, a filter left in place, a runner that collected nothing. Raise
# it when the suite legitimately grows; lowering it is a deliberate act that needs a reason in the
# commit message, exactly like check-state-size.py's MEASURED constants.
EXPECTED_AT_LEAST = 1425  # iter178: +7, init's six writes are atomic (no half-written scaffolded file)


def verdict(returncode, blob):
    """Is this run red, and why? A ZERO EXIT IS NOT BY ITSELF EVIDENCE THE SUITE RAN.

    ADDED ITER163 by the soft-flag sweep, and this file was one of its own targets. `one_run` used
    to compute `red = returncode != 0` and print whatever the summary regex happened to yield, so a
    run whose summary did not parse printed `total=? failed=?` next to the word PASS and exited 0.
    That is the iter162 defect in a second place: the script KNEW it could not account for the run
    and reported success anyway. The protocol's verification step is only worth something if the
    thing that says PASS has confirmed that tests executed.

    Four ways to be red, and the last three are new:
      * the runner said so (exit code) - always authoritative
      * exit 0 but no parseable summary: nothing here can say what ran
      * exit 0 with a real summary that is short of EXPECTED_AT_LEAST: something collected fewer
        tests than the suite has, which is the shape `dotnet test --nologo` (zero tests) and a stray
        `--filter-query` both produce
      * exit 0 with a non-zero `skipped:` count. THIS ONE WAS FOUND BY THE SWEEP LOOKING AT ITS OWN
        WORK: method-notes has recorded since iter134 that "a skip is a coverage hole that reports
        itself as success - read the summary line's skipped: count, not just failed:", and this
        script printed that count while `verdict` ignored it. The suite has no xUnit skip attribute
        anywhere today (checked: every `Skip` in tests/ is a LINQ call, a domain enum member or a
        directory list) and runs 0 skipped, so this costs nothing now and turns the day one appears
        into a decision somebody makes on purpose. Rule §4.2's env-gated live-sandbox tests are the
        case to watch: if one ever lands as a skip rather than as an absent test, that is the
        conversation this branch forces.
    """
    match = SUMMARY.search(blob)
    counts = match.groups() if match else None

    if returncode != 0:
        return True, counts, ""
    if counts is None:
        return True, counts, (
            "the runner exited 0 but printed no parseable `total:/failed:` summary, so this run is"
            " unaccounted for - a PASS here would be a guess. Read the log by hand"
        )
    total, skipped = int(counts[0]), int(counts[3])
    if total < EXPECTED_AT_LEAST:
        return True, counts, (
            f"the runner exited 0 having run {total:,} tests, short of the {EXPECTED_AT_LEAST:,}"
            " this suite has. Something collected less than the whole suite (a dropped test"
            " project, a leftover filter). If the drop is deliberate, lower EXPECTED_AT_LEAST in"
            " this file in the same commit and say why"
        )
    if skipped:
        return True, counts, (
            f"the runner exited 0 with {skipped:,} test(s) SKIPPED. A skip is a coverage hole that"
            " reports itself as success (method-notes, iter134), and this suite has had none since"
            " it existed. Name the skipped test and decide deliberately: fix it, delete it, or"
            " record why a permanent skip is correct here"
        )
    return False, counts, ""


def one_run(index, keep_green):
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    stamp = time.strftime("%Y%m%d-%H%M%S")
    tag = f"{stamp}-run{index:02d}"
    trx = ARTIFACTS / f"{tag}.trx"
    log = ARTIFACTS / f"{tag}.log"

    started = time.time()
    proc = subprocess.run(
        [
            "dotnet",
            "test",
            # NO `--` separator: `dotnet test -- <args>` reads the rest as a directory and dies
            # with "Specifying a directory for 'dotnet test' should be via '--project'".
            # The MTP runner's own options are passed straight through (measured iter154).
            "--report-xunit-trx",
            "--report-xunit-trx-filename",
            trx.name,
            "--results-directory",
            str(ARTIFACTS),
        ],
        cwd=REPO,
        capture_output=True,
        text=True,
    )
    elapsed = time.time() - started
    blob = proc.stdout + "\n" + proc.stderr
    log.write_text(blob)

    red, counts, unverified = verdict(proc.returncode, blob)
    shown = counts or ("?", "?", "?", "?")

    named = failing_from_trx(trx)
    if not named:
        named = [(n, "") for n in failing_from_stdout(blob)]

    print(
        f"run {index:02d}  {'FAIL' if red else 'PASS'}  exit={proc.returncode}  "
        f"total={shown[0]} failed={shown[1]} skipped={shown[3]}  {elapsed:.0f}s",
        flush=True,
    )
    for name, message in named:
        print(f"           FAILING: {name}", flush=True)
        if message:
            print(f"                    {message}", flush=True)
    # A run this script cannot account for is reported as its own kind of red, distinct from a named
    # test failure: nothing failed, and nothing was proven either.
    if unverified:
        print(f"           UNVERIFIED: {unverified}", flush=True)
    if red and not named and not unverified:
        print(
            f"           red with no named test - read {log.relative_to(REPO)} by hand",
            flush=True,
        )
    if red or keep_green:
        print(f"           artifacts: {log.relative_to(REPO)}", flush=True)
    else:
        log.unlink(missing_ok=True)
        trx.unlink(missing_ok=True)

    # NOT `proc.returncode`: an unaccounted-for run exits 0 and must still come back red, or this
    # function's whole judgement is discarded by the caller (which is what made the old soft branch
    # soft in the first place).
    return (proc.returncode or (1 if red else 0)), [n for n, _ in named]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repeat", type=int, default=1, help="number of full runs")
    parser.add_argument(
        "--keep-green", action="store_true", help="keep artifacts from green runs too"
    )
    args = parser.parse_args()

    codes = []
    tally = {}
    for i in range(1, args.repeat + 1):
        code, names = one_run(i, args.keep_green)
        codes.append(code)
        for name in names:
            tally[name] = tally.get(name, 0) + 1

    red = [c for c in codes if c != 0]
    if args.repeat > 1:
        print()
        print(f"{len(codes) - len(red)}/{len(codes)} runs green, {len(red)} red")
        for name, count in sorted(tally.items(), key=lambda kv: -kv[1]):
            print(f"  {count}x  {name}")

    return 1 if red else 0


if __name__ == "__main__":
    sys.exit(main())
