# Decision Log (ADR-lite, append-only)

## 2026-07-24 — CLI in .NET 10, single TFM

**Context:** Implementation runtime for the deterministic tier.
**Decision:** .NET 10, single target framework; CLI ships as a dotnet tool (`DocuMe.Cli`), all logic in a library (`DocuMe.Core`).
**Evidence:** `PLAN.md` §1 "Locked decisions (Mirko, 2026-07-24)", §4.

## 2026-07-24 — Jira feedback channel deferred

**Context:** Which feedback intake channels ship in v1.
**Decision:** Confluence comments only; the inbox item format (`PLAN.md` §5.4) is the pluggable seam for future channels.
**Evidence:** `PLAN.md` §1 non-goals, §9.

## 2026-07-24 — Single `approved` label; machine removes it on content change

**Context:** How reviewers assert approval and how approval invalidates.
**Decision:** Adding the `approved` label is the entire human gesture. A republish that changes `contentHash` removes the label (revision of the earlier "never remove labels" idea; removal scoped strictly to content changes, history preserved in state).
**Evidence:** `PLAN.md` §1 locked decisions, §8.

## 2026-07-24 — Thin custom Confluence client, pending spike S1

**Context:** Dependency risk vs convenience for ~12 REST endpoints.
**Decision:** Thin custom client on `HttpClient` (REST v2, plus v1 for label add and CQL); evaluate `Dapplo.Confluence` in spike S1 before committing.
**Evidence:** `PLAN.md` §4, §13 S1.
**Superseded by:** the 2026-07-26 entry "Spike S1 closed on evidence" at the foot of this log. The condition this entry attached to the choice has been discharged; the choice itself held. Left otherwise untouched because this log is append-only.

## 2026-07-24 — Custom Markdig renderer with golden-file contract

**Context:** Markdown → Confluence storage-format conversion is the highest-risk component.
**Decision:** Custom `ConfluenceStorageRenderer` (never md→html→regex); golden files reviewed by hand once, asserted forever.
**Evidence:** `PLAN.md` §7.

## 2026-07-24 — Regeneration runs both locally and in CI

**Context:** Where `/docs-refresh` runs.
**Decision:** Both a nightly CI cron (headless Claude) and locally on demand.
**Evidence:** `PLAN.md` §1 locked decisions, §10.

## 2026-07-24 — Align .NET build/test standards with Moberg house stack

**Context:** DocuMe should follow the same engineering standards as the rest of Moberg's .NET work.
**Decision:** Adopt the `moberghr/app-templates` house standards **scoped to DocuMe's CLI + library shape**: Central Package Management, max-strict analyzers (StyleCop + Roslynator + Sonar + Meziantou) with warnings-as-errors, `.editorconfig` severities, pinned SDK + test runner in `global.json`, xUnit v3 on the Microsoft Testing Platform, and no MediatR (Warp if a mediator is ever needed). The web/EF/PostgreSQL/Aspire/React parts of the house stack are explicitly out of scope. Coding style follows `moberghr/coding-guidelines@4043387` (general rules; skip its data-layer sections). The build-config migration (CPM + analyzers + xUnit v3) is handed to the build loop as a verified "standards hardening" slice, not applied by hand, so the just-landed M0 scaffold stays loop-owned.
**Evidence:** `.claude/references/dotnet/moberg-house-standards.md`; `moberghr/app-templates` root `CLAUDE.md`/`AGENTS.md` + `fullstack-app/backend-server/Directory.{Build,Packages}.props`; `moberghr/coding-guidelines@4043387ca2c70ed0cd76e005861f5c471908c3bb`.

## 2026-07-24 — Converter follows mark's actual behavior over PLAN.md prose, in both flagged conflicts

**Context:** Build-loop iterations 12 and 13 each researched a construct before coding and found `PLAN.md` §7's cell contradicted `kovetskiy/mark`, which the same rows name as the behavioral reference. The loop implemented mark, flagged both cells, and deliberately did not edit the plan. Decided here rather than left open.

**Decision:** mark wins in both cases; §7 is the inaccurate artifact and has been corrected.
1. **Code fence attributes:** space-separated, no `=`, `title` takes the rest of the line unquoted. `title=Foo` fails loud. Rationale: mark's own reaction to `title=Foo` is to silently emit a bogus `theme` param, which is the one outcome worse than an error — a wrong-but-plausible transform that a reviewer would have to catch by eye on 79 pages. Failing loud converts a silent corruption into a build error naming the page.
2. **Mixed task/plain lists:** echo the author's literal `[x] ` / `[ ] ` text, no emoji. Rationale: the same reasoning that made iter11 refuse to inject mark's alert title paragraph. Echoing the author's own characters invents nothing; an emoji pair is content the author never wrote, and §8 hashes the body, so injected glyphs would sit inside the hash that gates human approval.

