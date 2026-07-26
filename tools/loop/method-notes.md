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
