# Agent-rail selection for `docume init`

- **Date:** 2026-08-13
- **Scope:** new-feature
- **Security impact:** `secrets-change`
- **Slug:** `agent-rail-selection`

## Summary

`docume init` scaffolds six workflows. Four are rail-agnostic (`docs-drift`, `docs-drift-pr`,
`docs-publish`, `docs-sync`) — they run only `docume` and `git`. Two run a model: `docs-refresh` and
`docs-feedback`, and today both exist only in a Claude Code spelling that requires an
`ANTHROPIC_API_KEY`.

This adds a second rail. A consumer picks it once with `docume init --agent copilot|claude`; the
choice is recorded in `docume.json` and read back on later runs. Exactly one variant of each model
workflow is scaffolded, under its existing consumer-facing name.

## Success criteria

| id | Outcome | Verification | Channel | Observable |
|---|---|---|---|---|
| SC1 | `init --agent copilot` scaffolds the Copilot spelling of both model workflows and nothing of the Claude one | `ProjectScaffolderTests` | test-run | `.github/workflows/docs-refresh.yml` byte-equals `templates/workflows/docs-refresh.copilot.yml`; no `.claude.yml` or `.copilot.yml` file exists under `.github/workflows/` |
| SC2 | `init --agent claude` and bare `init` are byte-identical to today's output | `ProjectScaffolderTests` | test-run | all six scaffolded workflow bytes unchanged from the pre-change baseline |
| SC3 | The rail round-trips through `docume.json` | `ProjectScaffolderTests`, `ConfigSchemaTests` | test-run | after `init --agent copilot`, `docume.json` contains `"agent": "copilot"`; a second bare `init` reports both model workflows `skipped` and writes no Claude bytes |
| SC4 | A contradicting re-run is non-destructive and says what to do | `ProjectScaffolderTests` | test-run | `init --agent claude` over a copilot repo returns `Skipped` for both model workflows with a note naming both files to delete; on-disk bytes unchanged |
| SC5 | The four rail-agnostic workflows are unaffected on both rails | `ProjectScaffolderTests` | test-run | their four scaffolded files byte-equal their bare-named templates under either `--agent` value |
| SC6 | Both Copilot templates are valid, gated workflows | `WorkflowTemplateTests`, `WorkflowShellTests` | test-run | each parses as YAML, gates its model step on `hasDrift`/untriaged-count, and contains no `--allow-all-tools` |
| SC7 | The glob's add-a-file-and-it-ships property survives | `ProjectScaffolderTests` | test-run | a rail-agnostic `.yml` dropped into `templates/workflows/` is scaffolded with no scaffolder edit |
| SC8 | `--agent` and `agent` are documented where the suite already polices docs | `CliReferencePageTests`, `ConfigReferencePageTests` | test-run | `--agent` appears in `init`'s option table in `docs/wiki/20-reference/cli.md`; `agent` appears in `configuration.md` |

## Architecture and design

### Template naming

Rail variants carry a `.{rail}.` infix before `.yml`; rail-agnostic templates keep bare names.

```
templates/workflows/
  docs-drift.yml              rail-agnostic, unchanged
  docs-drift-pr.yml           rail-agnostic, unchanged
  docs-publish.yml            rail-agnostic, unchanged
  docs-sync.yml               rail-agnostic, unchanged
  docs-refresh.claude.yml     was docs-refresh.yml
  docs-refresh.copilot.yml    new
  docs-feedback.claude.yml    was docs-feedback.yml
  docs-feedback.copilot.yml   new
```

The csproj glob is unchanged, so the "add a workflow and it ships" property documented at
`BundledTemplates.cs:26` still holds for rail-agnostic files. `BundledTemplates` gains the parsing:
a resource name with a recognised rail infix is a variant of the bare name; anything else is
rail-agnostic. Both land on disk under the bare name, so consumer repos, path filters and every
existing doc keep referring to `docs-refresh.yml`.

An unrecognised infix is a hard error at load, not a silent pass-through — the same reasoning as
the empty-glob throw already at `BundledTemplates.cs:67`. A typo'd `docs-refresh.copilott.yml`
would otherwise scaffold a second nightly job racing the same concurrency group, which is exactly
the failure this whole feature exists to prevent.

