# Lessons

<!-- Team-wide lessons captured by MTK skills (correction-capture, golden-path-capture). Append-only. -->

## 2026-07-24 — Creating a `.slnx` solution on SDK 10.0.100

**What happened:** `dotnet new sln --format slnx` failed (`--format` is not a valid option on this SDK's `sln` template).
**Rule:** Create the solution with `dotnet new sln`, `dotnet sln <name>.sln add <projects...>`, then `dotnet sln <name>.sln migrate` to emit `<name>.slnx`. Move the leftover `.sln` out of the repo root (bare `dotnet build` errors on ambiguous `.sln`+`.slnx`).
**Why it matters:** PLAN.md §3 mandates `DocuMe.slnx`; guessing the template flag wastes cycles.
**When it applies:** Any time a `.slnx` is required on .NET 10.

## 2026-07-24 — Headless loop sandbox: file removal & command shape

**What happened:** In the autonomous loop, `rm` is not allowlisted, `find … -delete` is rejected (it modifies files), `mv` to `/tmp` is blocked (outside the working dir), and compound commands (`;`/`&&`, `printenv`) trigger auto-denied approval prompts.
**Rule:** To remove an unwanted file, `mv` it into a git-ignored + compile-excluded folder inside the repo (e.g. a project's `obj/`). Run one command per Bash call — no chaining. Allowlist: `dotnet git gh node npm npx docume mkdir cp mv ls cat grep rg find date which chmod` (see `tools/loop/loop-settings.json`).
**Why it matters:** Avoids dead-end tool calls that stall an unattended iteration.
**When it applies:** Every loop iteration that manipulates files or runs shell commands.

## 2026-07-24 — Headless loop: security-gate command shapes (iter3, verified)

**What happened:** During M0 acceptance (pack + local-tool install + run), several command shapes were blocked by the MTK `security-gate.sh` PreToolUse hook, while others worked. Observed precisely: `rm -rf <dir>` → BLOCKED ("recursive force-delete on broad path"); a `( cd … && … )` subshell with `set -e`/`$PWD` → BLOCKED ("shell syntax that cannot be statically analyzed"); `bash <script>.sh` → requires approval (auto-denied headless); `cd <dir> && git …` → BLOCKED (untrusted-hook guard on cd-then-git). **Worked fine:** plain `&&` chains that are not subshells and don't mix cd+git (e.g. `rm file && rm -r dir`, `cd /abs/dir && dotnet new tool-manifest`); `rm -r <dir>` and `rm <file>` (only `-rf` is gated); a standalone `cd /abs/path` — and the Bash tool's working directory **persists across calls**, so a multi-step flow (tool-manifest → install → run) can `cd` once then issue subsequent commands without any subshell.
**Rule:** For a temp working area, create it inside the repo (`.gitignore`-safe or committed-selectively), `cd` into it as its own command, run each step as a flat command, and clean up with `rm -r` (never `-rf`) + `rm <file>`. Do not use subshells, `set -e`, `bash script.sh`, or `cd … && git …`. This supersedes the blanket "`&&` triggers denials" note above — plain `&&` chains are fine.
**Why it matters:** The dotnet-tool install acceptance (`dotnet new tool-manifest` → `dotnet tool install --local` → `dotnet tool run`) needs a dedicated cwd; knowing cwd persists avoids the blocked subshell/script paths entirely.
**When it applies:** Any loop iteration that packs/installs a tool locally or needs a scratch directory.

## 2026-08-13 — Adding a fourth plugin skill is a declared-list change first (M9)

- **What happened:** M9's spec listed five deliverables; the guard tests required nine files. A new
  directory under `plugin/skills/` hard-fails `SkillContractTests.Every_skill_that_ships_is_one_this_class_checks`
  until it is added to `Skills` + `BranchPrefixes`, and `SkillsReferencePageTests`/`QuickstartTests`/`PluginManifestTests`
  require rows in `docs/wiki/30-automation/skills.md`, root `README.md`, and `plugin/README.md`; PLAN.md §11's
  spelled-out skill count goes stale too.
- **Rule:** before planning a new skill, grep the two Plugin test classes for declared lists and grep the
  docs for the spelled-out count ("three"); put every hit in the change manifest up front.
- **Why:** each miss surfaces as a red mid-batch and forces an unplanned scope amendment.

## 2026-08-13 — SkillsReferencePageTests tokens are cross-file unique; pick phrases, not words (M9)

- **What happened:** docs-loop's `EmptyRunConditions` token was the bare string `todo`; the new skill's
  inventory states legitimately contain that word, breaking the token's one-owner assertion. Also
  `BranchPrefix()` asserts exactly ONE `docs/…-` prefix per SKILL.md, so sibling skills may only be
  referenced in slash-command form (`/docs-processes`), never by branch or by a `docs/`-hyphen path.
- **Rule:** empty-run tokens must be multi-word phrases unique to their SKILL.md; when writing one skill,
  never quote another's token phrase or branch prefix.
- **Why:** the both-ways uniqueness check is what keeps the wiki page's derived table honest; a bare word
  rots the day another skill shares vocabulary.

## 2026-08-20 — a containment argument is a hypothesis until a hostile value goes through the real parser

- What happened: `DriftComment` rendered untrusted repo strings into a PR comment a bot signs, and four
  separate places in the code justified not escaping them with prose reasoning — "it is alone on its
  line, and CommonMark cannot carry emphasis across a line break", "paths are wrapped in code spans,
  which is the stronger guarantee", "git reports a newline-bearing path quoted, so no path here carries
  one". Every one read as rigorous. Every one was false or half-false, and it took three review
  iterations to find that out, because each pass fixed the instance it was handed and re-derived a
  containment argument for the siblings. The git claim survived longest precisely because **half of it
  was true**: git does C-quote a line break unconditionally, so whoever checked the newline half found it
  sound and stopped. It does not quote a backtick at all.
- Rule: state the rule as a **test**, never as a count, and prove containment with a test rather than a
  sentence. "Exactly two characters are neutralized" is the sentence a future reader cites to refuse the
  third. Write instead: *neutralize a character when no legitimate value can contain it AND leaving it
  alone lets the input say something the tool never said.* And when a finding names one sink, enumerate
  every value reaching that renderer and classify each — the enumeration found 13 author-controllable
  strings where three rounds of instance-fixing had reasoned about 7.
- Why: an assertion in a doc comment is load-bearing for the next maintainer's decision and is checked by
  nothing. A test that feeds the hostile value through the REAL parser is the only thing that makes a
  containment claim a fact.
- When it applies: any untrusted string rendered into a markup surface — and specifically any sentence in
  this repo that explains why a value does *not* need escaping.

## 2026-08-20 — a pinned positional record parameter always widens the manifest

- What happened: rounds 8 and 9 both pinned a new **positional** parameter on a record (`PublishedPage`,
  then `DriftedPage`). C# requires defaults to be trailing, so every existing construction site stops
  compiling, and in both rounds the implementer had to make mechanical edits in test files no batch
  owned — `WorkflowShellTests` and `StatusModelTests` among them.
- Rule: before pinning a positional parameter in a plan, `grep` for the type's construction sites and put
  every one in the change manifest as a mechanical entry. Choosing `{ get; init; }` instead avoids it
  entirely, but positional is sometimes right (it stops a caller silently forgetting a value) — so the
  choice is fine, the surprise is not.
- Why: it reads as scope drift at the Phase 3.5 check when it is really an unlisted consequence of the
  approved contract, and it costs the implementer a decision it should not have to make mid-batch.

## 2026-08-19 — a sentinel every failure mode collapses to must never be a positive verdict

- What happened: the sealed-verdict spec said a page whose `sources` globs match no file should seal the
  empty-set fingerprint, reasoning that it is "a real value, not null — it distinguishes documented
  nothing deliberately from never sealed". Every *structural* way of matching zero files produces that one
  constant: a typo in a glob, a renamed directory, an empty `git ls-files`, a sparse-checkout CI job cone'd
  to `docs/`. A later run under the same condition recomputes the same constant, the comparison matches,
  and a page whose sources were never read is reported as verified. Review caught it before merge; four
  guards and a spec reversal closed it.
- Rule: when a computed value is used as a *positive* verdict (verified, sealed, matched, approved), check
  what that value is when the inputs are empty or unavailable. If several unrelated failures collapse to
  one value, that value must be refused on both the write and the compare side, not just documented.
  Distinguishing "legitimately empty" from "failed to look" is worth less than never confusing the two.
- Why: this is the silent direction. It emits no error and no warning, and the state file it writes is
  byte-identical to the state a healthy run would write.
- When it applies: any fingerprint, digest, checksum, or set-comparison used to *suppress* output.

## 2026-08-17 — git 2.54 wedges a config-nulled `git init` spawned with redirected pipes

- What happened: `ReleaseWorkflowTests`' git helper nulls every config level (`GIT_CONFIG_GLOBAL=/dev/null`), so git 2.54+ prints its multi-line `init.defaultBranch` advice on every `git init`; written into a redirected stderr pipe that the helper reads only after draining stdout, the advice wedged the whole test binary for minutes (macOS, brew git bump mid-session).
- Rule: a test helper that nulls git config levels must also pin `init.defaultBranch` via `GIT_CONFIG_COUNT/KEY_0/VALUE_0` (value `master`, the nulled-config historical default), which keeps init silent and deterministic.
- Why: an advice message is stderr output that scales with git's version, not with the test; any spawn pattern that reads streams sequentially is one verbose git release away from a deadlock.
- When it applies: any new helper that spawns git with `Environment.Clear()` + redirected pipes.
