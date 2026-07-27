# Method notes — archive, generation 3

> Sections rotated out of `tools/loop/method-notes.md` from iter166 onwards, verbatim and round-trip
> asserted.
>
> **Why a third archive file rather than more of `method-notes-archive-2.md`:** measured at iter166,
> generation 2 was 54,043 B / ~22,517 tok — already past the 20,000-token budget and inside the Read
> tool's 25,000-token cap, so a whole Read of it truncates. Another rotation into it would have
> relocated the truncation rather than fixed it. Generation 2 is therefore frozen at its size, on
> exactly the reasoning iter162 used to freeze generation 1 at 32,101 B.
>
> **This file is DECLARED in two places and both were written in the change that created it:**
> `ARCHIVE_FILES` in `tools/loop/check-state-size.py` (which exempts it from the read-whole token
> budget) and `METHOD_NOTES_GENERATIONS` in the same file (which is what `check_method_notes_stubs`
> walks). Neither is inferred from the filename, deliberately — a rule like "anything matching
> `method-notes-archive*`" would let a generation 4 exempt itself by being named well.
>
> All three generations are history you open on purpose; `method-notes.md` is the one you read
> before writing code, and `grep` over `tools/loop/method-notes*.md` still spans all four files.
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

## A refusal that returns is a refusal that skips (iter165)

  * **A DECLARED REFUSAL IS PROSE UNTIL A CELL FIRES IT** - iter164's argument about "non-vacuous by
    construction", one level in. Four checks carried an explicit vacuity refusal that nothing had ever
    executed. All four fire (5 cells, 5 predictions, 5 matches), so **that half is a green measurement
    and worth one sentence, not a story.** Two of the four needed a *source* mutation rather than a data
    one, because their population comes from a walk and from their own declared list: `patch_source`
    asserts the anchor matched **exactly once**, since a no-op replace leaves the tree healthy and grades
    MISSED - a fabricated find.
  * **THE DEFECT WAS ONE LEVEL PAST THAT, AND IT IS A SHAPE TO LOOK FOR ANYWHERE: A GUARD THAT
    `return`s.** Two of the eight refusals ended in `return problems`; the other six append and carry
    on. Their populations are **not interdependent** - `check_gates_archive`'s substring-derived
    `citing` set going empty leaves 12 orphan-body comparisons and 4 declared keys fully assertable,
    and `check_settled_bodies`' spike names going empty leaves the whole blocker side. Measured by
    emptying the vacuous population **and planting a defect a skipped direction owns**: both printed
    "nothing to check" and named neither plant. So the refusal was **buying an honest label at the price
    of the findings**, and one of the two plants was the only defect in this tree that destroys text.
    Fix: append the refusal, never return on it. New verdict: **MASKED**.
  * **WHEN A CELL PASSES, RECORD WHO ELSE FIRED.** WRONG-CHECK catches a sibling covering for a SILENT
    check; the weaker question - is this check the only detector for that emptying? - needs the co-fired
    set printed on the CAUGHT line too. It immediately showed that iter164's `gate-mirror/mirror-drained`
    cell had been firing `check_gates_archive`'s refusal as collateral all along, unattributed: the
    branch iter165 set out to prove had been executing for an iteration and nobody could see it.
  * **A REMEDY INSTRUCTION GOES STALE LIKE ANY OTHER CLAIM ABOUT THE TREE - MEASURE THE POPULATION IT
    NAMES.** This file's header has told every iteration to "rotate the oldest settled sections" since
    iter162; counting them showed 20 of 24 are already stubs and cannot be rotated twice, so the
    instruction had one move left. Same family as iter151's gate steps that had you redo finished
    setup: **an instruction is a claim, and the cheap check is to count what it points at.**
  * **BOUND THE BLAST RADIUS BEFORE PREDICTING, IN THE CHECK'S SOURCE, NOT FROM MEMORY.** `blockersArchive.settled`
    is read by two checks; the prediction "cell 1 is the sole detector" was only defensible after reading
    that the second one merely loops over it (`for key in settled`) and so no-ops when drained.

