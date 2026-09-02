# Tree shape: structure lint and page moves

> Driven by the AurServices cutover of 2026-09-02, not by the borrow loop. The rebuilt wiki published
> 146 pages, every check reported green, and **54 of them landed in one flat pile on the space root**.
> Nothing in DocuMe was wrong; nothing in DocuMe could have said so either.

Date: 2026-09-02 · Scope: new-feature · Baseline area: publishing

## 1. The problem

`PageHierarchy` files a page under the nearest index page strictly above it, and refuses to synthesize
one that does not exist:

> A directory with no index page is skipped, not synthesized: with no `a/b/README.md`, `a/b/page.md`
> hangs under `a/README.md`. Inventing an intermediate page would publish a page with no source.

That refusal is correct and this spec does not touch it. The problem is what happens next. In the
AurServices wiki, fifteen directories hold publishable pages and no `README.md`, so **70 pages hang off
an ancestor that is not their own directory** — 54 of them off the space root, where seven overview
pages, ten integrations, ten infrastructure pages, four data pages, four security pages, three runbooks
and thirteen domain indexes sit interleaved in one alphabetical list. The business tier's index carries
thirty-one children with no grouping at all.

**Every tool said the run was healthy.** `convert` reported 114 pages, 0 failures, 0 degradations.
`publish` created 29 pages, updated 29, uploaded 22 diagrams and reordered 9 children, and reported
success. `status` ran its four probes — credentials, renderer, node, confluence — and found nothing to
say. The tree was structurally broken in the only way that matters to a reader, and the toolchain had no
vocabulary for it, because **DocuMe models a page's parent and has never modelled the shape of the
tree**.

There is a second half. Once a repo notices, it cannot fix it. `state.json` is keyed by markdown path
and there is no rename concept anywhere in the codebase — `GitRepository` passes `--no-renames`
deliberately, and nothing else looks. Moving `05-business/getting-a-card.md` into
`05-business/cards/getting-a-card.md` reads to DocuMe as one orphan plus one brand-new page: a delete
and a create, losing the page id, its comments, its labels, its approval history, and every URL anyone
has pasted. So the tool that cannot see the problem also cannot let you repair it, and
`docs-loop/SKILL.md` names restructuring out of scope precisely because the reasons for a tree's shape
"are usually not in the tree" — leaving tree shape as the one thing in the lifecycle **nobody owns**.

## 2. The change

DocuMe learns that a tree has a shape, and that a page can move without dying.

- **A `structure` check in `docume status`** — pure, offline, computed from the tree — naming every
  directory that holds pages and no index, and every parent wider than `wiki.maxChildren`.
- **`movedFrom:` in page frontmatter**, so a page that changes path keeps its Confluence identity: the
  same page id, comments, labels, approval and history, repositioned rather than recreated.
- **A `Move` that also covers a title change**, which today is invisible to the planner and silently
  never reaches Confluence.
- **`/docs-restructure`**, the skill that owns tree shape: it proposes a target tree, writes the index
  pages and the `movedFrom:` keys, and opens one pull request. It never touches page prose.

```yaml
---
title: Getting a Card
movedFrom: 05-business/getting-a-card.md
sources:
  - src/AurCards/**
---
```

```
$ docume status
structure  warning  15 directories hold pages but no index page; the widest parent has 54 children.
                    docs/wiki/20-integrations (10 pages) → filed under <root>; create 20-integrations/README.md
                    docs/wiki/30-infrastructure (10 pages) → filed under <root>; create 30-infrastructure/README.md
                    …
```

## 3. Design

### 3.1 The structure check is pure, and it is a warning

It is computed from the publishable path set and `wiki.homePage` — the same two inputs
`PageHierarchy.Resolve` already takes — so it needs no credentials, no network and no state, and it
runs under `--offline` like nothing else in `Checks` does. It lands in `StatusModel.Build` rather than
in `StatusProbes`, which is the I/O probe class and should stay that way.

**It reports `Warning`, never `Problem`.** A flat tree publishes perfectly well; what is wrong with it
is a judgement about readers, and DocuMe does not get to fail a repo's build over a judgement. But
`Warning` is the level `Checks` already uses for "worth reading, not fatal", the findings are carried
structurally in `--json` so a repo that wants to gate on them can, and the detail line names **the exact
file to create**. That last part is the whole intervention: the AurServices fix is seventeen `README.md`
files, and the reason nobody wrote them is that nothing ever asked for them by name.

**Two findings, and deliberately no more.**

- `orphaned-directory` — a directory with publishable pages and no index page. Carries the directory,
  the page count, and the ancestor its pages actually hang under, because "these ten pages are on the
  space root" is the sentence that makes a reader act.
- `wide-parent` — a parent with more than `wiki.maxChildren` children, `<root>` included. Default 12,
  configurable, and a repo that genuinely wants a flat section raises it rather than arguing with it.

Nothing here counts depth, checks section names, or has an opinion about what the top level should
contain.

### 3.2 There is no `wiki.structure` key, and that is a decision

