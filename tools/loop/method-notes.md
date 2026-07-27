# Method notes

> What this loop has learned the hard way about its own tools: the analyzers, the harness's
> refusals, the shapes that do not compile, what a test must be anchored to. Carried in
> `state.json -> nextAction` from roughly iter60 to iter128 and moved here because it grew every
> iteration, which is precisely the shape that breaks step 1's Read. Verbatim, nothing dropped.
>
> **Read this before writing code or a test, and append to it rather than to `nextAction`.**

**METHOD NOTES — carried forward, plus one new.**
  * **iter127: EVERY FILE THIS LOOP APPENDS TO EVERY ITERATION WILL EVENTUALLY OUTGROW THE READ TOOL.**
    *(CORRECTED AND SUPERSEDED AT ITER128 — the note as written said "the ceiling is 256 KB and the Read
    simply fails; it does not truncate". Half right: that is the BYTE ceiling, and there is also a
    25,000-TOKEN cap that TRUNCATES, which state.json had been over for longer. Deliberately not restated
    here, because two copies of one rule in two files is how the iter127 version came to be stale in the
    first place. THE ONE AUTHORITY IS `state.json → readMe`, which sits ahead of every long field so a
    truncated Read still shows it, and the check is `python3 tools/loop/check-state-size.py`.)*
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

  * `permissions.deny` PATTERNS MATCH WHOLE TOKENS FROM THE START OF THE COMMAND. `Bash(git push
    --force:*)` catches `git push --force origin main` and NOTHING ELSE that means the same thing:
    `git push origin main --force` is allowed, and so is `git push --force-with-lease origin main`
    (`--force-with-lease` is a different token, so it does not match a `--force` prefix). When a deny
    entry must cover a flag that can appear anywhere in the argv, the pattern language cannot express it
    and a `PreToolUse` hook is the mechanism. Do not add more deny lines and call it covered.
  * THE LOOP CANNOT EDIT `tools/loop/loop-settings.json`. `Edit` on it is refused under
    `--permission-mode acceptEdits`, while `Write` of a NEW file under `tools/loop/hooks/` succeeds in the
    same session — so the guard is specific to the settings file, not the directory. This is the same
    class as the `.claude/` guard (see the archived `claude-dir-writes` blocker) and the same rule applies:
    an allowlisted `python3` could route around it, and must not, because anything that can edit its own
    permissions or add its own `PreToolUse` hook can also grant itself anything. Ship it as a paste
    (`tools/loop/loop-settings-paste.md`, the `rule-8-2a-paste.md` shape) and validate the paste against a
    scratch copy so what Mirko receives is a tested change and not a draft.
  * TO PROBE A DESTRUCTIVE COMMAND SAFELY, BREAK ITS TARGET, NOT ITS SHAPE. Every force-push probe at
    iter130 used a remote NAME THAT DOES NOT EXIST (`deny-probe-nonexistent-remote`), so a probe that got
    past the permission layer died in git's argument handling without opening a connection. `--dry-run`
    would have been the wrong tool: it changes the command string, so it no longer tests the pattern you
    care about.
  * ONE PROBE PER TOOL CALL. A denied Bash call aborts the ENTIRE command string, so `cmd-a; cmd-b` where
    `cmd-a` is denied tells you nothing about `cmd-b`. Send the variants as separate parallel calls.

