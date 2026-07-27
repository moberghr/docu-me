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

Exit code mirrors the suite: 0 green, non-zero red. Artifacts land in .mtk/suite-runs/ (gitignored).
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

    match = SUMMARY.search(blob)
    counts = match.groups() if match else ("?", "?", "?", "?")
    red = proc.returncode != 0

    named = failing_from_trx(trx)
    if not named:
        named = [(n, "") for n in failing_from_stdout(blob)]

    print(
        f"run {index:02d}  {'FAIL' if red else 'PASS'}  exit={proc.returncode}  "
        f"total={counts[0]} failed={counts[1]} skipped={counts[3]}  {elapsed:.0f}s",
        flush=True,
    )
    for name, message in named:
        print(f"           FAILING: {name}", flush=True)
        if message:
            print(f"                    {message}", flush=True)
    if red and not named:
        print(
            f"           red with no named test - read {log.relative_to(REPO)} by hand",
            flush=True,
        )
    if red or keep_green:
        print(f"           artifacts: {log.relative_to(REPO)}", flush=True)
    else:
        log.unlink(missing_ok=True)
        trx.unlink(missing_ok=True)

    return proc.returncode, [n for n, _ in named]


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
