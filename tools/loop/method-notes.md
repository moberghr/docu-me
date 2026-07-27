# Method notes

> What this loop has learned the hard way about its own tools: the analyzers, the harness's
> refusals, the shapes that do not compile, what a test must be anchored to. Carried in
> `state.json -> nextAction` from roughly iter60 to iter128 and moved here because it grew every
> iteration, which is precisely the shape that breaks step 1's Read. Verbatim, nothing dropped.
>
> **Read this before writing code or a test, and append to it rather than to `nextAction`.**
>
> **THIS FILE HAS A HARD 20,000-TOKEN BUDGET AS OF ITER162, and `python3 tools/loop/check-state-size.py`
> now EXITS NON-ZERO on it** where it used to print "OVER CAP - Read TRUNCATES" and pass, which is how
> it spent ~23 iterations past the Read tool's cap while every iteration was ordered to read it.
> When your new section puts it over: rotate the oldest settled `##` sections into
> **`tools/loop/method-notes-archive-3.md`, the CURRENT generation (opened iter166)**, VERBATIM,
> assert the round trip BEFORE rewriting this file, and leave the heading plus its headlines behind
> as a stub (the heading has to stay — GATES.md cites one of them by name, and as of iter166
> `check_method_notes_stubs` fails on a stub whose body is not where it says it is). Generations 1
> and 2 are **full and frozen**: `method-notes-archive.md` at 32 KB since iter162, and
> `method-notes-archive-2.md` at 54 KB / ~22.5 K tok since iter166, which is already past the
> 20,000-token band and inside the Read tool's 25,000-token cap. **Do not rotate into either.**
> Worked recipe, re-runnable: `tools/loop/rotate-method-notes.py`, with
> `.mtk/paths-166/rotate-iter166.py` as the one-section form that also shows how to retarget it at a
> new generation. **A generation 4 must be declared in BOTH `ARCHIVE_FILES` and
> `METHOD_NOTES_GENERATIONS` in the same change that creates it — that is now a failing check, not
> advice.**
>
> **EVERY BUDGET LEVER ON THIS FILE IS NOW SPENT, MEASURED TWICE — SO THE REMEDY IS TO WRITE LESS.**
> iter166 withdrew "condense the stub layer" (only 1,829 B of 26,556 B was repeated phrasing; the rest
> is headline CONTENT) and named rotation as the live lever. iter170 measured rotation too: **ONE
> candidate worth +28 B**, and its two real rotations netted **-285 B** and **-37 B**, because a stub
> costs what its headlines cost. Rotation frees space for a NARRATIVE section, never for a terse list.
> **Full account, and the 8x headroom error that came with it: the iter170 section at the end of this
> file.** A stub is an INDEX that says "open the archive", not a summary that reproduces it.

**METHOD NOTES — carried forward, plus one new.**
  * **iter127: EVERY FILE THIS LOOP APPENDS TO EVERY ITERATION WILL EVENTUALLY OUTGROW THE READ TOOL.**
    Two ceilings, a 256 KB byte one that fails the Read and a 25,000-token one that truncates. Not
    restated here — two copies of one rule is how the iter127 wording went stale. **The one authority is
    `state.json → readMe`**; the check is `python3 tools/loop/check-state-size.py`.
  * **NEW, iter125: IN A YAML TEMPLATE THE PROSE AND THE CODE SHARE ONE FILE.** A bare `--fail-on-drift`
    Absent pattern was satisfied by the header comment "no `--fail-on-drift` here", and a bare
    `git merge-base` by the comment above the step. Anchor every template pattern to an invocation —
    `docume drift[^\n]*--fail-on-drift`, `base=\$\(git merge-base` — never to the bare token.
  * **NEW, iter125: A STEP CONDITION THAT SIX STEPS SHARE PROVES NOTHING ABOUT ANY ONE OF THEM.**
    `if: steps.inbox.outputs.work == 'true'` is on six steps of docs-feedback.yml, so deleting it from the
    `claude -p` step alone left the pattern matching. Bind it: `name: <step>\n        if: <cond>`.
  * **NEW, iter125: A CONTROL CASE EARNS ITS PLACE IN A MUTATION HARNESS.** One case renumbers §10's
    nested steps 4/5/6 and must NOT fail — it is the only proof that the claims key on phrases rather than
    ordinals. Report it as OK-IGNORED, distinct from CAUGHT.
  * A HEREDOC (`cat > f <<'PY'`) IS REFUSED IN THIS HARNESS — write probe scripts with the Write tool. A
    COMPOUND COMMAND CONTAINING A SUBSHELL is refused too, **and so is a BASH `for` LOOP over a list with
    `$f`-style expansion ("Contains simple_expansion") — put the loop in python instead.**
    **NEW, iter128: `${PIPESTATUS[0]}` is refused the same way ("Contains expansion").** To capture the exit
    code of a command whose output you are paging, drop the pipe and let the harness report the exit code
    itself, or `print()` the status from inside the python script.
  * **NEW, iter128: THE READ TOOL AND `count('\n')` DISAGREE BY ONE.** A claim like "99 lines" written
    against `text.count('\n')` will fail its own assertion — the tool (and `check-state-size.py`) count
    `text.count('\n') + 1`. Use the same convention as the thing you are quoting.
  * A NEW CLI FLAG COSTS THREE MORE EDITS (cli.md's option table, CHANGELOG.md's flag inventory, and
    PLAN.md if it declares it). **A NEW CONFIG FIELD COSTS FOUR:** §5.1's block, the schema, the record,
    and configuration.md's example. **A NEW §8/§9 SEMANTIC COSTS A CLAIM IN PlanSemanticsTraceTests, AND A
    NEW §10 WORKFLOW PROMISE NOW COSTS ONE IN PlanWorkflowTraceTests** — neither will let you add the
    bullet without naming the step that does it.
  * A MUTATION HARNESS MUST SAVE AND WRITE BACK THE TEXT ITSELF (`git checkout --` restores to HEAD, and
    these files carry uncommitted edits). Verify with `git diff --stat` after the run. AND IT MUST RE-RUN
    THE BASELINE AFTER RESTORING.
  * RECONCILE AGAINST PLAN.md DIRECTLY, NOT AGAINST A DERIVED ARTEFACT. Five iterations, four finds — and
    the lead is now exhausted.
  * `dotnet test --nologo` RUNS ZERO TESTS AND EXITS 1. Plain `dotnet test` is correct. A single class:
    `dotnet test --filter-query '/*/*/<Class>/*'`.
  * WHEN A HARNESS CRASHES, CASES AFTER THE CRASH DID NOT RUN. Read the tail and check the N/N line.

