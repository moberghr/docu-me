# Plan — Sealed source verdicts

Spec: `docs/specs/2026-08-19-sealed-source-verdicts.md` · Sidecar: `docs/specs/2026-08-19-sealed-source-verdicts.json`
Branch: `borrow/8-sealed-verdicts` (base `origin/main` @ 6accfb8) · Baseline: build 0 warnings, suite 1593/0/7.

Rigor: **MAX** (score 14 — 3 batches, 20 non-mechanical files → +7, 6 external contracts → +4 capped).
Hard-trigger floor HIGH independently (3 batches, ≥6 non-mechanical files). Subagent path, one fresh
implementer per batch, orchestrator drift micro-check between them.

## Batch 1 — Seal at publish

**Files:** `SourcesFingerprint.cs` (new), `DocumeState.cs`, `StateUpdates.cs`, `PublishExecutor.cs`,
`SourcesFingerprintTests.cs` (new), `StateUpdatesTests.cs`, `StateStoreTests.cs`, `PublishExecutorTests.cs`.

**Acceptance:** SC4, SC5, SC6. Build 0 warnings; State, Publishing and Drift test directories green.

**Boundary:** this batch only *writes* the seal. Nothing reads it yet, so drift's behavior is
byte-identical to main at the end of it — a property the checkpoint asserts by running the existing
`CliDriftTests` unchanged.

**Pinned contract** (implementer may not renegotiate):

```csharp
// preimage, ordinal by path:  "<path>\n<sha256-of-bytes>\n" concatenated, then ContentHash.OfBytes
public static string Of(IEnumerable<(string Path, string Hash)> files);
public static string Compute(string root, IReadOnlyCollection<string> patterns);  // reader half
```

`SealedVerdict` record: `SourcesHash`, `SealedAt`, `RepoSha` — all `string?`, `init`-only, camelCase on
the wire like every other state field. `PublishedPage` gains the fingerprint **positionally**, for the
reason `DiagramWidths` is positional (`StateUpdates.cs:16`): an optional property is one a caller
forgets and silently erases.

## Batch 2 — Make it real (added after batch 1, approved 2026-08-19)

**Files:** `SourcesFingerprint.cs`, `GitRepository.cs`, `PublishExecutor.cs`, `PublishCommand.cs`,
`SourcesFingerprintTests.cs`, `GitRepositoryTests.cs`, `CliPublishTests.cs`.

**Acceptance:** SC11, SC12; SC4 still pinned against constants after the contract change.

**Why it exists:** batch 1 reported two defects in the approved manifest, both verified against the
code. (C) Nothing wired `PublishExecutionOptions.Sealing`, so the feature would ship inert — `Sealing`
occurs only inside `PublishExecutor.cs`. (F) `Compute` walked the real directory, so it saw `bin/`,
`obj/` and every gitignored path, while drift's list comes from `git diff`, which never reports them;
a page with a broad glob would seal build output and never match again after a rebuild.

**Contract change** (supersedes batch 1's pinned reader half):

```csharp
public static string Compute(
    string root,
    IReadOnlyCollection<string> patterns,
    IReadOnlyCollection<string> candidateFiles);   // repo-relative, forward slashes — git's tracked files
```

`GitRepository.TrackedFilesAsync(root, ct)` supplies them (`git ls-files`), in the shape
`ChangedFilesBetweenAsync` already returns. The seal and the diff then see the same universe by
construction rather than by two implementations agreeing.

**Boundary:** turns the seal on and fixes its input. Still does not *read* the seal — that is batch 3.

## Batch 3 — Check the seal in drift

**Files:** `SealedVerdicts.cs` (new), `DriftReport.cs`, `DriftComment.cs`, `DriftCommand.cs`,
`SealedVerdictsTests.cs` (new), `DriftCommentTests.cs`, `DriftMarkPlannerTests.cs`, `CliDriftTests.cs`.

**Acceptance:** SC1, SC2, SC3, SC7, SC8, SC9. Full suite green.

**Boundary:** `DriftPlanner` is not touched. Its doc comment leads with "No git in here" and the same
discipline applies to the file system — the seal check is a separate pure pass the CLI applies, the
way `IgnoredCommitCount` is stamped on at `DriftCommand.cs:370`.

**Pinned contract:**

```csharp
public static DriftReport Apply(
    DriftReport report,
    IReadOnlyDictionary<string, string> sealsByPath,      // page path → sealed sourcesHash
    IReadOnlyDictionary<string, string> currentByPath);   // page path → freshly computed hash
```

A path missing from either dictionary is **not** sealed. That is the safe direction and SC9's subject:
an unreadable source tree must not suppress a drift report.

## Batch 4 — Contracts and prose

**Files:** `docs/wiki/20-reference/configuration.md`, `docs/wiki/10-concepts/approval-and-drift.md`,
`PLAN.md`, `CHANGELOG.md`.

**Acceptance:** SC10, plus every tree-sweeping contract test green
(`ConfigReferencePageTests`, `ChangelogTests`, `ConfigFieldSurfaceTests`).

**Boundary:** prose only. If a contract test demands a source change, that is drift — stop and report
rather than editing source in a prose batch.

## Verification

- Per batch: `dotnet build` (0 warnings, warnings-as-errors) then the batch's test directories.
- Batch close and final: `python3 tools/loop/run-suite.py` — never `dotnet test | tail`, which drops
  the failure lines above the summary. Expect **≥1593**; a drop means a test was lost.
- 7 env-gated skips are the known live-sandbox opt-ins (§4.2) and are the baseline, not a regression.

## Commit shape

**One commit** on `borrow/8-sealed-verdicts` at the end, not one per batch. Batch 1 established why:
the state-field documentation contracts (`ConfigReferencePageTests`, `PlanDataContractTests`) fire the
moment `PageState.Verdict` exists, and their fix lives in batch 4, so no intermediate batch can be
committed green. Rule §8.3 forbids committing a red build; committing once at the end satisfies it
without weakening any batch's own checkpoint.

Per-batch checkpoint bar for batches 1-3: `dotnet build` clean, the batch's own tests green, and **no
failure other than** the three known documentation contracts
(`The_page_documents_every_field_the_state_records_carry`, and the two `PlanDataContractTests`).
Batch 4 closes all three and the suite must be fully green before the PR.
