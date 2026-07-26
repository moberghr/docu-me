# Loop scratch inventory

> Moved out of `tools/loop/state.json` (`uncommittedChurnNote`) at iter128, verbatim, because
> 5 KB of scratch-file bookkeeping was part of why step 1's Read truncated. This is operational
> detail a session needs only when it is about to delete something under `.mtk/` or has just
> edited a file that a mutation harness guards - not orientation. Nothing was discarded.

THE PENDING PLAN.md EDIT LIST IS EMPTY AS OF ITER81. Do not go looking for
item (37) or for the 40-item list again. Iter69 applied 28 of the 40 in commit 63c3efb; iter81 verified the
rest against the tree and git history:

  (37) §4's and §7's mermaid render ceiling — ALREADY APPLIED at commit 6fd5250. PLAN.md:142 carries
       iter59's measured numbers (112 KB / 800 edges in ~1.5 s; 700 KB / 5 000 edges out of V8 heap after
       ~23 s, against MermaidRenderer's 30 s timeout) and PLAN.md:318 cross-references it. `git log -L
       142,142:PLAN.md` and `-L 318,318:PLAN.md` are the evidence. The previous nextAction called this
       outstanding and was stale.
  (21) + (32) `actions/action.yml` and the `@v1` ref — DONE. The composite-action question was settled
       (decisions.compositeAction: "floating-v1"), the action was built at db240dc, given a wiki page
       section at 894bf07, and PLAN.md:112, :129 and :384 all describe the shipped file and ref.
  (26) §12's consumer-pinning caveat closes when v0.1.0 is pushed (gate-m6-first-release). The prose is
       already written; only the tag is outstanding, and the tag is not the loop's.
  (12), (22), (23), (30), (35) were closed or superseded in earlier iterations.

TWO FINDINGS THE 40-ITEM LIST HAD MISSED, both fixed in 63c3efb and both worth the habit they imply:
`docume convert` shipped with no §6 subsection (now §6.7 — if you add a command, add its spec section in
the same slice), and `_meta/GAPS.md` is not scaffolded by init at all (§9 step 3 creates it; do NOT add a
14th init target). ITER81 ADDS A THIRD OF THE SAME KIND: if you add a step a consumer's job needs, say so
in the §10 bullet for that workflow in the same slice.

STILL modified-or-untracked and STILL NOT THE LOOP'S TO COMMIT (harness/MTK churn from Mirko's
side): .claude/analytics.json, tools/loop/{ITERATION-PROMPT.md,README.md,docume-loop.sh},
tools/loop/MODEL (contains "claude-opus-5"), and the untracked stray tests/golden/.claude/ (an MTK
analytics dir written by a tool that ran with cwd=tests/golden — harmless, the §4.3 corpus
enumerates *.md only, but it should be deleted or gitignored by whoever owns MTK config).

GITIGNORED, so never in git status: .mtk/order-smoke/, .mtk/move-smoke/, .mtk/brokenlink-smoke/,
.mtk/prune-smoke/, .mtk/publish-cli-smoke/, .mtk/publish-e2e/, .mtk/status-state.json,
.mtk/confluence-stub.py, .mtk/label-stub.mjs (KEEP IT — the sync, dashboard, mark and template
smokes all run on it), .mtk/comment-stub.mjs + .mtk/sync-comments-smoke/ (KEEP — the ingestion
smoke), .mtk/reply-stub.mjs + .mtk/sync-reply-smoke/ (KEEP BOTH — iter60's reply smoke runs the
stub and copies the fixture; the fixture's three iter50 stamps are INTACT, because the copy is what
gets un-stamped), .mtk/sync-smoke/, and .mtk/drift-smoke/ (a throwaway three-commit git repo; its
own .git means `git -C` resolves to it rather than to this repo). node_modules/ is present and
pinned by the root package.json (beautiful-mermaid 1.1.3).

SCRATCH TO KEEP, by iteration. .mtk/paths-81/: audit-with.mjs (THE `uses:`/`with:` COVERAGE SWEEP — run
it after any template or action edit), mutate.mjs (13 cases over the new SDK/Node/mermaid assertions —
run after any edit to docs-publish.yml's toolchain steps, actions/action.yml's inputs, package.json's pin
or Directory.Build.props' TFM), mutate-tfm.mjs and mutate-band.mjs (the two records of why the TFM case is
proven by a theory rather than by mutation). .mtk/paths-68/: mutate-manifests.mjs (the 11-case manifest
mutation harness — RUN IT AFTER ANY EDIT to plugin/.claude-plugin/plugin.json or
.claude-plugin/marketplace.json, and after any bump of the pinned CLI version in ci.yml) and
nocreds-probe.mjs (the evidence that `claude plugin validate` needs no credentials). .mtk/hold-61/:
mutate.mjs (17-case harness, ANCHOR REPAIRED AT ITER81 and 17/17 — RUN AFTER ANY EDIT to docs-publish.yml
or WorkflowTemplateTests; it SUPERSEDES .mtk/wire-60/mutate.mjs, which is dead — delete rather than run)
and shell-probe.mjs. .mtk/wire-60/: KEEP probe.mjs, cp-probe.mjs, reply-smoke.mjs. Also KEEP
.mtk/readme-57/mutate.mjs, .mtk/loop-58/{mutate,smoke,accept}.mjs and .mtk/flake-59/{loop,run,script-smoke,
big-probe,cli-smoke}.mjs plus .mtk/flake-59/{pre,post}/, and .mtk/paths-80/three-point.mjs (the
every-iteration check). SAFE TO DELETE: .mtk/state-69/update.mjs, .mtk/paths-68/update-state.mjs,
.mtk/paths-81/update-state.mjs, .mtk/hold-61/{update-state.mjs,commit-msg.txt},
.mtk/wire-60/{repo,remote.git,bare-repo,runner-temp,cp-probe,reply-smoke}/ and
.mtk/wire-60/commit-msg.txt; .mtk/init-manifest/ can go once gate-m6-first-release is ticked.

WHEN A HARNESS CRASHES, CASES AFTER THE CRASH DID NOT RUN. iter81 learned this the expensive way on
.mtk/hold-61/mutate.mjs. Read the tail of a harness run and check the N/N line, not the first CAUGHT.

.mtk/paths-131/ (iter131), BOTH WORTH KEEPING AND BOTH SPAWN CHILD `claude -p` SESSIONS (~4-9 s each, so
a run costs real time and tokens — do not re-run them idly):
  * probe-resume-path.py — the four questions about `docume-loop.sh`'s `--resume` path (context carried,
    session id preserved, the line-74 fallback regex, and the workspace-trust state). RE-RUN AFTER ANY EDIT
    to the resume block at docume-loop.sh:66-80, or after a CLI upgrade — every answer in it is a fact
    about CLI 2.1.219 and nothing pins these in tests/ (tests/ deliberately does not scan tools/loop).
  * probe-hook-from-settings.py — proves a PreToolUse hook loads and fires from a `--settings` file, using
    the exact fenced block in tools/loop/loop-settings-paste.md against a scratch copy. RE-RUN IF THAT
    PASTE IS EDITED, and re-run it once more after Mirko installs it (it then measures the live file's
    behaviour rather than a scratch copy's). It writes .mtk/paths-131/scratch-paste-settings.json, which is
    SAFE TO DELETE — it is regenerated from the paste on every run, and it is a settings file that must
    never be confused for the live one.