## C# analyzer and toolchain trivia (rotated from the preamble at iter170)

  * **MOVED to `tools/loop/method-notes-archive-3.md` at iter170**, round trip asserted first. **An
    INDEX, not a restatement — open the archive when one of these bites**, which is the whole point of
    a stub and is why the first draft of this one saved 37 B: SA1515 (comment first inside `[`),
    SA1118 (concatenated `customMessage:`), S127 (`for` variable), CS1574 (`<see cref>` to a private
    member or to a test type from `src/` — use `<c>`), `[GeneratedRegex]` needs options, the
    analyzers fight each other (RCS1215+S3981, MA0001/CA1865, S3220/S3878, RCS1118, MA0006, CA1861,
    Shouldly's `IEnumerable<char>` overload), `ShouldContain` over a whole section proves nothing,
    a generative-skill fixture lives outside the repo, and `dotnet tool install --add-source`
    installs the CACHED package. Also there: a mutation that does not compile is not evidence — which
    has its own `## ` section at iter158.

## The carried-forward preamble bullets: calibration, harness sizing, archive bookkeeping (rotated from the preamble at iter170)

  * **MOVED to `tools/loop/method-notes-archive-3.md` at iter170**, round trip asserted first — five
    bullets that lived in the PREAMBLE, which is why nothing had ever paired them: it is not a `## `
    section, so neither the rotation engine nor `check_method_notes_stubs` could reach it. **The one
    thing worth keeping inline, because a wrong copy of it cost iter170 an 8x error: the live
    constants are 2.4 B/tok markdown and 2.3 B/tok JSON, defined in `check-state-size.py`**, whose
    `MEASURED` table dates every measurement behind them — **never restate either undated**
    (`check_prose_constants` now fails on that). **Index of the rest:** the Read tool is the only
    tokenizer here and a bounded Read reports no totals; size a mutation from a target TOKEN count
    and assert the expected MESSAGE; N guarded files need N red branches, unmutated-green first;
    done-archive.jsonl mixes strings and dicts; a budget check the current iteration trips is the
    check working.

## Permissions and the loop's own settings (iter130)

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
## The CLI's own stderr, and probing with child sessions (iter131)

  * **MOVED to `tools/loop/method-notes-archive-2.md` at iter162**, verbatim and round-trip asserted
    (8.8 KB, the largest section in the rotation). **INDEXED at iter176** to fund that iteration's
    note. Open the archive for: `claude` writing real diagnostics to stderr that `2>&1` then feeds to
    a JSON parse (capture the two streams separately); `Write(path)` permission rules matching nothing
    where `Edit(path)` does; hooks being honoured from a `--settings` file, so a child `claude -p` can
    exercise one the loop may not install; no `Bash(bash:*)` here, so probes are `.py` driving bash;
    `cd` persisting between Bash calls; verifying green BEFORE you start; `modelUsage` as the only
    honest way to ask which model ran; and the regex and vacuous-floor lessons.
## Hooks in a project settings file (iter133)

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
## CI fidelity: the host the suite is verified on (iter134)

  * **MOVED to `tools/loop/method-notes-archive.md` at iter143**, verbatim and round-trip asserted,
    to pay back the budget iter143's own section spent (the rule this file's header states). Nothing
    was discarded, and both defects it records are fixed and committed (`f04e367`, `9efa2c4`).
    **The headlines, kept here because they are the parts you need at a glance:**
    `GITHUB_ACTIONS=true` changes what the CLI prints and `CI=true` does not, so to verify a claim
    about CI you must set the variable the runner sets; a clean-clone check has two shapes and only
    `--depth 1` is what `actions/checkout@v4` gives you; **a skip is a coverage hole that reports
    itself as success** — read the summary line's `skipped:` count, not just `failed:`; and when the
    invariant is about the runner, assert the workflow rather than the runner, so it fails on a
    laptop. Open the archive for the method behind each.