### Rail resolution

Precedence, highest first:

1. `--agent` on the command line
2. `agent` in an existing `docume.json`
3. `claude` (default — preserves today's behaviour for every existing consumer)

The resolved rail is written into the `docume.json` this run scaffolds. `init` already reads back
the config it just wrote (`ProjectScaffolder.cs:145`), so the read-back point exists.

### Contradiction handling

`Copy` already returns `Skipped` for any existing file (`ProjectScaffolder.cs:461-471`), so the
non-destructive floor is free. The feature adds only the *note*: when the requested rail differs
from the rail recorded in `docume.json` and the target model workflow already exists, the skip row
carries a note naming both files to delete. `ScaffoldResult.Note` and the note-rendering loop at
`InitCommand.cs:119` already exist; no new reporting surface.

This is deliberately not a refusal. A rail contradiction must not block the twenty other targets
`init` would happily scaffold, and §9.4 makes overwriting unavailable regardless.

## Security and compliance impact

`secrets-change`, and not `none` — stated plainly because the honest classification forces the
rigor this change deserves.

The Copilot templates introduce a **new long-lived credential** into scaffolded consumer CI:
`COPILOT_GITHUB_TOKEN`, a PAT belonging to a human with a Copilot seat, where the Claude rail used
a service API key. Three consequences the templates must handle and the tests must police:

1. **The fallback trap.** Copilot CLI resolves `COPILOT_GITHUB_TOKEN` → `GH_TOKEN` → `GITHUB_TOKEN`.
   An unset secret therefore does not fail; it silently falls through to the repo-scoped Actions
   token, which carries no Copilot entitlement, and dies later blaming the wrong thing. Both
   Copilot templates carry a preflight step that fails fast on an empty secret.
2. **No blanket tool grant.** `--allow-all-tools --allow-all-paths` is the Copilot equivalent of
   `--dangerously-skip-permissions`, which the existing template refuses on principle
   (`docs-refresh.yml:246`). The variants enumerate `shell(git:*)`, `shell(gh:*)`,
   `shell(dotnet:*)`, `shell(jq:*)` and `write`. A test asserts the blanket is absent.
3. **Transcript redaction.** `--share` writes a session transcript that becomes a CI artifact;
   `--secret-env-vars` names both tokens so neither reaches it.

Rule §0.3 and §1.1 are unaffected: no Confluence credential appears in either new template, and
neither model job holds one — publishing stays behind a human's merge in `docs-publish.yml`.

## Constitution Check

| Rule | How this design satisfies it |
|---|---|
| §0.4 skills output PRs only; only the CLI talks to Confluence | Both Copilot variants grant `shell(gh:*)` for PR creation and hold no Confluence credential. The skill body is unchanged and still reaches Confluence only through `dotnet tool run docume`. |
| §1.3 Confluence bodies/comments are untrusted input | The `docs-feedback` skill contract is untouched. The Copilot rail changes who executes it, not what it says — which is precisely why SC6 and the review phase carry the injection-resistance risk below rather than assuming it transfers. |
| §1.5 human reviews every docs change before publish | Both variants push a branch and open a PR; neither pushes to the default branch. Unchanged from the Claude rail. |
| §9.4 `init` is idempotent, never overwrites, reports skips | The contradiction path is a `Skipped` row plus a note. No new write path can overwrite a consumer file. |
| §9.5 repo-specific knowledge stays in the consumer repo | The rail is a consumer-repo choice recorded in the consumer's `docume.json`; neither the tool nor the skills learn anything repo-specific. |
| §4.3 goldens are a reviewed contract | Untouched — this change adds no converter cases. |

## Change manifest

### Unblocking the baseline (2) — added at the Phase 2.5 gate

Not part of the feature. The suite was red before batch 1 on two artifacts the Codex plugin setup
dropped at `2026-08-13 11:59:21`, and §8.3 forbids committing red. Declared here rather than done
silently; approved at the gate as "declare both as unshipped roots".

| Path | Action | Purpose |
|---|---|---|
| `tests/DocuMe.Core.Tests/Fixtures/ShippedTree.cs` | modify | `AGENTS.md` → `OutsideTheQuestion`, classified like `CLAUDE.md` |
| `tests/DocuMe.Core.Tests/Acceptance/DogfoodWikiTests.cs` | modify | `.codex/` → `UnshippedRoots`, classified like `.claude/` |

### Templates (4)

| Path | Action | Purpose |
|---|---|---|
| `templates/workflows/docs-refresh.yml` | rename → `docs-refresh.claude.yml` | rail infix; content unchanged |
| `templates/workflows/docs-feedback.yml` | rename → `docs-feedback.claude.yml` | rail infix; content unchanged |
| `templates/workflows/docs-refresh.copilot.yml` | create | drafted and YAML-validated this session |
| `templates/workflows/docs-feedback.copilot.yml` | create | same port applied to the feedback job |

### Source (5)

| Path | Action | Purpose |
|---|---|---|
| `src/DocuMe.Core/Scaffolding/BundledTemplates.cs` | modify | parse the rail infix; expose per-rail file lists; throw on unrecognised infix |
| `src/DocuMe.Core/Scaffolding/ProjectScaffolder.cs` | modify | resolve the rail, scaffold one variant per model workflow under the bare name, emit the contradiction note |
| `src/DocuMe.Core/Config/AgentRail.cs` | create | the rail enum (manifest refinement during B1: the approved design needs the type somewhere) |
| `src/DocuMe.Core/Config/DocumeConfig.cs` | modify | `agent` property |
| `src/DocuMe.Cli/Commands/InitCommand.cs` | modify | `--agent` option, validation, pass-through |
| `schema/docume.schema.json` | modify | `agent` enum `["claude","copilot"]` |

### Documentation (4)

| Path | Action | Purpose |
|---|---|---|
| `docs/wiki/20-reference/cli.md` | modify | `--agent` row in `init`'s option table |
| `docs/wiki/20-reference/configuration.md` | modify | `agent` key |
| `docs/wiki/20-reference/workflows.md` | modify | the two rails, and which files each produces |
| `README.md` | modify | one line in the init section |

### Tests (8)

| Path | Action | Purpose |
|---|---|---|
| `tests/DocuMe.Core.Tests/Scaffolding/ProjectScaffolderTests.cs` | modify | SC1–SC5, SC7; teach the anti-fork assertion (`:142`) and the add-a-file assertion (`:171`) the rail infix |
| `tests/DocuMe.Core.Tests/Templates/WorkflowTemplateTests.cs` | modify | SC6; run the existing model-driven assertions across both rails |
| `tests/DocuMe.Core.Tests/Templates/WorkflowShellTests.cs` | modify | SC6; the Copilot drift gate |
| `tests/DocuMe.Core.Tests/Templates/WorkflowsReferencePageTests.cs` | modify | reference page covers both rails |
| `tests/DocuMe.Core.Tests/Config/ConfigSchemaTests.cs` | modify | SC3; `agent` accepted, bad value rejected |
| `tests/DocuMe.Core.Tests/Config/ConfigReferencePageTests.cs` | modify | SC8 |
| `tests/DocuMe.Core.Tests/Cli/CliReferencePageTests.cs` | modify | SC8; `--agent` row (builds on uncommitted iteration-203 work) |
| `tests/DocuMe.Core.Tests/Packaging/ReadmeCliContractTests.cs` | modify | README contract |

## Requirements

### Ubiquitous
- The system shall scaffold exactly one variant of `docs-refresh.yml` and one of `docs-feedback.yml` per `init` run.
- The system shall write both model workflows to disk under their bare consumer-facing names regardless of the rail chosen.

### Event-driven
- When `--agent` is supplied, the system shall use that rail and record it in the scaffolded `docume.json`.
- When `--agent` is absent and `docume.json` declares `agent`, the system shall use the declared rail.
- When `--agent` is absent and no `agent` is declared, the system shall use the `claude` rail.
- When a template resource carries an unrecognised rail infix, the system shall throw at load naming the offending resource.

### State-driven
- While a repo records one rail and `init` is invoked with the other, the system shall report both model workflows as skipped and attach a note naming both files to delete.

### Unwanted behaviours
- If `--agent` is given a value outside `claude|copilot`, then the system shall exit non-zero without writing any file.
- If a Copilot workflow runs with an empty `COPILOT_GITHUB_TOKEN`, then the workflow shall fail at its preflight step rather than fall through to `GITHUB_TOKEN`.

## Implementation batches

| # | Batch | Files | Checkpoint |
|---|---|---|---|
| B1 | Rename Claude templates to the infix; teach `BundledTemplates` the rail | 2 renames + `BundledTemplates.cs` | build + `ProjectScaffolderTests` |
| B2 | Rail resolution and contradiction note in the scaffolder | `ProjectScaffolder.cs` | build + scaffolder tests |
| B3 | Config key, schema, `--agent` option | `DocumeConfig.cs`, `docume.schema.json`, `InitCommand.cs` | build + config tests |
| B4 | Write both Copilot templates | 2 new `.yml` | `WorkflowTemplateTests`, `WorkflowShellTests` |
| B5 | Reference pages and README | 4 docs files | full `dotnet test` |

## Risks and assumptions

- `[VERIFIED:tests/DocuMe.Core.Tests/Scaffolding/ProjectScaffolderTests.cs:142]` The anti-fork
  assertion maps template filename 1:1 onto scaffolded filename. The infix breaks that mapping by
  design; the test must learn the rail rather than be weakened. This is the single most likely
  place to accidentally reduce coverage.
- `[VERIFIED:templates/workflows/docs-refresh.yml:64]` A `${{ runner.temp }}` in a job-level `env:`
  is rejected by GitHub with a 0s run and no annotation. Both new templates must use `$RUNNER_TEMP`.
  This shipped broken once already (v0.1.0, three workflows).
- `[CITED:https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-programmatic-reference]`
  Copilot CLI flags: `-p`, `--model`, `--allow-tool`, `--no-ask-user`, `--share`, `--secret-env-vars`;
  token precedence `COPILOT_GITHUB_TOKEN` → `GH_TOKEN` → `GITHUB_TOKEN`.
- `[CITED:https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-skills]`
  Copilot CLI reads `SKILL.md` from `.github/skills`, `.claude/skills`, `.agents/skills`, and
  invokes by `/name`.
- `[ASSUMED]` `--secret-env-vars` accepts repetition, and bare `--allow-tool write` is valid without
  a path filter. Neither is stated in the docs. Both are first-run discoveries against live CI, not
  design risks — the templates are correct in shape either way.
- `[ASSUMED]` Instruction-following parity: the `docs-feedback` skill's §1.3 injection-resistance
  clause is prose the model must obey, and that behaviour is model-specific. This change ships the
  *rail*; it does not prove the clause holds on it. Mitigation is the `@github/copilot` install
  pin plus the transcript artifact, not a test.
- `[VERIFIED:git status]` **dirty worktree, out of scope:** `.claude/analytics.json`,
  `tools/loop/done-archive.jsonl`, `tools/loop/run-suite.py`, `tools/loop/state.json`,
  `tests/golden/.claude/`, `.codex/`, `AGENTS.md`. Only `CliReferencePageTests.cs` is both dirty and
  in the manifest — see the Phase 2.9 collision note.
- `[VERIFIED:dotnet test]` **The baseline is red before this change:** `ShippedTreeCoverageTests` and
  `DogfoodWikiTests` fail on `AGENTS.md` and `.codex/`, both created `2026-08-13 11:59:21` by the
  Codex plugin setup. Unrelated to this feature and blocking under §8.3. Resolution is a gate
  question, not a silent inclusion.

## Rejected alternatives

- **Replace the Claude template outright.** One template, no init changes, no new tests. Rejected:
  it deletes a working rail to add one that has never executed in CI.
- **Drop `docs-refresh-copilot.yml` beside the existing file.** Rejected at discovery: the glob
  scaffolds everything, so every consumer would get two nightly jobs racing one concurrency group.
- **A `--copilot` boolean rather than `--agent <rail>`.** Rejected: a third rail would need a third
  boolean and the two could be passed together.
- **Refuse a contradicting re-run.** Presented and declined — it blocks the twenty unrelated targets
  `init` would otherwise scaffold.

## Out of scope

- Installing either workflow into `moberghr/inventhor`.
- Any change to the three `SKILL.md` bodies.
- Porting `docs-loop` (it has no workflow template; it is invoked by hand).
- Deleting `AGENTS.md` or `.codex/` — the gate chose classification over removal.
