# Page owners and drift routing

> Borrow loop round 9. Borrowed from kubernetes-sigs/prow's `OWNERS`, Backstage TechDocs' `spec.owner`,
> and kubernetes triagebot's `[mentions]` config — **four independent convergences** across rounds 1, 3,
> 4 and 8 (round 8 added Connorrmcd6/surface, whose whole thesis is that a doc nobody re-confirms rots).
> The loop has never had a backlog item converge four times.

Date: 2026-08-20 · Scope: new-feature · Baseline area: drift

## 1. The problem

Drift detection is now good. Round 6 gave it path exemptions, round 7 commit exemptions, round 8 sealed
verdicts, and the report that comes out the far end is genuinely trustworthy: it names the pages whose
documented sources really moved, and it holds out the ones that provably did not.

**It is addressed to nobody.** `docume drift --format github-comment` posts a block on a PR listing pages
that may no longer describe the code. The PR author changed `src/Loans/Rate.cs`; they did not write
`domains/loans.md` and very likely have never opened it. The comment is a notice pinned to a wall.

This is the last link in the chain the loop has been building. A drift signal that is precise, disclosed
and unroutable is a signal that gets scrolled past, and every round spent sharpening its precision is
spent on a report with no addressee.

## 2. The change

A page may declare who owns it, and drift routes to them.

```yaml
---
title: Loans Domain
owner: "@moberghr/lending"
sources:
  - src/Loans/**
---
```

- **`owner:` in page frontmatter** (§5.2), a single string, carried verbatim.
- **The drift PR comment groups affected pages by owner** and names each owner at the head of their
  group, so a GitHub handle becomes a real mention and a real notification.
- **Pages with no owner are disclosed, not hidden** — grouped last under a named bucket, and counted in
  the verdict line. A drift report where nobody owns anything should say so out loud.
- **The dashboard gains an Owner column**, so the standing view answers "who do I ask about this page?"
  without opening the repo.

## 3. Design

### 3.1 `owner:` is carried verbatim, never normalized

The value goes into the comment exactly as the frontmatter spells it. DocuMe does **not** prepend `@`,
does not validate the shape, and does not resolve it against anything.

That is a deliberate refusal, and it is the §9.5 line: who owns a page, and what a handle looks like on
the forge that repo uses, is the consumer repo's knowledge. A tool that "helpfully" turned `alice` into
`@alice` would ping whichever GitHub account happens to hold that name — a stranger, in the common case
where the repo's convention is an email or a display name. Silence is the safe failure: an owner written
without `@` appears in the comment as plain text, mentions nobody, and is visibly wrong to the person
reading it.

**The renderer neutralizes what a forge handle cannot contain.** [REVISED 2026-08-19 after Stage 1
review, approved] This section first said the value "goes into the comment exactly as the frontmatter
spells it", and the renderer's own comment defended that with a containment argument: the owner sits
alone on its line, and CommonMark cannot carry emphasis across a line break. Both halves were false, and
the review reproduced both. `Owner` is a YAML scalar, and YAML has two ways to put a newline in one — a
`\n` escape in a double-quoted scalar, and a `|` block scalar — so a crafted value renders a forged
`### No drift detected` heading; and inline raw HTML needs no newline at all, because CommonMark passes
it through and GitHub allowlists `<details>`/`<summary>`, so an unclosed one collapses the real page list
and every other owner's group behind a triangle labelled "Resolved". The threat model is the part that
makes this matter: for a drift comment **the PR author is the adversary**, they have push access by
construction, and the scaffolded workflow posts the comment under the bot's identity.

**The rule is a test, and stating it as a count is what let a third construct through.** [REVISED
2026-08-20 after the final review, approved] This paragraph read "two characters, and only two", and
`DriftComment.Heading` said "exactly two things reach out of this line". Both were a count where a rule
was needed, and both are the sentences a reader would have cited to refuse the third one:
`owner: "[Resolved — see the fix](https://evil.example/login)"` renders as an arbitrarily-labelled
clickable link inside a comment the bot signs, and `![…](…)` is the same construct with an `!` in front,
loading its target without a click. `Heading` neutralizes a character when both halves hold: **no forge
handle can contain it**, *and* leaving it alone lets the owner say something the tool never said, either
by ending this block or by turning the rest of the line into a construct instead of text. Three
constructs meet that test today:

- **A line ending — it leaves the line.** `Owner` is a YAML scalar, and YAML carries a newline inside one
  two ways, so everything after it becomes a fresh CommonMark block. Collapsed to a space.
- **`<` — it stays on the line and stops being text.** CommonMark passes inline raw HTML through and
  GitHub allowlists `<details>`/`<summary>`. Rendered as `&lt;`.