## Reading the loop's own history back (iter136)

*Moved to `tools/loop/method-notes-archive.md` at iter154 (verbatim, round trip asserted) — settled:
`check-state-size.py --find` is committed and is what `doneArchive.howToRead` now prescribes.
**Headlines:** **`n` in done-archive.jsonl is a LINE INDEX, not an iteration number** and has not
matched one since line 50 (iter48 logged twice) — never read an iteration off `n` and never renumber
to "fix" it; **the archive has two entry shapes and any reader must handle both** (strings with a
leading `iterNNN`, objects with `{"iteration": NNN}`); **the documented `grep` lookup was wrong for
27 of 135 iterations and its failure mode looks like a hit**, because entries cite each other in
prose — use `python3 tools/loop/check-state-size.py --find <n>`; **a check over a file is not a check
over the thing the file records** (valid JSON + contiguous `n` + matching count are all satisfiable
by a file missing an iteration — ATTRIBUTION, COVERAGE and HEAD are the checks in the archive's own
domain); and **when a mutation harness scores less than N/N, suspect the prediction first** — assert
the expected SET of messages, since one mutation can legitimately fire two checks.*

## Two counters, and patching a script that is running (iter137)

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
## Estimating tokens from bytes, and the file nothing reads (iter138)

  * **MOVED to `tools/loop/method-notes-archive.md` at iter141**, verbatim and round-trip asserted,
    to pay back the budget iter141's own section spent (the rule this file's own header states).
    Nothing was discarded. **The headline, kept here because it is the part you need at a glance:**
    the Read tool's truncation notice is the only tokenizer on this machine, and
    `check-state-size.py` now prints the full bytes-per-token calibration table on every run — read
    that instead of re-deriving it. Open the archive for the method behind it and for the
    handoff-archive.md finding.

## Testing code that is dormant, and what the CLI default really is (iter140)

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
## A guard that is a placement, not a computation (iter141)

*Moved to `tools/loop/method-notes-archive.md` at iter144 (verbatim, round trip asserted) — its
subject is settled: the section's own closing bullet recorded that nothing made the §0.1/§1.4 write
lock structural, and iter143's `WriteLockCoverageTests` closed precisely that. Headlines kept here
because they still describe how to trace this class of rule: **testing a guard's logic says nothing
about whether any caller runs it** (the lock is enforced by WHERE the call sits, so a test must be
per write surface and must run the command); **enumerate the verbs, not the commands**, when tracing
an "every write path checks X" claim; **`&& !dryRun` is a swallow-by-construction shape** just as
`|| true` is; **a guard test that cannot go red is decoration — mutate the guard, not a neighbour.**
The successor section below, "Making a placement-enforced rule structural (iter143)", carries the
fix.*

## When the enforcement of a rule is a hardcoded list (iter142)

*Moved to `tools/loop/method-notes-archive.md` at iter145 (verbatim, round trip asserted); its subject
is settled. **INDEXED at iter176** to fund that iteration's note. Open the archive for: a list every
per-subject test iterates being the enforcement boundary rather than the directory it describes;
"something else enumerates the directory" not being "something else checks the rule"; **the control
cell being the finding**; cleaning a mutated directory up as a directory; and grading secret-scan
needles by kind without printing one. Also settled there: **§1.2 is traced and holds — do not
re-trace it** (one `SendAsync`, six call sites, all six through `ThrowIfFailed`).*

## Making a placement-enforced rule structural (iter143)

*Moved to `tools/loop/method-notes-archive.md` at iter146 (verbatim, round trip asserted) — settled:
`WriteLockCoverageTests` is committed and its method has been restated by iters 144, 145 and 146.
**Headlines:** a regex over C# **meets a nested generic and misattributes rather than misses**
(`Task<[^>]*>` fails `Task<IReadOnlyList<...>>` — use a greedy `<.*>`); walk back to the nearest
**member** declaration, not the nearest `public` one; **the MTP `--filter-query` takes exactly ONE
pattern** (`a|b` exits 1, `(a|b)` exits 8, both zero tests; `--treenode-filter` is dead); **the control
cell scales**; and a mutation adding a file under `src/` **has a second audience** in
`DogfoodWikiTests`. Open the archive for the method behind each.*
## When the mirror of a rule is a second copy nobody diffs (iter144)

