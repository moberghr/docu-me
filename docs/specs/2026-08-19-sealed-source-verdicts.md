# Sealed source verdicts

> Borrow loop round 8. Borrowed from Orthic-Labs/Cortex (sealed-verdict caching: a verdict keyed by
> a content fingerprint is reused until the fingerprint moves). Round-7 research recorded it as the
> unbuilt third of DocuMe's differentiating trio — receipts, sealed verdicts, steady-state zero cost.

Date: 2026-08-19 · Scope: new-feature · Baseline area: drift

## 1. The problem

`docume drift` is **range-addressed**. It asks git for the files that changed between
`state.baselineSha` and `HEAD`, matches them against each page's `sources` globs, and reports every
page that matched (`DriftPlanner.Plan`, `src/DocuMe.Core/Drift/DriftPlanner.cs:46`). Three
consequences follow from the range being the only evidence:

1. **One baseline serves the whole wiki.** `baselineSha` is a single field on `DocumeState`
   (`src/DocuMe.Core/State/DocumeState.cs:14`). A page published this morning and a page published in
   March are diffed from the same commit, so the older page's answer is right and the newer page's is
   noise — its sources "changed" in commits that are already in its published body.
2. **A revert inside the range still drifts.** Touch `src/Loans/Rate.cs`, revert it in the next
   commit, and the flat diff reports the file as changed. The page is labelled stale, a reviewer
   opens it, and nothing about the code it documents has moved.
3. **The two existing narrowings are both declarations.** `_meta/drift-ignore` (paths) and
   `_meta/drift-ignore-revs` (commits) require a human to have written the exemption down in advance.
   Neither can answer the question the machine can answer for free: *are the bytes this page was
   published against the same bytes that are there now?*

## 2. The change

Publish **seals** a per-page fingerprint of the source bytes; drift **checks the seal** before
reporting.

- At the moment a publish writes a body, it also records the fingerprint of every file that page's
  `sources` globs match, into the page's state entry, alongside when it was sealed and the repo sha.
- `docume drift` recomputes that fingerprint — for the flagged pages only — and holds out every page
  whose fingerprint equals its seal. Those pages are reported as `SEALED` in every output format,
  counted out of the exit code, and never labelled by `--mark`.
- A page with no seal keeps today's behavior exactly. The feature is additive to a wiki that has
  never published under it, and self-heals on the next publish of each page.

The verdict is a *computed* exemption, in the same shape as the two *declared* ones: narrowed inputs
are always disclosed, never silent.

### What sealing does and does not claim

The seal says: **these were the source bytes when the live body was published.** It does not claim
the body is correct, and it is not an approval — approval is §8 and untouched.

A publish that is a no-op (body unchanged) writes no new seal. That is correct rather than a gap: the
live body is still the body generated from the older sources, so its seal must keep pointing at them,
and a page whose sources moved under an unchanged body is exactly the drift that should fire.

## 3. Design

### 3.1 `SourcesFingerprint` (new, `src/DocuMe.Core/Drift/`)

```
sha256( for each matched file, ordinal by path: "<path>\n<sha256-of-bytes>\n" )
```

- Globbing is `DriftPlanner.BuildMatcher` — **the one shared matcher seam** (`:183`). A second glob
  implementation would eventually disagree with the first about what `**` means, which is the reason
  that method is already `internal` rather than private.
- **The candidate file list is supplied, never walked.** [AMENDED 2026-08-19 after batch 1, approved]
  `Compute` takes the files it may match, exactly as `DriftPlanner.Plan` takes its changed-file list,
  and the caller supplies git's tracked files. A tree walk was the first implementation and it is
  wrong in one specific, silent way: drift's file list comes from `git diff`, which never reports a
  gitignored path, while a walk sees `bin/` and `obj/`. A page with `sources: ["src/**"]` in a repo
  that builds in-tree would then seal a fingerprint containing build output and never match again
  after a rebuild — the feature would fail safe and no-op for the commonest glob shape. Taking the
  list makes the two halves symmetric by construction rather than by two implementations agreeing.
- Per-file bytes are hashed with `ContentHash.OfBytes` verbatim, no normalization: source files are
  matched by `sources` globs and may be binary, and `ContentHash` already documents why attachment
  bytes are not normalized (`src/DocuMe.Core/State/ContentHash.cs:38`).
- The path goes into the preimage, not just the bytes, so renaming a file to a name the same glob
  still matches moves the fingerprint.