**Consequence to watch, deliberately accepted:** a fence written in another toolchain's dialect (`title="x"`, `{1,3-4}`) now fails its page instead of degrading. If the M1 acceptance run over the 79 AurServices pages shows that dialect at scale, revisit *then* with real counts — a tolerated-alias shim is a 20-line change, and doing it now would be guessing.
**Evidence:** `PLAN.md` §7 rows for code-fence attributes and task lists (both corrected 2026-07-24); `kovetskiy/mark` source + `testdata/codes.html` and `testdata/tasklists-mixed.html`; loop `state.json → nextAction` (iter12, iter13 findings); `tools/loop/logs/iter-0016-*.log`.

## 2026-07-25 — Converter acceptance is the golden corpus; the 79 Aur pages are not required

**Context:** M1's acceptance bar was "all 79 AurServices pages convert with zero errors and zero unknown-construct warnings". M1 went feature-complete at iter26, so from then on the only thing holding the milestone open was getting those files onto the build machine — `gate-m1-aurservices-files`, open since iter21 and unactioned.

**Decision (Mirko, direct instruction):** "No need for aur wiki files, remove that requirement, just use your golden set." The golden corpus in `tests/golden/` is now the entire converter acceptance bar: it must convert with zero errors and zero unknown-construct warnings, with a case for every construct in `PLAN.md` §7's table. `gate-m1-aurservices-files` is closed without action and must not be reopened. No `acceptance.aurServicesWikiPath` is needed.

**What this costs, stated plainly so no iteration re-litigates it:** the goldens are hand-authored from the same understanding that built the renderer, which makes them a strong *regression* contract and a weak *discovery* instrument. They cannot surface a real-world dialect nobody predicted, because whoever wrote the renderer wrote the fixtures. Two concrete open questions lose their answer at M1: how many real pages use a code-fence dialect that now fails loud (`title="x"`, `{1,3-4}`), and how many of the 59 mermaid diagrams use spellings `beautiful-mermaid` rejects (`graph TD;` with a trailing semicolon, `pie`). Both were flagged as M1's highest-risk unknown.

**Where the risk goes instead:** M7 Aur adoption, which is where the Aur files actually live and where `docume init --adopt` runs against them. The failure mode is bounded by fail-loud design — an unrecognized dialect errors with the page named rather than silently mis-converting, so the discovery is late but not dangerous. Partial recovery available cheaply and recommended: adversarial golden cases for the four *known* risky spellings, which pins the fail-loud behavior as a documented contract without needing any real content.

**Consequence for M2:** its acceptance criterion changes with the same edit, since the "Aur bulk publish (79 pages)" it named has no files. M2 now publishes DocuMe's own docs **plus the golden corpus** to the sandbox for page-by-page review, and the Aur bulk publish moves to M7. This is a net gain in one respect: publishing the goldens is the first time converter output is rendered by real Confluence rather than asserted as a string, which is precisely the check a golden-file suite structurally cannot perform.

**Not changed:** `PLAN.md` §15's definition of done still requires the 79-page Aur wiki published and human-verified in space `AUR`. That is the project's reason to exist and was not in scope of this instruction.
**Evidence:** `PLAN.md` §7 (golden-file paragraph), §14 M1 + M2 acceptance cells; `.claude/rules/testing.md` §4.4; `GATES.md` closed-gate entry — all revised 2026-07-25.

## 2026-07-24 — Sandbox is a dedicated disposable space, not an existing live space (ITAPPS)

**Context:** M2+ needs a Confluence space for unattended verification. Production is space `AUR` on `kvika.atlassian.net` (customer tenant). Options considered: create a space on Moberg's own Confluence, or reuse the existing `ITAPPS` space.

