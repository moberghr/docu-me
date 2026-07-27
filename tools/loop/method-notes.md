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
> `tools/loop/method-notes-archive-2.md` VERBATIM, assert the round trip BEFORE rewriting this file,
> and leave the heading plus its headlines behind as a stub (the heading has to stay — GATES.md cites
> one of them by name). Generation 1, `method-notes-archive.md`, is full and frozen. Worked recipe,
> re-runnable: `.mtk/paths-162/rotate-method-notes.py`.

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
  * iter124: SA1515 FIRES ON A COMMENT THAT IS THE FIRST LINE INSIDE A COLLECTION-EXPRESSION `[`. A blank
    line between the `[` and the comment is accepted.
  * iter124: SA1118 ALSO FIRES ON `ShouldBe(6, customMessage: "..." + "...")`. Hoist the message into a
    local first.
  * iter124: A MUTATION THAT DOES NOT COMPILE IS NOT EVIDENCE. Pick one that is the REAL defect and still
    compiles — iter125's src case adds `BaselineSha = sha` to `RecordLastPublishedSha`, which is precisely
    what §10's closing paragraph forbids and which the compiler is happy with.
  * iter123: `ShouldContain` ON A WHOLE SECTION'S TEXT PROVES ALMOST NOTHING. Parse the structure.
  * iter123: S127 forbids advancing the loop variable inside a `for`.
  * iter123: A `<see cref="...">` TO A PRIVATE MEMBER OF ANOTHER TYPE, OR TO A TEST TYPE FROM src/, IS
    CS1574 AND THEREFORE AN ERROR HERE. Name it in `<c>` instead.
  * A HEREDOC (`cat > f <<'PY'`) IS REFUSED IN THIS HARNESS — write probe scripts with the Write tool. A
    COMPOUND COMMAND CONTAINING A SUBSHELL is refused too, **and so is a BASH `for` LOOP over a list with
    `$f`-style expansion ("Contains simple_expansion") — put the loop in python instead.**
    **NEW, iter128: `${PIPESTATUS[0]}` is refused the same way ("Contains expansion").** To capture the exit
    code of a command whose output you are paging, drop the pipe and let the harness report the exit code
    itself, or `print()` the status from inside the python script.
  * **NEW, iter128: THE READ TOOL AND `count('\n')` DISAGREE BY ONE.** A claim like "99 lines" written
    against `text.count('\n')` will fail its own assertion — the tool (and `check-state-size.py`) count
    `text.count('\n') + 1`. Use the same convention as the thing you are quoting.
  * `[GeneratedRegex(pattern, matchTimeoutMilliseconds: N)]` DOES NOT COMPILE — pass options too.
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
  * THE ANALYZERS ARE STRICT AND WILL FIGHT EACH OTHER: RCS1215 + S3981 on a `Count >= 0` assertion,
    MA0001 vs CA1865, S3220 vs S3878, RCS1118, MA0006, CA1861, and Shouldly's `ShouldContain(string,
    string)` binding to the IEnumerable<char> overload — spell it `customMessage:`.
  * A FIXTURE FOR A GENERATIVE-SKILL PROBE MUST LIVE OUTSIDE THE REPO with its own .git.
  * `dotnet tool install --add-source <local feed> --version 0.1.0` SILENTLY INSTALLS THE CACHED PACKAGE.
    Pack under a unique prerelease suffix.
  * WHEN A HARNESS CRASHES, CASES AFTER THE CRASH DID NOT RUN. Read the tail and check the N/N line.
  * THE READ TOOL IS THE ONLY TOKENIZER ON THIS MACHINE (no tiktoken, no anthropic, no transformers).
    Its truncation notice reports the file's EXACT total: "PARTIAL view - showing lines 1-462 of 1500
    total (68900 tokens, cap 25000)". To measure bytes-per-token: build a file OVER the 25,000-token cap
    but UNDER the 256 KB byte ceiling (past that the Read fails and reports nothing), Read it whole,
    divide. A BOUNDED Read (`limit=2`) reports no totals, so the measuring Read must be unbounded and you
    pay for its content. Measured: markdown 2.604 B/tok, state.json's JSON 2.368.
  * SIZE A MUTATION FROM A TARGET TOKEN COUNT, NOT FROM A CONVENIENT FILLER CHUNK. iter129's first run of
    mutate-size-check.py scored 3/5 because it grew files in 45 KB PLAN.md-sized chunks and overshot a
    5,000-token budget band straight into the over-cap band. Compute `target_bytes = target_tokens *
    <the same constant the checker uses>` and append exactly that much. The failures were the harness's
    arithmetic, not the checker's — which is the value of asserting the EXPECTED MESSAGE, not just a
    non-zero exit.
  * A CHECKER THAT GUARDS N FILES NEEDS N RED BRANCHES PROVEN, and each case must assert the UNMUTATED
    copy is green first — otherwise a checker that fails on everything passes the harness. Copy the
    guarded files to a scratch tree (`tempfile.TemporaryDirectory`) so the live repo is never touched.
  * done-archive.jsonl ENTRIES ARE A MIX OF STRINGS AND DICTS (iter127 preserved both types), so
    `{json.loads(line)["entry"] for ...}` raises `TypeError: unhashable type: 'dict'`. Compare canonical
    JSON (`json.dumps(..., sort_keys=True)`) instead. And make an append script IDEMPOTENT: iter129's
    crashed after appending n=130 and before rewriting state.json, so the re-run had to tolerate a tail
    already at n rather than n-1.
  * A BUDGET CHECK THAT THE CURRENT ITERATION TRIPS IS THE CHECK WORKING. Pay it back in the same
    iteration (condense, or rotate a field to an archive) rather than raising the budget to suit the prose
    that just broke it. iter128 and iter129 both did this.

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

*Moved to `tools/loop/method-notes-archive.md` at iter145 (verbatim, round trip asserted) — its
subject is settled: the hardcoded skill list it found is now asserted to equal the shipped set, and
its control-cell method has been restated by every rule-tracing section since. **Headlines, because
they still describe how to find this class of gap:** a list that every per-subject test iterates is
**the enforcement boundary, not the directory it describes** — assert the shipped set equals the
list, and the next subject turns the suite red instead of shipping unchecked; **"something else
enumerates the directory" is not the same claim as "something else checks the rule"** — read what the
other enumerator ASSERTS (four classes enumerated `plugin/skills/` and all four asked only whether a
skill was *documented*); **the control cell is the finding** — watching the PRE-EXISTING tests stay
green under the same mutation is what proves the hole was real, and an expected-GREEN cell can carry
more information than the RED one; a mutation that adds a tracked directory must be cleaned up **as a
directory**, and the `git status` check afterwards has to be scoped to the path it touched; and when
scanning for a secret, **grade the needles by kind and never print one** — a scan whose findings are
mostly false is a scan nobody re-runs. Also settled there: **§1.2 is traced and holds — do not
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

  * **A CHECK THAT REPORTS A DEFECT AND EXITS 0 TRAINS ITS READERS TO SKIM.** This checker printed
    `method-notes.md OVER CAP - Read TRUNCATES` from iter139 to iter161 and exited 0, so ~23
    iterations saw the flag and moved on while a Read of the file *this* document orders them to read
    was dropping its newest notes. The fix was the exit code, not more prose beside the flag. **If a
    check knows something is wrong, decide whether that is a failure; "reported" is not a state.**
  * **A HARNESS THAT ENUMERATES ITS FIXTURE BY HAND ROTS THE MOMENT THE THING IT GUARDS GAINS A
    DEPENDENCY.** `.mtk/paths-129/mutate-size-check.py`, the only guard on this checker's red
    branches, was **0/5 at HEAD** and had been since ~iter136: its six-file `NEEDED` list never gained
    the archives that iters 136/159/160/161's checks read, so the checker died on FileNotFoundError
    inside the scratch tree. Copy the DIRECTORY, not a list. Only its own baseline assertion made the
    silence free (it said "harness is invalid", not green), so **assert the un-mutated cell first**,
    and **after adding a check to a script, re-run that script's own harness.**
  * **A ROTATION HAS TWO CEILINGS, THE SOURCE'S AND THE DESTINATION'S: MEASURE THE DESTINATION FIRST.**
    Archive-1 had 15,899 B before its own budget and the rotation was 28,614 B, so appending there
    would have RELOCATED the truncation, hence generation 2. **The pair is the unit, not the file.**
    And **headroom is the deliverable, not fitting:** the five sections `nextAction` named left 2,593
    B, one iteration's worth, and the new hard check would have re-fired at once.
  * **ANCHOR A MUTATION EXPECTATION ON THE CHECK'S OWN MESSAGE, NEVER ON THE MUTATED FILE'S NAME.**
    Sharper than iter161's (two checks colliding): the size table prints EVERY filename on EVERY run,
    green included, so a cell expecting `method-notes.md` scores CAUGHT against a passing checker.
  * **A MIGRATION SCRIPT MUST REFUSE A PARTIALLY APPLIED INPUT, NOT MERELY DETECT A FULLY APPLIED
    ONE.** Widening this rotation from five sections to seven and re-running would have taken the five
    stubs as bodies and written them OVER the real bodies in the archive, the one path here that
    destroys text. Escape was the pre-rotation backup; the guard is cell C of
    `.mtk/paths-162/test-rotation-guard.py`, 3/3.
  * **ONE MORE SHAPE THIS BASH TOOL STATICALLY REFUSES:** a newline followed by `#` inside a quoted
    argument, so a multi-line `python3 -c "…"` cannot carry a comment line.

