---
description: DocuMe lifecycle invariants — rules unique to this product
---

# Project-Specific Rules

- **§9.1 [CONVENTION]** The repo is the source of truth; hand edits in Confluence are lost on republish. Never build features that read Confluence page bodies back as a content source (`PLAN.md` §1, §13 S6).
- **§9.2 [CONVENTION]** `contentHash` excludes the injected banner; banner-only or machine edits must never invalidate approval (`PLAN.md` §8).
- **§9.3 [CONVENTION]** Staleness marking uses labels + dashboard only — never page-body edits (no page-version churn, no approval disturbance) (`PLAN.md` §6.4).
- **§9.4 [CONVENTION]** `docume init` is idempotent: never overwrite existing consumer files; report skips (`PLAN.md` §6.1).
- **§9.5 [CONVENTION]** Repo-specific knowledge (domain list, tone, audience, structure) lives in the consumer repo (`docume.json`, `_meta/STYLE.md`, frontmatter); the tool and skills stay generic (`PLAN.md` §1).
- **§9.6 [CONVENTION]** Orphan deletion (`publish --prune`) requires interactive confirmation and never runs in CI (`PLAN.md` §6.2).
- **§9.7 [CONVENTION]** Autonomous build loop bookkeeping: update `tools/loop/state.json` every iteration; human gates live in `GATES.md` as `- [ ]` checkboxes mirrored into state.
