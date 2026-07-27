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
