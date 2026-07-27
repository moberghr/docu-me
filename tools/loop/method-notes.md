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
> Worked recipe, re-runnable: `.mtk/paths-162/rotate-method-notes.py`, with
> `.mtk/paths-166/rotate-iter166.py` as the one-section form that also shows how to retarget it at a
> new generation. **A generation 4 must be declared in BOTH `ARCHIVE_FILES` and
> `METHOD_NOTES_GENERATIONS` in the same change that creates it — that is now a failing check, not
> advice.**
>
> **THE OTHER REMEDY THIS HEADER USED TO NAME IS MEASURED AND WITHDRAWN (iter166).** It told you the
> next budget lever was condensing the stub layer, since 24 pointers averaging 1,173 B are summaries
> rather than pointers. Counted (`python3 .mtk/paths-166/measure-stub-boilerplate.py`): of 26,556 B
> of stub bodies, **only 1,829 B is repeated phrasing** — the destination path, "verbatim and
> round-trip asserted", "Nothing was discarded.", "**The headlines:**". Stripping every one of them
> nets about 1.1 KB, less than the section documenting the work would cost, and the other ~24.7 KB is
> per-stub headline CONTENT that cannot go without discarding lessons. **So condensing is not a
> budget lever; rotating into the current generation is.** The stub layer is the read path — it is
> what makes this file worth reading whole — and it is not the fat.

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

  * **MOVED to `tools/loop/method-notes-archive-2.md` at iter165**, verbatim and round-trip asserted,
    to leave this file the headroom one rotation did not buy. Nothing was discarded, and all three
    mechanisms it records are committed and re-proven green by `.mtk/paths-163/run-all.py` (4/4).
    **The headlines:** **a check can print a verdict it never reached** - the mirror image of iter162,
    and `check_gate_pointers` printed "every pointer resolves" over an empty population for 12
    iterations, so **assert the WALK, not the finding count**, and note that **the check most likely
    to be vacuous is the one whose defect was fixed by removing its population**; **"warn and
    continue" is not always on the menu - measure the channel first**, because in the driver's
    invocation a PreToolUse hook's stderr is silent at exit 0 AND exit 1 and only exit 2 is quoted
    back, so a branch that knows it inspected nothing must block; **a probe that enables the
    visibility it is measuring proves nothing** (reproduce the invocation under test, flags included);
    **an anchor phrase must be CONTIGUOUS in the source**, or a wrapped string literal makes a correct
    hook score WRONG-CHECK; and **a number the protocol asks every iteration to eyeball is a missing
    assertion** - "expect 1390 tests" is now `EXPECTED_AT_LEAST`, a floor that fires when the count
    drops and needs no edit when it grows.
