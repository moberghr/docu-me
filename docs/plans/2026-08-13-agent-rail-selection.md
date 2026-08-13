# Plan — Agent-rail selection for `docume init`

Spec: `docs/specs/2026-08-13-agent-rail-selection.md`
Sidecar: `docs/specs/2026-08-13-agent-rail-selection.json`

**Rigor: MAX (score 18)** — 5 batches (+5), 19 non-mechanical files of 23 (+7),
`security_impact=secrets-change` (+3), 2 external contracts (+2), 1 internal-tooling contract (+1).
Hard-trigger floor independently forces ≥ HIGH on all three counts.

## Batch order and why

The order is a dependency chain, not a preference. B1 changes what `BundledTemplates` returns; B2
consumes it; B3 supplies the rail B2 resolves; B4 is the only batch whose content is new prose
rather than wiring; B5 is documentation the suite polices and must come last so it describes what
was actually built.

---

### B1 — Rename Claude templates, teach `BundledTemplates` the rail

**Files:** `templates/workflows/docs-refresh.yml` → `docs-refresh.claude.yml`,
`templates/workflows/docs-feedback.yml` → `docs-feedback.claude.yml`,
`src/DocuMe.Core/Scaffolding/BundledTemplates.cs`,
`tests/DocuMe.Core.Tests/Scaffolding/ProjectScaffolderTests.cs`

Use `git mv` for both renames — macOS is case-insensitive and the global rule forbids
delete-and-recreate for renames.

`BundledTemplates` gains a rail concept:
- A resource whose stem ends `.claude` or `.copilot` is a variant of the stem without it.
- Anything else is rail-agnostic and ships on every rail.
- An unrecognised infix throws at load, naming the resource — same shape as the empty-glob throw
  already at `:67`.

`WorkflowFileNames` becomes rail-aware. Its ordinal sort stays: `init` must report the same order
on every machine.

**Acceptance:** build clean; `ProjectScaffolderTests` compiles against the new surface; the
anti-fork assertion at `:142` and the add-a-file assertion at `:171` both understand the infix and
still fail if a template is forked or dropped.
**Boundary:** no change to `ProjectScaffolder`, config, or CLI.

---

### B2 — Rail resolution and the contradiction note

**Files:** `src/DocuMe.Core/Scaffolding/ProjectScaffolder.cs`,
`tests/DocuMe.Core.Tests/Scaffolding/ProjectScaffolderTests.cs`

`Scaffold` takes the requested rail. Resolution precedence: parameter → `agent` in the config read
back at `:145` → `claude`. The resolved rail is written into `docume.json`.

The workflow copy loop at `:158` selects one variant per model workflow and writes it under the
bare name. The contradiction note is attached to the `Skipped` row when the requested rail differs
from the recorded one and the target exists.

**Acceptance:** SC1, SC2, SC4, SC5, SC7 green.
**Boundary:** no CLI or schema change yet — the rail arrives as a parameter with a `claude` default,
so every existing caller and test keeps working.

---

### B3 — Config key, schema, `--agent`

**Files:** `src/DocuMe.Core/Config/DocumeConfig.cs`, `schema/docume.schema.json`,
`src/DocuMe.Cli/Commands/InitCommand.cs`, `tests/DocuMe.Core.Tests/Config/ConfigSchemaTests.cs`

`--agent` follows the existing option shape in `InitCommand.Build()`. An out-of-range value exits
non-zero before any write, mirroring the `--legacy-map` refusal at `:58` — a flag that silently
falls back to a default is the failure mode that refusal exists to prevent.

**Acceptance:** SC3 green; a bad `--agent` value writes nothing.
**Boundary:** no template or doc edits.

---

### B4 — Write both Copilot templates

**Files:** `templates/workflows/docs-refresh.copilot.yml`,
`templates/workflows/docs-feedback.copilot.yml`,
`tests/DocuMe.Core.Tests/Templates/WorkflowTemplateTests.cs`,
`tests/DocuMe.Core.Tests/Templates/WorkflowShellTests.cs`

`docs-refresh.copilot.yml` starts from the session draft at
`<scratchpad>/docs-refresh-copilot.yml`, already YAML-validated and diffed against the Claude
template so only the model-running steps differ. `docs-feedback.copilot.yml` applies the same six
changes to the feedback job: token env, preflight, install line, skill-install step, the `copilot -p`
invocation, transcript artifact.

Three properties the tests must police, all from the spec's security section: `$RUNNER_TEMP` not
`${{ runner.temp }}` at job level; no `--allow-all-tools`; a preflight that fails on an empty
`COPILOT_GITHUB_TOKEN`.

**Acceptance:** SC6 green; both templates parse; existing model-driven assertions run across both
rails.
**Boundary:** no source changes.

---

### B5 — Reference pages and README

**Files:** `docs/wiki/20-reference/cli.md`, `docs/wiki/20-reference/configuration.md`,
`docs/wiki/20-reference/workflows.md`, `README.md`,
`tests/DocuMe.Core.Tests/Templates/WorkflowsReferencePageTests.cs`,
`tests/DocuMe.Core.Tests/Config/ConfigReferencePageTests.cs`,
`tests/DocuMe.Core.Tests/Cli/CliReferencePageTests.cs`,
`tests/DocuMe.Core.Tests/Packaging/ReadmeCliContractTests.cs`

The `--agent` row must satisfy the option-table row pattern the CLI reference tests enforce, and
land inside a command section — the uncommitted iteration-203 test asserts no page region is
orphaned outside every section, and an option documented in an orphaned region is a phantom that
nothing quantifies over.

**Acceptance:** SC8 green; full `dotnet test`.
**Boundary:** documentation only.

---

## Gate sequence

5 batches → Phase 3.5 drift check → Stage 1 `compliance-reviewer` → Stage 2 [`test-reviewer`,
`architecture-reviewer`, `silent-failure-hunter`] → Phase 6 cleanup → Phase 7 compound.

## Two things the gate must settle

1. **The baseline is red before batch 1.** `ShippedTreeCoverageTests` and `DogfoodWikiTests` fail on
   `AGENTS.md` and `.codex/`, dropped by the Codex plugin setup at 11:59 today. §8.3 forbids
   committing red, so this blocks the finish line regardless of how well the feature lands.
2. **MAX rigor prescribes subagent implementation and a three-reviewer Stage 2.** This session is
   configured not to spawn agents unless asked. Running inline instead is a real ceremony
   reduction and gets recorded as such in lane accounting, not passed off as a clean review.