**Decision:** Create a dedicated, disposable sandbox space on Moberg's own tenant. Do **not** point the loop at `ITAPPS`.
**Rationale:** the loop's write profile is hostile to any space with human content or watchers. `publish --prune` deletes orphan pages (§6.2) and §9.1 makes republish overwrite hand edits by design, so aiming it at a live space is a genuine data-loss path, not just noise. On top of that, M2's acceptance is a 79-page bulk publish that will run more than once, and M3–M5 add label churn, a dashboard page, and machine-authored comments — every one of which would spam watchers and pollute colleagues' search results. A throwaway space can be emptied and recreated between acceptance runs, which a shared space cannot. The cost is one click; the blast radius saved is the whole reason the M7 production gate exists.
**Consequence:** sandbox and production are different tenants, so neither the token nor the base URL is shared. Three follow-ons, all accepted: (a) M7 cutover swaps credentials *and* base URL, not just the space key; (b) `state.json → confluence` currently has no `sandboxBaseUrl` field, so the schema is single-tenant and needs that field threaded through the config loader before the first publish attempt — flagged as a checklist item in `GATES.md`; (c) tenant-specific behavior (custom macros, user-mention resolution, space permissions) stays unexercised until M7. Storage-format rendering is tenant-independent and M2's page-by-page human review is the check that would catch a difference, so (c) is a real but bounded gap.
**Caveat:** `ITAPPS` could not be inspected (Atlassian MCP was disconnected, no `DOCUME_CONFLUENCE_*` credentials in the loop shell), so this assumes it is a live shared space. If it is in fact already an empty scratch space, the decision is cheap to reverse — set `confluence.sandboxSpaceKey` and nothing else changes.
**Evidence:** `PLAN.md` §6.2 (`--prune`), §9.1, §14 M2 acceptance; `.claude/rules/security.md` §1.4; `GATES.md` sandbox setup item.

## 2026-07-26 — Spike S1 closed on evidence: `Dapplo.Confluence` rejected, the thin client stays

**Context:** The 2026-07-24 entry above took the thin custom client *conditionally* — the dependency was to be measured first. It was, at build-loop iteration 27, and the answer was written into `ConfluenceClient`'s XML remarks. It was never carried back here. So this log kept its "pending" heading, and `architecture-principles.md` P4 kept telling every reader the evaluation was still owed, for sixty-odd iterations after the code had committed to the outcome.

**Decision:** The default was taken, and on a measured reason rather than by drift. `Dapplo.Confluence` is alive — 1.0.41, published January 2026, with a `net10.0` target — but it hard-wires its API root to `/rest/api`, the v1 content API, and reaches Atlassian through `Dapplo.HttpExtensions` + Newtonsoft. `PLAN.md` §4 asks for REST v2 with v1 only where v2 lacks (label add, CQL search), and for `Microsoft.Extensions.Http.Resilience` on the transport, so the package would have to be worked around on both counts for the ~12 endpoints DocuMe needs. DocuMe ships the hand-written client. Do not re-open this without new evidence that Dapplo has grown a v2 surface.

**What it changes in the code:** nothing. `ConfluenceClient` has been the hand-written client since M2. What changes is that no reader of the reference docs is told a decision is still owed when it is not — which matters here more than in most repos, because these two files are auto-loaded into every agent session, so a stale "evaluate X before committing" reads as a live instruction.

**The failure worth naming, because it is the recurring one:** a question gets answered, the answer is written down in the one place the person answering it was standing — an XML remark, a test name, a state field — and never reaches the artifact a reader consults. `SpikeTableTests` guards the `PLAN.md` §13 half of that. `SpikeClosureTests` now guards this half: no reader-facing doc may describe a spike as pending once §13's row records the outcome and reserves nothing.

**Evidence:** `src/DocuMe.Core/Confluence/ConfluenceClient.cs` remarks; `PLAN.md` §13 row S1; `tools/loop/handoff-archive.md` (iter27); `tests/DocuMe.Core.Tests/Acceptance/SpikeClosureTests.cs`.

## 2026-08-05 — A second publication target: repo/GitHub-native lifecycle, seam now (M7a), implementation later (M8)

**Context:** Mirko, exploring: "I would like to explore option to have not only confluence, but option to store documentation in the repo itself." The literal request was already satisfied — §1's first design principle puts the store in the repo and `docs/wiki/*.md` is the artifact every section treats as ground truth. The real gap is that four capabilities are obtainable only from Confluence, and only one of them is storage: the page store (which the directory tree already provides), the **approval gesture**, the **feedback channel**, and the **reader/dashboard surface**. So the feature is a repo-native *interaction surface*.

