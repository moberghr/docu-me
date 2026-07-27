#!/usr/bin/env python3
"""Does check_citation_resolution actually FIRE, in every direction it claims?

A check that has only ever been run against a green tree asserts nothing - it has never been shown
to distinguish anything from anything. Every cell below breaks ONE thing and demands a failure, plus
two cells that demand a PASS so the check is not simply always-red.

NON-DESTRUCTIVE BY CONSTRUCTION: nothing here touches the real tree. The check's own functions are
imported and run against a FIXTURE repo built in a temp directory, with module.REPO and the two
declared constants swapped for the duration. The one cell that does touch the real tree only READS
it (cell 9 runs the live script and demands exit 0).
"""
import importlib.util
import os
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
CHECKER = os.path.join(REPO, "tools", "loop", "check-state-size.py")

spec = importlib.util.spec_from_file_location("checker", CHECKER)
mod = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mod)

# A fixture orientation layer big enough to clear the vacuity floor (>= 20 citations).
REAL_FILES = [f"tools/loop/probe-{i:02d}.py" for i in range(24)]
ABSENT = "tools/hooks/format-on-edit.py"


def build_fixture(root, *, prose_extra="", cite_absent=True, make_absent_exist=False):
    os.makedirs(os.path.join(root, "tools", "loop"), exist_ok=True)
    os.makedirs(os.path.join(root, "tools", "hooks"), exist_ok=True)
    for rel in REAL_FILES:
        with open(os.path.join(root, rel), "w", encoding="utf-8") as handle:
            handle.write("# fixture\n")
    if make_absent_exist:
        with open(os.path.join(root, ABSENT), "w", encoding="utf-8") as handle:
            handle.write("# recreated against a recorded decision\n")

    body = "Run these: " + ", ".join(f"`{rel}`" for rel in REAL_FILES) + ".\n"
    if cite_absent:
        body += f"Do NOT recreate `{ABSENT}`.\n"
    body += prose_extra
    with open(os.path.join(root, "NOTES.md"), "w", encoding="utf-8") as handle:
        handle.write(body)
    return ("NOTES.md",)


def run_check(root, sources, known_absent):
    """Call the REAL check with its constants pointed at the fixture. Returns its problem list."""
    saved = (mod.REPO, mod.CITATION_SOURCES, mod.CITATION_KNOWN_ABSENT)
    mod.REPO, mod.CITATION_SOURCES, mod.CITATION_KNOWN_ABSENT = root, sources, known_absent
    try:
        import io
        import contextlib
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            return mod.check_citation_resolution()
    finally:
        mod.REPO, mod.CITATION_SOURCES, mod.CITATION_KNOWN_ABSENT = saved


def cell(name, expect_fail, build, *, known_absent=None, sources=None, match=None):
    root = tempfile.mkdtemp(prefix="cite-")
    try:
        default_sources = build(root)
        problems = run_check(
            root,
            sources if sources is not None else default_sources,
            {ABSENT: "declared absent on purpose"} if known_absent is None else known_absent,
        )
        failed = bool(problems)
        ok = failed == expect_fail
        if ok and match:
            ok = any(match in p for p in problems)
        verdict = "PASS" if ok else "**MISS**"
        want = "fires" if expect_fail else "stays green"
        detail = f" :: {problems[0][:88]}" if problems else ""
        print(f"  {verdict}  {name:<52} (want {want}){detail}")
        return ok
    finally:
        shutil.rmtree(root, ignore_errors=True)