- **`[` and `]` — they stay on the line and stop being text.** A link or image label. Rendered as `&#91;`
  and `&#93;`. The pair travels together even though closing `[` alone would stop the link today: a label
  is one construct with two ends, and pinning only the opener would make this line's safety depend on no
  neighbouring sink ever emitting a bare `[` into the same block, which is the containment shape the rest
  of this renderer gave up.

Everything else is left exactly as written, whatever CommonMark makes of it, and `_` and `*` are why the
full `Escape` helper can never be reached for here: they are ordinary bytes in `@my_org/team`,
`_platform_` and `*docs*`, and escaping them leaves a comment that renders almost right and mentions
nobody. So the set is expected to grow when a construct turns up that this list does not name; what it
must never do is grow onto a byte a handle carries. The verbatim claim survives all three, because no
handle on any forge contains a line break, a `<` or a bracket.

Numeric character references rather than backslash escapes, and not only to match `&lt;`: `Heading`
deliberately leaves `\` alone, so `\[label](url)` would come out as `\\[label](url)` — an escaped
backslash rendering a literal `\`, followed by a live link. `&#91;` has no such predecessor, and an
author who prefixes it with a backslash only escapes the `&` and gets the entity printed at the reader.
What is knowingly left standing is the bare URL: GitHub autolinks a literal `https://` or `www.` run, so
an owner that is one becomes clickable. That is accepted rather than missed — an autolink's label *is*
its href, so it cannot claim to lead somewhere it does not, and breaking one would mean touching `/` or
`.`, which `@moberghr/lending` and `mirko.budimir@moberg.hr` are made of.

Each sink neutralizes what its own syntax makes dangerous, which is why this lives in the renderer and
not the parser: `--format json` treats a newline as data, and the dashboard HTML-escapes at
`DashboardPage.cs:251`.

**The line-ending half belongs to the class, not to the owner.** [REVISED 2026-08-20 after the Stage 1
re-review, approved] The first fix closed the instance and left the class open, which the re-review
caught: `title:` is the sibling YAML scalar, same frontmatter, same author, same comment, and
`DriftComment.Escape` neutralized markdown metacharacters while doing nothing about line endings — so a
multi-line `title:` forged the same `### No drift detected` heading. `Escape` now collapses line endings
first, which makes every value routed through it line-safe at once: both titles, the seal timestamp read
back from the committed and hand-editable `_meta/state.json`, the exemption reason, and the baseline and
head revisions. The audit that found the third of those is the point — fixing the reported instance and
declaring the class closed is how the second Critical happened. `Heading` keeps its own narrow path, and
must: routing the owner through `Escape` would escape `_` and `*` and stop `@my_org/team` mentioning
anyone.

**The class is thirteen values, not seven, and the six that were missing were the ones inside code
spans.** [REVISED 2026-08-20 after the Stage 3 re-review, approved] Two rounds ended by enumerating what
`Escape` covers and calling the class closed, which quietly redefined the class as "the values that go
through `Escape`". A mechanical walk of `DriftComment.Render` finds thirteen author-controllable strings,
and the six left over are the paths and globs the renderer wrapped in backticks: the affected page's
path and its matched glob and files, the sealed page's path, and the exempted change's path and glob. All
six were argued safe from their producers, and both arguments were false. Wiki page paths do not come
from git at all — `WikiTree.InScope` reads them off the working tree with `Directory.EnumerateFiles`
(`WikiTree.cs:255-258`), so a line break in a filename arrives literally and forges the same
`### No drift detected` heading at column 0, before inline parsing considers the code span. And a code
span holds nothing on its own: one **backtick** in a filename or a glob closes it early and the rest of
the line is live markdown, with no line break needed and no quoting anywhere to stop it — git C-quotes
control bytes, but a backtick is an ordinary printable character it passes through untouched
(`GitRepositoryTests.Quotes_a_line_break_in_a_path_and_leaves_a_backtick_alone` pins both halves). The
`sources` containment had its own hole besides: `DriftPlanner.NormalizePattern` trims before matching, so
a glob whose raw spelling opens with a line break matches exactly what its trimmed spelling matches and
is then recorded raw.

**So there is one rule and no containment arguments.** Every author-controllable value is neutralized at
the sink it is rendered into, against the syntax of that sink: prose through `Escape`, code spans through
the new `DriftComment.Code`, and the owner through `Heading`'s narrow set. That split moved the two
revisions as well — the provenance line prints them as code, and a backslash is not an escape inside a
code span (CommonMark gives one no escape mechanism at all), so `Escape` was leaving a backtick in
`--baseline` free to close the span and every other backslash it wrote visible to the reader. Eight
values go through `Code`, four through `Escape`, one through `Heading`. `Code` collapses line
endings for `Escape`'s reason and then opens the span with a backtick fence longer than the longest
backtick run in the value, padding with a space at each end when the value's own edges would fuse with
the fence — the CommonMark-correct way to render arbitrary bytes as code, and unlike routing a path
through `Escape` it leaves no backslashes inside the span for a reviewer to puzzle over. Ordinary paths
render byte-identically to before. What this deliberately gives up is the shape that caused three
rounds: no part of this renderer's safety now depends on what `WikiTree`, `DriftPlanner` or git do next.

