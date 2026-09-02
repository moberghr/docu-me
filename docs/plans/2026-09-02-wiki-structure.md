# Plan — Tree shape: structure lint and page moves

Spec: `docs/specs/2026-09-02-wiki-structure.md` · Sidecar: `docs/specs/2026-09-02-wiki-structure.json`
Base: `origin/main` @ `a4bd26b` (carries the v0.3.0 bump; the tag is still unpushed).
Branch: `feat/wiki-structure`.
Baseline measured 2026-09-02: build **0 warnings**, suite **1725 / 0 failed / 0 skipped** (Release, `npm ci` done).

The sidecar already carries a three-batch plan. Its shape is right and this plan keeps it. What follows
records the four places its file manifest does not survive contact with the tree, and pins the contracts
each batch has to hit.

## Manifest corrections

**1. `PublishPipeline.cs` is missing, and it owns the spec's central risk.** §3.3 says the rekey happens
"in `PublishPlanner`, before the orphan comparison". `PublishPlanner.PlanPage`
(`src/DocuMe.Core/State/PublishPlan.cs:97`) is per-page and pure, so it cannot host a whole-state rekey.
The orphan set is computed at `src/DocuMe.Core/Publishing/PublishPipeline.cs:163`
(`PublishPlanner.OrphanPages`), and the per-page state lookup that a moved page must find its inherited
entry through happens on the way into `PlanOne` at `:252`. The rekey therefore runs **once, at the top of
the pipeline, before both**. `PublishPipeline.cs` joins batch 2; the ordering test asserts against it.

**2. `PageLinkResolver.cs` is one line and cannot be modified into a feature.** It is a delegate
declaration (`src/DocuMe.Core/Markdown/PageLinkResolver.cs:15`). The delegate is constructed in
`src/DocuMe.Core/Markdown/WikiTree.cs:198`, which is where a tombstone lookup goes. `WikiTree.cs` joins
batch 2.

**3. Two test files in the manifest do not exist.** `tests/.../Plugin/SkillManifestTests.cs` is really
three files — `PluginManifestTests.cs`, `SkillContractTests.cs`, `SkillsReferencePageTests.cs`.
`tests/.../Markdown/PageLinkResolverTests.cs` does not exist and there is no link-resolution test file at
all, so SC7 needs a new one written against `WikiTree`.

**4. Batch 3 under-scopes the fifth skill.** "Four" is hardcoded in
`tests/DocuMe.Core.Tests/Plugin/SkillContractTests.cs:41` (the `Skills` array, and the `BranchPrefixes`
dictionary beside it), `docs/wiki/30-automation/skills.md:10` and `:12`, and `PLAN.md:394`. M10's own
commit — "every page and count that still said six" — is the precedent for how far this ripples. Batch 3
is ~10 files, not 5.

## Batch 1 — The tree has a shape and status says so

**Files:** `StructureReport.cs` (new), `DocumeConfig.cs`, `schema/docume.schema.json`, `StatusReport.cs`,
`StatusModel.cs`, `StatusCommand.cs`, `StructureReportTests.cs` (new), `StatusModelTests.cs`,
`ConfigLoaderTests.cs`.

**Acceptance:** SC1–SC4. Read-only: writes nothing to Confluence and nothing to state.

**Boundary:** reports the shape. Changes no page and moves nothing.

**Pinned contract:**

```csharp
public sealed record StructureFinding(string Kind, string Directory, int PageCount, string? ResolvedAncestor);
public static StructureReport Of(IReadOnlySet<string> paths, string? homePage, int maxChildren);
public int MaxChildren { get; init; } = 12;   // WikiConfig
```

`Of` takes the same two inputs `PageHierarchy.Resolve` takes (`PageHierarchy.cs:49`) plus the number, so
it is pure by construction and runs under `--offline`. It lands in `StatusModel.Build`, never in
`StatusProbes` — that class is the I/O half and stays that way. Outcome is `Warning`
(`StatusReport.cs:65`), never `Problem`.

The detail line names the exact `README.md` to create. That sentence is the whole intervention: seventeen
files nobody wrote because nothing asked for them by name.