## Proving a vacuity judgement instead of writing one (iter164)

  * **MOVED to `tools/loop/method-notes-archive-3.md` at iter166**, verbatim and round-trip asserted,
    into the generation this rotation opened. Nothing was discarded, and the seam it records is
    closed: iter165 proved the four declared refusals fire and fixed the two that skipped their
    findings. **The headlines:** **"non-vacuous by construction" is a claim, and three of four were
    false** - when an iteration hardens one instance of a defect and reasons the siblings safe, the
    cheapest real work available is a cell per sibling; **"the checker exited non-zero" attributes
    nothing when `main()` runs every check**, so slice stdout by each check's own section header and
    ask whether THAT block holds a `BROKEN:` line, which yields the verdict **WRONG-CHECK - a sibling
    covered for it**; **the population that silently empties is the one whose counter the same edit
    can repair**; **scoping a check to a prose prefix is a one-keystroke vacuity**, and an instruction
    that depends on somebody remembering it is not a fix, placement is; **a non-empty assertion is the
    wrong fix for a population that legitimately empties** - put the refusal on the one population
    that may never empty and catch the rest by flagging a status marker the classifier cannot read;
    **predict each cell's verdict in writing before the first run**, which is what makes a count a
    measurement rather than a story told after the fact; and **this Bash tool statically refuses
    `<->` inside a quoted argument** (a zsh numeric-range glob) and refuses redirecting to `/tmp`.
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

  * **MOVED to `tools/loop/method-notes-archive-3.md` at iter167**, verbatim and round-trip asserted.
    Nothing was discarded, and the seam it records is closed: 24/24 stubs resolved and
    `check_method_notes_stubs` (check #9) now stands over them. **The headlines:** **a "verbatim,
    round-trip asserted" claim is asserted ONCE, by the script doing the rotating, and then never
    again**, so pair the stub with its body in a check - and when an earlier iteration enumerates
    "every stub/body split" scoped to one file, **the sibling in another file is the thing to look
    for**; **a regex that under-matches does not miss findings, it FABRICATES them** - `[Mm]oved`
    missed the all-caps `**MOVED to`, reclassified 18 stubs as live bodies and reported their good
    archived bodies as 18 orphans, confident and specific and wrong - so **an enumeration keyed on a
    phrase must be checked against an INDEPENDENT count of the same population before its findings
    are believed**, because **a partial match is worse than no match** (a vacuity refusal fires on
    zero and stays silent on 6-of-24, so the floor must be a hard minimum); **the fourth direction of
    a pairing check is therefore "a stub that does not parse is NOT a live body"**, detected
    structurally, or the classifier's own blind spot is unobservable from inside the check; **a
    remedy instruction goes stale like any other claim** - "condense the stub layer" was withdrawn
    after measuring that only 1,829 B of 26,556 B is repeated phrasing, so **measure what the bytes
    ARE, not just how many there are**; and **a scratch probe that restates a committed declaration
    goes stale inside one iteration** (this one hardcoded the generations and creating generation 3
    falsified it the same session - import the declaration instead).

## The probe that reproduced iter166's bug three times while implementing its lesson (iter167)

  * **THE INSTRUMENT GUARD BELONGS INSIDE THE CHECK AS A FAILING FACT, NOT IN ITS DOCSTRING.** iter166's
    lesson was "an enumeration keyed on a phrase must be checked against an INDEPENDENT count of the same
    population". iter167 set out to apply it, wrote two independent extractions on purpose - and then
    shipped the same class of bug **three times in a row** anyway: 43 fabricated findings, then 4, then 2.
    Every batch was confident, specific and wrong, and every one was caught ONLY by the two extractions
    disagreeing. So in `check_citation_resolution` the agreement is **fact (1), and a disagreement FAILS
    the check outright**: when the instrument is wrong the other three facts are noise, and a green from
    them is worse than no check at all. **Knowing the lesson does not protect you from the bug; wiring the
    counter-measure into the thing you ship does.**
  * **THE THREE BUGS, BECAUSE THE SHAPES RECUR.** (a) **Stripping punctuation from BOTH ends of a token
    eats a leading dot**: every dot-rooted path lost it, and 43 perfectly good scratch-probe paths
    reported MISSING. (b) **A sentence-ending period is INSIDE the segment class**, so a citation that
    ends a sentence keeps the period, fails the extension test, and is dropped *silently* - the opposite
    failure mode, an under-count nobody would have queried. (c) **Scanning a serialisation is not
    scanning the content**: reading `tools/loop/state.json` as raw text made the newline ESCAPE inside a
    string literal part of the next token, gluing a stray `n` to the front of four paths. **Parse the
    JSON and walk its string values.** All three normalisations now live in ONE function both
    extractions route through, because a rule applied in two places is a disagreement waiting to happen.
  * **THE CHECK PUTS ONE SMALL DISCIPLINE ON PROSE, AND IT CAUGHT THIS SECTION FIRST.** An illustrative
    fake path in a sentence is indistinguishable from an instruction to open a file, so the first run
    after this section was written failed on two invented example paths in the bullet above. **Do not
    write specimen paths; name the real file or describe the shape in words.**
  * **A BLUNT "EVERY CITATION MUST RESOLVE" RULE WOULD HAVE REPORTED THE TREE'S CLEAREST DOCUMENTATION AS
    FOUR DEFECTS.** Three of the four non-resolving citations are absences the orientation layer is
    deliberately *telling* you about: `tools/hooks/format-on-edit.py` is cited to say DO NOT RECREATE IT,
    `cases/mermaid.md` is cited as deliberately absent, `_meta/feedback/inbox/` is a CONSUMER-repo path.
    **A pointer to a thing that is meant not to exist is not a broken pointer.** Hence
    `CITATION_KNOWN_ABSENT`, declared with a reason each, and checked in BOTH directions - a declaration
    that starts resolving is stale (and for format-on-edit.py that direction *is* the event worth
    catching), and a declaration nobody cites any more exempts nothing.
  * **SCOPE THAT IS DECLINED MUST BE COUNTED, OR IT READS AS COVERAGE.** 74 bare filenames with no
    directory (`docs-drift-pr.yml`, `PublishGuard.cs`) are deliberately NOT checked: resolving them
    needs a tree search, and a search that finds *a* file named that is exactly how a probe fabricates.
    The measurement prints the number it is not checking. Same for single-segment directory refs.
  * **A MEASURED FINDING WORTH MORE THAN THE CHECK: 26 OF THE 81 RESOLVING CITATIONS POINT INTO
    GITIGNORED SCRATCH.** `.mtk/` is untracked (`.gitignore:7`), and `nextAction` calls
    `.mtk/paths-163/run-all.py` "THE ONE COMMAND THAT RE-CHECKS EVERYTHING ITERS 162-166 TOUCHED". It
    resolves on this machine and on no other: a clone, or anyone who cleans scratch, loses the loop's own
    regression harness with no error message. **The check cannot enforce this without failing today**, and
    iter162's rule forbids a printed defect that exits 0 - so it is recorded here and in `nextAction`
    rather than added as a flag that trains its reader to skim.