The obvious sibling to this check is a declared taxonomy in `docume.json` — "these are the sections,
this is their order" — and it was designed and dropped.

The lint does not need it: "a directory has pages and no index" is computable from the tree alone, and
a declared structure that drifts from the tree is a second source of truth that will be wrong before
anyone notices. The *skills* do need a taxonomy to file a new page into — but that is the consumer
repo's editorial judgement, it belongs in `_meta/STYLE.md`, and `docs-loop/SKILL.md` already reads a
**Structure** section there. AurServices' `STYLE.md` has a *File naming* section and no *Structure*
section, which is the content-side root cause of this whole spec: the skill read a contract that was
never written, and inferred the tree from the code's shape instead. The fix for that is a paragraph in
the consumer's style guide, not a key in the tool's config (rule §9.5).

`wiki.maxChildren` is the one number the tool needs, so it is the one number the tool takes.

### 3.3 `movedFrom:` rekeys state before anything else looks at it

The key names the page's previous wiki-root-relative path. It is consumed at plan time, in
`PublishPlanner`, before the orphan comparison and before any action is decided, and consuming it means:
the state entry moves from the old key to the new one, carrying `pageId`, `contentHash`,
`publishedVersion`, `attachments`, `diagramWidths`, `verdict` and the approval record intact.

Everything downstream then works unchanged, which is the point of doing it there. `PrunePlanner`
computes orphans as state entries with no file behind them, and after the rekey there is no entry at the
old path, so a moved page never presents as an orphan and no refusal logic is needed. `PageHierarchy`
resolves the new path's parent the way it resolves any other. The planner compares body hashes and finds
them identical, so the write is a `Move`: a bodyless reposition that spends no page version and never
touches approval.

**Explicit rather than inferred from git.** `GitRepository` already refuses rename detection on purpose,
a plan must be computable offline from state plus the tree, and rename heuristics answer "these two
files look similar" when the question is "is this the same page". The precedent is `pageId:`, which is
the existing frontmatter escape hatch for claiming an identity; `movedFrom:` is the same idea aimed at a
path. It also has the property that matters most for a restructure: the claim is a line in the diff,
where a reviewer can see it.

### 3.4 A consumed move leaves a tombstone

After the rekey, state keeps a row at the old path holding nothing but `movedTo`. It is not a page and
never publishes.

Without it, a second run cannot tell a completed move from a typo: both look like a `movedFrom:` naming
a path state has never heard of. That ambiguity would force the key to be a silent no-op, and a silent
no-op on a mistyped path is exactly how a page gets recreated and its comments lost. With the tombstone,
a repeat run resolves the key to a tombstone whose `movedTo` is this page and does nothing at all, while
a path nothing knows about is a warning with a real signal behind it. It also means `movedFrom:` is safe
to leave in the file forever, which matters because DocuMe writes state and never edits the consumer's
markdown — nothing is going to clean the key up.

The tombstone earns its second keep in `PageLinkResolver`: an in-tree link to the old path resolves
through it, so the restructure PR does not have to rewrite every link in the wiki in the same commit to
avoid publishing broken ones.

`StateRebuilder` must skip tombstones. `sync --rebuild-state` adopts pages by title and would otherwise
resurrect a moved-away path as a live page pointing at an id its successor also holds — two state
entries, one page id, and a duplicate on the next publish.

### 3.5 The claims that are errors

A `movedFrom:` that resolves to nothing is a warning and the page is treated as new. Every ambiguous
claim is a hard error at plan time, before any write:

- **Two pages naming the same `movedFrom`** — nothing decides which one inherits the id.
- **`movedFrom` equal to the page's own path** — a page cannot move to where it is.
- **`movedFrom` naming a path that still has a live file** — that is a copy, and both copies would want
  one page id.
- **`movedFrom` together with `pageId`** — two contradictory claims on one page's identity.
- **A move onto a path state already records** — the target already exists; honouring it would orphan
  one of the two.

**The identity transfer is real and the existing approval rule already covers it.** A crafted
`movedFrom:` points a new page at an approved page's id, inheriting its `approved` label. But a new page
has a new body, the body hash moves, and `InvalidatesApproval` strips the label on exactly that
condition (§6.2 step 7) — so the transfer buys nothing a push-access author could not do by editing the
page directly, and the approval does not survive it. What must not happen is a *silent* transfer, which
is what the error list above is for.

### 3.6 A title change becomes a `Move`

`PagePublishPlan` carries no title, `ContentHash.OfBody` hashes the body only, and nothing else in the
planner compares titles. A page whose `title:` changes while its body does not therefore plans as
`Skip`, and the Confluence page keeps its old title indefinitely.

This is a pre-existing bug, it is unrelated to paths, and it is in this spec because a restructure is
the thing that finds it: renaming a section page is the most ordinary edit a tree reshape makes.
`PagePublishPlan` gains the resolved title, and `Move` widens from "the parent differs" to "the parent
or the title differs". The executor's `Move` branch calls the Confluence move API, which repositions and
does not rename, so a title change is written as a titled update against the same id — a page version,
but not a body change, so approval is untouched for the same reason a reposition is.

