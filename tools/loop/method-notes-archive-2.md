# Method notes — archive, generation 2

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

## When the rule is "do not carry knowledge", and the knowledge is in the tree (iter146)

§9.5 ("the tool and skills stay generic") reads untestable — genericity is a negative over all possible
content. It is testable **because this repo dogfoods**: `docs/wiki/_meta/STYLE.md` is a filled-in
consumer guide sitting in the same tree as the code that must not contain it.

  * **WHEN A RULE FORBIDS CARRYING SOMEBODY'S KNOWLEDGE, FIND A COPY OF IT ALREADY IN THE TREE AND DIFF
    AGAINST THAT.** A hardcoded needle list ages into yesterday's mistakes; a derived one tracks what it
    protects. Proof it is really derived is a GREEN cell that REWORDS the source (cell F): needles change
    wholesale, suite stays green. iter145's rename cell, one level out.
  * **A PHRASE SCAN NEEDS A MEASURED n, AND THE BAND HAS TWO EDGES.** n=4 indicted ordinary prose ("pages
    in confluence a"); n=7 let the defect through, its longest lift being six words. 5 and 6 both worked;
    6 taken as the wide end. One edge measured would have shipped 4 or 8 with equal confidence.
  * **A STYLE GUIDE QUOTES THE PRODUCT AS AN EXAMPLE, AND THE QUOTE IS NOT A LEAK.** Its Tone section
    quotes *"The repo is the source of truth"* — a PLAN.md §1 statement `src/` makes for its own reasons.
    Four innocent files were indicted until quoted spans were dropped from the needle source. Generalise:
    **the illustrative register is not the assertive one.**
  * **A MECHANISM THAT REMOVES NOTHING ONCE ANOTHER LANDS MUST NOT SHIP.** The first fix for that was
    subtracting PLAN.md's own n-grams. It worked — then quote-stripping subsumed it and it measurably
    removed **0**, so it was dropped rather than kept as belt-and-braces. Nobody can tell which of two
    mechanisms is load-bearing.
  * **A PER-PART FLOOR BEATS A FLOOR OVER THE UNION.** Three sections contribute 28, 45 and 91 phrases to
    a union of 164, so any global floor loose enough to survive editing the largest waves through losing
    the smallest. Assert each part contributes — cell I proves it then fails loudly.
  * **TWO NETS OVER ONE RULE MUST BE PROVEN INDEPENDENT, OR ONE IS DECORATION.** Cell E is a taxonomy leak
    with zero phrase overlap and fails the taxonomy fact ALONE.
  * **REUSE THE REPO'S OWN DEFINITION INSTEAD OF RESTATING IT** (iter142, applied before the fact).
    "Shipped" is already `DogfoodWikiTests.ShippedRoots`, and §9.5's per-skill half went into
    `SkillContractTests` over its existing `Skills` list — a new class with its own list would have
    re-created the exact defect iter142 fixed, while fixing a different rule.

## When a test compares two machine-generated copies (iter147)

§9.4 ("`init` never overwrites; it reports skips") already had a right-shaped test —
`CliExecutionTests.Init_scaffolds_the_tree_and_a_second_run_writes_nothing_new` snapshots the tree,
edits a file, re-runs, compares. It caught four of five injected write-anyway defects. The fifth
shipped **green through all 1388 tests**, and the reason generalises past this repo.

  * **A GENERATOR PRODUCES THE SAME BYTES FROM THE SAME INPUTS, SO AN UNTOUCHED SECOND RUN IS
    BYTE-IDENTICAL WHETHER OR NOT IT REWROTE ANYTHING.** Only content that was *not* machine-generated
    tells "skipped" from "overwrote with an identical copy". That test knew it — its comment says so —
    and edited ONE file, leaving twelve targets compared against bytes the scaffolder had just
    produced. **A sampled byte check is not a weak version of the real one; on the unsampled rows it
    asserts nothing at all.**
  * **THE TARGET THAT ESCAPES IS THE ONE THAT IS EMPTY AT CREATION AND LOAD-BEARING LATER.**
    `_meta/state.json` is written empty, so re-saving `new DocumeState()` moves no bytes — and from the
    first publish on it is the only record of which page each file owns. **Ask of any idempotence test:
    which target's content is CONSTANT across the two runs?** That is the unchecked one.
  * **EDIT EVERY TARGET, AND MAKE THE EDIT DECISION-PRESERVING.** Three targets' skip turns on their
    content (the manifest keeps its pin, `.gitignore` a `node_modules` line, `docume.json` stays
    loadable because three paths are read back out of it); the other ten get an appended line. **The
    default is what a fourteenth target gets** — cells F/G are one new target misbehaving and behaving,
    and only that pair proves the property derived rather than pinned to thirteen.
  * **A PERTURBATION HARNESS NEEDS A GUARD THAT IT PERTURBED.** Assert every file's bytes CHANGED
    before the second run, or a target the editor silently skipped is compared against machine output
    again — the original defect rebuilt inside its own fix. Cell I drops one edit and it fails loudly.
  * **JUDGE A CELL PER-TEST, NOT PER-SUITE, WHEN IT CHANGES A DELIBERATE INVENTORY.** A fourteenth
    target reddens `ExpectedFiles` on purpose; counting that as a catch scores F and G the same.
  * S1144 a second time (iter145, now paid twice): dropping a call orphans its private helper and the
    cell stops compiling. Delete the helper in the same cell, or the cell is not evidence.

## When the verification command destroys its own evidence (iter154)

The protocol's non-negotiable step is `dotnet build` + `dotnet test` green, and every iteration ran
the suite as `dotnet test 2>&1 | tail -N` — which keeps the summary and drops the per-test failure
lines above it. **MTP writes no artifact unless asked** (`TestResults/` is empty, every project). So
a red run is a bare number. This iteration opened on `total: 1388, failed: 1` at HEAD before any
write, lost the name, and went green for the next sixteen runs. **Iteration 120 hit the identical
shape** and closed with a standing instruction "to capture the name in the same command".

  * **AN INSTRUCTION THAT DEPENDS ON THE NEXT AGENT REMEMBERING IT IS NOT A FIX; PLACEMENT IS.**
    iter120 wrote its instruction into its **done-archive entry**, which `doneArchive.howToRead`
    tells every iteration not to read for orientation; iter145 restated it as a bullet in a section
    about something else ("read the failing test NAMES, not just the count"). Both correct, both
    unreachable when needed — the same failure recurred 34 iterations later and was lost the same
    way. **If a lesson is about what to run, it belongs in the command.**
  * **RUN `python3 tools/loop/run-suite.py`, NOT `dotnet test | tail`.** Full log + xUnit TRX into
    `.mtk/suite-runs/` (gitignored), failing ids and assertion messages printed from the TRX,
    artifacts dropped on green, suite's exit code mirrored; `--repeat N` is the flake hunt. Proven
    both directions 10/10 (`.mtk/paths-154/prove-capture.py`): the red cell mutates one unique
    assertion and must NAME it — printing "1 failed" is scored FAIL, being exactly what iters 120
    and 154 already had — and the green cell must name nothing. Tree restored in a `finally` from
    the text read, asserted byte-identical.
  * **`dotnet test -- <runner args>` DIES ON THIS SDK** (*"Specifying a directory for 'dotnet test'
    should be via '--project' or '--solution'"*). Pass MTP options with NO separator:
    `--report-xunit-trx --report-xunit-trx-filename <name> --results-directory <dir>`. It exits
    non-zero in ~0 s having run nothing, which reads as a red suite unless you check the duration.
  * **THE FLAKE IS STILL UNNAMED AND THAT IS THE HONEST STATE.** 14 runs plus 2 re-runs did not
    reproduce it (1 red in 17 today, ~6%); it is **not** the iter59 mermaid flake, which was fixed.
    A lead, not a conclusion: both recorded occurrences were **the first suite run after a rebuild**,
    and the suite spawns real processes. The next occurrence will name itself.
  * **A GATE'S "IT IS YOUR CALL" CAN STILL CONTAIN A QUESTION OF FACT THAT IS THE LOOP'S TO ANSWER
    — CHECK BEFORE ENDING ON WAITING-GATE.** `decisions.mermaidDialectGap` sat open from iter113 to
    iter156 as a three-way judgement Mirko owed, and it was — but option (a) was written
    *"upgrade or replace `beautiful-mermaid` (check whether a later version takes `graph TD;` and
    `pie`)"*, and that parenthesis was **homework assigned to Mirko that `npm view
    beautiful-mermaid versions` answers in 30 seconds**: 1.1.3 IS `latest`, so the upgrade half was
    a dead end for 43 iterations while the gate went on offering it. Measuring the rest took one
    probe and found the gap was 17 diagram types rather than 1, a root cause that made option (b)
    cheap, and a **fourth failure nobody had recorded** (YAML frontmatter). None of that picks the
    option; all of it changes which option a reasonable person picks. **The judgement is Mirko's,
    the facts are the loop's — and the facts were sitting inside the sentence describing the
    judgement.** Before writing "only gated work remains", re-read the gate prose for embedded
    verbs: *check*, *find out*, *see whether*, *confirm*. That is the loop's work wearing a gate's
    clothes.
  * **MEASURE A LIBRARY'S SURFACE, NOT THE FOUR CASES YOUR CORPUS HAPPENS TO HOLD.** iter113
    measured mermaid dialects with the 4 diagrams in `tests/golden/cases/mermaid.md` and reported
    "rejects 2 of them", which the wiki then wrote up as a **two-row denylist** — a shape that reads
    as though `pie` were an exception. It is one of seventeen. The corpus is a sample of what *this
    repo wrote*, never of what the dependency *supports*; for the supported set, read the
    dependency's own dispatch (`detectDiagramType`, `index.ts:54`) and then confirm it by driving
    the real script over the full surface. Predict first and score the predictions — 13/13 held,
    which is what makes the mechanism claim quotable rather than a guess.
  * **A DOC TABLE CAN BE PINNED TO THE GOLDEN CORPUS, SO A TRUE FACT CAN STILL BE THE WRONG THING TO
    PUT IN IT.** iter156 measured that YAML frontmatter fails every mermaid diagram type and added a
    row for it to `docs/wiki/20-reference/conversion.md`'s rejected-header table. Build green,
    `GapsPageTests` green, full suite **red**:
    `MermaidAcceptanceTests.The_conversion_page_names_exactly_the_headers_the_real_renderer_rejects`
    renders **every row of that table through the real renderer** and fails any row the *golden
    corpus* does not actually reject — *"names a header the real renderer no longer rejects, so the
    page warns a reader off a diagram that works"*. No golden case carries frontmatter, so a
    measured-elsewhere fact reads to that test as an over-claim, which is exactly what it is there to
    catch. **The fix was prose under the table, not a weaker test.** Two lessons: (i) before adding a
    row to a table in `docs/wiki/`, grep `tests/` for the page name — several tables are contracts
    with a corpus behind them, not lists; (ii) **a green single-class run is not a green suite** —
    `GapsPageTests` passed because its regex only counts rows, while the test that actually *renders*
    them lives in a different class.
  * **WHEN A LATE FAILURE CHANGES WHAT YOU DID, GO BACK AND CORRECT THE RECORD YOU ALREADY WROTE.**
    iter156 had already written "adds the frontmatter row" into `state.json → phase` and into
    done-archive line 157 before the suite refuted it. Both were rewritten before the commit. An
    archive entry is the only account a cold session gets, and a done entry that describes work that
    was reverted is worse than no entry: the next iteration reads it as HEAD's state.