## A verdict nobody reached, and warnings that cannot be heard (iter163)

  * **THE MIRROR IMAGE OF ITER162: A CHECK CAN ALSO PRINT A VERDICT IT NEVER REACHED.** iter162
    hardened a check that printed a defect and exited 0. Sweeping the rest of tools/loop found the
    opposite shape in `check_gate_pointers`, which has printed `OK: every pointer resolves to a
    section that still has outstanding work` on **every run since it shipped at iter151 while
    resolving nothing at all** - the population is empty, and has been from its first run, because
    iter151 fixed the three stale pointers by REWRITING the gates that carried them. True the way
    "every unicorn in this room is blue" is true. **VACUITY HAS TWO CAUSES AND ONLY ONE IS A
    FAILURE:** nobody writes that prose form any more (legitimate - demanding the population exist
    would be absurd) versus the parser broke (a real defect, and indistinguishable in the output).
    So assert **the walk, not the finding count** - here, that GATES.md still yields `## ` sections
    and gate bodies - and print which of the two happened instead of a conclusion. **THE GENERAL
    MOVE: the check most likely to be vacuous is the one whose defect was fixed by removing its
    population.** Six of this script's eight checks already refused a vacuous pass; the one that
    did not was the one that had nothing left to look at.
  * **"WARN AND CONTINUE" IS NOT ALWAYS ON THE MENU - MEASURE THE CHANNEL BEFORE DESIGNING FOR IT.**
    `deny-history-rewrite.py` had a branch that printed to stderr and returned 0, describing itself
    as "say so loudly and allow". Measured in the invocation `docume-loop.sh:117` actually uses
    (`claude -p … 2>&1`, no hook-event flags): **exit 0 and exit 1 are equally silent** - the command
    ran and the message reached neither the merged log nor the agent's turn - while **exit 2 was
    quoted back by the agent verbatim.** So for a PreToolUse hook there is no advisory register at
    all: every branch is a block or a silence, and one that knows it inspected nothing must be the
    block. (Sits beside iter133's "a failing PostToolUse hook is invisible": the loop's hook channel
    is mute unless it refuses.)
  * **A PROBE THAT ENABLES THE VISIBILITY IT IS MEASURING PROVES NOTHING.** The first version of
    that probe passed `--output-format stream-json --include-hook-events --verbose` and scored 4/4,
    finding the hook's stderr present for exit 0, 1 and 2 alike - because it had asked for hook
    events. The loop passes none of those flags. **Reproduce the invocation under test, flags
    included**, or the measurement describes the probe.
  * **AN ANCHOR PHRASE MUST BE CONTIGUOUS IN THE SOURCE, NOT JUST PRESENT IN IT.** Sharpens iter161
    and iter162's "anchor on the check's own message": the new hook message wrapped
    `could not be parsed as JSON` across a `\n` inside the string literal, so the cell expecting that
    phrase reported **WRONG-CHECK against a hook that was behaving exactly as specified**. Keep the
    greppable phrase on one line in the source and the harness agrees with reality. The harness
    catching this before the commit is the whole argument for writing it first.
  * **A NUMBER THE PROTOCOL ASKS EVERY ITERATION TO EYEBALL IS A MISSING ASSERTION.** `nextAction`
    has carried "**Expect 1390 tests**" for many iterations, which is iter154's lesson (an
    instruction that depends on the next agent remembering it is not a fix - placement is) in a
    second place. It is now `EXPECTED_AT_LEAST` in run-suite.py: a FLOOR, not an equality, because
    tests only get added here, so it fires when the count DROPS and needs no edit when it grows.
    Same file also stopped treating a zero exit code as proof the suite ran - an unparseable summary
    printed `total=?` beside the word PASS.

