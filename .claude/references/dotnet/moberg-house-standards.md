---
description: Moberg house .NET standards (from moberghr/app-templates), scoped to DocuMe's CLI + library shape
globs: ["src/**", "tests/**", "*.slnx", "Directory.*.props", "global.json", ".editorconfig"]
alwaysApply: false
---

# Moberg house .NET standards — DocuMe alignment

## Provenance

Distilled from `moberghr/app-templates` (the house starter templates; root `CLAUDE.md`, `AGENTS.md`,
`fullstack-app/backend-server/Directory.Build.props` + `Directory.Packages.props`) and
`moberghr/coding-guidelines@4043387ca2c70ed0cd76e005861f5c471908c3bb` (the coding style already pulled
to [`coding-guidelines.md`](./coding-guidelines.md)). Recorded 2026-07-24.

app-templates targets a **fullstack web app** (ASP.NET Core + EF Core + PostgreSQL + .NET Aspire +
React/Expo). DocuMe is a **.NET 10 CLI dotnet tool + library** (`PLAN.md` §3): no web framework, no
database, no EF Core. This doc keeps only the house standards that apply to that shape and marks the
rest out of scope, so the loop aligns without dragging in web/DB machinery.

## Applies to DocuMe

- **Layered projects** — `DocuMe.Core` (library) + `DocuMe.Cli` (thin tool) + `*.Tests`, matching the
  house `*.Core / *.Api / *.Tests` layering (DocuMe has no `.Data` layer — no DB). Already in place.
- **Central Package Management (CPM)** — one `Directory.Packages.props` with
  `ManagePackageVersionsCentrally=true`; package versions live there as `<PackageVersion>`, and
  `<PackageReference>` entries carry **no inline `Version=`** (app-templates §0.3).
- **Max-strict analyzers, warnings-as-errors** — StyleCop.Analyzers, Roslynator.Analyzers,
  SonarAnalyzer.CSharp, Meziantou.Analyzer referenced from `Directory.Build.props` with
  `PrivateAssets="all"`; `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild=true`,
  `TreatWarningsAsErrors=true` (already set), `NuGetAuditMode=all` with the transitive-advisory
  NU190x codes kept as warnings. Rule severities and intentional relaxations live in `.editorconfig`
  (root rules + per-project test globs).
- **Pinned toolchain** — `global.json` pins the SDK (already: `10.0.100`, `rollForward latestFeature`)
  and, once on xUnit v3, the Microsoft Testing Platform test runner.
- **Test stack** — xUnit **v3 on the Microsoft Testing Platform** + Shouldly (+ NSubstitute for fakes,
  which DocuMe already uses). Golden-file assertions for the converter stay as planned (`PLAN.md` §7).
- **No MediatR** — if an in-process mediator / job / pub-sub is ever needed, use `Moberg.Warp.*`
  (`IRequest`/`IRequestHandler`/`IMediator`), never the MediatR package (app-templates §0.2). DocuMe
  is command-driven via System.CommandLine today, so no mediator is required yet.
- **No floating versions** — exact pins only; enforced by CPM above (and `save-exact` on any JS side,
  N/A here).
- **Coding style** — the full `moberghr/coding-guidelines` doc (file-scoped namespaces, `var`,
  `_camelCase` private fields, avoid `else`, one-declaration-per-line, split LINQ chains, etc.). Its
  MediatR/EF Core/`IOptions`/DbContext sections are web/DB-specific — apply the general style, skip the
  data-layer rules that don't map to a CLI.

## Out of scope for DocuMe (house stack, but not this product)

ASP.NET Core hosting, `.NET Aspire`, EF Core + Npgsql + snake_case, PostgreSQL, cookie-session auth /
hybrid RBAC / 2FA, RFC 7807 `ProblemDetails` HTTP envelopes, React/Vite/Tailwind web app, Expo mobile
app. Ignore the app-templates rules covering these (their §1 security, §5 data-layer, §7 infrastructure).

## Alignment checklist (loop work — "standards hardening", handed to the build loop)

Current M0 scaffold vs. the house baseline. Do this as verified MTK slices; keep the build green.

- [ ] **CPM** — add `Directory.Packages.props` (`ManagePackageVersionsCentrally=true`); move every
      version off the inline `PackageReference Version=` attrs in `src/DocuMe.Core/DocuMe.Core.csproj`,
      `src/DocuMe.Cli/*.csproj`, and `tests/DocuMe.Core.Tests/*.csproj` into `<PackageVersion>` entries.
- [ ] **Analyzers** — add the four analyzers to `Directory.Build.props` (`PrivateAssets="all"`) and set
      `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild=true`, `NuGetAuditMode=all`
      (+ `WarningsNotAsErrors=$(WarningsNotAsErrors);NU1901;NU1902;NU1903;NU1904`). `TreatWarningsAsErrors`
      is already on — expect a fix cascade; resolve rules, don't blanket-suppress. Add `.editorconfig`
      with the house severities and test-glob relaxations.
- [ ] **xUnit v3 on MTP** — migrate `tests/DocuMe.Core.Tests` from xUnit v2 (`xunit` 2.9.3 +
      `Microsoft.NET.Test.Sdk` + `xunit.runner.visualstudio`) to `xunit.v3` on the Microsoft Testing
      Platform; pin the runner in `global.json`; keep Shouldly + NSubstitute; keep all 12 tests green.

Confirm exact analyzer/xUnit versions against app-templates
`fullstack-app/backend-server/Directory.Packages.props` at build time (they pin to the current Warp repo
set); do not hardcode versions from this doc — treat them as the shape, not the source of truth.
