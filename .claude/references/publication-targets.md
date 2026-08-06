# Publication targets — repo/GitHub-native lifecycle (M7a seam, M8 target)

> Spec for `PLAN.md` §14's **M7a** and **M8** rows. Lives here rather than in `PLAN.md` because
> `PLAN.md` is on the build loop's step-1 read path and holds a 20,000-token budget
> (`tools/loop/check-state-size.py`), which it had ~2.7 KB of headroom against when this was written.
> A milestone spec two milestones out does not need to be re-read every iteration. Rationale,
> alternatives and accepted risks: `decisions.md`, entry "A second publication target" (2026-08-05).


**The request is not storage.** §1 already puts the store in the repo. Confluence supplies four capabilities and only the first is storage; a repo-native mode is a repo-native *interaction surface*:

| Capability | Confluence | Bare repo |
|---|---|---|
| Page store + hierarchy | pages, parents, child order (§6.2) | **already there** — the directory tree and its numeric prefixes |
| Approval gesture | the `approved` label (§8) | absent |
| Feedback channel | inline + footer comments (§9) | absent |
| Reader + dashboard surface | rendered pages, Status page (§6.5) | absent |

**The seam is the plan, not the client.** Every planner is a pure static function over `DocumeState` plus an observation record and takes no client; exactly eight Core types touch `ConfluenceClient` (`PublishExecutor`, `PruneExecutor`, `DashboardPublisher`, `LabelReader`, `FeedbackReader`, `FeedbackReplyReader`, `FeedbackReplyExecutor`, `StatusProbes`). A target is therefore a set of observation producers plus plan executors, and **M8 changes no planner**. The state model needs no migration: `PageId`, `ParentPageId`, `Title` are already nullable, `ApprovedVersion` is `int?`, and `PublishedVersion`'s `0` already means "never written". One state file with target-specific fields nullable, never one per target: a repo publishing to both surfaces is legitimate and two files would race on the approval block.

**Not abstracted, deliberately:** `PublishExecutor` and `PruneExecutor` stay Confluence writers and the repo target gets a *sibling* executor off the same `PublishReport`. One contract spanning "upsert a page over REST v2" and "write `_meta/STATUS.md`" would describe neither, and page versions, attachment hashes and bodyless reparenting have no repo-side meaning. The repo target declines prune outright rather than no-opping it: deleting a documented file is a git operation its author performs.

**Capabilities are declared, and refusals are loud.** Every command checks the target before doing work, modelled on `PublishGuard.WriteRefusal`. A silent no-op is the worst outcome available here, because a dashboard reporting "0 approvals revoked" reads as good news.

**The GitHub-native target:**

| Confluence | GitHub-native |
|---|---|
| page create / update / move | no remote write; commit what the repo cannot render itself |
| attachments | files already in the repo |
| ```` ```mermaid ```` → SVG attachment | fence left alone; GitHub renders mermaid natively, so no Node (§10) and no §4 ceiling |
| `approved` label | a PR review approval, recorded against `contentHash`; `approvedBy` is a real author, which spike S3 could not give |
| `stale` label (§6.4 `--mark`) | `stale: true` in state, `_meta/STATUS.md`, the §10 PR comment |
| comments (§9) | PR review comments and labelled issues into the **unchanged §5.4 inbox** |
| Status page (§6.5) | committed `_meta/STATUS.md` plus a CI job summary |
| §8 banner | omitted: a repo reader has git history, and the banner would be a machine edit inside a file humans diff |

## Two consequences for sections already written

Both belong in `PLAN.md` §8 and §10 **when M8 implements them, not before**. They were drafted into those sections on 2026-08-05 and backed out the same day, because `PlanSemanticsTraceTests` and `PlanWorkflowTraceTests` require every §8/§9/§10 unit to be traced by exactly one claim pinning it to code, and a spec bullet describing unbuilt behaviour can be traced by nothing. That guard is correct and this file is the holding place it implies: M8 moves each one into its section together with the claim that traces it.

**§8: the gesture is coarser than the record, and the record wins.** A label is per page, so in Confluence mode the two match exactly. A repo-mode approval is a PR review, which approves a *diff*: one gesture on a PR touching six pages covers six `contentHash` values, and it is recorded as six per-page approvals sharing one timestamp and one author. The alternative reading — a single approval keyed to the PR — would put a thing that is not a page into `state.pages` and break every §8 invariant that keys off `contentHash`.

**§10: repo mode inverts the trigger, not the contract.** Every §10 trigger assumes publish writes *outside* git, which is what makes "on merge to default branch" the right moment. A repo-mode publish writes state, `_meta/STATUS.md` and any rendered asset **into the working tree**, so it must run inside the docs PR or in a job that commits back; on merge it would produce a commit nobody reviewed, on the branch §1 calls the source of truth. The §10 failure contract is unaffected: state is safe before any held exit code is honoured.

| # | M8 open question | Default if unanswered |
|---|---|---|
| S8 | Can DocuMe observe a PR review going stale after a later push? If yes, that is §8 invalidation for free. | Key off `contentHash` alone, as Confluence mode already does |
| S9 | Which reader surface do consumers want: GitHub's own rendering, or a generated site? Answer from M7 onboarding. | GitHub's own rendering; no site, no hosting (§1) |
