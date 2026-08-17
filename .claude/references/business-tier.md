# Business & process tier — user-oriented pages from the same verified pipeline (M9)

> Spec for `PLAN.md` §14's **M9** row. Lives here for the same two reasons as
> `publication-targets.md`: `PLAN.md` is on the build loop's step-1 read path with a 20,000-token
> budget, and a milestone spec does not need re-reading every iteration. Rationale, market scan and
> accepted risks: `decisions.md`, entry "A business & process documentation tier" (2026-08-13).

**The request.** The technical wiki serves an engineer. The same codebase holds every fact a
business reader needs, and nobody maintains that tier: what the system does, how a process runs end
to end, what the rules are, who can do what, what a refusal means. Inventhor is the proof of demand:
its `docs/wiki/_meta/STYLE.md` names an end-user surface (`website/`, "deliberately no
implementation detail") that is hand-written inside feature commits, with no generation, no drift
detection, no verification. A market scan (2026-08-13, decision log) found no product doing
continuously refreshed, code-verified business documentation; the fragments that exist are one-shot
modernization extracts (Swimm, CAST), UI-recorded SOPs with no code grounding (Scribe), and
technical-only generators (Dosu, Rovo). The wedge is the discipline DocuMe already has, pointed at a
second audience.

**What it is.** One new skill plus a register contract. Business pages are ordinary pages in an
ordinary subtree of the same `wiki.root`, so every CLI behaviour applies to them unchanged:
hierarchy and ordering (`src/DocuMe.Core/Publishing/PageHierarchy.cs`), publish and `contentHash`,
`sources`-glob drift, `approved`/`stale` labels, the dashboard, the feedback inbox. The register
changes; the evidence discipline does not.

**What M9 does not touch.** No CLI, converter, schema, or state-model change; the golden corpus is
untouched. Not a second space (`confluence.spaceKey` stays a scalar). Not a publication target
(M7a/M8's seam is a different axis: *where* pages go, not *who* they are for). No screenshots or UI
automation (S10). No `docume init` scaffolding change (S12).

## Five mechanisms

**1. Placement, free.** The consumer names the tier's directory in `STYLE.md`'s Structure section
(Inventhor: `40-business/`); a directory with a `README.md` is a fully formed Confluence subtree
with zero code change. Skills identify tier membership by that declaration, never by a hard-coded
name (rule §9.5). Where `STYLE.md` is silent the skill uses `40-business/` and says so in the PR
body, the same fallback contract `docs-loop` uses for a scaffolded `STYLE.md`. Titles are unique
per space and `publish` hard-errors listing duplicates (§6.2 step 1), so a collision is loud; the
convention that avoids most of them is verb-phrase process titles ("Requesting time off") against
the technical tier's noun titles ("Time off"), with frontmatter `title:` as the override.

**2. Citations move into HTML comments.** The verification contract holds unchanged: every factual
claim carries a citation. On business pages the citation is an HTML comment on its own line under
the paragraph it backs:

```markdown
A week off spanning a public holiday costs four days, not five, and both ends of the
range are included: Monday to Friday is five days.
<!-- cites: backend-server/Inventhor.Api/Services/WorkingDayCalculator.cs -->
```

Three properties, all pinned by the existing golden case (`tests/golden/html-comments.md` →
`.storage.xml`):

- The converter drops comments from output, so a business reader never sees a file path.
- The comments stay in the repo markdown, so `/docs-refresh` and `/docs-feedback` verify and update
  them exactly as they do visible citations.
- `contentHash` is computed over the *converted* body (§5.3, §6.2 step 5), which no longer contains
  the comment, so a citation-only edit (a line number moved by a refactor) never invalidates an
  approval and never spends a page version.

Block form, not inline: the golden shows an inline comment leaves the author's spacing behind as
residue. Grammar: `<!-- cites: <ref>[; <ref>] -->` where a ref is a repo path with optional
`:line`, a symbol name, or `_meta/BUSINESS.md § <heading>`. `links.repoBlobUrl` linkification never
sees these refs; that is fine, they are for skills and reviewers, not readers.

**3. Markers are banned on business pages: verified or absent.** `⚠️ UNVERIFIED` is reader-visible
by design and this reader cannot act on it. A sentence the code and `_meta/BUSINESS.md` cannot
settle does not appear; the question goes to `_meta/GAPS.md`. Stricter than the technical tier, not
looser.

**4. A second ground truth: `_meta/BUSINESS.md`.** Some sentences a business page needs are not in
code: the legal intent behind a constant, a policy rationale, org context. These come from a
consumer-owned seed-facts file, cited like code (mechanism 2's grammar). The skill never writes
facts into it — the same ownership rule `docs-loop` holds for `STYLE.md` — and proposes candidate
entries in the PR body instead. Missing on first run, the skill creates an instructional stub (the
`GAPS.md` precedent, `PLAN.md` §3) and works from code alone. It lives under `_meta/**`, so
`wiki.exclude` keeps it unpublished.

**5. A process inventory of its own.** The unit is a process, journey, or overview, not a code
unit. Bookkeeping lives in `_meta/PROGRESS-BUSINESS.md` — same table format and four states as
`docs-loop`'s `PROGRESS.md`, a separate file so `docs-loop`'s "first `todo` in file order" rule is
untouched. Derivation, on the inventory-building first run: actors from permission and module-access
checks; processes from state-machine enums plus the handler groups that move them, background jobs
that advance state, and integrations that trigger flows; the overview page is the subtree's
`README.md` and the first unit. A process page's `sources` globs are the handlers, services and
enums it derives from, and they cross technical-page boundaries by design — drift then marks both
tiers from one code change, which is the point. **`baselineSha` rule:** the oldest generation sha
across *both* progress files; `docs-loop` step 6 gains that one sentence (D2).

## The register

Defaults, used when `STYLE.md` has no Business-tier section; the consumer's section wins where it
exists. Second person for the actor ("you request, your manager approves"). No type names, file
paths, HTTP verbs or SQL in visible prose. Rules stated as their consequence, not their
implementation. Every refusal explained with what the reader does next. Diagrams are mermaid
`flowchart` or `sequenceDiagram` showing actor-visible states and gestures, never classes;
`beautiful-mermaid` rejects `pie` and `graph TD;` spellings (§7 revision note), and
`docume convert` with `--render-mermaid` is the pre-flight. Every page opens with what the process
is for, before any mechanics. The benchmark for this register is Inventhor's hand-written
`website/docs/time-off.md`.

Verification adds one register check to `docs-loop`'s step 5: grep the visible prose (outside
fences and comments) for code-shaped tokens — `.cs`, `src/`, `()`, `Features/` — and rewrite any
hit. The PR body keeps the full claims table with real code refs: the reviewer sees exactly what
the reader does not.

## Deliverables

| # | What | Where |
|---|---|---|
| D1 | New skill: process inventory, register, comment citations, `BUSINESS.md` contract. Mirrors `docs-loop`'s anatomy; system-contract clauses 1–3 and 5–7 verbatim, clause 4 replaced by mechanisms 2–4. Branch `docs/processes-<date>` (rule §8.4) | `plugin/skills/docs-processes/SKILL.md` |
| D2 | `baselineSha` = oldest sha across both progress files; "what this skill does not do" gains the business tier | `plugin/skills/docs-loop/SKILL.md` |
| D3 | A stale page under the business directory regenerates in the register, comment citations maintained, no markers introduced | `plugin/skills/docs-refresh/SKILL.md` |
| D4 | Replies to comments on business pages answer in the page's register; verification discipline unchanged | `plugin/skills/docs-feedback/SKILL.md` |
| D5 | Skill table row for the marketplace entry | `plugin/README.md` |

## Acceptance

On a real consumer (Inventhor, space `INVENTHOR`, which is not the §1.4-locked space):

1. First run builds the inventory as a PR; a human reorders it before any page is written.
2. Subsequent runs generate the overview plus one process page (time off); `docume convert` exits 0
   with every accepted code named in the PR body.
3. Published under the business subtree and reviewed page-by-page, the M2 bar.
4. **The benchmark:** the generated time-off page covers every rule the hand-written
   `website/docs/time-off.md` states, each claim carrying a citation the hand-written page never
   had; divergences are findings, listed in the PR body.
5. Touching one of the process's source files puts the business page in `docume drift`'s output
   alongside the technical page that shares the glob.

## Open questions

| # | Question | Default if unanswered |
|---|---|---|
| S10 | Screenshots: a UI-facing process page without captures may underserve end users. Worth a capture pass (Playwright) later? | Ship text + diagrams only; a text page is verifiable, a screenshot never is |
| S11 | Does the business tier eventually publish to a second reader surface (a site, Docusaurus)? Depends on M8's executor seam and S9's answer | Confluence only; the subtree is the surface |
| S12 | Should `init` scaffold the Business-tier `STYLE.md` section and the `BUSINESS.md` stub? C# + scaffolder tests | Not until the file shapes have survived one real consumer; the skill's stub-on-first-run covers the gap |
