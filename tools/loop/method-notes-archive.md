# Method notes — archive

> Sections rotated out of `tools/loop/method-notes.md` when that file crossed its 20,000-token
> budget. Verbatim, nothing dropped, each with a pointer left behind in the live file. This is
> history you open on purpose; `method-notes.md` is the one you read before writing code.

## Estimating tokens from bytes, and the file nothing reads (iter138)

  * **"ROUNDED DOWN, THEREFORE CONSERVATIVE" IS NOT A SAFETY ARGUMENT — CHECK THE DIRECTION.**
    `check-state-size.py` estimates `tokens = bytes / constant`, and its docstring claimed that
    rounding the constant DOWN from the measurement made the estimate over-state tokens, so the
    check "trips slightly early rather than slightly late". That holds only against the file the
    constant was measured on. A DENSER file has a LOWER B/tok ratio, and the moment a real file's
    ratio falls under the constant the estimate UNDER-states it and the check trips LATE. Measured
    counterexample: markdown was calibrated at 2.604 and the constant set to 2.5, but
    `handoff-archive.md` is **2.4464**, so its 32,783 real tokens were estimated at 32,080. **Pin a
    constant at or below the DENSEST file ever measured of its kind, never at the average.**
  * **A BLEND CANNOT CALIBRATE THE FILES IT AVERAGES.** iter129 measured a CONCATENATION of seven
    repo files and got one number. Measured individually at iter138, real files straddle it:
    PLAN.md **2.6459** (sparser, so the constant was honest there), `handoff-archive.md` **2.4464**
    (denser, so it was not). Identifier-heavy prose — type names, XML fragments, paths — tokenizes
    worse than spec prose. `.mtk/paths-138/calibrate-file.py <file>` narrows iter129's recipe to one
    file by repeating it past the cap; repetition is sound because BPE is context-free per token, so
    N copies carry ~N times the tokens and the ratio survives.
  * **MY FIRST THEORY WAS THE LINE-NUMBER PREFIX, AND THE DATA KILLED IT.** The Read tool prefixes
    every line, so more lines per byte should mean more tokens per byte. iter129's `calib.md`
    (~120 B/line) has far MORE lines per byte than `handoff-archive.md` (341 B/line), yet measured a
    HIGHER ratio — the opposite of the prediction. Prose density dominates; do not attribute a ratio
    difference to line structure without testing it.
  * **TURN A ONE-OFF Read MEASUREMENT INTO DATA PLUS A CHECK, OR IT ROTS AS PROSE.** iter129's
    measurement lived only in a docstring, so a constant that contradicted it could not be noticed
    for nine iterations. The measurements are now a `MEASURED` table and `check_calibration()` fails
    when any constant exceeds the densest recorded ratio. Proven 8/8 + 1 control by
    `.mtk/paths-138/mutate-calibration-guard.py`.
  * **TWO MUTATIONS THAT DIFFER BY ONE DIGIT PRODUCE FILES OF IDENTICAL SIZE, AND importlib CACHES
    ON (mtime, size).** The harness named its scratch modules after `len(label)`; two labels tied, so
    two mutations wrote the same filename in the same second and the second import was served the
    FIRST one's `.pyc` — a json mutation reporting a markdown failure. Give scratch modules an
    explicit unique index AND set `sys.dont_write_bytecode = True`. Same family as iter137's
    lookup-resolves-every-key-to-itself: a wrong answer that looks like a working one.
  * **A PURE CHECK CAN BE MUTATION-TESTED BY IMPORT, WITHOUT A SCRATCH REPO.** `check_calibration`
    reads only module-level constants and the module does no IO at import time, so
    `spec_from_file_location` + `exec_module` on a mutated copy drives it directly. Pair that with
    ONE subprocess case against a mirrored tree to prove the failure reaches the **exit code** —
    unit-level red branches say nothing about whether `main()` actually returns 1.
  * **`tools/loop/handoff-archive.md` IS UNREADABLE PAST LINE 152 OF 235, AND NOTHING READS IT.**
    80,200 B / **32,783 tok** against the 25,000 cap, measured. The lead that opened iter138 asked
    whether any skill or protocol step reads it whole: **nothing does.** The only reference in the
    whole tree is `.claude/references/decisions.md:92`, citing it as evidence for the S1/Dapplo
    decision — and that content sits at **line 82**, inside the readable half, so the citation is
    sound. What IS past the cut (lines 153-235) is the 22 pending decision-log entries, the sandbox
    verification items, and a `BUILD/TEST COMMANDS` block whose advice is now actively STALE
    (line 211 says "python3 is denied but node is allowed" — the reverse of today's harness). Its
    durable content was migrated into THIS file long ago, which is why nobody noticed. Do not
    rehabilitate it; read it with `offset`/`limit` or grep if ever needed.
  * **EDITING `tools/loop/docume-loop.sh` CHANGES NOTHING UNTIL THE DRIVER IS RESTARTED, AND THE
    DRIVER HAS NOT BEEN RESTARTED SINCE 2026-07-24 14:38:30.** This is the one file in the tree an
    iteration can edit but cannot make take effect: bash parses the `while` loop once, and that
    process has been inside it ever since. Measured at iter139 — the driver predates its own newest
    commit by **56.6 h**, and two commits' worth of changes (`c6fbf1e`, and `7de6a86`, which was
    iter137's whole deliverable) have never executed. Proof does not depend on reading the process's
    memory: the driver's own output still matches the FIRST committed version's format strings and
    not the working tree's — log line `Iteration 164: fresh session <sid>` against today's
    `Iteration $next_iter (pass $pass_n): fresh session $sid [model: ...]`, and log filename
    `iter-0163-<ts>.log` against today's `$iter_label-pass<NNNN>-<ts>.log`. `current_model`,
    `state_iteration` and `pass_n` are all absent from what is running. **So `tools/loop/MODEL` is
    inert too** (it reads `claude-opus-5`; the sessions are opus-5 by CLI default, so the intent is
    met by coincidence, not by the file). **WHAT IS STILL LIVE, because the running driver re-reads
    it by PATH every pass:** `ITERATION-PROMPT.md` (`cat "$PROMPT_FILE"`) and `loop-settings.json`
    (`--settings "$SETTINGS"`) — which is why the pending force-push-hook paste WOULD take effect
    immediately, with no restart. There is no read-past-offset hazard: nothing follows `done`.
    Re-runnable in ~2 s: `.mtk/paths-139/probe-driver-version-drift.py`, 8/8, and it extracts both
    candidate formats from `git show` rather than retyping them. **BEFORE YOU "FIX" THE DRIVER,
    CHECK WHETHER YOUR FIX IS ALREADY THERE AND SIMPLY NOT RUNNING.**
  * **AN UNEXPLAINED DETAIL FROM THAT SAME MEASUREMENT, recorded so it is not rediscovered as new:**
    the committed driver does `exec caffeinate -is "$0" "$@"`, which should REPLACE the shell — yet
    `ps` shows pid 62335 as `bash ./tools/loop/docume-loop.sh` with `caffeinate` as its **child**
    (62340), the mirror image of what `exec` produces. Verified against raw unparsed `ps` output
    (`.mtk/paths-139/check-raw-ps.py`), so it is not a parsing artefact. The running image therefore
    matches `bb43d9b` on its log formats but contradicts it on this line, meaning what is executing
    is a Jul-24 working-tree state that no commit captures. It changes nothing about the conclusion
    above. Do not assume the running driver equals any particular commit — assume only that it is
    old, and measure its behaviour rather than reading a file.
  * **A `PostToolUse`-shaped blind spot has a sibling in the driver: `notify()` discards both streams
    AND the exit code (`>/dev/null 2>&1 || true`).** Measured at iter139: the `osascript` call itself
    **exits 0** and a deliberately broken script exits 1 through the same path, so the command works
    — unlike the dead `PushNotification` channel. But **delivery is unprovable from inside the loop**:
    Notification Center's DB (`~/Library/Group Containers/group.com.apple.usernoted/db2/db`) and the
    Focus assertions file are both **TCC-blocked** to this process, so "exit 0" is the strongest
    claim available and it is NOT "Mirko saw it". Do not upgrade one to the other.

## CI fidelity: the host the suite is verified on (iter134)

  * **`GITHUB_ACTIONS=true` CHANGES WHAT THE CLI PRINTS, AND `CI=true` DOES NOT.** Spectre.Console
    colourises when it detects a CI host, so on a runner `docume` emits
    `\x1b[38;5;2m4\x1b[0m comment(s) on …` and any assertion reading the text across a colour boundary
    fails. This is why 133 iterations of green local runs said nothing about ci.yml: no developer
    machine sets `GITHUB_ACTIONS`. **To verify a claim about CI, set the variable the runner sets** —
    `CI=true` alone is not a runner, and it was the marker that did nothing here.
  * **NEUTRALISE IT WITH `NO_COLOR`, MEASURED: `TERM=dumb` DOES NOT WORK,** and an **EMPTY `NO_COLOR`
    STILL COUNTS AS SET** (Spectre keys on presence, not on the NO_COLOR spec's non-empty rule) — so
    `NO_COLOR=""` cannot serve as the "colour back on" control cell. `ProcessStartInfo.Environment`
    can only set a variable, never unset one, so a harness that pins `NO_COLOR` for its children has
    no way to hand one of them colour again.
  * **WHEN THE ASSERTION'S "EXPECTED" IS VISIBLY PRESENT IN THE "ACTUAL", STOP STRIPPING ANSI AND
    PRINT `repr()`.** iter134's probe scrubbed escapes before printing, so Shouldly's
    "should contain … but did not" sat directly above stdout that plainly contained the string, and
    the cause stayed invisible for an hour. The same rule as `modelUsage` and `--include-hook-events`:
    ask the layer for raw bytes rather than a rendering.
  * **A "CLEAN CLONE" CHECK HAS TWO SHAPES AND ONLY ONE IS WHAT GITHUB DOES.** `git clone` gives
    tracked files with full history; `actions/checkout@v4` gives `--depth 1`. Run both
    (`.mtk/paths-134/probe-ci-clean-clone.py`): the full cell catches a dependency on a gitignored
    file, the shallow cell catches a test that walks history. Clone from `file://<repo>` so the clone
    is of LOCAL HEAD — cloning from `origin` would test iter74, since the loop has never pushed.
  * **PIN THE ENVIRONMENT A CHILD PROCESS INHERITS, NOT JUST THE ONE YOU PASS IT.**
    `tests/…/Cli/DocumeCli.cs` already pinned the credential placeholders on the reasoning that "a
    suite whose output depends on whether the developer exported a token is a suite that passes
    locally only"; `GITHUB_ACTIONS` was the same hazard one layer out, and the fix belonged in the
    same three lines. When a test must be immune to the host, set the hostile variable on the CHILD
    inside the test — then it fails on a laptop too, instead of only on the runner.
  * **NEW, iter135: A SKIP IS A COVERAGE HOLE THAT REPORTS ITSELF AS SUCCESS, AND `Assert.SkipUnless`
    IS HOW ONE GETS INSTALLED ON PURPOSE.** The seven renderer tests skip when
    `node_modules/beautiful-mermaid` is absent, which is the right bargain for a first clone and the
    wrong one for CI — a runner without it ran 1368 of 1375 tests and reported green. **When a test
    decides for itself whether it can run, something has to assert that the environment it needs
    exists where it matters.** Read the summary line's `skipped:` count, not just `failed:`.
  * **NEW, iter135: WHEN THE INVARIANT IS ABOUT THE RUNNER, ASSERT THE WORKFLOW, NOT THE RUNNER.** A
    guard written as "if `GITHUB_ACTIONS` then require the dependency" only fires where nobody runs it
    before pushing — iter134's own lesson, one turn later. `CiMermaidToolchainTests` parses ci.yml and
    fails on a laptop instead: every job that runs `dotnet test` must run `npm ci` first, with a pinned
    `setup-node`, and the package must be the one the skip check probes for (`BundledRenderScript.Package`,
    a const precisely so the two ends cannot drift). Proven 7/7 by `.mtk/paths-135/mutate-ci-mermaid.py`,
    including a relabel control and an anti-vacuity case.
  * **NEW, iter135: PARSE THE WORKFLOW IN THE PROBE TOO, DO NOT RETYPE ITS STEPS.**
    `.mtk/paths-134/probe-ci-clean-clone.py` hardcoded ci.yml's six steps, so it could not have seen a
    seventh; `.mtk/paths-135/probe-ci-renderer-coverage.py` reads them out of the clone's own ci.yml
    with `yaml.safe_load` (PyYAML 6.0.3 is on this machine) and withholds ONE step to build its control
    cell. A probe that retypes the artefact it is checking measures the retyping.
  * **`state.json` HAS ~30 TOKENS OF HEADROOM AGAINST `check-state-size.py`'s 20,000-token BUDGET
    (iter134), and these `done` records are lengthening: iter133 3.4 KB, iter134 5.6 KB.** When the
    check trips, condense the `doneRecent` entry in BOTH state.json and the archive so the
    duplication stays verbatim (`doneArchive.howToAppend` permits exactly that). Do not raise the
    budget to suit the prose that broke it. And note where this paragraph lives: iter134 first wrote
    it into `nextAction`, which pushed state.json over the budget it was warning about — durable
    method advice goes HERE, which is the rule this file exists to enforce.

## A guard that is a placement, not a computation (iter141)

  * **TESTING A GUARD'S LOGIC SAYS NOTHING ABOUT WHETHER ANY CALLER RUNS IT.** `PublishGuard` is a pure
    function and `PublishGuardTests` covers it thoroughly — case-insensitivity, blank entries, the
    override, seven cases. None of that can tell you whether `dashboard` calls it, or calls it before
    the client. **The lock is enforced by WHERE the call sits, so the test has to be per write surface
    and it has to run the command.** Three of the four surfaces had one; `dashboard` — the surface that
    creates a page — did not, for 141 iterations, while `docs/wiki/20-reference/cli.md:50` told readers
    all four refuse.
  * **HOW TO TRACE A "EVERY WRITE PATH CHECKS X" CLAIM: enumerate the verbs, not the commands.** Grep
    `HttpMethod.Post|Put|Delete` in the client, name the public methods around them (10 here), grep
    every caller outside the client, then walk each caller up to its guard. Enumerating *commands*
    instead would have missed that `publish` reaches six of the ten and that `--prune`'s delete is
    guarded only transitively, through `PublishOutcome.Succeeded` being false when the executor stops.
  * **`|| true` IS NOT THE ONLY SWALLOW-BY-CONSTRUCTION SHAPE. `&& !dryRun` IS ANOTHER.** Every one of
    the four surfaces bypasses the refusal under `--dry-run` deliberately, which is only safe because
    each also returns before its first write. Two of them say so out loud; `dashboard` bypasses the
    guard silently and is saved by an early return 145 lines later. Check the second half whenever a
    guard is conditional on a flag.
  * **A GUARD TEST THAT CANNOT GO RED IS DECORATION — MUTATE THE GUARD, NOT A NEIGHBOUR.** 3/3,
    `.mtk/paths-141/mutate-dashboard-guard.py`: baseline green, then `&& !dryRun` → `&& dryRun` (the
    real hole: a live run stops refusing) and `allowProtectedSpace` → `allowProtectedSpace || true`.
    Both still compile, per iter124. The script saves and writes back the file text itself and asserts
    the byte-for-byte restore, because `git checkout --` would drop the iteration's other edits.
  * **RESIDUAL RISK, RECORDED RATHER THAN FIXED:** nothing makes the invariant *structural*. An
    eleventh write method, or a fifth command, is guarded only if whoever adds it remembers. Four
    per-surface tests are the whole enforcement of rules §0.1/§1.4.

## When the enforcement of a rule is a hardcoded list (iter142)

  * **A LIST THAT EVERY PER-SUBJECT TEST ITERATES IS THE ENFORCEMENT BOUNDARY, NOT THE DIRECTORY IT
    DESCRIBES.** `SkillContractTests.Skills` is `["docs-refresh", "docs-feedback", "docs-loop"]`, and
    five per-skill checks loop over it — rule §1.3's untrusted-input clause and rule §0.4's CLI
    boundary among them. A fourth skill under `plugin/skills/` was therefore subject to the
    prompt-injection defense (CLAUDE.md §0.2, PLAN.md §9) *by rule and by no test*. This is iter141's
    residual risk in a cheaper form: here the invariant CAN be made structural, by asserting the
    shipped set equals the list, so the next skill turns the suite red until it is listed — at which
    point all five checks pick it up at once.
  * **"SOMETHING ELSE ENUMERATES THE DIRECTORY" IS NOT THE SAME CLAIM AS "SOMETHING ELSE CHECKS THE
    RULE."** Four other classes do enumerate `plugin/skills/` (`PluginManifestTests`,
    `SkillsReferencePageTests`, `QuickstartTests`), and all of them ask whether a skill is
    *documented* — in README.md, in `docs/wiki/30-automation/skills.md`, in the manifest. An author who
    documents a fourth skill satisfies every one of them without ever writing the clause. Check what
    the other enumerator ASSERTS before concluding a gap is covered.
  * **THE CONTROL CELL IS THE FINDING.** Mutating in a fourth skill and watching the NEW test go red
    proves the test works; running the PRE-EXISTING §1.3 and §0.4 tests against the same mutation and
    watching them stay GREEN is what proves the hole was real rather than already covered. 9/9,
    `.mtk/paths-142/mutate-skill-coverage.py`, cells A/B/C/D/E. Extends iter125's control-case note: an
    expected-GREEN cell can carry more information than the expected-RED one.
  * **A MUTATION THAT ADDS A TRACKED DIRECTORY MUST BE CLEANED UP AS A DIRECTORY** (`shutil.rmtree` in
    a `finally`), and the `git status` check afterwards has to be SCOPED to the path the mutation
    touched — `git status --short tests` fails on the iteration's own uncommitted edit, which is the
    right reason and the wrong cell. Assert the edited file's restore by comparing bytes instead.
  * **SCANNING FOR A SECRET: GRADE THE NEEDLES BY KIND, AND NEVER PRINT ONE.**
    `.mtk/paths-142/scan-credential-leak.py` reads the credentials from env per rule §1.1 and prints
    only counts, paths and lengths. Its first run reported **11 "LEAK" lines that were all
    `author.email` in the plugin manifests** — the probe had lumped `DOCUME_CONFLUENCE_EMAIL` in with
    the token, and this repo publishes that address on purpose. A scan whose findings are mostly false
    is a scan nobody re-runs: only the token and a base64 basic-auth header fail it now, the email is
    reported as `info`. **The result that matters: the 192-char token appears in ZERO of 167 iteration
    logs, 335 tracked files, 452.9 MB of `.mtk/` scratch and 17 bookkeeping files.** Rule §0.3's
    "never in logs" is measured, not assumed. Re-runnable in ~40 s.
  * **§1.2 TRACED AND IT HOLDS — DO NOT RE-TRACE IT.** iter141 nominated it as the next placement worth
    walking; it turns out to be a *computation* behind a single choke point, which is why there was
    nothing to find. `ConfluenceClient` has exactly one `_httpClient.SendAsync` (:1535) and six private
    call sites, and **all six call `ThrowIfFailed`**, which maps 401/403 to
    `ConfluenceAuthenticationException` before anything else looks at the status. The retry half is one
    hand-written predicate (`ConfluenceHttp.IsRetryable`) wired through `ShouldHandle`, deliberately not
    delegated to the library's transient predicate so a package upgrade cannot widen it. Both are
    pinned by tests that CAN go red: the test helper defaults to `maxRetryAttempts: 2`, so
    `LogEntries.Count.ShouldBe(1)` on a 401 would read 3 if the predicate drifted, and a sibling proves
    a 500 does retry to `retries + 1`. All seven catch placements in the executors put the
    auth-specific `catch` above the base `ConfluenceException` (or filter it out explicitly with
    `when (ex is not ConfluenceAuthenticationException)`), and `DriftCommand.LabelAsync`'s per-page loop
    returns on the first failure rather than continuing — so no loop replays an expired token.

## Making a placement-enforced rule structural (iter143)

  * **A REGEX OVER C# SOURCE WILL EVENTUALLY MEET A NESTED GENERIC, AND ITS FAILURE MODE IS
    MISATTRIBUTION, NOT A MISS.** `Task(?:<[^>]*>)?\s+(\w+)\s*\(` matches `Task<ConfluencePage>` and
    fails `Task<IReadOnlyList<ConfluenceLabel>>` — the character class stops at the inner `>`. The
    first run of `.mtk/paths-143/measure-write-surface.py` therefore reported **9** write methods
    instead of 10: `AddLabelsAsync` did not match, so the backward walk kept going and filed its
    `Post` under `UploadAttachmentAsync`, which already had a verb and looked entirely normal. Use a
    greedy `<.*>`, or skip the return type altogether and take the token before `(`. Same family as
    iter132's `owner/docu-me` meeting a filesystem path: a classifier over real text, wrong in a way
    that reads as a result.
  * **WALK BACK TO THE NEAREST MEMBER DECLARATION, NOT THE NEAREST `public` ONE.** Stopping at the
    first `public` steps straight over a private helper and files its verbs under whichever public
    method happens to sit above it. Stop at the first line that declares *any* member and report a
    non-public one as an orphan — a write issued from a private helper is invisible to a caller scan
    that keys on public names, which is worth failing on rather than silently reattributing.
  * **THE MTP `--filter-query` TAKES EXACTLY ONE PATTERN.** `'/*/*/A/*|/*/*/B/*'` runs **zero tests
    and exits 1**; `'/*/*/(A|B)/*'` runs zero and exits **8**. Neither says "no such syntax". One run
    per class, and a harness that needs five control checks pays for five `dotnet test` invocations
    (the rebuild is shared, so the cost is per mutation, not per run). Note also that
    `--treenode-filter` — recorded above from iter132 — now runs zero tests and exits 1; the live
    spelling is `--filter-query`.
  * **THE CONTROL CELL SCALES: FIVE GREEN CHECKS SAY MORE THAN ONE RED ONE.** iter142's lesson applied
    to a rule enforced by placement rather than by a list. `.mtk/paths-143/mutate-write-lock-coverage.py`
    (17/17) puts an eleventh write method on the client (cell B) and a fifth caller file in `src/`
    (cell D), and against **both** runs `PublishGuardTests` (8/8) and all four per-surface tests —
    **ten green results across two real holes.** That, not the new class going red, is what
    establishes the gap was open. Cell F adds an eleventh *read* method and everything stays green,
    which is what stops the class from keying on "the client changed".
  * **A MUTATION THAT ADDS A FILE UNDER `src/` HAS A SECOND AUDIENCE.** `DogfoodWikiTests` fails any
    shipped file no wiki page's `sources` glob covers, so a fixture dropped into `src/` goes red there
    too — for documentation coverage, not for the write lock. Scope the control runs to the checks
    whose silence is the claim, or the finding drowns in an unrelated failure.

## When the mirror of a rule is a second copy nobody diffs (iter144)

Rule §9.7 has two halves: "update `state.json` every iteration" (which every iteration does, visibly)
and "human gates live in GATES.md as `- [ ]` checkboxes **mirrored into state**" (which nothing
checked). `paste-rule-8-2a` had no key in `state.json -> gates` from iter75 to iter144 — **69
iterations, every one of which wrote that file.** The structural fix is `check_gate_mirror` in
`tools/loop/check-state-size.py`, the second non-size invariant to live there for
`check_done_archive`'s reason: it is the script `readMe` already requires after every edit to
state.json **and** to GATES.md, which is exactly when a two-copy invariant breaks.

  * **EQUAL COUNTS ARE NOT A MATCHING SET, AND THE COUNT IS WHAT AN EYE CHECKS.** GATES.md carried 11
    checkboxes and `gates` carried 11 keys. They were different elevens: one mirror key belongs to
    `gate-m1-aurservices-files`, whose heading is struck through (`~~id~~`) rather than a checkbox,
    which bought back the slot `paste-rule-8-2a` had vacated. Diff the sets, never the cardinalities.
  * **GREP CONFIRMS THE WRONG BLOCK.** `paste-rule-8-2a` appears in state.json four times over — in
    `nextAction`'s list of side items and, spelled `rule-8-2a`, as a **`blockers`** key. So searching
    the file for the id returns hits, from a block that is not the mirror the rule names. Same family
    as iter136's archive grep and iter143's `Task<...>` regex: a confident wrong answer that reads as
    a clean result. When a rule names a *specific* structure, assert against that structure.
  * **ANCHOR A MARKDOWN-STRUCTURE SCAN TO THE LINE START AND THE BOLD ID.** Gate bodies cite other
    gate ids constantly, and their steps are written as indented `- [ ]` sub-bullets. `^- \[([ x])\]
    \*\*([a-z0-9-]+)\*\*` picks up headings only; cell J proves an indented box stays invisible.
    Three heading shapes exist and they mean different things — checkbox (mirror REQUIRED), `~~id~~`
    struck (permitted), bold-bullet under "Anticipated" (permitted). Requiring all three would have
    failed on `gate-m7-production`, which is correctly absent.
  * **THE DIRECTION WITH A FUTURE IS STATUS DRIFT, NOT ABSENCE.** Directions 1 and 2 catch a gate
    going missing; direction 3 catches the mirror's status going stale, which is what happens the day
    Mirko finally ticks a box — the loop orients off `gates`/`nextAction`, so a mirror that still says
    PENDING is how it skips work it now owes. "PENDING" is present in every open gate's mirror and in
    no closed one; that convention is the only machine-readable status in a free-prose field.
  * **THE `git show HEAD:<script>` CONTROL BLOCK.** iter143 proved a gap was open by running other
    tests against the mutation; for a standalone script there is a cheaper move — run the SAME five
    defects against the checker as it stood at HEAD and assert all five exit 0. Five green results,
    no fixture, one `git show`. `.mtk/paths-144/mutate-gate-mirror.py`, 16/16.
  * **A JSON ROUND TRIP IS A MUTATION UNTIL A CONTROL SAYS IT IS NOT.** Rewriting state.json with
    `json.dumps(indent=2)` to delete one key also reflows the whole file; with `ensure_ascii=True`
    the `§` and `—` characters inflate it, and main() checks the token budget BEFORE the mirror, so
    the cell would go red with the wrong message. Cell K round-trips with no semantic change and
    must stay green. Assert the SPECIFIC failure message, never just a non-zero exit.

## When a rule's enforcement is one argument at one call site (iter145)

Rule §9.6 has two halves that are enforced in different KINDS of way, and the kind is what decides
whether anything can cover it. "Never runs in CI" is a computation — `PruneGuard.Refusal` — and
`PruneGuardTests` drives it through an injected env reader, so it goes red when deleted. "Requires
interactive confirmation" is not a computation anywhere: it is the third argument of
`PublishCommand.cs:453`. `PruneExecutor` deletes whatever its delegate agrees to, deliberately, so
the "no" case is testable offline — and the consequence nobody had drawn is that **every test that
reaches the executor supplies its own stub, so not one of them has an opinion about the real one.**

  * **WHEN A SEAM EXISTS TO MAKE A RULE TESTABLE, THE PRODUCTION ARGUMENT IS WHAT NOBODY TESTS.** The
    injected-delegate pattern (`PruneConfirmation`, `DiagramRenderer`) is right, and it moves the
    invariant out of the class the tests exercise and into the one line that wires it. Wherever this
    repo injects a collaborator to keep a path offline-testable, ask what asserts the real argument.
    Sibling shape to iter141's placement lesson: there the guard's CALL SITE was untested, here the
    guard's ARGUMENT is.
  * **A LAMBDA SHOULD BE REFUSED, NOT INSPECTED.** `(_, _) => Task.FromResult(true)` and a lambda
    that really prompts are the same shape to any scan short of a compiler, so
    `PruneConfirmationCoverageTests` requires the argument to be a bare identifier and reads the
    named method's body. Cheaper than parsing, and it fails toward the reviewable option.
  * **BUDGET THE ANALYZERS INTO THE CELL DESIGN, NOT INTO A RETRY.** Three of seven cells needed a
    second edit purely to keep compiling, and the first run of the measurement lost BOTH cells to it:
    swapping the call-site argument orphans the prompt (**S1144** unused private method, so delete the
    method too — check first what else calls its helpers; `RenderPaths` had 7 other callers), and
    deleting the guard block orphans a parameter (**S1172**, so drop the parameter and its argument).
    A cell that does not compile reports as red and is not evidence — print the compiler line and
    call it INCONCLUSIVE rather than counting it.
  * **A CONTROL THAT RENAMES THE THING IS WORTH MORE THAN ONE THAT LEAVES IT ALONE.** Cell E renames
    the prompt at both ends and must stay GREEN: that is the only cell proving the class keys on the
    wiring rather than on the string `ConfirmPruneAsync`, which is the failure mode a source-scanning
    test is most likely to ship with. Cell F, rewording the prompt, does the same for its message.
  * **RUN THE FULL SUITE UNDER THE FLAGSHIP DEFECT, ONCE.** One `dotnet test` under cell B1 gives the
    finding and the fix in a single number: 2 failed of 1384, both in the new class, nothing else —
    which is iter142's "the control cell is the finding" without paying for a run per legacy class.
    Read the failing test NAMES, not just the count.

## Reading the loop's own history back (iter136)

  * **`n` IN done-archive.jsonl IS A LINE INDEX, NOT AN ITERATION NUMBER, AND HAS NOT MATCHED ONE
    SINCE LINE 50.** iter48 logged two slices, so from n=50 onward `n = iteration + 1` — 87 of 136
    lines. `doneCount` 136 against iteration 135 is that offset, not a miscount. Never read an
    iteration number off `n`, and never renumber to "fix" the gap: the duplicate is a real record.
  * **THE ARCHIVE HAS TWO ENTRY SHAPES AND ANY READER MUST HANDLE BOTH.** 107 entries are strings
    naming themselves in a leading `iterNNN`; 29 are objects carrying `{"iteration": NNN, "slice": …}`.
    Both are legal per `doneArchive.format`, but every instruction written for the archive assumed
    the first.
  * **THE DOCUMENTED LOOKUP WAS WRONG FOR 27 OF 135 ITERATIONS, AND ITS FAILURE MODE LOOKS LIKE A
    HIT.** `grep -n 'iterNNN' done-archive.jsonl` — the exact command `doneArchive.howToRead`
    prescribed — returned nothing for 3 (the object-shaped ones) and, for 24 more, returned only
    lines belonging to some *other* entry: ask for iter113 and it hands back iter134's and iter135's
    records, which cite it in prose. Because loop entries constantly reference earlier ones, 65 of
    135 iterations get at least one match that is not their own. Use
    `python3 tools/loop/check-state-size.py --find <n>`, which resolves both shapes and lists prose
    mentions separately. Measured by `.mtk/paths-136/probe-archive-lookup.py`.
  * **A CHECK OVER A FILE IS NOT A CHECK OVER THE THING THE FILE RECORDS.** iter133's archive checks
    (valid JSON, contiguous `n`, matching `doneCount`, `doneRecent` round-trip) are all satisfiable
    by a file that is missing an iteration entirely — deleting a middle record and renumbering passes
    every one of them. The checks that catch it have to be stated in the archive's own domain:
    ATTRIBUTION (every entry names its iteration), COVERAGE (no gap in the range), HEAD (the newest
    entry is the iteration state.json claims to be on). The head check is the one that would have
    caught iter132's loss at the moment it happened.
  * **WHEN A MUTATION HARNESS SCORES LESS THAN N/N, SUSPECT THE PREDICTION FIRST.** Stripping an
    entry's attribution also opens a coverage gap, so two checks fire on one mutation and a
    "exactly one message" assertion reports a false FAIL. Same shape as iter135's 6/7. Assert the
    expected SET — every predicted message fires and nothing else does — which still proves
    non-redundancy without pretending each mutation has exactly one consequence.
