#!/usr/bin/env python3
"""Rotate seven settled sections out of tools/loop/method-notes.md into a SECOND-GENERATION archive.

WHY GENERATION 2 RATHER THAN THE EXISTING method-notes-archive.md, measured before anything was
written: the live file is 62,678 B (~26,115 tok) and must reach 48,000 B to sit inside the 20,000-tok
budget check-state-size.py enforces, so it has to shed ~14.7 KB net. method-notes-archive.md is
already 32,101 B, which leaves it 15,899 B before IT crosses the same budget and 27,899 B before it
crosses the Read tool's 25,000-tok cap. This rotation moves 28,614 B. Into archive-1 that would put
the archive at 60,715 B, past the CAP and not merely the budget, i.e. it would relocate the
truncation instead of removing it. So the pair is out of room and the answer is a generation:
archive-1 is frozen at its current size and keeps a pointer forward.

The set is the five `nextAction` named (iter130/131/133/137/140, all environment-and-history whose
durable half is either shipped or written up in GATES.md) plus iter146 and iter147, because the five
alone left only 2,593 B of headroom - one iteration's worth - and the point of a rotation is headroom,
not fitting (`state.json -> readMe`). Deliberately NOT moved: the iter123-128 preamble bullets (what
the analyzers reject, what this Bash harness refuses), iter154 (`run-suite.py`, cited by `nextAction`
every iteration), and everything from iter157 on.

METHOD, per `state.json -> readMe` ("assert the round-trip BEFORE rewriting the live file"):
  1. parse the live file into (preamble, [(heading, body)]) and require each moved heading exactly once
  2. write archive-2, then READ IT BACK FROM DISK and re-parse it
  3. assert every moved body is byte-identical to what the live file still holds
  4. only then rewrite the live file, replacing each moved section with a pointer stub
Nothing is discarded: the stub keeps the heading (GATES.md cites one of them by name) and the
headlines, and the archive keeps the body verbatim.

Idempotent: a second run finds the sections already stubbed and exits 0 without writing.

MOVED INTO TRACKED SPACE AT ITER168. It lived in `.mtk/paths-162/`, gitignored, while `state.json` and
this file's own consumers cited it as the rotation recipe every iteration is told to reuse - so the one
tool the budget rule depends on was a `rm -rf` away from gone, with the citation left pointing at
nothing. The STUBS below are iter162's own seven sections, kept as the worked example; a caller sets
`ARCHIVE_2` and `STUBS` on this module and calls `main()`, which is the one-section form.

ONE FAILURE MODE WORTH KNOWING, MET AT ITER168: if you TRIM a stub AFTER rotating it, the four-state
machine sees the live body no longer matching the declared stub and refuses with "still has its full
body in the live file AND a body in the archive". Nothing is wrong with the tree in that case -
`check_method_notes_stubs` pairs the stub with its archived body and passes - it is this script that
cannot tell a trimmed stub from an unrotated section. Sync the caller's STUBS to the text now in the
live file and rerun; do not "fix" it by pasting the body back.
"""

import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
LIVE = os.path.join(REPO, "tools/loop/method-notes.md")
ARCHIVE_2 = os.path.join(REPO, "tools/loop/method-notes-archive-2.md")

ARCHIVE_2_HEADER = """# Method notes — archive, generation 2

> Sections rotated out of `tools/loop/method-notes.md` at iter162, verbatim and round-trip asserted,
> when that file had been past the Read tool's 25,000-token cap for ~23 iterations and a plain Read
> of it was silently dropping its newest notes.
>
> **Why a second archive file rather than more of `method-notes-archive.md`:** measured at iter162,
> archive-1 was 32,101 B with 15,899 B of headroom before it crossed the same 20,000-token budget
> the live file had just failed, and 27,899 B before the cap. This rotation is 28,614 B, so putting
> it there would have pushed archive-1 past the CAP and merely relocated the truncation. Archive-1
> is therefore frozen at its size and this file takes the rotation.
> Both are history you open on purpose; `method-notes.md` is the one you read before writing code,
> and `grep` over `tools/loop/method-notes*.md` still spans all three.

"""