**Decision, two parts deliberately split:**
1. **M8** builds a GitHub-native target: PR review approval recorded against `contentHash`, PR review comments and labelled issues into the unchanged §5.4 inbox, `stale` as state plus `_meta/STATUS.md`, GitHub's own markdown rendering as the reader surface, mermaid fences left alone.
2. **M7a**, the enabling refactor, lands **before M7** (Mirko's call when offered "write it up as M8" versus "enabling refactor now"): the target discriminator in config with target-conditional validation, capability declaration with loud refusals, and interfaces at the five reader/executor seams. Behaviour-preserving; every Confluence-mode test must stay green *without being edited*, which is the acceptance criterion.

**Why the seam is smaller than it looks, measured rather than assumed:** the codebase already runs observe → plan → execute, and every planner is a pure static function over `DocumeState` plus an observation record — `LabelSyncPlanner`, `FeedbackInboxPlanner`, `FeedbackReplyPlanner`, `DriftMarkPlanner`, `PublishPlanner`, `ChildOrderPlanner`, `PrunePlan` take no client, and `PublishPipeline.Plan` is documented as calling nothing so `--dry-run` cannot drift from a real run. Exactly eight Core types touch `ConfluenceClient`. So a target is a set of observation producers plus plan executors and **no planner changes**. The state model needs no migration either: `PageId`, `ParentPageId`, `Title` are already nullable and `ApprovedVersion` is `int?`.

**What is deliberately NOT abstracted, and why this is the load-bearing judgement:** `PublishExecutor` (~1 200 lines) and `PruneExecutor` stay Confluence-specific, and the repo target gets a *sibling* executor off the same `PublishReport`. A single interface spanning "upsert a page over REST v2" and "write `_meta/STATUS.md`" would describe neither, and the §6.2 semantics it would have to generalize over (page versions, attachment hashes, bodyless reparenting) have no repo-side meaning. Prune is sharper still: deleting a documented file is a git operation its author performs, so the repo target declines the capability rather than implementing a no-op.

**Two accepted losses that invert into gains:** spike S3 settled `approvedBy` at `"unknown"` because Confluence exposes nobody as a label's adder, and a PR review has an author, so repo mode satisfies §8's audit requirement instead of conceding it. And §10's Node + `beautiful-mermaid` prerequisite exists only because publish renders diagrams; repo mode renders none.

**Two spec consequences recorded before M8 can trip over them:** §10's trigger inverts, because a repo-mode publish writes into the working tree and so must run inside the docs PR rather than on merge (the failure contract is unchanged). And §8's gesture is coarser than its record: one PR approval covering six pages is recorded as six per-page approvals sharing a timestamp and author, because the alternative would put a non-page into `state.pages`.

**Risk being accepted, stated plainly:** M7a is a refactor with one implementation, which is the classic way to get an abstraction wrong. The mitigation is scope — it extracts interfaces around boundaries that already exist as separate types, adds no second implementation, and is gated on the existing suite passing unedited. If M8 finds the shape wrong, one caller has to move.

**Where the spec lives, and why not in `PLAN.md`:** `.claude/references/publication-targets.md`. Drafted as a `PLAN.md` §16 first and moved the same day, on two measurements. `PLAN.md` holds a 20,000-token budget (`tools/loop/check-state-size.py:125`) because it is on the build loop's step-1 read path, and it had ~2.7 KB of headroom against that before this work — less than the section needed, and a milestone spec two milestones out does not need re-reading every iteration. Separately, the §8 and §10 consequence notes were drafted into their sections and backed out: `PlanSemanticsTraceTests` and `PlanWorkflowTraceTests` require every §8/§9/§10 unit to be traced by exactly one claim pinning it to code, and a bullet describing unbuilt behaviour can be traced by nothing. Both notes wait in the reference doc and move into their sections at M8, each with its claim. The guard was right and is left unmodified.

**Evidence:** `.claude/references/publication-targets.md` (the spec); `PLAN.md` §1 non-goals (amended), §14 M7a + M8 rows; `src/DocuMe.Core/Config/ConfigLoader.cs:48-55` (the unconditional `confluence` requirement that blocks a repo-only config); `src/DocuMe.Core/Publishing/PublishPipeline.cs:46-51`; `src/DocuMe.Core/State/DocumeState.cs:26-30`; `tests/DocuMe.Core.Tests/Acceptance/PlanDataContractTests.cs:88` (why §5.1 and `DocumeConfig` must change in one commit); `tests/DocuMe.Core.Tests/Acceptance/PlanSemanticsTraceTests.cs:377`.

## 2026-08-13 — A business & process documentation tier: one skill, zero code (M9)

**Context:** Mirko, after reviewing the Inventhor Engineering Wiki: the technical tier is what he wanted, and the next level is "business and process documentation alongside this technical one — process flows, meaningful info for users, more user oriented." A market scan the same day found nobody shipping continuously refreshed, code-verified business documentation: the fragments are one-shot modernization business-rule extraction (Swimm post-pivot, CAST Imaging, EPAM ART), UI-recorded SOPs with no code grounding and silent staleness (Scribe; Tango pivoted away), and technical-only generators (DeepWiki, Komment, Mintlify). The two to watch both touch Confluence and are both technical-only: Dosu, which has diff-triggered doc sync, and Atlassian Rovo Dev, which owns the destination. Inventhor is also the internal proof of demand: its `website/docs/` end-user tier is hand-written inside feature commits (`git log` on `website/docs/time-off.md`) with no generation loop, no drift detection, no verification.

**Decision:** a second documentation tier as **one new skill plus a register contract, with no CLI, converter, schema or state change**. Business pages are ordinary pages in a consumer-named subtree of the same `wiki.root` (Inventhor: `40-business/`), so hierarchy, publish, `contentHash`, drift, labels, dashboard and the feedback inbox all apply to them unbuilt. Four contracts carry the tier: citations move into `<!-- cites: … -->` HTML comments (invisible to readers, greppable by `/docs-refresh` and `/docs-feedback`, and outside `contentHash` because the hash is over the converted body — the drop and the comment-under-its-paragraph form pinned by `tests/golden/html-comments.md`, the hash-invariance by `ContentHashTests`); the `⚠️` markers are banned on business pages, verified-or-absent, stricter than the technical tier because this reader cannot act on an uncertainty flag; facts not in code (legal intent, policy rationale) come only from a consumer-owned `_meta/BUSINESS.md`, cited like code, stub-created on first run per the `GAPS.md` precedent; and the unit is a process discovered from actors, state-machine enums and the handler groups that move them, tracked in `_meta/PROGRESS-BUSINESS.md` so `docs-loop`'s first-`todo` rule is untouched. The one cross-skill edit: `baselineSha` becomes the oldest generation sha across both progress files.

**Alternatives rejected:** a second Confluence space (cleaner separation, but `confluence.spaceKey` is a scalar with `additionalProperties: false` at every schema level, and the title-collision problem it solves is already loud — `publish` hard-errors listing duplicates); a `tier`/`audience` frontmatter field (directory placement plus the `STYLE.md` declaration already carry the fact; a new field is a §5.2 contract change with nothing reading it); folding the tier into `docs-loop` with a mode switch (the register, unit model and verification bar all differ — a mode switch that changes four contracts is a second skill wearing a flag); generating Inventhor's Docusaurus site directly (a publication-target question, M8's axis, deferred as S11).