## Batch 2 — A page keeps its identity through a move

**Files:** `PageFrontmatter.cs`, `FrontmatterParser.cs`, `DocumeState.cs`, `PublishPlan.cs`,
**`PublishPipeline.cs`**, `PrunePlan.cs`, `PublishExecutor.cs`, `StateRebuilder.cs`, **`WikiTree.cs`**,
plus `FrontmatterParserTests.cs`, `PublishPlannerTests.cs`, `PrunePlannerTests.cs`,
`StateRebuilderTests.cs`, and a new `WikiTreeLinkTests.cs`.

**Acceptance:** SC5–SC9.

**Boundary:** the CLI can move a page. No skill asks it to yet.

**Pinned contract:**

```csharp
public string? MovedFrom { get; init; }   // PageFrontmatter — wiki-root-relative, blank collapses to null
public string? MovedTo   { get; init; }   // PageState — tombstone row: no pageId, never published, never adopted
public sealed record PagePublishPlan(string Path, string Title, PagePublishAction Action, …);
```

**The invariant, and it is the batch.** The rekey runs before `OrphanPages` and before any per-page state
lookup. A `movedFrom` consumed after `PrunePlanner` reads state presents the old path as an orphan, and a
`--prune` in the same session deletes the page the move existed to preserve. This gets a test that asserts
the ordering directly — a `--prune`-armed plan over a moved page, asserting the old path never reaches
`PlannedPrune` — not a comment.

Three edits carry the rest of the risk:

- **`PagePublishPlan` gains `Title`** (`PublishPlan.cs:54`), which touches all five construction sites in
  that file and every test that builds one. Mechanical, wide, do it first inside the batch.
- **The executor's `Move` branch grows a second shape** (`PublishExecutor.cs:602`). Today it builds
  `ConfluencePageMove(pageId, Append, target)` — position only, no title, which confirms §3.6's
  assumption. A title change is a titled update against the same id. Approval is untouched on both paths
  because neither changes the body, and that needs its own assertion rather than an argument.
- **`StateRebuilder` must skip tombstones** (`StateRebuilder.cs`). It adopts by title; a tombstone adopted
  as a page yields two state entries holding one page id and a duplicate on the next publish.

The five ambiguous claims of §3.5 throw at plan time with the offending paths named. A `movedFrom`
resolving to nothing warns and plans as `Create`.

## Batch 3 — `/docs-restructure` owns tree shape

**Files:** `plugin/skills/docs-restructure/SKILL.md` (new), `plugin/skills/docs-loop/SKILL.md`,
`SkillContractTests.cs`, `SkillsReferencePageTests.cs`, `PluginManifestTests.cs` (if it counts),
`docs/wiki/30-automation/skills.md`, `PLAN.md`, `README.md`, `CHANGELOG.md`.

**Acceptance:** SC10, plus every "four" above reads "five" and the suite proves it.

**Boundary:** documentation and skill prose. No production code.

`docs-loop/SKILL.md:389` changes from "Propose it in the PR body and let a human decide" to a pointer at
the new skill. The new SKILL.md carries the §1.3 untrusted-input contract like every other skill, and its
three refusals are the load-bearing part: no page prose, no invented taxonomy, no page moved to improve it.

## Sequencing and verification

Batches are independent enough to review separately and are ordered by risk, not by dependency: 1 is pure
and read-only, 2 touches the write path, 3 is prose. Between each, `dotnet build` + `dotnet test` green
against the 1725 baseline, and no batch commits red (§8.3).

Two things to settle before batch 2 starts, both cheap to answer in code:

- **Does anything else construct `PagePublishPlan` outside `PublishPlan.cs`?** If the tests do, the `Title`
  addition is wider than the manifest says.
- **Does `docume convert` need the structure check too?** The spec puts it in `status` only. `convert`
  reported 114 pages and 0 failures on a tree that was 54-pages-flat, so it is the other place a reader
  would expect to be told. Out of scope as written; worth one line in the PR body.

## Note on the release

v0.3.0 is bumped on `main` and still untagged, because GitHub Actions is refused for billing. This work
lands after it either way; if the tag goes up first, this becomes 0.4.0 material.