*Moved to `tools/loop/method-notes-archive.md` at iter147 (verbatim, round trip asserted) — settled:
`check_gate_mirror` is committed in `tools/loop/check-state-size.py` and has gated every
state.json/GATES.md edit since. **Headlines:** **equal counts are not a matching set** (11 checkboxes
vs 11 keys were different elevens — diff the sets, never the cardinalities); **grep confirms the
wrong block** (the id appears in `nextAction` and in `blockers`, so a search hits something that is
not the mirror the rule names); anchor a markdown-structure scan **to the line start and the bold
id**, since gate bodies cite ids constantly; **the direction with a future is status drift, not
absence** (a mirror still reading PENDING the day a box is ticked is how the loop skips work it now
owes); the **`git show HEAD:<script>` control block**; and **a JSON round trip is a mutation until a
control says it is not**. Open the archive for the method behind each.*

## When a rule's enforcement is one argument at one call site (iter145)

*Moved to `tools/loop/method-notes-archive.md` at iter154 (verbatim, round trip asserted) — settled:
`PruneConfirmationCoverageTests` is committed and has gated every `--prune` call site since.
**Headlines:** **when a seam exists to make a rule testable, the production argument is what nobody
tests** — wherever this repo injects a collaborator to keep a path offline-testable, ask what asserts
the real argument; **a lambda should be refused, not inspected**, because an auto-yes and a real
prompt are the same shape to any scan short of a compiler; **budget the analyzers into the cell
design, not into a retry** (S1144 orphaned prompt, S1172 orphaned parameter — a cell that does not
compile is INCONCLUSIVE, not red); **a control that renames the thing is worth more than one that
leaves it alone**, since only that cell proves the test keys on the wiring and not on a string; and
**run the full suite under the flagship defect once** — but see iter154 below, because that bullet's
closing line ("read the failing test NAMES, not just the count") is one of the two places this lesson
was written down and never mechanised.*

## When the rule is "do not carry knowledge", and the knowledge is in the tree (iter146)

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
## When a test compares two machine-generated copies (iter147)

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
## When the verification command destroys its own evidence (iter154)

  * **MOVED to `tools/loop/method-notes-archive-2.md` at iter162**, verbatim and round-trip asserted.
    **INDEXED at iter176** to fund that iteration's note. **The one headline that stays inline, because
    every iteration runs it: `python3 tools/loop/run-suite.py`, NOT `dotnet test | tail`** - the pipe
    keeps the summary and drops the failure lines above it. Open the archive for: an instruction that
    depends on the next agent remembering it not being a fix (placement is); `dotnet test -- <args>`
    dying on this SDK; **a green single-class run not being a green suite**; grepping `tests/` before
    adding a row to a `docs/wiki/` table; measuring a library's surface rather than your corpus's four
    cases; a gate's "your call" still containing a question of fact the loop owes; and correcting the
    record you already wrote when a late failure changes it.
## When a check is named after one file but guards the whole tree (iter157)

  * **MOVED to `tools/loop/method-notes-archive-2.md` at iter164**, verbatim and round-trip asserted,
    to pay back the budget iter164's own section spent (the rule this file's header states). Nothing
    was discarded, and both tests it produced are committed. **The headlines:** **grep for the
    ASSERTION, not for the test class you expect to hold it** - `CliReferencePageTests` sweeps ~132
    documented invocations across README, `docs/wiki/`, every `SKILL.md` and the workflow templates,
    so a class named after `cli.md` owns a tree-wide sweep and the "gap" was nearly a duplicate test;
    **the corollary is what found the real gap - ask what DIMENSION the existing sweep covers**, since
    the tree was swept for CLI *options* a doc names and not for *config fields* a doc promises;
    **a decision's settle instructions are a claim about the tree and rot like any other** (three
    places named, six real, two of them skills - write the surface into a `Places` array pinned both
    ways and "remember to edit all six" becomes a failing test); and **an exclusion is better made
    structural than filtered**, so walk `docs/wiki` rather than `docs` with a `.Where` a later edit
    can drop.
## A mutation that does not compile is not evidence (iter158)

  * **MOVED to `tools/loop/method-notes-archive-2.md` at iter164**, verbatim and round-trip asserted.
    Nothing was discarded. **The headlines:** **BUILD-FAILED IS NOT CAUGHT, AND IT READS LIKE IT** -
    a harness that folds the two together will tell you a test works when it was never invoked, so
    report it as its own verdict beside CAUGHT and MISSED; in C# a rename inside `src/` is
    compiler-enforced across every reader, so **to mutate a rename realistically, patch the record AND
    every reader** and leave only the prose stale, because the docs are the interesting half;
    **assert the RESTORE, not just the run** (hash every touched file before the first mutation and
    after the last, print IDENTICAL or DIRTY, then rebuild at restored HEAD); and **the dimension
    question is re-askable and most answers come back clean** - four probed, one gap, and **probing
    four dimensions to ship one test is the expected ratio**, because a probe costs a script while a
    test guarding a dimension that was never at risk costs forever.
