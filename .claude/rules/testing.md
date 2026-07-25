---
description: Testing rules for DocuMe — xUnit stack, WireMock.Net, golden files
---

# Testing Rules

- **§4.1 [CONVENTION]** Test stack is xUnit + Shouldly + NSubstitute (`PLAN.md` §4). Do not introduce Moq, FluentAssertions, or NUnit.
- **§4.2 [CONVENTION]** Confluence HTTP is tested with WireMock.Net. Live-sandbox tests are opt-in only (env-gated), never part of the default `dotnet test` run (`PLAN.md` §3).
- **§4.3 [CONVENTION]** Golden files (`tests/golden/<case>.md` → `<case>.storage.xml`) are the converter contract: reviewed by hand once, asserted forever. NEVER regenerate goldens to make a failing test pass without surfacing the diff for human review (`PLAN.md` §7).
- **§4.4 [CONVENTION]** Converter acceptance bar: **the golden corpus** (`tests/golden/`) converts with zero errors and zero unknown-construct warnings, and covers every construct in `PLAN.md` §7's table. *(Revised 2026-07-25 by Mirko: the bar was "all 79 AurServices pages"; those files are deliberately not required on the build machine. Real-corpus validation moves to M7 Aur adoption — see `.claude/references/decisions.md`.)* Adding a §7 construct means adding its golden case in the same slice.
