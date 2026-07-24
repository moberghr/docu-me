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

## 2026-07-24 — Custom Markdig renderer with golden-file contract

**Context:** Markdown → Confluence storage-format conversion is the highest-risk component.
**Decision:** Custom `ConfluenceStorageRenderer` (never md→html→regex); golden files reviewed by hand once, asserted forever.
**Evidence:** `PLAN.md` §7.

## 2026-07-24 — Regeneration runs both locally and in CI

**Context:** Where `/docs-refresh` runs.
**Decision:** Both a nightly CI cron (headless Claude) and locally on demand.
**Evidence:** `PLAN.md` §1 locked decisions, §10.