**Risk accepted:** the register is the hard part, and an LLM drifts toward marketing prose. Mitigations: the benchmark acceptance (the generated time-off page must cover every rule the hand-written `website/docs/time-off.md` states, divergences listed as findings), the consumer-owned Business-tier section of `STYLE.md` winning over skill defaults, and the PR claims table keeping real code refs so a reviewer checks the page in minutes. Second risk: comment citations are invisible in Confluence, so a business page's *reader* cannot audit it the way an engineer audits the technical tier — accepted, because the reviewer of the PR can, and the reader could not have used a `path:line` anyway.

**Where the spec lives:** `.claude/references/business-tier.md`, for the two reasons the 2026-08-05 entry established: `PLAN.md`'s 20,000-token budget on the loop's step-1 read path, and a milestone spec not needing re-reading every iteration. `PLAN.md` gets the §14 M9 row and one dependencies sentence (M9 needs only M6).

**Evidence:** `.claude/references/business-tier.md` (the spec); `PLAN.md` §14 M9 row; `tests/golden/html-comments.md` + `.storage.xml` (comment-drop behaviour, inline residue, and the reason block-form citations are the convention); `PLAN.md` §5.3 (`contentHash` over converted body) and §6.2 step 1 (duplicate-title hard error); `src/DocuMe.Core/Publishing/PageHierarchy.cs` (subtree-for-free); `plugin/skills/docs-loop/SKILL.md` step 6 (the `baselineSha` rule M9 amends); `~/Dev/inventhor-docume/website/docs/time-off.md` (the benchmark page and register model).
