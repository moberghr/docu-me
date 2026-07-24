# Plan — M0 Config + State + `init`

Companion to `docs/specs/2026-07-24-m0-config-state-init.md`. Three batches, each ending green.

## Rigor

HIGH by hard-trigger floor: 3 batches, 12+ non-mechanical files. `security_impact: none`. No external contracts. Autonomous unattended loop → Phase 2.5 gate auto-proceeds (PLAN.md is the approved spec); review is a single focused compliance pass at the end rather than the full subagent panel, sized to a `security_impact: none` local-IO change.

## Batch 1 — Config (§5.1)

**Files:** `Json/DocumeJson.cs`, `Config/DocumeConfig.cs`, `Config/ConfigExceptions.cs`, `Config/ConfigLoader.cs`, `tests/…/Config/ConfigLoaderTests.cs`.

- `DocumeJson.Options`: `PropertyNamingPolicy = CamelCase`, `PropertyNameCaseInsensitive = true`, `ReadCommentHandling = Skip`, `AllowTrailingCommas = true`, `WriteIndented = true`, `DefaultIgnoreCondition = WhenWritingNull`.
- Config models as `record` types with init props + defaults matching §5.1 (labels approved/stale, dashboard title "Documentation Status", drift.defaultBranch "dev", mermaid.renderer "tools/render-mermaid.mjs", wiki.root "docs/wiki", exclude ["_meta/**"], homePage "README.md").
- `ConfigLoader.Load(path)`: not-found → `ConfigNotFoundException`; deserialize with `DocumeJson.Options`; `Validate()` → collect missing required (`confluence.baseUrl`, `confluence.spaceKey`, `wiki.root`) → if any, `ConfigValidationException(errors)`.
- **Acceptance:** valid loads; invalid throws with error list; missing throws not-found.

## Batch 2 — State (§5.3)

**Files:** `State/DocumeState.cs`, `State/StateExceptions.cs`, `State/StateStore.cs`, `tests/…/State/StateStoreTests.cs`.

- `DocumeState` record: `Version` (default 1), `BaselineSha?`, `LastPublishedSha?`, `Pages` dict. `PageState`: pageId, title, parentPageId, contentHash, publishedVersion, attachments dict, approval, stale, feedbackCursor. `ApprovalState`: status(string), approvedBy, approvedAt, approvedVersion, history[].
- `StateStore.CurrentVersion = 1`. `Load`: read text → `JsonNode.Parse` → peek `version` → `Migrate(node, version)` (throws `StateVersionException` if version > current; while-loop seam for future upgrades) → `Deserialize<DocumeState>`. `Save`: serialize `state with { Version = CurrentVersion }` via `DocumeJson.Options`, `File.WriteAllText`; create parent dir if needed.
- **Acceptance:** populated round-trip; empty round-trip; newer-version rejected.

## Batch 3 — Scaffolder + `init` (§6.1)

**Files:** `Scaffolding/ScaffoldResult.cs`, `Scaffolding/ProjectScaffolder.cs`, `Cli/Commands/InitCommand.cs`, `Cli/Program.cs`, `tests/…/Scaffolding/ProjectScaffolderTests.cs`; remove `ScaffoldSmokeTests.cs`.

- `ScaffoldAction { Created, Skipped }`; `record ScaffoldResult(string RelativePath, ScaffoldAction Action)`.
- `ProjectScaffolder.Scaffold(targetDir, spaceKey?, baseUrl?)`: writes, only if absent, `docume.json` (template with flag values or placeholders), `docs/wiki/README.md`, `docs/wiki/_meta/STYLE.md`, `docs/wiki/_meta/state.json` (empty state via `StateStore.Save`). Returns ordered `ScaffoldResult` list. Never overwrites.
- `InitCommand.Build()` → `Command("init", …)` with `--space`, `--base-url`; action resolves target = cwd, calls scaffolder, prints Spectre table (path + Created/Skipped), returns 0.
- `Program.cs`: `rootCommand.Subcommands.Add(InitCommand.Build())`. Keep the proven bare-invocation `--help` shim (still valid with subcommands; low-risk vs. relying on unverified default-help behavior).
- **Acceptance:** idempotency test; flag values land in a config that re-parses; e2e via M0 acceptance run.

## M0 acceptance (after batch 3)

Temp-dir only (never the repo):
```
dotnet pack src/DocuMe.Cli -c Release
cd <tmp> && dotnet new tool-manifest
dotnet tool install --local --add-source <repo>/src/DocuMe.Cli/bin/Release DocuMe.Cli
dotnet tool run docume -- --version   # 0.1.0…
dotnet tool run docume -- init         # creates files
dotnet tool run docume -- init         # reports skips
```

## Review & close

- Full `dotnet build` + `dotnet test`; behavioral diff; one `compliance-reviewer` pass (sized to security_impact=none).
- Commit `M0: config loader + state store + init command; M0 acceptance (pack/install/run) green`.
- Update `tools/loop/state.json`: milestone M0→complete/M1-ready, iteration 3, done[], nextAction for M1.