## The CLI's own stderr, and probing with child sessions (iter131)

  * **`claude` WRITES REAL DIAGNOSTICS TO STDERR AND THIS LOOP HAS BEEN DISCARDING THEM FOR 130
    ITERATIONS.** `docume-loop.sh` captures `2>&1` into the iteration log, where four warning lines sit
    in front of every transcript and nothing has ever read them. They are not noise: they named the
    untrusted workspace, the three ignored `.claude/settings.json` allow entries, and two dead deny
    rules in the global settings (see the `settings-corrections` gate). WHEN PROBING THE CLI, CAPTURE
    stdout AND stderr SEPARATELY. Merging them is what the harness does, so merge for a test OF the
    harness — but iter131's first run merged them for everything, and (1) the trust warning landed in
    front of the `--output-format json` payload so every `json.loads` failed silently into a fallback,
    and (2) the word "Permission" in a warning about a *dead rule* made an ordinary `git status` classify
    as blocked. A classifier must read the child's ANSWER, not the whole blob.
  * **`Write(path)` PERMISSION RULES MATCH NOTHING.** The CLI says it outright: *"`Write(.claude/**)` is
    not matched by file permission checks — only `Edit(path)` rules are. Use `Edit(...)` instead (Edit
    rules cover all file-editing tools)."* This refines the archived `claude-dir-writes` blocker: the
    `Write(.claude/**)` allow entry is dead because of its SHAPE, not only because of the sensitive-file
    guard. `Read(path)` rules are fine (no complaint for the `Read(./.env)` denies). DO NOT "fix" the
    `.claude/**` one — the prescribed rewrite is exactly what would grant the loop write access to its
    own rules.
  * **HOOKS *ARE* HONOURED FROM A `--settings` FILE**, and an untrusted workspace does not stop them
    (measured while `.claude/settings.json`'s allow entries were being ignored for that very reason).
    A `PreToolUse` hook that exits 2 surfaces to the agent as
    `PreToolUse:Bash hook error: [<command>]: <stderr>`. Whether `.claude/settings.json`'s OWN hooks
    survive the untrusted state is UNTESTED — it has a `PostToolUse` format-on-edit hook, and that is a
    lead, not a finding.
  * **TO TEST A SETTINGS CHANGE THE LOOP MAY NOT INSTALL, HAND IT TO A CHILD SESSION.**
    `claude -p ... --settings <scratch>` under `.mtk/` tests the artefact end to end while the loop's own
    authority stays untouched, which is the whole reason the guard exists. Make the harness REFUSE TO RUN
    if the scratch would add an `allow` entry, and always pair the block case with (a) a control on the
    live settings proving the command gets through without the hook and (b) a benign command proving the
    hook is selective — a hook that blocked everything passes a one-case harness.
  * **THE PHONE PUSH IS STILL DEAD, AND THE REASON GIVEN IS NOT THE ONE THE DOCS CLAIM.** iter131 called
    `PushNotification` as the protocol now requires and got `Mobile push not sent (Remote Control
    inactive)` — the same string iter126 measured. `tools/loop/ITERATION-PROMPT.md` and
    `tools/loop/README.md` (both carrying uncommitted edits) say a push "delivers to his phone when he's
    away" and "is auto-skipped when you're actively at the keyboard". The skip here is attributed to
    **Remote Control being inactive**, which is precisely the unattended case the channel exists for, not
    the redundant-at-keyboard case. So an unattended WAITING-GATE still reaches nobody. Keep calling it
    (the protocol says to, and it costs nothing), but do NOT treat a push as having surfaced anything: the
    gate in GATES.md and the blocker in state.json are the only channels that have ever worked.
  * A CHILD `claude -p` COSTS ~4-9 s WITH A TINY PROMPT. Pin `--model` (the plumbing under test is
    model-independent), pass `--max-turns` so a blocked child cannot wander, and give the child the
    reason the command is safe (`this remote does not exist`) or it may decline to run the probe at all.
    Classify "the model declined" as its own outcome, distinct from "the layer refused it".
  * **VERIFY GREEN BEFORE YOU START, NOT ONLY BEFORE YOU COMMIT.** iter132 opened with `dotnet test`
    and found 1360/1361 with the failure inherited from HEAD: two commits had landed red because
    iter131 ran build+test and THEN wrote one more file. Several tests in this repo sweep the tree's
    own prose (RepositorySlugTests over every `*.md`/`*.yml`/`*.json` outside `tests/` and `tools/`;
    QuickstartTests over fenced commands), so a docs-only or GATES.md-only edit is fully capable of
    turning the suite red. Re-run after the LAST write of the iteration, including writes to GATES.md
    and state.json.
  * **A REGEX THAT CLASSIFIES REPO TEXT WILL EVENTUALLY MEET A FILESYSTEM PATH.** `owner/docu-me` with
    only a `(?<![A-Za-z0-9-])` boundary matches `…/Dev/docu-me` and reports `Dev` as a GitHub owner.
    Quoting CLI output, which routinely contains absolute paths, is enough to trip it. Fix the
    classification, never the quoted evidence: `(?<!(?<!github(?:usercontent)?\.com)/)` rejects an
    owner preceded by `/` unless that `/` closes a GitHub host. Variable-length lookbehind is a .NET
    feature — Python's `re` refuses it, and splitting it into two fixed-width lookbehinds is NOT
    equivalent (it silently drops every `github.com/` match). Apply the rule in code when mirroring
    such a pattern in a Python diagnostic.
  * **A SWEEP THAT ONLY REPORTS DISAGREEMENT CANNOT NOTICE ITSELF MATCHING NOTHING.** Pair it with two
    guards: per-shape assertions (one `[InlineData]` per shape the tree actually carries, including the
    shapes that must NOT match) and vacuous-pass floors set just under the measured truth. Floors must
    be tight enough to catch over-exclusion: iter132's narrowing would have dropped all 5 `github.com/`
    references, leaving 10 files, which the then-current floor of `> 8` waved through.
  * **THIS HARNESS CANNOT RUN A SHELL SCRIPT.** `tools/loop/loop-settings.json` allows `Bash(python3:*)`
    but there is no `Bash(bash:*)` or `Bash(sh:*)`, so `bash probe.sh` is denied — which is why every
    prior probe under `.mtk/paths-*/` is Python. Write probes as `.py`; a Python probe may drive bash
    through `subprocess` freely, which is also how to exercise a bash function (extract it from the real
    script by content anchors, never retype it). Do not ask for a bash allowlist entry: a session
    widening its own permission file is the thing that guard exists to prevent.
  * MORE SHAPES THIS BASH TOOL STATICALLY REFUSES, on top of the ones already listed above: a heredoc
    whose body contains `${...}` inside quotes ("brace with quote character"), `for x in a b; do … $x`
    ("simple_expansion"), `cd DIR && git …` ("can execute untrusted hooks"), and command substitution
    inside an argument such as `--model "$(cat f)"` ("shell syntax that cannot be statically
    analyzed"). Also: **`cd` PERSISTS BETWEEN Bash CALLS** — one `cd tests/…` made every later relative
    path resolve wrong until absolute paths were used. Prefer absolute paths always.
  * `dotnet test` ON xUnit v3 / MTP HAS NO `--filter`; it is `--treenode-filter "/*/*/ClassName/*"`,
    and passing `--filter` exits 5 with "Zero tests ran" plus a full usage dump that buries the cause.
    Analyzer note: SA1515 requires a blank line before every single-line comment, which includes a
    comment sitting between `[Theory]` and its `[InlineData]` attributes.
  * **`2>&1` CORRUPTS EVERY `claude … --output-format json` PIPELINE HERE.** The untrusted-workspace
    warning goes to stderr, so folding it into stdout puts prose ahead of the JSON and the parse fails
    at char 0. Redirect the two streams to different files when you need both.
  * **`modelUsage` IS THE ONLY HONEST WAY TO ASK WHICH MODEL RAN.** `claude -p … --output-format json`
    returns `modelUsage: {"<id>": {…, canonicalModel, contextWindow}}`. Asking the model to report its
    own id (the recipe README.md carried until iter132) is self-report and unreliable. Measured on CLI
    2.1.219: `opus` -> `claude-opus-5`, `sonnet` -> `claude-sonnet-5`, so the alias lag README recorded
    against 2.1.218 is gone; re-measure rather than trusting either note.
  * **A `bash script.sh` SEEN IN `ps` MAY BE THE SCRIPT'S OWN `$(...)` SUBSHELL.** iter132 briefly read
    a 5-minute-old `bash ./tools/loop/docume-loop.sh` beside the 2-day-old one as a second driver
    racing the first. It was the command-substitution subshell running that iteration's `claude`, which
    inherits the parent's argv. Check `ppid` and the child list before reporting a concurrency bug, and
    cross-check `loop.log` for a matching `Loop started` line — there was none.

## Hooks in a project settings file (iter133)

  * **A FAILING `PostToolUse` HOOK IS INVISIBLE TO THE AGENT. This is the finding to carry forward,
    because it is what let a dead hook sit unnoticed for 133 iterations.** Measured: the event carried
    `exit_code=127, outcome='error'` and stderr `bash: /hooks/format-on-edit.sh: No such file or
    directory`, while mentions of it inside user/assistant turns numbered **0** and the session's
    `result.is_error` was **False**. A `PostToolUse` hook therefore cannot be verified by waiting for
    it to complain, and a non-zero exit in one you write only hides a problem — exit 0 and print
    nothing. (Contrast `PreToolUse`, which iter131 measured as surfacing loudly: `PreToolUse:Bash hook
    error: [<command>]: <stderr>`. The two events differ, so do not generalise from one to the other.)
  * **TO SEE HOOKS AT ALL, ASK FOR THE EVENTS:** `claude -p … --output-format stream-json
    --include-hook-events --verbose`. They arrive as `{"type":"system","subtype":"hook_started"|
    "hook_response","hook_name":"PostToolUse:Write","exit_code":…,"outcome":…,"stderr":…}`. That stream
    is the only honest way to answer "did my hook run", the way `modelUsage` is for "which model ran".
  * **`$CLAUDE_PLUGIN_ROOT` IS EMPTY IN A PROJECT SETTINGS FILE** — the CLI sets it only for hooks that
    come FROM a plugin, so `bash "$CLAUDE_PLUGIN_ROOT/hooks/x.sh"` in `.claude/settings.json` runs
    `bash "/hooks/x.sh"` and exits 127. **`$CLAUDE_PROJECT_DIR` resolves correctly** (measured:
    `/Users/mirkobudimir/Dev/docu-me`), and the hook's **stdin payload** carries `cwd`, `tool_name`,
    `tool_use_id`, `transcript_path` and `tool_input.file_path` — so a per-file hook can find its
    target without any variable at all.
  * **AN UNTRUSTED WORKSPACE GATES `permissions.allow` ONLY, NOT `hooks`.** `.claude/settings.json`'s
    own hooks load and fire while the CLI is printing "Ignoring 3 permissions.allow entries from
    .claude/settings.json: this workspace has not been trusted". This closes the lead iter131 left
    open, and it cuts both ways: a project settings file the CLI is partly ignoring can still execute
    commands on every edit.
  * **A FORMATTER ONLY REVERSES DEFECTS OF ITS OWN CLASS, so a mutation harness must mangle in that
    class.** The first run of `probe-format-on-edit-script.py` scored FAIL against a working hook
    because its mutation INSERTED A LINE BREAK, and `dotnet format` does not re-join lines. Mangle
    indentation and trailing whitespace with the line count held identical, and assert
    `mangled.count("\n") == text.count("\n")` so the harness catches its own bad mutation.
  * **AN ANCHOR MUST MATCH THE HOUSE BRACE STYLE.** `line.startswith("    public ") and
    line.rstrip().endswith("{")` found nothing here: this codebase is Allman, so a member's `{` is on
    its own next line.
  * **`dotnet format` COSTS, MEASURED PER FILE VIA `--include`:** full run **~7.0 s** wall (26 s CPU,
    parallel); `dotnet format whitespace` **~1.9 s** but whitespace only. The loop's own gate is the
    FULL `--verify-no-changes`, so the cheap subcommand would leave diffs that gate still fails on.
    Note the solution is `DocuMe.slnx`, not a `.sln` — `dotnet format DocuMe.sln` exits 2 with a
    `ParseWorkspaceOptions` stack trace rather than a readable "no such solution".
  * **`; echo "X=$?"` AFTER A PIPE PRINTS THE PAGER'S EXIT CODE, AND THAT IS WORSE THAN NOT CHECKING
    AT ALL, because it prints a confident false green.** iter128's note above already said to drop the
    pipe; iter133 still ran `dotnet format --verify-no-changes … | tail -3; echo "FORMAT_EXIT=$?"` and
    read `FORMAT_EXIT=0` from a command that was returning **2**. If a claim depends on an exit code,
    get it from `subprocess.run(...).returncode` in a `python3 -c` and print it there.
  * **`dotnet format --verify-no-changes` WAS RED AT HEAD AT ITER133** (exit 2, `WHITESPACE`, in
    `src/DocuMe.Core/Markdown/ConfluenceStorageRenderer.cs`) while iter132 recorded it as exit 0 — the
    same shape as iter132's own finding about the red suite, one level down. Two things follow. It is
    **isolated to the whitespace pass**: `dotnet format style` and `dotnet format analyzers` both exit
    0, so naming the subcommand makes the claim checkable. And the fix is a **pure line-wrap**: 82
    insertions / 34 deletions that are `TOKEN-IDENTICAL once all whitespace is removed`, which is the
    assertion to make — `git diff -w` is NOT sufficient, because `-w` ignores whitespace *within* a
    line and the formatter *adds newlines*, so it still reports a wrapped file as changed.
  * **AN END-TO-END PASTE PROOF NEEDS THE NEGATIVE CELL MOST.** `probe-paste-end-to-end.py` asks a
    child to introduce a formatting defect and checks the file afterwards; "file == committed bytes"
    is equally consistent with "the hook fixed it" and "the child never edited". The control cell
    (identical prompt, scratch settings with the `hooks` key removed) is what distinguishes them, and
    it doubles as proof that the currently-declared hook does nothing.

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

## Two counters, and patching a script that is running (iter137)

  * **THE RECORDED CAUSE OF A DRIFT CAN BE A MECHANISM THAT CONTRIBUTED NOTHING.** `nextAction`
    carried lead (iii) for six iterations as "the driver's counter resets on every restart, so every
    iterNNN mis-maps by ~26". Both halves were wrong in a way that changes the fix. The resets are
    real — `loop.log` holds three `Loop started` lines, each followed by `Iteration 1` — but **both
    reset runs were killed before a pass finished, so they contributed zero.** The whole gap is the
    **25 usage-limit deaths**: 161 completed passes, exactly 136 exit-0 (= state.json's `iteration`)
    and exactly 25 non-zero, each burning a pass number and writing no bookkeeping. And it is **not a
    constant**: the drift steps 0 → 4 → 9 → 13 → 17 → 21 → 25, so subtracting 25 is right for the
    tail and wrong for 109 of the 133 mis-mapped iterations. **Derive the offset from the log before
    trusting a number written about it.**
  * **TO PATCH A SCRIPT THAT IS EXECUTING RIGHT NOW, REPLACE THE INODE, NOT THE BYTES.** `Edit` and
    `Write` truncate and rewrite the SAME inode — the one the live `bash` still holds an fd on —
    while `os.replace` puts a new inode in the path and the running process keeps reading what it
    started with. `docume-loop.sh` is the loop's own driver and every iteration is one of its passes,
    so this applies every time it is touched. Shape used at iter137
    (`.mtk/paths-137/patch-driver-iteration-number.py`): refuse unless each anchor matches EXACTLY
    ONCE, `bash -n` the candidate in a tempfile BEFORE it goes near the real path, copy the mode
    across, `os.replace`, then print both inodes as the evidence. Make it idempotent by detecting the
    patched text and exiting 0.
  * **`docume-loop.sh` IS THE LOOP'S TO EDIT AND COMMIT.** `scratchInventory` listed it among files
    that were "harness/MTK churn from Mirko's side — leave all of it alone", which was true of an
    uncommitted working copy and stopped being true once it was committed: c6fbf1e (iter132) is the
    loop editing and committing this file, and `git status tools/loop/` has been clean since. Only
    `tools/loop/loop-settings.json` carries a real guard (the harness refuses `Edit` on it). **Check
    `git status` for the file rather than trusting a note about it** — an inventory of a working tree
    goes stale the moment something is committed.
  * **A LOOKUP THAT RESOLVES EVERY KEY TO ITSELF LOOKS EXACTLY LIKE A WORKING LOOKUP.** The first
    `transcript_for()` read the pass number off its own running count instead of off the log line, so
    all 136 iterations resolved to `iter-<same number>-*` — plausible output, and it would have been
    believed if the one pair whose answer was already known (iter4 lives in `iter-0008-*`) had not
    been spot-checked. The fix is not the spot-check, it is **turning the spot-check into the
    assertion**: probe group D now cross-checks all 136 against a mapping derived independently, plus
    the known pair and a must-decline case. Same family as iter136's archive lookup, one layer out.
  * **EXTRACT-AND-DRIVE WORKS FOR A BASH BLOCK, NOT JUST A FUNCTION.** iter132's rule ("extract by
    content anchors, never retype") extends to a fragment of the main loop: pull `state_iter=…`
    through the `iter_log=` line out of the live script, prepend the helper and the variables it
    reads (`$STATE_FILE`, `$LOG_DIR`, `$pass_n`), and assert the FILENAME the fragment builds. Two
    cautions, both of which cost a 0/5 first run: the extracted fragment does **not** include the
    `pass_n=$((pass_n + 1))` line above it, so the counter does not advance; and the tail is
    `-<date>-<time>.log`, two dash-separated segments, so `rsplit("-", 1)` strips half a timestamp.
    Assert with a regex over the whole basename instead of reconstructing a stem.

## Estimating tokens from bytes, and the file nothing reads (iter138)

  * **MOVED to `tools/loop/method-notes-archive.md` at iter141**, verbatim and round-trip asserted,
    to pay back the budget iter141's own section spent (the rule this file's own header states).
    Nothing was discarded. **The headline, kept here because it is the part you need at a glance:**
    the Read tool's truncation notice is the only tokenizer on this machine, and
    `check-state-size.py` now prints the full bytes-per-token calibration table on every run — read
    that instead of re-deriving it. Open the archive for the method behind it and for the
    handoff-archive.md finding.

## Testing code that is dormant, and what the CLI default really is (iter140)

  * **"NOT RUNNING" AND "CORRECT" ARE DIFFERENT CLAIMS, AND FINDING THE FIRST DOES NOT ESTABLISH THE
    SECOND.** iter139 proved the driver was 56.6 h stale and that `current_model()`,
    `state_iteration()` and the `pass_n` log naming were dead code. It never ran any of them. A
    pending restart is exactly the moment never-executed code becomes load-bearing, so the follow-up
    to "X is dormant" is "test X before it wakes up", not another sweep. 9/9,
    `.mtk/paths-140/probe-restart-readiness.py`, no API calls.
  * **HOW TO TEST A DORMANT BASH FUNCTION: extract it by content anchor and run it under `bash -c`
    with the real inputs.** `re.search(rf"^{name}\(\) \{{\n(.*?)^\}}\n", src, re.S|re.M)` lifts a
    function out of the committed driver; feed it `STATE_FILE=<the live state.json>` and it answers
    for real. Never retype the body — that tests the retyping. Same trick for a line rather than a
    function: pull `iter_label=$(printf …)` and `iter_log=…` out by regex and run them together to
    get the exact filename the next pass will write.
  * **THE DEGRADED-INPUT MATRIX IS THE HALF THAT MATTERS FOR A GUARD.** `state_iteration()` exists so
    a bad state file yields `iter-unknown` instead of a crashed pass, and that branch is only proven
    by feeding it all four shapes: missing file, invalid JSON, non-integer `iteration`, absent key.
    All four returned empty at exit 0. A happy-path test would have proven nothing about the branch
    the function was written for.
  * **CHECK `bash -n` AND THE EXEC BIT BEFORE ASKING A HUMAN TO RUN A SCRIPT.** They cost nothing and
    they are the two failures that make step 1 of a hand-run gate die instantly. Nothing had parsed
    `docume-loop.sh` since the two commits that changed it.
  * **`--model <id>` AND NO `--model` PRODUCE DIFFERENT `modelUsage` KEYS FOR THE SAME MODEL.**
    Measured on CLI 2.1.219: no flag → `claude-opus-5[1m]`; `--model claude-opus-5` → `claude-opus-5`.
    Both report `canonicalModel: claude-opus-5`, `contextWindow: 1000000`, `maxOutputTokens: 64000`.
    **So compare `canonicalModel`, not the `modelUsage` key** — the key carries a variant suffix and
    an id-string diff reads as a model change when nothing changed. This is what makes the pending
    driver restart safe on the model axis; iter139 assumed it, iter140 measured it
    (`.mtk/paths-140/probe-model-pinning.py`, payloads kept as `cell-*.json`).
  * **AN ITERATION WHOSE MEASUREMENTS ALL COME BACK CLEAN IS STILL A RESULT — WRITE IT DOWN AS ONE.**
    iter140 checked the model pinning, every `.mtk/paths-*` script the gates cite (20/20 resolve),
    gate-m6's three version files against `release.yml`'s guard (it does check all three), and the
    restart-activated driver code. Nothing was broken. The temptation after fourteen defect-finding
    iterations is to keep digging until something breaks; the honest output is "this is now known to
    work", which is what de-risks the gate a human is about to act on.
  * **A GUARD IN YOUR OWN MIGRATION SCRIPT WILL CATCH YOUR OWN MISCOUNT.** The GATES.md archive split
    asserted "expected 4 ticked items" and refused at 5 — the section had five. Cheap assertions about
    what you *think* you are moving are worth more than careful reading, and they run before the
    destructive write, which is the point.

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