## The bookkeeping file's own invariants were the last unguarded seam (iter159)

  * **MOVED to `tools/loop/method-notes-archive-2.md` at iter164**, verbatim and round-trip asserted.
    Nothing was discarded, and all three checkers it produced are committed. **The headlines:**
    **when one instance of a pattern gets a checker, ENUMERATE THE OTHER INSTANCES** - `gates` got one
    at iter144 only after a gate sat unmirrored for 69 iterations, and its two siblings kept the same
    exposure for another 15; **a multi-place edit written out in prose and enforced by nothing is the
    tell**; **a check must not fire on the event the loop is waiting for**, so direction (3) is scoped
    to stubs still reading OPEN and the harness carries must-stay-GREEN cells for an answered one;
    **scoping a check creates a vacuity risk, so assert the population** - and read iter164 next,
    which found that this section put that assertion in its own scratch harness rather than in the
    check, where nothing re-ran it; and **`git checkout --` is the right restore when the mutated
    files are clean at HEAD**, exact by construction and usable from a `finally`, but not once your
    own increment has dirtied them (see iter160).
## Calibrating the size checker (moved verbatim from `state.json -> readMe` at iter160)

  * **MOVED ON to `tools/loop/method-notes-archive-2.md` at iter164**, verbatim and round-trip
    asserted - this section was itself a historical quote, the paragraph that stood at the top of
    state.json from iter129 to iter160, and it is now two moves from where it was written. Nothing was
    discarded. **The headlines:** **truncation is NOT silent** - the Read tool prints an explicit
    `[Truncated: PARTIAL view - showing lines 1-462 of 1500 total (68900 tokens, cap 25000)]`, so the
    risk is an agent skimming past the notice, not the tool hiding anything - and **that notice is the
    only tokenizer on this machine**, which is how every bytes-per-token constant here was measured.
    The live version of all of it is `check-state-size.py`'s docstring, its `MEASURED` table and
    `check_calibration`, which fails when a constant drifts optimistic; read those, not a paragraph
    about them.
## Mutation harnesses, iter160

  * **MOVED to `tools/loop/method-notes-archive-2.md` at iter164**, verbatim and round-trip asserted.
    Nothing was discarded. **The headlines:** **when an earlier iteration enumerated a family and
    closed it, check whether the enumeration skipped a LIFECYCLE HALF** - iter159 swept the
    stub-to-OPEN-body splits correctly and completely, and the fourth pair was on the *settled* side,
    already broken at five tombstones to four bodies; the generalisation is open vs settled, added vs
    removed, live vs archived, and iters 161-164 each found another instance of it; **assert before
    you mutate, not after** - iter160's migration refused its own first run over a 1806-vs-1771
    character count, and because the assertion ran before the write the archive was untouched when it
    reported failure; and **`git checkout --` is unusable once your own increment has dirtied the
    mutated files**, so snapshot each touched file as BYTES before the first mutation and digest the
    whole set before and after.
## Two checks can print BROKEN for one mutation (iter161)

  * **MOVED to `tools/loop/method-notes-archive-2.md` at iter164**, verbatim and round-trip asserted,
    in the second pass of this rotation - the first five sections left only 3,434 B of headroom, which
    is less than one recent section, and headroom is the deliverable rather than fitting (iter162's
    own mistake, made with 2,593 B). Nothing was discarded, and `check_gates_archive` is committed.
    **The headlines, both of which iter164's harness leans on directly:** **anchor a harness
    expectation on the CHECK'S OWN MESSAGE TEXT, not on the mutated key's name** - `main()` runs every
    check and collects problems before evaluating any FAIL banner, so one mutation legitimately trips
    two checks and both print their BROKEN line, and iter164 sharpened this into slicing stdout by
    each check's section header (see it there: exiting non-zero attributes nothing); and **prefer a
    DECLARED exemption list to a key-shape regex**, because a kebab-case regex would classify the
    mis-keyed gate mirror the check exists to catch as "not a gate" - with the declaration itself
    checked both ways. Verdicts worth keeping distinct: CAUGHT, MISSED, WRONG-CHECK, CRASH, and a
    GREEN/REGRESSION pair for the must-stay-green control.
## A printed defect that exits 0, and a fixture list that rots (iter162)

  * **MOVED to `tools/loop/method-notes-archive-2.md` at iter165**, verbatim and round-trip asserted,
    to pay back the budget iter165's own section spent. Nothing was discarded, and its mechanism is
    committed: `check_read_whole_files`, whose vacuity refusal iter165 proved fires. **The headlines:**
    **a check that reports a defect and exits 0 trains its readers to skim** - if a check knows
    something is wrong, decide whether that is a failure, because "reported" is not a state; **a
    harness that enumerates its fixture by hand rots the moment the thing it guards gains a
    dependency** (0/5 at HEAD for ~26 iterations, so copy the DIRECTORY, not a list, and assert the
    un-mutated cell first); **a rotation has two ceilings, the source's and the destination's -
    measure the destination first**, and **headroom is the deliverable, not fitting**; **anchor a
    mutation expectation on the check's own message, never on the mutated file's name**, since the
    size table prints every filename on every run, green included; and **a migration script must
    REFUSE a partially applied input**, not merely detect a fully applied one.
