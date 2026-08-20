# Plan — Page owners and drift routing

Spec: `docs/specs/2026-08-20-page-owners.md` · Sidecar: `docs/specs/2026-08-20-page-owners.json`
Branch: `borrow/9-page-owners` (base `origin/main` @ 5fb14b4, which carries round 8's seal).
Baseline: build 0 warnings, suite **1667 / 0 failed / 7 env-gated skips**.

Rigor: **MAX** (score 14 — 3 batches, 19 non-mechanical files → +7, 6 external contracts → +4 capped).
Subagent path, one fresh implementer per batch, orchestrator drift check between them.

## Batch 1 — `owner:` reaches the report

**Files:** `PageFrontmatter.cs`, `FrontmatterParser.cs`, `DriftReport.cs`, `DriftPlanner.cs`,
`FrontmatterParserTests.cs`, `DriftPlannerTests.cs`.

**Acceptance:** SC1, SC2, SC3.

**Boundary:** parses and carries the owner; renders it nowhere. Drift's *findings* must not move —
the existing `DriftPlannerTests` and `CliDriftTests` pass unchanged.

**Pinned contract:**

```csharp
public string? Owner { get; init; }          // PageFrontmatter — verbatim, never normalized
public sealed record DriftedPage(string Path, string Title, string? Owner, IReadOnlyList<SourceMatch> Matches);
public int UnownedCount { get; init; }       // DriftReport — affected pages with no owner
```

`Owner` follows `Title`/`PageId`'s existing precedent in the parser: blank collapses to null. It is
**never** trimmed beyond what YAML already does, never lowercased, never prefixed.

## Batch 2 — Routing: the comment, the verdict, the dashboard

**Files:** `DriftComment.cs`, `DriftCommand.cs`, `StatusReport.cs`, `StatusProbes.cs`,
`DashboardPage.cs`, `DriftCommentTests.cs`, `DashboardPageTests.cs`, `StatusModelTests.cs`,
`CliDriftTests.cs`.

**Acceptance:** SC4–SC10.

**Boundary:** renders the owner. Does not change what drift finds, and does not touch sealing or the
exemption disclosures on either side of `WriteBody`.

**The property that matters:** grouping is a **partition, not a filter**. Group sizes must sum to
`AffectedCount`, every affected page appears exactly once, and a test asserts that rather than trusting
it. A grouping bug that dropped a page would hide exactly the drift this feature exists to route — the
same failure shape as round 8's Critical, in a different costume.

Ordering is ordinal by owner with unowned last, because the comment is rewritten in place by a bot and
must be a function of its inputs alone.

## Batch 3 — Contracts and prose

**Files:** `PLAN.md`, `docs/wiki/20-reference/configuration.md`,
`docs/wiki/10-concepts/approval-and-drift.md`, `CHANGELOG.md`.

**Acceptance:** SC11, fully green suite.

`PlanDataContractTests` maps §5.2 to `PageFrontmatter` and requires two things of a new member: a line in
§5.2's YAML block, **and** a real dotted read somewhere in `src/`. Round 8 learned the second half the
hard way — a property-pattern read (`Frontmatter is { Owner: ... }`) is invisible to the scanner. Batch 2
must therefore read it as `page.Parsed.Frontmatter.Owner` somewhere, which the grouping does naturally.

## Verification

- Per batch: `dotnet build` (0 warnings, warnings-as-errors) then the batch's test directories.
- Close: `python3 tools/loop/run-suite.py` — never `dotnet test | tail`. Expect ≥ 1667, failed=0 at the
  end of batch 3. The 7 env-gated skips are the baseline, not a regression.

## Commit shape

One commit at the end, as round 8 established: the §5.2 contract test reddens the moment
`PageFrontmatter.Owner` exists and its fix is batch 3's file, so no intermediate batch can be committed
green (§8.3 forbids committing red). Per-batch bar for batches 1–2: build clean, batch tests green, and
no failure other than the known `PlanDataContractTests` §5.2 pair.
