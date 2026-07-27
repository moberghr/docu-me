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

  * **BEFORE WRITING A TEST FOR A GAP YOU INFERRED, GREP FOR THE ASSERTION, NOT FOR THE TEST CLASS
    YOU EXPECT TO HOLD IT.** iter157 found `ReadmeCliContractTests.Every_option_the_README_hangs_on_a_
    command_exists_on_that_command`, checked `SkillContractTests` for the same check over the three
    shipped `SKILL.md` files, found nothing, and concluded the skills were unguarded. They are not:
    **`CliReferencePageTests.Every_documented_invocation_names_a_real_command_with_real_options`
    sweeps ~132 invocations across the whole consumer-facing tree** — README, `docs/wiki/`, every
    `SKILL.md`, `templates/workflows/*.yml` — and validates each `--option` against the real declared
    set, with a companion anti-vacuity test naming four of those files so the regex cannot go blind.
    A class named after `cli.md` owns a tree-wide sweep. The near-miss was a duplicate test.
  * **THE COROLLARY THAT FOUND THE REAL GAP: ASK WHAT DIMENSION THE EXISTING SWEEP COVERS.** The tree
    is swept for CLI *options* a doc names. It was NOT swept for *config fields* a doc promises:
    `PlanDataContractTests` held the two dead knobs against `configuration.md` alone, so
    `plugin/skills/docs-refresh/SKILL.md:44` could list both dead knobs as inputs an agent reads and
    nothing noticed. Same shape of defect, one dimension over, unguarded. The gap was not "the skills
    are unchecked" but "checked for one kind of claim, not the other".
  * **A DECISION'S SETTLE INSTRUCTIONS ARE A CLAIM ABOUT THE TREE, AND THEY ROT LIKE ANY OTHER.**
    `DeadFields[0].Why`, `state.json` and the archive all said to strike the promise from §6.2 "and
    the skill", singular, for 34 iterations. Measured: six files, two skills. Writing the surface into
    a `Places` array pinned in both directions turns "remember to edit all six" into a failing test —
    the general move when an instruction says *everywhere* and names three of six places.
  * **AN EXCLUSION IS BETTER MADE STRUCTURAL THAN FILTERED.** The inventory walks `docs/wiki` rather
    than `docs`, so the dated records under `docs/plans/` are out of scope by the shape of the walk
    instead of by a `.Where` a later edit can drop. The control case in
    `.mtk/paths-157/mutate-dead-knob-surface.py` proves it (mention a dead knob in an m0 plan record →
    correctly OK-IGNORED), which is iter125's control-case rule applied to a scope decision.