## A verdict nobody reached, and warnings that cannot be heard (iter163)

  * **MOVED to `tools/loop/method-notes-archive-2.md` at iter165**, verbatim and round-trip asserted;
    all three mechanisms are committed and green in `run-harnesses.py`. **INDEXED at iter175** to fund
    that iteration's own note, which is the lever iter170 named: a stub is a pointer, not a summary.
    Open the archive for: a check printing a verdict it never reached (assert the WALK, not the
    finding count); "warn and continue" needing the channel measured first; a probe that enables the
    visibility it measures; anchors having to be CONTIGUOUS in source; and `EXPECTED_AT_LEAST`.
## Proving a vacuity judgement instead of writing one (iter164)

  * **MOVED to `tools/loop/method-notes-archive-3.md` at iter166**, verbatim and round-trip asserted;
    the seam is closed (iter165 fixed the two refusals that skipped their findings). **INDEXED at
    iter175** to fund that iteration's note. Open the archive for: "non-vacuous by construction" being
    a claim (three of four false); attributing a failure when `main()` runs every check (WRONG-CHECK);
    scoping to a prose prefix as one-keystroke vacuity; **predicting each cell's verdict in writing
    before the first run**; and this Bash tool refusing `<->` in a quoted argument and `/tmp` redirects.
## A refusal that returns is a refusal that skips (iter165)

  * **MOVED to `tools/loop/method-notes-archive-3.md` at iter166**, verbatim and round-trip asserted,
    to pay back the budget iter166's own section spent. Nothing was discarded, and the fix is committed
    in all eight of check-state-size.py's checks. **The headlines:** **a declared refusal is prose until
    a cell fires it** (all four fired, 5/5 - a green measurement worth one sentence, not a story), and
    where a cell must mutate a SCRIPT rather than data, **assert the anchor matched exactly once**,
    since a no-op replace leaves the tree healthy and grades MISSED, a fabricated find; **A GUARD THAT
    `return`s IS A GUARD THAT SKIPS** - two of eight refusals returned where six append, their
    populations were not interdependent, and both printed "nothing to check" while naming neither
    planted defect, so the refusal was buying an honest label at the price of the findings (new verdict:
    **MASKED**); **when a cell passes, record who ELSE fired**, which exposed iter164's own
    `gate-mirror/mirror-drained` cell as having fired a sibling's refusal as unattributed collateral for
    a whole iteration; **a remedy instruction goes stale like any other claim about the tree - measure
    the population it names**; and **bound the blast radius in the check's SOURCE, not from memory**
    before predicting that one cell is the sole detector.