# heading -> the stub that replaces its body in the live file. The heading line is repeated inside
# each value on purpose: the script asserts the two agree, so a mis-keyed stub cannot land silently.
STUBS = {
    "## Permissions and the loop's own settings (iter130)": """## Permissions and the loop's own settings (iter130)

  * **MOVED to `tools/loop/method-notes-archive-2.md` at iter162**, verbatim and round-trip
    asserted, to pay back the budget that had put this file past the Read tool's cap. Nothing was
    discarded. **The headlines:** `permissions.deny` patterns match whole tokens **from the start of
    the command**, so `Bash(git push --force:*)` covers nothing that spells the flag later, and a
    flag that can appear anywhere in the argv is not expressible in that language (a `PreToolUse`
    hook is the mechanism); **the loop cannot `Edit` `tools/loop/loop-settings.json`**, the guard
    being on the file rather than the directory, so ship such a change as a paste validated against a
    scratch copy; **to probe a destructive command safely, break its TARGET, not its shape** (a
    remote name that does not exist, never `--dry-run`, which changes the string under test); and
    **one probe per Bash call**, because a denied call aborts the entire command string.
""",
    "## The CLI's own stderr, and probing with child sessions (iter131)": """## The CLI's own stderr, and probing with child sessions (iter131)

  * **MOVED to `tools/loop/method-notes-archive-2.md` at iter162**, verbatim and round-trip asserted
    (8.8 KB, the largest section in the rotation). Nothing was discarded. **The headlines:**
    **`claude` writes real diagnostics to stderr and this loop has been discarding them**, so capture
    stdout and stderr to SEPARATE files, because `2>&1` puts the untrusted-workspace warning in front
    of every `--output-format json` payload and the parse dies at char 0; **`Write(path)` permission
    rules match nothing, only `Edit(path)` does**, and the rewrite the CLI prescribes for
    `.claude/**` is exactly the one that must not be applied; **hooks ARE honoured from a
    `--settings` file**, so hand a settings change the loop may not install to a child `claude -p`
    under `.mtk/`, always with a control cell and a benign cell; **this harness cannot run a shell
    script** (there is no `Bash(bash:*)`), so probes are `.py` driving bash through `subprocess`, and
    **`cd` PERSISTS between Bash calls**; **verify green BEFORE you start, not only before you
    commit**; **`modelUsage` is the only honest way to ask which model ran**; and the phone push is
    still dead, attributed to "Remote Control inactive". Open the archive for the method behind each,
    for the further statically-refused bash shapes, and for the regex and vacuous-floor lessons.
""",
    "## Hooks in a project settings file (iter133)": """## Hooks in a project settings file (iter133)

  * **MOVED to `tools/loop/method-notes-archive-2.md` at iter162**, verbatim and round-trip
    asserted. Nothing was discarded, and **this heading is cited by name from GATES.md's
    `paste-format-on-edit-hook`**, so it stays here as the pointer. **The headline, which is what let
    a dead hook sit unnoticed for 133 iterations: a failing `PostToolUse` hook is INVISIBLE to the
    agent** (measured `exit_code=127, outcome='error'`, zero mentions in any turn, `result.is_error`
    false), so such a hook cannot be verified by waiting for it to complain, and a non-zero exit in
    one you write only hides a problem. `PreToolUse` surfaces loudly; do not generalise from one to
    the other. To see hooks at all, ask for the events: `claude -p … --output-format stream-json
    --include-hook-events --verbose`. **`$CLAUDE_PLUGIN_ROOT` is empty in a project settings file**
    (the CLI sets it only for hooks that come from a plugin), while `$CLAUDE_PROJECT_DIR` resolves
    and the hook's stdin payload carries `tool_input.file_path`. **An untrusted workspace gates
    `permissions.allow` only, not `hooks`.** And `; echo "X=$?"` after a pipe prints the PAGER's exit
    code, which is a confident false green. Open the archive for the `dotnet format` cost table and
    the mutate-in-the-formatter's-own-class lesson.
""",
    "## Two counters, and patching a script that is running (iter137)": """## Two counters, and patching a script that is running (iter137)

  * **MOVED to `tools/loop/method-notes-archive-2.md` at iter162**, verbatim and round-trip
    asserted. Nothing was discarded. **The headlines:** **to patch a script that is executing right
    now, replace the INODE, not the bytes**, since `Edit` and `Write` rewrite the same inode the live
    `bash` still holds an fd on while `os.replace` leaves the running process reading what it started
    with (`bash -n` the candidate in a tempfile first, copy the mode across, print both inodes as the
    evidence); **the recorded cause of a drift can be a mechanism that contributed nothing**, so
    derive the offset from the log rather than trusting a number written about it; **a lookup that
    resolves every key to itself looks exactly like a working lookup**, so turn the spot-check into
    the assertion; **extract-and-drive works for a bash BLOCK, not just a function**; and
    `docume-loop.sh` is the loop's own to edit and commit, so check `git status` for a file rather
    than trusting an inventory note about it.
""",
    "## Testing code that is dormant, and what the CLI default really is (iter140)": """## Testing code that is dormant, and what the CLI default really is (iter140)

  * **MOVED to `tools/loop/method-notes-archive-2.md` at iter162**, verbatim and round-trip
    asserted. Nothing was discarded, and the gate that motivated it (`restart-loop-driver`) carries
    the findings in full. **The headlines:** **"not running" and "correct" are different claims**, and
    a pending restart is exactly the moment never-executed code becomes load-bearing, so the
    follow-up to "X is dormant" is "test X before it wakes up"; **extract a dormant bash function by
    content anchor and run it under `bash -c` with the real inputs**, never retyped; **the
    degraded-input matrix is the half that matters for a guard** (all four bad-state shapes, not the
    happy path); **check `bash -n` and the exec bit before asking a human to run a script**;
    **compare `canonicalModel`, not the `modelUsage` key**, which carries a variant suffix so an
    id-string diff reads as a model change when nothing changed; **an iteration whose measurements
    all come back clean is still a result, so write it down as one**; and a cheap assertion in your
    own migration script will catch your own miscount before the destructive write.
""",
    "## When the rule is \"do not carry knowledge\", and the knowledge is in the tree (iter146)": """## When the rule is "do not carry knowledge", and the knowledge is in the tree (iter146)

  * **MOVED to `tools/loop/method-notes-archive-2.md` at iter162**, verbatim and round-trip
    asserted. Nothing was discarded. **The headlines:** **when a rule forbids carrying somebody's
    knowledge, find a copy of it already in the tree and diff against that**, because a hardcoded
    needle list ages into yesterday's mistakes while a derived one tracks what it protects, and the
    proof it is really derived is a GREEN cell that rewords the source; **a phrase scan needs a
    measured n and the band has two edges** (4 indicted ordinary prose, 7 let the defect through);
    **the illustrative register is not the assertive one**, so a style guide quoting the product back
    is not a leak; **a mechanism that removes nothing once another lands must not ship**, because
    nobody can tell which of two mechanisms is load-bearing; **a per-part floor beats a floor over
    the union**; **two nets over one rule must be proven independent, or one is decoration**; and
    **reuse the repo's own definition instead of restating it**.
""",
    "## When the verification command destroys its own evidence (iter154)": """## When the verification command destroys its own evidence (iter154)

  * **MOVED to `tools/loop/method-notes-archive-2.md` at iter162**, verbatim and round-trip
    asserted, in the second pass of that rotation - the first pass left this file 2,593 B under its
    budget, which is less than one iteration's section. Nothing was discarded. **The operational
    headline, which `nextAction` also carries: run `python3 tools/loop/run-suite.py`, NOT
    `dotnet test | tail`** - the pipe keeps the summary and drops the failure lines above it, so a
    red suite reads as a bare number and MTP writes no artifact unless asked. The runner prints
    failing ids with assertion messages, mirrors the exit code, leaves a log + TRX in gitignored
    `.mtk/suite-runs/`, and takes `--repeat N`. **The other headlines:** **an instruction that depends
    on the next agent remembering it is not a fix, placement is** (iter120 wrote this same lesson into
    a done-archive entry and it recurred 34 iterations later); **`dotnet test -- <runner args>` dies
    on this SDK**, so pass MTP options with no separator; **a green single-class run is not a green
    suite**, and before adding a row to a table in `docs/wiki/` grep `tests/` for the page name,
    because several of those tables are contracts with a corpus behind them; **measure a library's
    surface, not the four cases your corpus happens to hold**; **a gate's "it is your call" can still
    contain a question of fact that is the loop's to answer**, so re-read gate prose for embedded
    *check / find out / see whether* before ending on WAITING-GATE; and **when a late failure changes
    what you did, go back and correct the record you already wrote.**
""",
    "## When a test compares two machine-generated copies (iter147)": """## When a test compares two machine-generated copies (iter147)

  * **MOVED to `tools/loop/method-notes-archive-2.md` at iter162**, verbatim and round-trip
    asserted. Nothing was discarded. **The headlines:** **a generator produces the same bytes from
    the same inputs, so an untouched second run is byte-identical whether or not it rewrote
    anything**, and only content that was NOT machine-generated distinguishes "skipped" from
    "overwrote with an identical copy", so a sampled byte check asserts nothing at all on the
    unsampled rows; **the target that escapes is the one that is empty at creation and load-bearing
    later**, so ask of any idempotence test which target's content is CONSTANT across the two runs;
    **edit every target, and make the edit decision-preserving**; **a perturbation harness needs a
    guard that it perturbed**; and **judge a cell per-test, not per-suite, when it changes a
    deliberate inventory**.
""",
}