### 3.7 `/docs-restructure` owns tree shape

The fourth generative skill, and the one the lifecycle has been missing. `docs-loop` excludes
restructuring, `docs-refresh` owns drift, `docs-processes` owns the business tier and `docs-feedback`
owns the inbox — so a wiki whose shape has gone wrong has no skill to fix it and no PR to review, which
is why AurServices' shape went wrong quietly over 146 pages.

It reads `docume status --json` for the findings, proposes a target tree in the PR body **before**
touching anything, writes the index pages the check named, and adds `movedFrom:` to the pages it moves.
Its output is one pull request, like every other skill's. Its refusals matter more than its steps:

- **It does not write page prose.** An index page is a section map; a restructure PR whose diff also
  rewrites paragraphs is a restructure nobody reviews.
- **It does not invent a taxonomy.** `_meta/STYLE.md`'s **Structure** section is the contract; if the
  repo has none, the skill proposes one in the PR body and stops.
- **It does not move a page to improve it.** A page moves because its section moved.

`docs-loop/SKILL.md`'s exclusion changes from "propose it in the PR body and let a human decide" to a
pointer at this skill.

## 4. Success criteria

- **SC1** — `StructureReport` names every directory with publishable pages and no index page, carrying
  the directory, the page count and the ancestor its pages resolve to. Pure function of (paths,
  homePage); no state, no network.
- **SC2** — It names every parent whose child count exceeds `wiki.maxChildren`, `<root>` included, and
  the default is 12.
- **SC3** — `docume status` renders a `structure` check and `--json` carries the findings structurally.
  Both run under `--offline`.
- **SC4** — Given `wiki.maxChildren` absent from `docume.json`, the default applies and the schema
  accepts the key when present.
- **SC5** — A page whose `movedFrom` names a live state entry plans as `Move`, the state entry is rekeyed
  with `pageId`, `contentHash`, `publishedVersion`, `attachments`, `diagramWidths`, `verdict` and
  approval intact, and the old path does not appear in `PrunePlan`.
- **SC6** — A tombstone is written at the old path, a second run over the same tree is a no-op, and
  `StateRebuilder` does not adopt a tombstone as a page.
- **SC7** — An in-tree link to a moved page's old path resolves to the new page.
- **SC8** — Each of the five ambiguous claims in §3.5 throws at plan time with the offending paths
  named, and a `movedFrom` resolving to nothing warns and plans as `Create`.
- **SC9** — A page whose title changes and whose body does not plans as `Move` and reaches Confluence
  with the new title; approval survives it.
- **SC10** — `plugin/skills/docs-restructure/SKILL.md` exists, is registered, and passes the plugin
  tests; `docs-loop`'s restructure exclusion points at it.

## 5. Out of scope

- **Synthesizing index pages.** `PageHierarchy`'s refusal stands. The check names the file to write; a
  human or a skill writes it, with content.
- **`wiki.structure` in `docume.json`.** §3.2.
- **`order:` frontmatter.** Sibling order is ordinal path order and numeric prefixes express it. A
  path-independent ordering key is a real want for a reader-sequenced tier, and it is a separate spec —
  it needs a state-side story for what happens when two pages claim the same order.
- **Git rename detection.** §3.3.
- **Rewriting in-tree links on a move.** The tombstone makes the old links resolve; mass link rewriting
  is the restructure skill's editorial choice, not the CLI's.
- **Deleting tombstones.** They are bookkeeping rows, they are small, and a verb that removes page
  identity records needs the same care `--prune` gets. Left to a human editing `state.json`.
- **Failing a build on structure findings.** `Warning`, and `--json` for repos that want to gate.
- **The AurServices content work itself.** Seventeen index pages and the business-tier regrouping are
  that repo's PRs, tracked there.

## 6. Risks

- **The rekey runs before the orphan comparison, and getting that order wrong deletes pages.** A
  `movedFrom` consumed after `PrunePlanner` reads state presents the old path as an orphan, and a
  `--prune` in the same session trashes the page the move was meant to preserve. The ordering is a
  plan-time invariant with a test that asserts it directly, not a comment.
- **Reparenting 70 pages notifies every watcher.** Unavoidable and worth saying out loud: the
  AurServices Phase 1 publish will fire a notification per moved page. It is one deliberate run, not a
  drip.
- **`wide-parent` is an opinion with a number on it.** 12 is a guess. It is configurable, it is a
  warning, and if it turns out to nag it costs one line in `docume.json` rather than a code change.

## 7. Assumptions

- The Confluence move API repositions without renaming, so a title change is an update against the same
  id rather than a move — verified against the existing `Move` branch, to be re-confirmed in
  implementation.
- A page's `verdict` is sealed against content and sources, not path, so carrying it through a rekey is
  correct. A move that also changes `sources:` changes the sources hash and unseals it the ordinary way.
- `wiki.homePage` stays a single name applied per directory. A per-directory index override would change
  what "orphaned directory" means and is not contemplated here.