- Spelling is `sha256:` + 64 lowercase hex, the one spelling §5.3 uses for every stored hash.
- **A page whose globs match no file seals nothing at all.** [REVISED 2026-08-19 after review, approved]
  This bullet used to say the opposite: an empty match "seals as the empty-set fingerprint, which is a
  real value, not null: it distinguishes 'documented nothing, deliberately' from 'never sealed'". The
  distinction is real and it is not worth what it costs. Every *structural* way of matching zero files
  produces the one constant `sha256:e3b0c442…b855` — a glob with a typo in it, a page pointed at a
  directory that was renamed, a candidate list `git ls-files` answered empty, a sparse-checkout CI job
  cone'd to `docs/`. A later drift run under the same structural condition recomputes that same constant,
  `SealedVerdicts.Apply` sees a byte-for-byte match, and the page leaves `Pages` — out of `HasDrift`, out
  of `AffectedCount`, out of `--fail-on-drift`, out of `--mark`. A page whose sources were never read
  would be reported as verified, which is the one direction §3.3 exists to refuse. It is also
  inconsistent with how this codebase already treats a dead glob: `DriftPlanner.NormalizePattern`'s doc
  comment calls one "the one failure mode of an advisory check that gets believed: nobody investigates a
  green run." So the rule is symmetric — **never write an empty-set seal, and never let an empty-set
  value count as a match**, so an older state file carrying one cannot suppress a report either. Publish
  warns when a page *declared* globs and matched nothing (a dead glob is worth investigating) and is
  quiet when it declared none (drift never looks at such a page). `SourcesFingerprint.Of([])` is
  unchanged and still pinned by test: the pure function's value is right, it is simply never a verdict.
- Split in two, following `DriftPlanner`'s own split: a pure combiner over `(path, hash)` pairs, and a
  reader that walks a root. The pure half is what tests pin.

### 3.2 State (`PageState.Verdict`)

```json
"verdict": {
  "sourcesHash": "sha256:…",
  "sealedAt": "2026-08-19T09:12:44Z",
  "repoSha": "6accfb8…"
}
```

Owned by publish, like `contentHash` beside it. Absent on every page published before this change,
and on any page whose globs the publish could not read.

### 3.3 Drift consumption

`DriftPlanner` stays pure and stays file-system-free — the property its own doc comment leads with.
The seal check therefore lands as a second pure function applied after it, exactly as
`IgnoredCommitCount` is stamped on by the CLI where the narrowing happened
(`src/DocuMe.Cli/Commands/DriftCommand.cs:370`):

1. `DriftCommand` computes current fingerprints **for the flagged pages only** — the report is
   already narrowed to them, so a wiki with 400 pages and 2 flagged reads 2 pages' worth of sources.
2. `SealedVerdicts.Apply(report, sealsByPath, currentByPath)` moves each page whose current
   fingerprint equals its seal out of `Pages` and into `Sealed`.
3. `HasDrift`, `AffectedCount`, `--fail-on-drift` and `DriftMarkPlanner` all read `Pages`, so they
   inherit the exclusion by construction rather than by a second rule that could disagree.

A page whose sources cannot be read (deleted directory, permission) is **never** sealed silently — it
stays in `Pages`, which is the safe direction: an unreadable seal must not suppress a report. The same
answer covers a `git ls-files` that succeeds and returns nothing: an empty candidate list is an unusable
answer, not a small one, so both `PublishCommand.SealingAsync` and `DriftCommand.SealedAsync` treat
`tracked.Count == 0` exactly as they treat a `GitException`.

### 3.4 Disclosure

| Format | Disclosure |
|---|---|
| table | `SEALED — n page(s) whose sources are byte-identical to their seal`, one line per page with the seal date |
| json | `sealed[]` array, `DocumeJson.Options` like everything else |
| github-comment | a `SEALED` section mirroring the exempt section's shape |
| `--mark` | sealed pages are not in `Pages`, so they are never labelled; the skip is visible in the table above the plan |

## 4. Success criteria

| id | description | verification | observable |
|---|---|---|---|
| SC1 | A page whose sources are byte-identical to its seal is reported SEALED, not drifted, when the diff touched them | `SealedVerdictsTests` | report.Pages excludes it; report.Sealed includes it |
| SC2 | A page whose sources actually changed is unaffected by the seal | `SealedVerdictsTests` | still in report.Pages |
| SC3 | A page with no seal keeps today's range-based answer | `SealedVerdictsTests`, `CliDriftTests` | in report.Pages, absent from report.Sealed |
| SC4 | Fingerprint is deterministic, ordinal, and moves on rename, content change, addition and deletion | `SourcesFingerprintTests` | pinned constants |
| SC5 | Publish writes the seal at the moment it writes contentHash; a no-op publish leaves the previous seal standing | `PublishExecutorTests`, `StateUpdatesTests` | state.pages[p].verdict |
| SC6 | The seal round-trips through `StateStore` and an older state file without `verdict` loads unchanged | `StateStoreTests` | load/save equality |
| SC7 | Sealed pages are disclosed in table, json and github-comment, and counted out of the exit code | `CliDriftTests`, `DriftCommentTests` | exit 0 under `--fail-on-drift` when every drifted page is sealed |
| SC8 | `--mark` never labels a sealed page | `DriftMarkPlannerTests`, `CliDriftTests` | no label request for a sealed path |
| SC9 | An unreadable source tree does not seal a page silently | `SealedVerdictsTests` | page stays in Pages |
| SC10 | The new state field is documented where the self-enforcing tests demand | `ConfigReferencePageTests` | suite green |

