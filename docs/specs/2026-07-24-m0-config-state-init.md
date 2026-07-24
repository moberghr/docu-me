# Spec — M0 Config + State + `init`

> Source of truth: `PLAN.md` §5.1 (`docume.json`), §5.3 (`_meta/state.json`), §6.1 (`docume init`), §14 (M0 row).
> Supplied-plan mode (mtk:implement Phase 0.7): PLAN.md is the pre-approved spec. Approval gate auto-proceeds under the unattended build loop (`tools/loop/ITERATION-PROMPT.md`), consistent with iter2.

## Scope classification

New feature — second (and M0-closing) batch of the greenfield scaffold. Builds on the iter2 solution skeleton. `security_impact: none` (no auth, no network, no secrets — pure local file I/O + JSON models). No external consumer-facing contracts (`docume.json`/`state.json` are consumer-repo data files, not a wire API; the CLI surface is internal-tooling).

## Goal

Give `DocuMe.Core` the two data contracts every later milestone reads and writes — `docume.json` (config) and `_meta/state.json` (machine-owned page state) — with a loader, model-level validation, save with a version-migration seam, and a minimal idempotent `docume init` that scaffolds a consumer repo. Closing this batch reaches full M0 acceptance: the tool packs, installs locally as a dotnet tool, and runs (`--version`, `init`).

## Change manifest

| Path | Purpose |
|---|---|
| `src/DocuMe.Core/Config/DocumeConfig.cs` | Config model records for §5.1 (confluence/wiki/labels/dashboard/drift/links/mermaid) |
| `src/DocuMe.Core/Config/ConfigLoader.cs` | Load `docume.json` (comments + trailing commas tolerated), validate, throw typed errors |
| `src/DocuMe.Core/Config/ConfigExceptions.cs` | `ConfigNotFoundException`, `ConfigValidationException` (carries the error list) |
| `src/DocuMe.Core/State/DocumeState.cs` | State model records for §5.3 (state + page + approval + attachments) |
| `src/DocuMe.Core/State/StateStore.cs` | `Load`/`Save` with `CurrentVersion` + migration hook; peeks version before deserialize |
| `src/DocuMe.Core/State/StateExceptions.cs` | `StateVersionException` (state written by a newer tool) |
| `src/DocuMe.Core/Json/DocumeJson.cs` | Shared `JsonSerializerOptions` (camelCase, indented, skip comments, trailing commas, ignore-null-on-write) |
| `src/DocuMe.Core/Scaffolding/ProjectScaffolder.cs` | Idempotent file scaffolder: writes docume.json + docs/wiki skeleton, never overwrites, returns per-file Created/Skipped |
| `src/DocuMe.Core/Scaffolding/ScaffoldResult.cs` | `record ScaffoldResult(string RelativePath, ScaffoldAction Action)` + enum |
| `src/DocuMe.Cli/Commands/InitCommand.cs` | Builds the `init` System.CommandLine subcommand (`--space`, `--base-url`), calls the scaffolder, prints a Spectre table |
| `src/DocuMe.Cli/Program.cs` | Register the `init` subcommand |
| `tests/DocuMe.Core.Tests/Config/ConfigLoaderTests.cs` | valid / invalid / missing-file |
| `tests/DocuMe.Core.Tests/State/StateStoreTests.cs` | round-trip, empty-state save/load, newer-version rejection |
| `tests/DocuMe.Core.Tests/Scaffolding/ProjectScaffolderTests.cs` | first run creates, second run skips (idempotency), flag values land in docume.json |
| `tests/DocuMe.Core.Tests/ScaffoldSmokeTests.cs` | remove (superseded by real Core tests) |

## Test manifest

- **ConfigLoaderTests** — a valid `docume.json` loads with expected values and defaults; a config missing required fields (`confluence.baseUrl`/`spaceKey`, `wiki.root`) throws `ConfigValidationException` listing them; a missing file throws `ConfigNotFoundException`.
- **StateStoreTests** — a populated `DocumeState` survives Save→Load (top-level SHAs + one page's fields); an empty state round-trips; a state file with `version` > `CurrentVersion` throws `StateVersionException`.
- **ProjectScaffolderTests** — first run on an empty temp dir marks every file `Created` and the files exist; a second run marks them all `Skipped` and does not modify them; `--space`/`--base-url` values appear in the generated `docume.json` and it parses back via `ConfigLoader`.

## Batches

1. **Config** — `DocumeJson` options + config models + loader + validation + exceptions + `ConfigLoaderTests`.
2. **State** — state models + `StateStore` (load/save/migration hook) + exceptions + `StateStoreTests`.
3. **Scaffolder + `init`** — `ProjectScaffolder` + templates + `ProjectScaffolderTests`, then `InitCommand` wired into `Program.cs`.

Each batch ends green (`dotnet build` + `dotnet test`). After batch 3, run M0 acceptance.

## Acceptance

- `dotnet build DocuMe.slnx -c Release` — 0 warnings / 0 errors (warnings-as-errors on).
- `dotnet test` — all green (config, state, scaffolder).
- **M0 close:** `dotnet pack` → in a **temp dir** (not the repo): `dotnet new tool-manifest`, `dotnet tool install --local --add-source <nupkg-dir> DocuMe.Cli`, then `docume --version` prints `0.1.0…` and `docume init` scaffolds files; a second `docume init` reports skips (idempotent).
- Committed to `main` with an `M0:` message. State.json advanced to iter3 with M0 marked done.

## Assumptions & risks

- **[ASSUMED]** System.CommandLine 2.0.10 GA API: `Option<T>("--name"){ Description=… }`, `Command.SetAction(Func<ParseResult,int>)`, `parseResult.GetValue(opt)`, `root.Parse(args).Invoke()`. The iter2 `Program.cs` already compiles against 2.0.10; `dotnet build` under warnings-as-errors is the verification gate.
- **[DECISION]** "JSON-schema validation" (§5.1) is implemented for M0 as **model-level validation** (required-field checks with a clear error list), not a JSON Schema library dependency. The `$schema` URL in the template points at the future published schema (M6 distribution concern). Rationale: PLAN.md §4 values thin dependencies; formal schema publishing isn't an M0 deliverable.
- **[DECISION]** The scaffolder + templates live in `DocuMe.Core` (as string constants) so idempotency is unit-testable without a Cli test project. Full templated `init` (`tools/render-mermaid.mjs`, workflow yml, `--adopt`) is deferred to M6 per §14.
- **[DECISION]** Approval `status` modeled as `string` (contract uses `"approved | needs-review"`), not an enum, to avoid enum-naming-policy surface in M0.
- **[RISK]** Running `docume init` must never pollute this repo — acceptance runs it only inside a throwaway temp dir.