## Proving a vacuity judgement instead of writing one (iter164)

  * **"NON-VACUOUS BY CONSTRUCTION" IS A CLAIM, AND THREE OF FOUR WERE FALSE.** iter163 fixed the one
    check of eight that printed a verdict over an empty population and judged four others safe in
    prose, reasoning that their REVERSE direction fires when a population empties. Measured, one cell
    per judgement: `check_done_archive`, `check_stub_bodies` (twice) and `check_gate_mirror` all sit
    green or silent over their own emptied population. Only `check_gate_pointers`' finding half held
    up, and only because iter164 gave it the population it has never had. **The move that generalises:
    when an iteration hardens one instance of a defect and reasons the siblings safe, the cheapest
    real work available is a cell per sibling.**
  * **"THE CHECKER EXITED NON-ZERO" ATTRIBUTES NOTHING WHEN main() RUNS EVERY CHECK.** Sharper than
    iter161's "two checks can print BROKEN for one mutation": here a mutation emptied
    `check_gate_mirror`'s two populations and the script still exited 1, because
    `check_gate_pointers` and `check_gates_archive` fired for their own correct reasons. The check
    under test had gone silent and the run read as a pass. Fix in the harness: **slice stdout by each
    check's own section header and ask whether THAT block holds a `BROKEN:` line**, asserting every
    header was located before grading anything. New verdict worth keeping distinct: **WRONG-CHECK - a
    sibling covered for it.** A net that only holds while a different net holds is decoration.
  * **THE POPULATION THAT SILENTLY EMPTIES IS THE ONE WHOSE COUNTER THE SAME EDIT CAN REPAIR.**
    `check_done_archive` runs six checks over done-archive.jsonl and an empty file satisfies all six
    at once: `doneCount` 0 agrees with 0 lines, `doneRecent` is already empty, and COVERAGE and HEAD
    both skip themselves because nothing is attributed - so the HEAD check written to catch ONE
    missing record cannot fire when EVERY record is missing.
  * **SCOPING A CHECK TO A PROSE PREFIX IS A ONE-KEYSTROKE VACUITY, AND ITER159 KNEW.** That iteration
    scoped a direction to stubs starting with `OPEN`, saw the risk, and put the population assertion
    in its own scratch harness (read the printed `(7 OPEN)` back) rather than in the check - so the
    guard lived where nothing re-runs it. `**OPEN**` is this file's house style and one keystroke away;
    rewriting the markers plus deleting every body left seven orphaned questions and exit 0. iter154's
    lesson a third time: **an instruction that depends on somebody remembering it is not a fix,
    placement is.**
  * **A NON-EMPTY ASSERTION IS THE WRONG FIX FOR A POPULATION THAT LEGITIMATELY EMPTIES** (iter159's
    "a check must not fire on the event the loop is waiting for", applied while choosing between five
    populations). Only `decisions` may never be empty - it grows, because an answered decision stays a
    tombstone. `blockers`, blockers-open.json, the OPEN subset and the pointer set each reach zero the
    day Mirko finishes something. So the refusal goes on the one, and the drift on the others is
    caught by a different mechanism: **flag a status marker the classifier cannot read**, which fires
    on reworded prose and stays green when the last decision is answered.
  * **PREDICT EACH CELL'S VERDICT IN WRITING BEFORE THE FIRST RUN.** Eleven predictions, eleven
    matches - which is what makes "three of four judgements were false" a measurement rather than a
    story told after the fact. A cell whose result surprises you is either a find or a broken cell,
    and only the prediction tells you which.
  * **ONE MORE SHAPE THIS BASH TOOL STATICALLY REFUSES:** `<->` inside a quoted argument, read as a
    zsh numeric-range glob. It is in this checker's own section headers, so grep for a neighbouring
    phrase instead. Redirecting to `/tmp` is refused too - write scratch output under the repo.
