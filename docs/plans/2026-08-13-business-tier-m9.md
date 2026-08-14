# Plan — M9 Business & Process Tier (docs-processes skill)

Spec: `.claude/references/business-tier.md` (engineer-supplied, adopted per Phase 0.7 — not rewritten).
Sidecar: `docs/specs/2026-08-13-business-tier-m9.json`. Todo: `tasks/todo.md`.
Rigor: HIGH (score 8 — 3 batches, 10 files, 1 external contract; hard floor also trips on batches ≥ 3
and non-mechanical files ≥ 6). Subagent path, Opus implementers (engineer's explicit directive).

## What the spec's D1–D5 turned out to also require

Reconnaissance against the guard tests widened the manifest beyond the spec's deliverables list:

- `SkillContractTests.Every_skill_that_ships_is_one_this_class_checks` hard-fails on a fourth
  directory until it is added to `Skills` and `BranchPrefixes` (`SkillContractTests.cs:146`). Adding it
  subjects the new SKILL.md to every per-skill check: untrusted-input + claims-to-verify phrases,
  `_meta/STYLE.md` named, `docume` invoked, no REST path fragments, no `docume publish` / `--reply`
  inside bash fences, branch prefix literal present, frontmatter name == directory, description > 40 chars.
- `SkillsReferencePageTests` derives its sets from the shipped directory: the wiki page
  `docs/wiki/30-automation/skills.md` must gain a table row and a `## `/docs-processes`` section, the
  intro's spelled-out count moves three→four, and — the trap — each `EmptyRunConditions` token must
  identify its skill **alone across all shipped SKILL.mds**. docs-loop's token is the bare string
  `todo`, which the new skill's inventory vocabulary (`todo/done/blocked/dropped`, same as PROGRESS.md
  by spec) inevitably contains. So docs-loop's token must change to a longer phrase unique to
  docs-loop (candidate: `Nothing is ``todo``` from its edge-case heading), the new skill gets its own
  unique token, and both page cells must carry their tokens. `BaselineStamps` gains
  `("docs-processes", "oldest")` — the new skill carries system-contract clause 7 verbatim, which makes
  it a `baselineSha` writer in that test's derivation, so its page section must also name the field.
- `PLAN.md:376` (§11) says "The three skills are the three directories" — stale the moment the fourth
  ships; §11 also gets one docs-processes bullet. PLAN.md sits under a 20k-token loop budget
  (`tools/loop/check-state-size.py`), so the edit stays terse and the checker runs after.
- `CHANGELOG.md` entry is conditional: inspect `ChangelogTests`' bounds first; skills named there must
  exist (they will, by then).

## Batches

### Batch 1 — Ship the fourth skill green (atomic)
`plugin/skills/docs-processes/SKILL.md` (new) + `SkillContractTests.cs` + `SkillsReferencePageTests.cs`
+ `docs/wiki/30-automation/skills.md` + `plugin/README.md` + `PLAN.md` §11.

The SKILL.md follows docs-loop's anatomy. Content contract (spec mechanisms 1–5): frontmatter
`name: docs-processes`, description > 40 chars saying when to use it; system contract = docs-loop's
clauses 1–3 and 5–7 with clause 4 replaced (citations in `<!-- cites: -->` block comments; markers
banned — verified or absent, gaps to `_meta/GAPS.md`; `_meta/BUSINESS.md` as the only non-code ground
truth, consumer-owned, stub-created on first run); tier directory read from `STYLE.md`'s Structure
section with `40-business/` fallback declared in the PR body; inventory in
`_meta/PROGRESS-BUSINESS.md` (same table shape/states as PROGRESS.md); first run builds the inventory
only; register rules + register self-check (grep visible prose for code-shaped tokens); mermaid
`flowchart`/`sequenceDiagram` only; verify via `docume convert` + `--render-mermaid`; branch
`docs/processes-<date>`; PR body keeps the full claims table with real code refs; `baselineSha` =
oldest generation sha across both progress files ("oldest" is the load-bearing stamp word).

Token-uniqueness sub-task (do this deliberately, verify by grep across all four SKILL.mds):
docs-loop's new token, docs-processes' token, and the existing `hasDrift`/`untriaged` must each match
exactly one SKILL.md; the new skill's text must avoid `hasDrift`, `untriaged`, and docs-loop's chosen
phrase; page cells updated to carry the tokens.

Checkpoint: `dotnet build` 0 warnings → `python3 tools/loop/run-suite.py` no new failures →
`python3 tools/loop/check-state-size.py` OK.

### Batch 2 — Cross-skill amendments (D2–D4)
- docs-loop step 6: baselineSha = oldest across `PROGRESS.md` **and, when present,
  `_meta/PROGRESS-BUSINESS.md`**; "What this skill does not do" gains business-tier pages → `/docs-processes`.
- docs-refresh: a stale page under the business-tier directory regenerates in the business register
  (STYLE.md Business-tier section, fallback defaults), maintains `<!-- cites: -->` comments, never
  introduces `⚠️` markers (gap → GAPS.md).
- docs-feedback: replies about business pages answer in the page's register (no type names); the
  verification and inbox mechanics are unchanged.

Constraint: amendments must not break batch 1's token uniqueness (no bare token collisions) and must
keep docs-loop's "oldest" stamp word. Checkpoint: run-suite.py no new failures.

### Batch 3 — Bookkeeping + full verification
CHANGELOG entry if `ChangelogTests` bounds allow (else note the skip); full suite; `docume convert docs/wiki`
via the packed tool if wired, else note; behavioral diff written; workflow artifact gates recorded.

## Out of scope (explicit)
- `tools/loop/state.json`, `GATES.md` — loop-owned bookkeeping; M9 ran interactively. The loop's next
  iteration will see M9 in PLAN.md §14; Mirko coordinates gating.
- The Inventhor acceptance run (spec Acceptance 1–5) — a follow-up run of the shipped skill.
- The 2 pre-existing suite failures (`.codex/`, `AGENTS.md` — untracked Codex-session artifacts from
  11:59) — surfaced to Mirko separately; not touched here.
- `docume init` scaffolding (spec S12), screenshots (S10), second reader surface (S11).

## Success criteria
See sidecar `success_criteria`. The bar for every checkpoint: **no new failures** vs the 2-failure
pre-existing baseline, 0 build warnings, PLAN.md inside budget.