## A mutation that does not compile is not evidence (iter158)

  * **BUILD-FAILED IS NOT CAUGHT, AND IT READS LIKE IT.** iter158's harness renamed
    `DashboardConfig.Title` in the record to prove `ConfigFieldSurfaceTests` catches a config field
    whose docs were left behind. The first run reported the case as failing — but it failed at
    `dotnet build` (`PublishPipeline.cs:104` still read `.Title`), so the test under examination never
    ran at all. In C# a rename inside `src/` is compiler-enforced across every reader, which is exactly
    why the *docs* are the interesting half: **to mutate a rename realistically, patch the record AND
    every reader** (here four: `PublishPipeline`, `DashboardCommand`, `DriftCommand`, and one test), so
    the tree compiles and the only thing stale is the prose. Have the harness report `BUILD-FAILED` as
    its own verdict, distinct from `CAUGHT` and `MISSED` — a harness that folds the two together will
    tell you a test works when it was never invoked.
  * **ASSERT THE RESTORE, NOT JUST THE RUN.** Each case restores in a `finally` and the harness hashes
    every touched file before the first mutation and after the last, printing `IDENTICAL` or
    `DIRTY — RESTORE FAILED`, then rebuilds at restored HEAD. Five files were being rewritten per case;
    without the digest, a crash between patch and restore leaves a mutation in the tree that the next
    `git status` blames on the increment. (iter154's lesson, applied ahead of the failure.)
  * **THE DIMENSION QUESTION IS RE-ASKABLE, AND MOST ANSWERS COME BACK CLEAN.** iter158 measured four
    of the dimensions iter157 listed before writing anything: `DOCUME_*` env var names (two real, and
    the two apparent phantoms are GitHub secret names, one of them documented at its point of use
    inside the template comment), Confluence label names, and the `§`-numbers docs cite at each other
    (`§0` resolves to CLAUDE.md's critical rules, not to a missing PLAN.md section). Only the config
    fields had a gap. **Probing four dimensions to ship one test is the expected ratio** — the cost of
    a probe is a script, the cost of a test guarding a dimension that was never at risk is permanent.

## The bookkeeping file's own invariants were the last unguarded seam (iter159)

  * **WHEN ONE INSTANCE OF A PATTERN GETS A CHECKER, ENUMERATE THE OTHER INSTANCES.** `state.json` has
    three stub-plus-archived-body splits — `gates`, `blockers`, `decisions` — and they fail identically:
    a key present in one place and invisible in the other. `gates` got `check_gate_mirror` at iter144
    only after `paste-rule-8-2a` sat unmirrored for **69 iterations**. The sibling fields kept the same
    exposure for another 15. The cheapest way to find real work is not a new dimension: it is asking
    which siblings of an already-proven defect were never checked. The prose even prescribed the
    three-place edit (`blockers._archive`: "delete the key from both ... append a one-line verdict"),
    which is the tell — **a multi-place edit written out in prose and enforced by nothing.**
  * **A CHECK MUST NOT FIRE ON THE EVENT THE LOOP IS WAITING FOR.** `decisions-archive.json`'s own
    `authoritative` field says state.json wins and the archive may be stale, so requiring a body for an
    *answered* decision would turn Mirko's reply into a red checker. Direction (3) is therefore scoped
    to stubs still starting with `OPEN`, and the harness carries **two must-stay-GREEN cases**
    (`answered-body-left-stale`, `answered-body-removed`) alongside the five must-be-CAUGHT ones. When a
    file documents its own permitted staleness, that sentence is a specification of what not to assert.
  * **SCOPING A CHECK CREATES A VACUITY RISK, SO ASSERT THE POPULATION.** Narrowing direction (3) to
    OPEN stubs means a broken detector inspects nothing and still prints OK. The harness reads the
    reported count back (`10 decision stubs (7 OPEN)`) and fails on `(0 OPEN)` before running any
    mutation — iter158's anti-vacuity rule, applied to a filter rather than to a file-tree walk.
  * **`git checkout --` IS THE RIGHT RESTORE WHEN THE MUTATED FILES ARE CLEAN AT HEAD.** No saved copies,
    no re-serialization drift to undo, exact by construction, and it works from a `finally` after a crash.
    The harness still asserts the digest per case; it prints the clean `git status` of the three files
    first, because that precondition is what makes the restore exact. **Not usable for an increment that
    has already edited those files** — check `git status` before reaching for it.


## Calibrating the size checker (moved verbatim from `state.json -> readMe` at iter160)

The paragraph below stood at the top of state.json from iter129 to iter160. It was moved here under
the rule `methodNotes.appendHere` states - durable method advice belongs in this file, not in
state.json - when iter160's increment put state.json over its Read budget and had to pay it back.
Its one operative sentence (truncation is not silent) stayed behind in `readMe`.

> CORRECTED AT ITER129, because a later method depends on it: TRUNCATION IS NOT SILENT. The tool
> prints "[Truncated: PARTIAL view - showing lines 1-462 of 1500 total (68900 tokens, cap 25000)]".
> iter128 called truncation "the worse failure because it looks like success"; the real risk is an
> agent SKIMMING PAST an explicit notice, not the tool hiding anything. That notice is also the ONLY
> tokenizer on this machine (no tiktoken, no anthropic, no transformers), and it is how iter129
> calibrated the size checker: build a file over the cap but under 256 KB, Read it whole, divide
> bytes by the reported total. Markdown measured 2.604 B/tok, this file's JSON 2.368.

## Mutation harnesses, iter160

* **WHEN AN EARLIER ITERATION ENUMERATED A FAMILY AND CLOSED IT, CHECK WHETHER THE ENUMERATION
  SKIPPED A LIFECYCLE HALF.** iter159 swept state.json's stub-plus-archived-body splits, counted
  three (`gates`, `blockers`, `decisions`), checked all three and declared the seam spent. Its sweep
  was correct and complete *for the half it looked at* - stub to **OPEN** body. The fourth pair is
  on the **settled** side (`blockersArchive.settled` -> blockers-archive.jsonl, twinned by
  `spikesArchive.settled` -> spikes-archive.json), and it was already broken: five tombstones, four
  bodies. The generalisation to carry forward is open vs settled, added vs removed, live vs
  archived - a closed enumeration is only closed for the lifecycle stage it enumerated.
* **ASSERT BEFORE YOU MUTATE, NOT AFTER - AND THE GUARD WILL CATCH YOU, NOT JUST A FUTURE READER.**
  iter160's migration refused its own first run: it expected the recovered blocker body to be 1806
  characters, which was the JSON-**escaped** length printed during the investigation; the raw string
  is 1771. Because the length check ran before the write, blockers-archive.jsonl was untouched and
  the fix was a one-line constant. Had the assertion run after the rewrite, the archive would have
  been in an unknown state at the moment the script reported failure.
* **`git checkout --` IS UNUSABLE ONCE YOUR OWN INCREMENT HAS DIRTIED THE MUTATED FILES** (the
  iter159 note, hit for real at iter160). `.mtk/paths-160/mutate-settled-bodies.py` snapshots each
  touched file as BYTES before the first mutation, restores from that snapshot after every case, and
  digests the whole touched set before and after to prove nothing leaked.

## Two checks can print BROKEN for one mutation (iter161)

  * **ANCHOR A HARNESS EXPECTATION ON THE CHECK'S OWN MESSAGE TEXT, NOT ON THE MUTATED KEY'S NAME.**
    A refinement of iter158's "a non-zero exit is not a catch". check-state-size.py's main() runs
    EVERY check and collects problems BEFORE it evaluates any FAIL banner, so one mutation
    legitimately trips two checks and both print their BROKEN line. iter161's "remove a gate from
    `gates` and leave its archived body orphaned" case expected the bare string
    `'settings-corrections'` - which check_gate_mirror also prints, for its own correct reason - so it
    reported CAUGHT while proving nothing about check_gates_archive. Tightened to
    "gates-archive.json holds a body 'settings-corrections'". Verdicts worth keeping distinct: CAUGHT,
    MISSED, WRONG-CHECK, CRASH, and a separate GREEN/REGRESSION pair for the must-stay-green control.
  * **PREFER A DECLARED EXEMPTION LIST TO A KEY-SHAPE REGEX.** check_gates_archive has to tell a gate
    mirror from a settled-section body inside one flat JSON object. A kebab-case regex would have
    classified a MIS-KEYED gate mirror - the exact defect the check exists to catch - as "not a gate"
    and exempted it. So the three non-gate keys are named in GATES_ARCHIVE_NON_GATE_KEYS with a reason
    each, and the declaration is itself checked BOTH ways: a declared name absent from the archive is
    dead weight, and a declared name that is ALSO a live gate would exempt a real mirror forever. Both
    are mutation cases in `.mtk/paths-161/mutate-gates-archive.py`.

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