## The stub layer nobody had paired, and a regex that fabricated 18 findings (iter166)

  * **MOVED to `tools/loop/method-notes-archive-3.md` at iter167**, verbatim, round trip asserted.
    **An INDEX, not a restatement — open the archive when one of these bites.** Seam closed:
    `check_method_notes_stubs` (check #9) stands over 24/24 stubs. Topics: a "round-trip asserted"
    claim is asserted ONCE by the rotating script and never again, so pair stub to body in a check,
    and when an iteration enumerates "every stub/body split" scoped to one file, look for the
    sibling in another file; **a regex that under-matches FABRICATES findings rather than missing
    them** (`[Mm]oved` vs the all-caps `**MOVED to` — 18 confident, specific, wrong orphans), so
    check a phrase-keyed enumeration against an INDEPENDENT count and make a vacuity floor a hard
    minimum, a partial match being worse than none; the fourth pairing direction is "a stub that
    does not parse is NOT a live body"; measure what the bytes ARE, not how many; and a scratch
    probe restating a committed declaration goes stale inside one iteration.

## The probe that reproduced iter166's bug three times while implementing its lesson (iter167)

  * **MOVED to `tools/loop/method-notes-archive-3.md` at iter168**, verbatim, round trip asserted.
    **An INDEX — open the archive when one of these bites.** Topics: **the instrument guard belongs
    inside the check as a failing fact, not in its docstring** — iter167 set out to apply iter166's
    lesson, wrote two extractions on purpose, and STILL shipped that bug three times (43 fabricated
    findings, then 4, then 2), so their agreement is fact (1) of `check_citation_resolution`;
    **knowing a lesson does not protect you, wiring the counter-measure into what you ship does**;
    the three bug shapes (punctuation stripped from both ends, a sentence period inside the segment
    class, scanning a serialisation not the content); why a blunt "every citation must resolve"
    would indict the tree's clearest documentation, hence `CITATION_KNOWN_ABSENT` both ways; **do
    not write specimen paths in prose**; and declined scope must be counted or it reads as coverage.
  * **THE LAST BULLET OF THE ARCHIVED BODY WAS ACTED ON AT ITER168**: the seven re-runnable harnesses
    moved into `tools/loop/`, `check_harness_tracking` (check #11) now fails when a harness or its
    runner is untracked, and that move retargeted the runner path quoted inside the archived bullet.

## Existing is not the same as being available to anyone else (iter168)

  * **MOVED to `tools/loop/method-notes-archive-3.md` at iter169**, verbatim and round-trip asserted,
    to pay back the four tokens of budget this file had left. Nothing was discarded. **The
    headlines:** **a citation that resolves can still resolve on ONE MACHINE ONLY** - existence and
    availability are different properties and only the second survives a clone, which is why check
    #11 asserts TRACKED rather than present; **when a check enumerates a declared set, ask what
    CARRIES the set and put that in the same population**, because facts satisfied by a file merely
    being READABLE cannot notice that nobody else can get it; **a ratchet is the third option when a
    defect class cannot be fixed today**, asserting nothing about today's count and everything about
    the next one; **a move that preserves DEPTH edits no path arithmetic**, and wrong path math
    crashes rather than lying; and **a check that needs git must separate "fixture" (no `.git`, so
    tracked-ness is UNKNOWABLE) from "broken" (`.git` present and unreadable)**, or every sibling's
    temp-tree fixture silently stops asserting the git fact.

## A harness that has to mutate the live tree, and a guard that checks itself (iter169)

  * **MOVED to `tools/loop/method-notes-archive-3.md` at iter170**, verbatim, round trip asserted
    first. **An INDEX — open the archive before writing a live-tree harness.** When no fixture is
    possible (a compiled test's `RepoRoot` walks up to `DocuMe.slnx`): refuse to start on a dirty
    target, restore in a `finally`, verify the restore TWO ways, declare the target list once. And
    **N/N on a first run is a reason to check the harness** — prove a cell can report FAIL. Worked
    example: `tools/loop/mutate-toolchain-pinning.py`. **A test may also assert a hazard is STILL
    THERE**: a tripwire reddens the moment the decision is settled and carries its own instruction.
## A number copied into prose is a mirror nobody diffs (iter170)

  * **A HAND-DERIVED COUNT IN AN ORIENTATION DOCUMENT OUTRANKS NOTHING; RUN THE TOOL.** `nextAction`
    sent iter170 at "condensing the stub layer, the largest ungated work left" on 22 stubs; the
    checker says 29, and this file's header had **measured that job and withdrawn it** at iter166. The
    stub layer has **three** spellings and iter169 matched `MOVED to` case-sensitively, so seven
    italic `*Moved to ...*` stubs read as live bodies. **When two orientation documents disagree, the
    one with a re-runnable script wins — and neither beats running it.**
  * **A CONSTANT COPIED INTO PROSE IS A MIRROR, AND MIRROR DRIFT NEEDS A CHECK.** iter138 lowered the
    markdown constant to 2.4; the preamble kept iter129's blend **average** as undated fact for 32
    iterations, in the file you must read before writing a probe. iter170 used it and computed
    **4,674 B of headroom against a true 594** — 8x, on the number deciding whether the file can take
    another note. `check_calibration` guards the DEFINITION; a prose copy is a sentence. **Ask of any
    constant: who else states it, and what fails when it moves?** Guard: `check_prose_constants`.
  * **THE UNGUARDED BLOCK IS THE ONE OUTSIDE THE STRUCTURE THE CHECKS PARSE** — every guard splits
    this file on `## `, so the 7,101 B preamble was invisible to both the rotation engine and the stub
    pairing. Moving 15 bullets into two `## ` stubs brought them under `check_method_notes_stubs`
    **with no checker change**: coverage extended by reshaping content, not the guard.
  * **BUT IT WAS NOT FAT, AND THE PREDICTION WAS WRONG TWICE BEFORE BEING MEASURED.** Both rotations
    netted almost nothing — **-285 B**, then **-37 B** — because a stub costs what its headlines cost
    and these were already headline-dense one-liners. **iter166 was right, iter169 wrong: rotation
    frees space for NARRATIVE sections, never for a terse list.** All three levers here are spent, so
    the only remedy left is **write less** — a stub is an INDEX saying "open the archive", not a
    summary reproducing it.
  * **A HARNESS CARRIES THE DEFECT IT PLANTS, SO IT CANNOT BE SWEPT AS PROSE.** Exempt it via an
    **existing declared list** (`HARNESSES`, already paired with the runner), never a `mutate-*` glob
    a future document could hide behind. And **a partial mutation grades the CHECK broken**: two
    hand-listed anchors both `count == 1`, a third ratio in the check's own comment, tree healthy,
    MISSED. Sweep, and assert none survives.

## A build failure that scores GREEN, which is worse than scoring CAUGHT (iter173)

  * **A BUILD-FAILURE DETECTOR KEYED ON `error CS` IS BLIND TO THIS REPO.** Max-strict analyzers
    mean most mutations die on `error S1172` / `IDE0059` / `S1481`, and the banner reads
    `Build FAILED.`, not `Build failed` — two independent misses, both silent. iter158 warned that
    build-failed READS LIKE caught; landing on **GREEN is worse**, because a test that was never
    invoked scores as a test that had no objection, and the harness then certifies the mutation as
    undetectable. Match `error [A-Z]+\d+` case-insensitively, treat a non-zero exit with no parsed
    failure as BUILD-FAILED, and **carry a cell that MUST report BUILD-FAILED** so the detector is
    proven to fire rather than assumed to (orphan a local; the analyzers do the rest).
  * **DELETING A FAILURE PATH ORPHANS ITS VARIABLES — MUTATE THE CALL SITE INSTEAD.** Removing an
    `errors.Add(...)` left `owner` and the `errors` parameter unused and therefore unbuildable.
    Passing `[]` at the one call site expresses the same "collect but never fail" semantics and
    compiles, which is what makes it evidence.

## A zero-test run also scores GREEN, and a racy cancellation test means the fix is short (iter175)

  * **iter173 CLOSED ONE DOOR TO "NEVER INVOKED READS AS NO OBJECTION"; THERE IS A SECOND.** This
    iteration's harness carried `--nologo`, which `nextAction` warns **runs zero tests and exits 1**
    (iter172). Every cell came back `0 failed of 0` and the grader scored all five GREEN — so it
    reported the live defect it had just re-planted as **undetectable**, with a build-failure detector
    that worked perfectly. **Ask "did it run?" before "did it object?": grade against the POPULATION,
    not the failure count.** `CLASS_SIZE = 12`, a total below it is its own `NO-TESTS` verdict, and
    both floors sit in `grade()` where every cell must pass them. The generalisation of iter163's
    `EXPECTED_AT_LEAST` and iter162's vacuity rule: **a count of zero problems is only evidence once
    the denominator is asserted.**
  * **A CANCELLATION TEST THAT CAN ONLY BE MADE DETERMINISTIC BY A DELEGATE IS TELLING YOU THE FIX IS
    INCOMPLETE.** iter174 dodged the in-flight-vs-next-turn race with an injectable renderer; the
    reply pass has no such seam, and the race looked like a test problem. It was a **code** problem:
    a loop-top `IsCancellationRequested` guard leaves the window where the token trips the request
    itself, so the honest fix is that guard **plus** `catch (OperationCanceledException) when
    (cancellationToken.IsCancellationRequested)` at every await that writes. With both, the two paths
    return the same accumulated result and the race stops mattering — the WireMock
    `WithCallback(_ => cts.Cancel())` seam then needs no delegate. **Scope the `when` clause to the
    token**, or the same catch starts swallowing client timeouts, which arrive as `TaskCanceledException`
    with the token untouched.

## The write itself was the unguarded seam, plus two harness bugs of mine (iter176)

  * **EVERY ACCUMULATOR IN THIS PRODUCT FUNNELS INTO ONE `File.WriteAllText`, WHICH TRUNCATES THE LIVE
    FILE BEFORE THE FIRST NEW BYTE LANDS.** iters 174-175 asked that question of aborts and of
    cancellation; under both sits the primitive, where `FileMode.Create` means a killed save leaves
    `docs/wiki/_meta/state.json` half-written - losing page ids EARLIER runs earned. **Ask of any durable
    write: is the live file the write target?** Temp sibling -> flush -> one rename; the sibling must be a
    sibling: a rename is atomic within one volume only.
  * **IN C# THE PROSE AND THE CODE SHARE ONE FILE, as iter125 found for YAML templates.** A doc comment naming a
    Confluence read method took `RemoteBodyReadTests`' §9.1 scan from 5 files to 6: it greps
    `src/` text and cannot tell a mention from a call. The scan is right to be
    blunt, so reword the comment - **inflating a tripwire's count to fit prose is how the next real
    call site passes silently.**
  * **A "REVERT THE FIX" CELL SHOULD BE `git show HEAD:<file>`, NEVER A HAND-WRITTEN INVERSE.** Mine
    replaced the new write block with the old one-liner and scored BUILD-FAILED - it orphaned
    `temporary` and the `System.Text` using, both errors here (iter173). HEAD is the pre-fix file
    exactly and builds by construction; the no-op guard is "HEAD differs from the working tree".
  * **A GREEN RUN MEANS TWO DIFFERENT THINGS AND THEY MUST NOT SHARE A WORD.** I predicted MISSED for
    the unguarded-flush cell; the grader printed GREEN because GREEN is all it could print. Green on a
    mutated tree is MISSED, green on a control is GREEN, and **a predicted verdict your grader cannot
    emit is a bug in the grader that costs you the cell.** (Recorded gap: `flushToDisk` is unobservable in-process,
    so nothing guards it.)
  * **RUN THE FULL SUITE PER CELL** (7 cells, ~4 min): the copy-instead-of-rename cell was also caught
    by `ProjectScaffolderTests.Scaffold_SecondRun_...` noticing the leaked temp file, a second
    independent net a single-class run hides.