## 4b. Amendments after batch 1 (approved 2026-08-19)

Batch 1 shipped the seal and reported three defects in this spec. Two were scope-bearing and were
approved at a re-opened gate; the third is recorded as intended behavior.

| # | Defect | Resolution |
|---|---|---|
| C | **Nothing turns the feature on.** No batch touched `PublishCommand.cs`, so `PublishExecutionOptions.Sealing` stays null and a shipped `docume publish` seals nothing. Verified: `Sealing` occurs only inside `PublishExecutor.cs`. | New batch 2 wires it. SC11 below. |
| F | **Gitignore asymmetry** (see §3.1 amendment). | `Compute` takes the candidate list; `GitRepository` supplies tracked files. SC12 below. |
| E | Null-seal semantics were stated only for a no-op publish, which never reaches `RecordPublish`. `Move` and `UpdateAttachments` do. | Confirmed as batch 1 implemented it: **null carries the previous seal through, never erases it.** A page that moved still describes the sources it was generated from. Pinned by test, not left to the reader. |
| B | Manifest was short the two files a positional record parameter forces (`PublishPipelineTests.cs`, `StatusModelTests.cs`). | Recorded as mechanical manifest entries; no ceremony change. |
| A | Batch 1's checkpoint could not be green: the state-field documentation contracts fire the moment `PageState.Verdict` exists. | Not a defect in the work. Round 8 is one commit, so no batch is ever committed red (§8.3); the checkpoint bar for batches 1-3 is "build clean, no failure other than the known documentation contracts", and the final batch closes them. |

| id | description | verification |
|---|---|---|
| SC11 | A real `docume publish` seals: the CLI supplies repo root, per-page globs and tracked files, and the written state carries a verdict | `CliPublishTests` |
| SC12 | A gitignored file under a page's glob is never in the fingerprint, so the same tree fingerprints identically before and after a build | `SourcesFingerprintTests`, `GitRepositoryTests` |

## 5. Out of scope

- No new CLI flag. The seal is not defeasible by a switch: it only ever removes pages whose bytes are
  provably identical, and the declared exemptions already cover "ignore this on purpose".
- No re-seal verb (`docume seal`) — publish is the only sealer this round.
- No fingerprint of the page body against sources (per-claim citation verification, backlog item).
- No sealed-verdict reuse in `/docs-refresh` skill prompts — CLI only this round.
- No change to `contentHash`, approval, or the banner (rules §9.2, §8 untouched).
- No change to `baselineSha` semantics; the range still produces the candidate set.

## 6. Risks

1. **Cost on a page with a broad glob.** `sources: ["src/**"]` fingerprints the whole tree on every
   drift run that flags it. Mitigated by computing only for flagged pages, but a repo where one page
   claims everything pays for it. Accepted: it is one hash pass over files git just reported changed
   as documented, and the alternative is the phantom-drift status quo.
2. **A seal is only as honest as the publish that wrote it.** A publish from a dirty working tree
   seals uncommitted bytes. The `repoSha` field is what makes that auditable after the fact.
3. **Fingerprint spelling is pinned forever.** Changing the preimage would unseal every page at once —
   the same failure mode `ContentHash` documents, and the reason SC4 pins constants.
4. **The seal covers publish-time bytes, not the bytes the prose was verified against.** Concretely:
   `/docs-loop` writes a page on Monday against `Rate.cs` v1; `Rate.cs` becomes v2 on main on Tuesday;
   the docs PR merges and publishes on Wednesday, so the seal is `F(Rate v2)` — bytes the prose never
   described. A later drift run recomputes `F(Rate v2)`, matches, and holds the page out, and the stale
   prose never surfaces. This is sanctioned by §2 (the seal claims the bytes at publish, never that the
   body is correct) and it is not a bug, but it is the one shape where the safe-by-construction argument
   does not hold: everywhere else a page held out is a page whose sources genuinely did not move, and
   here it is a page whose sources moved between generation and publish. Not mitigated this round. The
   fix is a fingerprint the *generator* records — per-claim citation verification, already in §5 as a
   backlog item — and until then the narrowing is a merge-latency window, so the exposure is the age of
   a docs PR.

## 7. Assumptions

- `[VERIFIED:src/DocuMe.Core/Drift/DriftPlanner.cs:183]` `BuildMatcher` is `internal` and reusable as
  the single glob seam.
- `[VERIFIED:src/DocuMe.Core/State/StateUpdates.cs:50]` `RecordPublish` is the one transition that
  writes `contentHash`, so it is the one place a seal can be written consistently.
- `[VERIFIED:tests/DocuMe.Core.Tests/Config/ConfigReferencePageTests.cs:65]`
  `The_page_documents_every_field_the_state_records_carry` makes documenting the new state field a
  build-breaking requirement, not a nicety.
- `[VERIFIED:src/DocuMe.Cli/Commands/DriftCommand.cs:370]` the report is post-processed by the CLI
  already, so a second pure pass needs no new architecture.