HEADING = re.compile(r"^## ", re.M)


def parse(text):
    """-> (preamble, [(heading_line, body_including_trailing_newline)]) in file order."""
    starts = [m.start() for m in HEADING.finditer(text)]
    if not starts:
        return text, []
    preamble = text[: starts[0]]
    bounds = starts + [len(text)]
    sections = []
    for i, start in enumerate(starts):
        chunk = text[start : bounds[i + 1]]
        heading, _, body = chunk.partition("\n")
        sections.append((heading, body))
    return preamble, sections


def main():
    with open(LIVE, encoding="utf-8") as handle:
        live_text = handle.read()
    preamble, sections = parse(live_text)
    by_heading = {}
    for heading, body in sections:
        by_heading.setdefault(heading, []).append(body)

    for heading, stub in STUBS.items():
        if not stub.startswith(heading + "\n"):
            print(f"REFUSED: stub for {heading!r} does not open with that heading")
            return 1
        found = by_heading.get(heading, [])
        if len(found) != 1:
            print(f"REFUSED: {heading!r} appears {len(found)} times in the live file, expected 1")
            return 1

    # APPEND MODE, added at iter162 after the first widening of STUBS showed why it has to exist:
    # the next rotation cannot restore a pristine file (this file's newest sections did not exist when
    # any backup was taken), so a rerun has to decide PER SECTION rather than all-or-nothing. Four
    # states, and two of them are refusals because they mean text is about to be lost or duplicated.
    stub_bodies = {h: s.partition("\n")[2] for h, s in STUBS.items()}
    archive_prev = ARCHIVE_2_HEADER
    existing = {}
    if os.path.exists(ARCHIVE_2):
        with open(ARCHIVE_2, encoding="utf-8") as handle:
            archive_prev = handle.read()
        existing = dict(parse(archive_prev)[1])

    to_move, done_already, refusals = [], [], []
    for heading in STUBS:
        is_stub = by_heading[heading][0] == stub_bodies[heading]
        in_archive = heading in existing
        if is_stub and in_archive:
            done_already.append(heading)
        elif not is_stub and not in_archive:
            to_move.append(heading)
        elif is_stub:
            refusals.append(f"{heading!r} is ALREADY A STUB in the live file but the archive holds no"
                            " body for it - its text is in neither file. Recover it from git"
                            " (`git log -p -- tools/loop/method-notes.md`) before touching anything")
        else:
            refusals.append(f"{heading!r} still has its full body in the live file AND a body in the"
                            " archive - one is a duplicate. Diff them by hand; a rerun would append a"
                            " second copy or overwrite the archived one")
    if refusals:
        print(f"REFUSED: partially applied - {len(refusals)} of {len(STUBS)} sections are in a state"
              " a rerun would make worse:")
        for line in refusals:
            print(f"  {line}")
        return 1
    if not to_move:
        print(f"already rotated: {len(done_already)}/{len(STUBS)} sections are stubs with their"
              " bodies in the archive. Nothing to do.")
        return 0

    # 1. append the new bodies to whatever the archive already holds, in live-file order
    moved = [(h, by_heading[h][0]) for h, _ in sections if h in to_move]
    if len(moved) != len(to_move):
        print(f"REFUSED: matched {len(moved)} sections to move, expected {len(to_move)}")
        return 1
    if not archive_prev.endswith("\n"):
        archive_prev += "\n"
    archive_text = archive_prev + "".join(f"{h}\n{b}" for h, b in moved)

    # 2. write it, then read it BACK OFF DISK and re-parse - the round trip, before the live file is
    #    touched at all. A crash between here and step 4 loses nothing: the live file still has both.
    with open(ARCHIVE_2, "w", encoding="utf-8") as handle:
        handle.write(archive_text)
    with open(ARCHIVE_2, encoding="utf-8") as handle:
        reread = handle.read()
    _, archived_sections = parse(reread)
    archived = dict(archived_sections)

    # 3. assert byte-identical, both directions, before the destructive write. The second loop is the
    #    one append mode makes necessary: an earlier rotation's bodies must survive this one untouched.
    if len(archived_sections) != len(existing) + len(moved):
        print(f"REFUSED: archive re-parsed to {len(archived_sections)} sections, expected"
              f" {len(existing)} existing + {len(moved)} new")
        return 1
    for heading, body in moved:
        if archived.get(heading) != body:
            print(f"REFUSED: round trip is not verbatim for {heading!r}")
            return 1
        if body not in live_text or body not in reread:
            print(f"REFUSED: body of {heading!r} is not a substring of both files")
            return 1
    for heading, body in existing.items():
        if archived.get(heading) != body:
            print(f"REFUSED: this run changed an ALREADY-ARCHIVED section, {heading!r}")
            return 1
    print(f"round trip asserted: {len(moved)}/{len(moved)} new sections byte-identical on disk,"
          f" {len(existing)} previously archived sections unchanged")

    # 4. rewrite the live file, stubs in place, every other section untouched. Sections already
    #    stubbed by an earlier run get the same stub back, so this is a no-op for them.
    out = [preamble]
    for heading, body in sections:
        out.append(STUBS[heading] if heading in STUBS else f"{heading}\n{body}")
    new_live = "".join(out)
    for heading, body in moved:
        if body in new_live:
            print(f"REFUSED: {heading!r} body would survive in the live file")
            return 1
    before = len(live_text.encode())
    with open(LIVE, "w", encoding="utf-8") as handle:
        handle.write(new_live)

    after = os.path.getsize(LIVE)
    print(f"method-notes.md   {before:,} B -> {after:,} B  (~{int(after / 2.4):,} tok, budget 20,000)")
    # The NAME, not the literal: the one-section form points ARCHIVE_2 at whichever generation is
    # current, and iter169 watched this line report gen 3's new size under gen 2's name.
    print(f"{os.path.basename(ARCHIVE_2)}  {os.path.getsize(ARCHIVE_2):,} B "
          f"(~{int(os.path.getsize(ARCHIVE_2) / 2.4):,} tok)")
    print(f"headroom before the live file is over budget again: {48_000 - after:,} B")
    return 0


if __name__ == "__main__":
    sys.exit(main())