## The stub layer nobody had paired, and a regex that fabricated 18 findings (iter166)

  * **A "VERBATIM, ROUND-TRIP ASSERTED" CLAIM IS ASSERTED ONCE, BY THE SCRIPT DOING THE ROTATING, AND
    THEN NEVER AGAIN.** method-notes.md's 24 stubs each carried that sentence; `check_read_whole_files`
    asserted only that the archive FILES exist, which says nothing about whether a given stub's section
    is still in the file it names. This was the same split iters 159/160/161 closed three times inside
    state.json - stub plus archived body - and the one instance they missed, because it lives in a
    document rather than in `state.json`. **When an earlier iteration enumerates "every stub/body split"
    and the enumeration is scoped to one file, the sibling in another file is the thing to look for.**
    Closed by `check_method_notes_stubs`, 7/7 both directions; nothing was broken (24/24 resolved).
  * **A REGEX THAT UNDER-MATCHES DOES NOT MISS FINDINGS, IT FABRICATES THEM - AND IT LOOKS LIKE A FIND.**
    `[Mm]oved` does not match the all-caps `**MOVED to`, so 18 stubs were reclassified as live bodies
    and their perfectly good archived bodies then reported as 18 ORPHAN BODY findings. The output was
    confident, specific and entirely wrong. Fixing the case then still missed `MOVED ON to`, the one
    section rotated twice, whose wording differs *because* its history does. **Two lessons: an
    enumeration keyed on a phrase must be checked against an INDEPENDENT count of the same population
    before its findings are believed** (`.mtk/paths-165/split-stubs-vs-bodies.py` said 24; the probe said
    6, and the disagreement was the bug); and **a partial match is worse than no match**, because a
    vacuity refusal fires on zero and stays silent on 6-of-24. The floor that catches it is a hard
    minimum, not a non-empty assertion.
  * **SO THE FOURTH DIRECTION OF A PAIRING CHECK IS "A STUB THAT DOES NOT PARSE IS NOT A LIVE BODY".**
    Without it the classifier's own blind spot is unobservable from inside the check: an unparsed stub
    silently joins the live-body count and its body reports as an orphan, which blames the archive for
    a defect in the reader. Detect it structurally - a section that NAMES an archive but carries no
    parseable provenance sentence - so a fifth spelling fails loudly instead of being absorbed.
  * **THE NAMED INCREMENT WAS NOT AVAILABLE, AND THE MEASUREMENT IS WHY.** `nextAction` had named
    "condense the stub layer" on the reasoning that 24 pointers averaging 1,173 B are summaries rather
    than pointers. Measured (`.mtk/paths-166/measure-stub-boilerplate.py`): of 26,556 B of stub bodies,
    only **1,829 B is repeated phrasing** - the destination path, "verbatim and round-trip asserted",
    "Nothing was discarded.", "**The headlines:**". Condensing every one of them nets ~1.1 KB, less than
    the section that would document the work. The other ~24.7 KB is per-stub headline CONTENT, so
    condensing in place cannot buy budget without discarding lessons. **iter165's lesson one turn on: a
    remedy instruction goes stale like any other claim, and the cheap check is to measure the population
    it names - but measure what the bytes ARE, not just how many there are.** The rotation into a new
    generation was the move that paid; the condensation was correctly abandoned rather than performed
    for its own sake.
  * **A SCRATCH PROBE THAT RESTATES A COMMITTED DECLARATION GOES STALE INSIDE ONE ITERATION.** This
    probe hardcoded the two generations, and creating generation 3 falsified it the same session. It now
    imports `METHOD_NOTES_GENERATIONS` from the checker. iter144's "a mirror nobody diffs" applies to
    throwaway harnesses too, and the cost of importing is one `importlib.util.spec_from_file_location`.
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
    `tools/loop/run-harnesses.py` "THE ONE COMMAND THAT RE-CHECKS EVERYTHING ITERS 162-166 TOUCHED". It
    resolves on this machine and on no other: a clone, or anyone who cleans scratch, loses the loop's own
    regression harness with no error message. **The check cannot enforce this without failing today**, and
    iter162's rule forbids a printed defect that exits 0 - so it is recorded here and in `nextAction`
    rather than added as a flag that trains its reader to skim.
