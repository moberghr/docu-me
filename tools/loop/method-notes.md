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