The reference page says this in one sentence: **write the handle the way your forge mentions people.**

### 3.2 Where the owner travels

`DriftedPage` gains `Owner`. `DriftPlanner` reads it off the page's frontmatter in the loop it already
runs, so nothing new is enumerated and the planner stays a pure function of (changed files, pages).

`DriftReport` gains `UnownedCount` — affected pages with no owner. A count rather than a flag, because
the verdict line states it and a reviewer needs the proportion, not the boolean.

### 3.3 Grouping in the comment

Affected pages group by owner, ordinal by owner string, with **unowned last** under a fixed heading. Two
properties matter and are pinned by test:

- **Ordinal, stable ordering.** The comment is rewritten in place by a bot on every push (the sticky
  marker at `DriftComment.Marker`). A grouping whose order depended on a hash seed or dictionary order
  would produce a diff on every run with no change in the answer.
- **Every affected page appears exactly once.** Grouping is a partition, not a filter. The count in the
  verdict must equal the sum of the group sizes, and a test asserts that rather than trusting it — a
  grouping bug that dropped a page would hide exactly the drift the feature exists to route.

### 3.4 What is not routed

Sealed pages (round 8) and exempted files (rounds 6 and 7) never enter `Pages`, so they are never routed.
That falls out of the existing design rather than needing a rule: routing consumes `Pages`, and everything
already held out is already out.

## 4. Success criteria

| id | description | verification |
|---|---|---|
| SC1 | `owner:` parses off frontmatter, absent means null, blank means null | `FrontmatterParserTests` |
| SC2 | An affected page carries its owner into the report; an unowned one carries null | `DriftPlannerTests` |
| SC3 | `UnownedCount` counts affected pages with no owner, and only affected ones | `DriftPlannerTests` |
| SC4 | The PR comment groups by owner, ordinal, unowned last | `DriftCommentTests` |
| SC5 | Grouping is a partition: every affected page appears exactly once, group sizes sum to the affected count | `DriftCommentTests` |
| SC6 | The owner is emitted verbatim — no `@` added, no case change, no trimming beyond YAML's own | `DriftCommentTests` |
| SC7 | The verdict line discloses how many affected pages have no owner | `CliDriftTests` |
| SC8 | `--format json` carries `owner` per page and `unownedCount` | `CliDriftTests` |
| SC9 | The dashboard's per-page table carries an Owner column | `DashboardPageTests` |
| SC10 | A sealed or exempted page is never routed to an owner | `CliDriftTests` |
| SC11 | `owner` is declared in PLAN.md §5.2 and read by something in `src/` | `PlanDataContractTests` |

## 5. Out of scope

- No `OWNERS`-file or `CODEOWNERS` inheritance, no directory-level cascade. One page, one `owner:` key.
- No validation, resolution, or existence check of the owner value against any forge.
- No multiple owners. A page with shared ownership names a team handle, which is what a forge mention
  already supports; a list can be added later without changing what a single value means.
- No reviewer auto-assignment on the PR, no notification outside the comment DocuMe already posts.
- No owner-driven behavior in `--mark`, publish, prune, or approval (§8 untouched).
- No owner in the feedback loop (§6.3) this round.

## 6. Risks

1. **An owner written without the forge's mention syntax silently pings nobody.** Mitigated by the
   reference page saying so plainly, and by the value appearing verbatim in the comment where it is
   visibly not a link. Deliberately not "fixed" by normalizing — see §3.1.
2. **A stale owner outlives the person.** DocuMe cannot know that. The dashboard column is what makes it
   visible enough to notice, which is the same answer TechDocs and prow settle on.
3. **Grouping changes an established output shape.** The drift comment is consumed by an existing
   scaffolded workflow; the sticky marker and the section order stay as they are, and the group headings
   sit inside the existing body section rather than reordering it.

## 7. Assumptions

- `[VERIFIED:src/DocuMe.Core/Markdown/PageFrontmatter.cs:14]` frontmatter carries only `sources`,
  `title`, `pageId`, `publish` today — `owner` is genuinely unbuilt.
- `[VERIFIED:tests/DocuMe.Core.Tests/Acceptance/PlanDataContractTests.cs:89]` §5.2 maps to
  `PageFrontmatter`, so a new member must be declared in PLAN.md and read by `src/` or the suite reddens.
- `[VERIFIED:src/DocuMe.Core/Dashboard/DashboardPage.cs:227]` the per-page table's header row is one
  `WriteRow` call, so a column is an additive change to a row builder that already exists.
- `[VERIFIED:src/DocuMe.Core/Drift/DriftComment.cs:127]` the comment body is one `WriteBody` section,
  which is where grouping lands without disturbing the sealed and exempt disclosures around it.