def main():
    results = []
    print("check_citation_resolution mutation cells (iter167):")

    # -- the check must stay green on a healthy fixture -------------------------------------
    results.append(cell("baseline: everything resolves", False, build_fixture))

    results.append(cell(
        "baseline: a NEW citation that resolves", False,
        lambda root: build_fixture(root, prose_extra="Also `tools/loop/probe-00.py` again.\n")))

    # -- (2) a citation that does not resolve -----------------------------------------------
    results.append(cell(
        "citation names a file that is not on disk", True,
        lambda root: build_fixture(root, prose_extra="See `tools/loop/ghost-probe.py`.\n"),
        match="not on disk"))

    results.append(cell(
        "citation names a directory that is not on disk", True,
        lambda root: build_fixture(root, prose_extra="Inbox at `tools/loop/no-such-dir/`.\n"),
        match="not on disk"))

    # -- (3) a declared absence that started resolving --------------------------------------
    results.append(cell(
        "declared-absent file was RECREATED", True,
        lambda root: build_fixture(root, make_absent_exist=True),
        match="EXISTS now"))

    # -- (4) a declared absence nobody cites any more ---------------------------------------
    results.append(cell(
        "declared-absent entry is no longer cited", True,
        lambda root: build_fixture(root, cite_absent=False),
        match="cites"))

    # -- a declaration with no reason -------------------------------------------------------
    results.append(cell(
        "declaration carries an empty reason", True, build_fixture,
        known_absent={ABSENT: "   "}, match="no reason"))

    # -- a source that vanished -------------------------------------------------------------
    results.append(cell(
        "a CITATION_SOURCES entry is not on disk", True, build_fixture,
        sources=("NOTES.md", "GONE.md"), match="not on disk"))

    # -- the vacuity refusal ----------------------------------------------------------------
    def tiny(root):
        os.makedirs(os.path.join(root, "tools", "loop"), exist_ok=True)
        with open(os.path.join(root, "NOTES.md"), "w", encoding="utf-8") as handle:
            handle.write("Almost no citations here at all.\n")
        return ("NOTES.md",)

    results.append(cell(
        "extraction went quiet (vacuity refusal)", True, tiny,
        known_absent={}, match="far below"))

    # THE REFUSAL APPENDS, IT DOES NOT RETURN (iter165's shape). A vacuous population must not stop
    # the declaration checks: this fixture is BOTH vacuous AND has a stale declaration, and the
    # check has to report both, not just the one it noticed first.
    def tiny_and_stale(root):
        tiny(root)
        os.makedirs(os.path.join(root, "tools", "hooks"), exist_ok=True)
        with open(os.path.join(root, ABSENT), "w", encoding="utf-8") as handle:
            handle.write("# recreated\n")
        return ("NOTES.md",)

    root = tempfile.mkdtemp(prefix="cite-")
    try:
        tiny_and_stale(root)
        problems = run_check(root, ("NOTES.md",), {ABSENT: "declared absent"})
        both = any("far below" in p for p in problems) and any("EXISTS now" in p for p in problems)
        print(f"  {'PASS' if both else '**MISS**'}  "
              f"{'refusal APPENDS: vacuity + stale declaration both reported':<52} (want both)")
        results.append(both)
    finally:
        shutil.rmtree(root, ignore_errors=True)

    # -- (1) the instrument guard: the two extractions must agree ---------------------------
    # Force a disagreement by making ONE extraction see a different normalisation, the exact bug
    # iter167's probe shipped three times. Patching cite_normalise for extraction B only reproduces
    # "B strips the leading dot" without editing the checker.
    root = tempfile.mkdtemp(prefix="cite-")
    try:
        sources = build_fixture(root, prose_extra="And `.mtk/paths-167/x.py`.\n")
        os.makedirs(os.path.join(root, ".mtk", "paths-167"), exist_ok=True)
        with open(os.path.join(root, ".mtk", "paths-167", "x.py"), "w", encoding="utf-8") as handle:
            handle.write("# fixture\n")
        original = mod.cite_extract_tokens
        mod.cite_extract_tokens = lambda text: {t.lstrip(".") for t in original(text)}
        try:
            problems = run_check(root, sources, {ABSENT: "declared absent"})
        finally:
            mod.cite_extract_tokens = original
        hit = any("extractions disagree" in p for p in problems)
        print(f"  {'PASS' if hit else '**MISS**'}  "
              f"{'extractions disagree (the instrument guard)':<52} (want fires)")
        results.append(hit)
    finally:
        shutil.rmtree(root, ignore_errors=True)

    # -- the live tree, end to end ----------------------------------------------------------
    proc = subprocess.run([sys.executable, CHECKER], capture_output=True, text=True, cwd=REPO)
    live_ok = proc.returncode == 0 and "orientation-layer citations" in proc.stdout
    print(f"  {'PASS' if live_ok else '**MISS**'}  "
          f"{'live checker exits 0 with check #10 present':<52} (want stays green)")
    results.append(live_ok)

    print(f"\n  {sum(results)}/{len(results)} cells")
    return 0 if all(results) else 1


if __name__ == "__main__":
    sys.exit(main())
