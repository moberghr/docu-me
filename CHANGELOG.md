# Changelog

All notable changes to DocuMe are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

One version covers everything: the `DocuMe.Cli` and `DocuMe.Core` packages and the Claude Code plugin all
ship off a single `vX.Y.Z` tag, so a heading here describes all three. The version at the top of this file
is the one `Directory.Build.props` declares, whether or not its tag has been pushed yet.

## [0.3.0] - 2026-08-20

Two reports got honest in this release. Publish now says what it overwrites, stamps what it wrote, and
records what it wrote it from. Drift reads those records and stops reporting pages whose code demonstrably
did not move.

Drift has always answered from a commit range, and a range is evidence about commits rather than about
bytes. A publish now records what it published against, and drift checks that record before it reports.
Two exemption files sit beside it, for the paths and the commits a repo can declare in advance never mean
the docs moved.

The last of it answers a different complaint about the same report. A drift finding that names exactly the
right pages is still addressed to nobody: the engineer who changed the code did not write the page and has
very likely never opened it. Pages can now say who owns them, and drift routes to them.

Setup changed too. `init` asks which agent the model-driven workflows are written for, and the style guide
it scaffolds asks every question the generation skills answer to.

### Added

- **`docume status` learned that a tree has a shape.** A new `structure` check names every directory with
  publishable pages beneath it and no index page, carrying how many pages are under it, how many are in it
  directly, and where its index page would hang once written; and every parent with more children than the
  new `wiki.maxChildren`, which defaults to 12 and is the one number the check takes. It is computed from
  the publishable paths and `wiki.homePage` alone, so it needs no credentials, no network and no state, and
  it answers under `--offline` like nothing else in the check table. It is a warning and never a problem: a
  flat tree publishes perfectly well, and what is wrong with it is a judgement about readers, which DocuMe
  does not get to fail a build over. Every line names the exact `README.md` to create, which is the whole
  intervention — a wiki that published 146 pages with 54 of them in one flat pile on the space root needs
  seventeen index files, and the reason nobody wrote them is that nothing ever asked for them by name.
  `--json` carries the findings under `structure` for a repo that would rather gate on them than read them.
- **Publish names the hand edits it overwrites.** A live page whose version is ahead of the one
  `_meta/state.json` recorded was edited in Confluence, and the body write that follows discards that
  edit. Overwriting is the design, because the repo is the source of truth; doing it without a word was
  the gap. The run now warns per page and names both versions, so the reader can open the page history and
  judge what is being lost. Both numbers come from the optimistic-lock read the publish already does, so
  the warning costs no extra request and nothing is refused.
- **Every body update stamps its provenance into the Confluence version message:**
  `docume publish — repo <sha>, content sha256:<hex>`, which tool wrote the version, from which commit, of
  which content. Page history becomes legible next to hand edits, since a version without the stamp was
  not written by DocuMe, and the content hash is the same banner-excluded value the state file records, so
  a page and its state can be paired again from Confluence alone if `_meta/state.json` is ever lost.
- **Every page DocuMe creates carries a `docume` content property** saying the page is managed and which
  file owns it, and a body update stamps a page that predates the marker. `publish --prune` reads the
  property live before each delete, after the confirmation it already required, and refuses any orphan
  whose page is not stamped: the page is reported under a new Unmanaged list, nothing is deleted, and the
  run still exits 0. Presence in the state file is weaker proof of authorship than the page's own stamp,
  and `init --adopt`, which seeds ids for pages DocuMe never created, is exactly where the two diverge.
- **`docume sync --rebuild-state` reconstructs the page map from the space.** The marker is the registry,
  so a state file that was lost, forked without its history, or hand-edited into nonsense now has a
  recovery path: the run walks the space, reads each page's `docume` property, and prints an adoption
  manifest. A stamped page whose path names a file in this repo is adopted with its id, title and marked
  flag, and deliberately no content hash, so the next publish re-records it honestly. State disagreements,
  duplicate claims and paths naming no file are reported and never written, because a prune cannot touch
  what state does not hold. Approvals are never rebuilt; they are the reviewers' to give back, and the run
  says so. The listing stops loudly at 12,500 pages rather than adopting from a truncated walk, and it
  writes no Confluence byte, which is why the protected-space lock has nothing in it to refuse.
- **`publish: false` in a page's frontmatter parks it as a draft.** The page is held back before the
  converter sees it: it cannot fail the run, plans no attachments, is invisible to every drift count, and
  is never an orphan, because its file still exists, so a copy published earlier stays frozen in Confluence
  until the flag flips back. Drafts are named in the publish report and on a `DRAFTS` line in the CLI, and
  a child under a never-published draft parent fails with a message that names the draft rather than
  blaming its parent.
- **`_meta/drift-ignore` names the changes that never mean drift.** One glob per line, matched against
  changed paths exactly the way `sources` globs are, a column-zero `#` for a comment, and an optional
  trailing `# reason` the report quotes back. A changed file that any pattern matches is held out of the
  matching for every page and disclosed in every format: the table's `EXEMPT` section, the JSON's
  `exempted` list, the PR comment's exemption note. Exempt files count toward neither the exit code nor
  `--mark`. A malformed line fails the run naming its line number, because an exemption that silently
  never fires reads as protection that does not exist.
- **`_meta/drift-ignore-revs` holds mechanical commits out of drift.** A format sweep or a licence-header
  pass touches the very files the docs describe, and a path exemption written for it would outlive the
  sweep and swallow the next real change. The revs file names the commit instead, in git's own
  `blame.ignoreRevsFile` format as git actually reads it: full 40-character shas, comments behind a
  leading or trailing `#`, abbreviations refused. When a listed commit is in the compared range the diff
  is attributed per commit, so a file counts as changed only when an unlisted commit touched it, and a
  file touched by both still drifts. Every format carries the disclosure, and commit exemptions compose
  before the path exemptions above.
- **Publish seals the sources it published against.** A run that writes a page body fingerprints every
  file that page's `sources` globs match, taken over the files git tracks so gitignored build output can
  never be in it, and records the hash in `_meta/state.json` under the page's new `verdict` key with the
  moment it was taken and the commit the run was publishing.
- **`docume drift` reports a sealed page rather than a drifted one.** It recomputes the fingerprint for
  the pages the diff flagged and holds out every page whose sources are byte-identical to its seal, so a
  revert inside the range, a merge that re-introduces identical bytes, and a `baselineSha` older than a
  page's own last publish all stop producing phantom drift. Sealed pages are named in the table, carried
  in the `sealed` array of `--format json` and in their own section of the PR comment, counted out of
  the exit code under `--fail-on-drift`, and never labelled by `--mark`. There is no new command and no
  new flag: the check only ever removes pages whose bytes are provably unchanged, so there is nothing to
  switch on and nothing to switch off.
- **A page with no seal keeps the answer it had.** Every page published before this existed, and every
  page whose sources a publish could not read, answers drift from the commit range exactly as before,
  and the page's next publish seals it. A publish seals only what it can prove: a directory git cannot
  answer for, an empty tracked-file list (an empty index, or a sparse checkout cone'd away from the
  code), and globs that match no tracked file all seal nothing and say so. All three would otherwise
  record the fingerprint of no files, which every one of those conditions reproduces exactly — so a
  later run would match it and report a page whose sources were never read as verified. `docume drift`
  refuses that value from the other side too, for state files that already carry one.
- **`owner:` in page frontmatter, carried verbatim.** A page may name a single owner. DocuMe never
  prepends `@`, never changes the case and never resolves the value against a forge: how a mention is
  spelled is the consumer repo's knowledge, and a tool that turned `alice` into `@alice` would notify
  whichever account happens to hold that name. Write the handle the way your forge mentions people; one
  written without the mention syntax reaches the comment as plain text and pings nobody, which is at
  least visible to the person reading it. Verbatim stops where a forge handle stops: the PR comment
  collapses line endings and neutralizes `<`, `[` and `]` before it prints the handle, because a YAML
  scalar can carry a newline, inline raw HTML needs none, and `[label](url)` needs neither — each of the
  three lets a crafted owner forge a "no drift" heading, hide the real report behind a `<details>`, or
  post a clickable link to anywhere, inside a comment the bot signs. None of them appears in a forge
  handle, which is the test: `_` and `*` are left alone precisely because `@my_org/team`, `_platform_` and
  `*docs*` are made of them, so a mention is unaffected.
- **The drift PR comment groups the affected pages by owner.** One heading per owner, ordinal by the
  owner string so the comment a bot rewrites in place on every push comes out in the same order every
  time, with the pages nobody owns last under a `**No owner**` heading that says how many they are and
  that the drift is addressed to nobody. The verdict line under the table discloses the same count, and
  `--format json` carries `owner` per page alongside `unownedCount`. An unowned page carries no `owner`
  key at all, so "unowned" has exactly one spelling on the wire. The grouping is a partition rather than
  a filter: every affected page appears under exactly one heading, and a sealed or exempted page is
  never routed, because routing reads the affected list those pages already left.
- **The dashboard's per-page table gained an Owner column**, so the standing view answers "who do I ask
  about this page?" without opening the repo. There is no new command and no new flag. One limit is
  worth stating: a stale owner outlives the person, DocuMe cannot know that, and the column is what
  makes it noticeable.
- **`docume init --agent claude|copilot` picks the rail the scaffolded workflows are written for.** The two
  model-running workflows, refresh and feedback, ship in a `claude` and a `copilot` spelling, and a repo
  now receives the pair for its rail instead of both. The choice is recorded in `docume.json`, where a
  re-run without the flag keeps it; an unknown value is refused before any file is written, and a repo
  scaffolded before the copilot rail existed reads as `claude`. The `docume.json` schema gained the enum,
  and the scaffolder writes the value as a string, where it previously wrote `"agent": 0`, an integer no
  reader or editor could interpret.
- **The scaffolded `_meta/STYLE.md` asks seven questions rather than four.** Scope, Diagrams and Business
  join audience, tone, structure and verification, still questions and never answers, so a consumer
  decides depth, diagrams and whether the business tier is wanted before the first generation run instead
  of discovering the levers after a sparse wiki ships. The wiki states the mechanics that were invisible
  beside it: `_meta/STYLE.md` is the lever, generation is two skills whose first runs inventory before
  they write, and a missing business tier means `/docs-processes` has not run.

### Fixed

- **The drift PR comment can no longer be forged by a file it names.** Every string the comment renders
  that a pull request's author writes — page titles and owners, page paths, `sources` globs and the files
  they matched, `_meta/drift-ignore` paths, globs and reasons, the seal date, and the two revisions — is
  now neutralized where it is printed, against the syntax it is printed into. Two shapes were reachable
  before, both by committing a file: a line break in a wiki page's **filename** ended the list and opened
  a forged `### No drift detected` heading inside a comment the bot signs, and a single **backtick** in a
  filename or a glob closed its code span early and turned the rest of the line into live markdown, which
  GitHub renders as an unclosed `<details>` that hides the real report. Paths and globs keep their exact
  spelling and gain no backslashes, and an ordinary path renders byte for byte as it did. The two
  revisions in the provenance line are fenced the same way, so a backtick in `--baseline` no longer
  leaves half a sha outside its code span.
- **The full test suite could deadlock instead of finishing.** Six process helpers redirected both streams
  and read them in sequence, so a child that filled the unread stderr pipe blocked in `write(2)` forever
  while the test blocked reading stdout to EOF. The advice git 2.54 began printing about the branch
  name an `init` picks made `git init --bare` that child: two full-suite runs hung at the same spot,
  one of them for 41 hours. Every helper now drains stderr concurrently, and the release-workflow
  helper, which nulls every config level and so drew that advice on every init, pins the branch name
  outright.
- **A clean checkout failed the tree sweeps over files only a developer's checkout has.** The M10 merge
  declared `AGENTS.md` and `.codex/` in the sweeps and both are gitignored by design, so every fresh clone
  went red on files it could not have. `.codex/` is skipped like `.playwright-mcp/` before it, and
  `AGENTS.md` is classified when a checkout carries it and never missed when one does not.

## [0.2.0] - 2026-08-14

Until now every page DocuMe generated spoke to an engineer. This release adds a second documentation
audience: a business & process tier, generated with the same verified-claims discipline and pointed at
readers who never open the source. Nothing in the CLI, converter or schema changed — business pages are
ordinary pages in an ordinary subtree, which is what makes the whole tier possible in a skills-only
release.

### Added

- **`/docs-processes`, the fourth plugin skill.** It generates business and process pages — what a process
  is for, who can do what, what a refusal means — into a consumer-named subtree of the same wiki
  (`40-business/` when `_meta/STYLE.md` does not choose one), so publish, drift, approval, dashboard and
  feedback all apply to them unchanged. Its unit is a process rather than a code unit, inventoried in
  `_meta/PROGRESS-BUSINESS.md`, and its PRs arrive on `docs/processes-<date>` branches.
- **Citations a reader never sees.** Every claim on a business page is still cited, but the citation is an
  HTML comment (`<!-- cites: … -->`) directly under the paragraph it backs: dropped by the converter,
  greppable by `/docs-refresh` and `/docs-feedback`, and outside `contentHash`, so a citation-only edit —
  a line number moved by a refactor — never invalidates an approval. The `⚠️` markers are banned on this
  tier: a claim is verified or absent, and open questions go to `_meta/GAPS.md`.
- **`_meta/BUSINESS.md`**, a consumer-owned seed-facts file for what code cannot state: legal intent,
  policy rationale, organisational context. The skill creates an instructional stub when the file is
  missing and never writes facts into it.
- **The three existing skills learned the tier.** `/docs-loop` now stamps `baselineSha` as the oldest
  generation sha across both progress files, `/docs-refresh` regenerates a stale business page in that
  tier's register without introducing markers, and `/docs-feedback` answers a business-page comment in
  plain language while its PR still carries the code evidence.

## [0.1.1] - 2026-08-12

The first release met its first consumer repo and half the CI it scaffolds did not run. Both bugs are in
`templates/workflows/`, the one part of DocuMe its own CI cannot exercise: those files only ever execute in a
consumer's repository. Both are now covered by tests that read the shipped templates.

### Fixed

- **Three templates were rejected by GitHub outright.** `docs-drift-pr`, `docs-feedback` and `docs-refresh`
  each set a scratch path in a job-level `env:` using `${{ runner.temp }}`. That context is available from
  step level down and not in `jobs.<id>.env`, and GitHub's response is to refuse the whole file. The symptom
  names nothing useful: a 0-second run attributed to the `push` event — on workflows whose triggers are
  `pull_request` and `schedule` and do not include push — reporting only "This run likely failed because of a
  workflow file issue", with no annotation and no line number. Each now resolves its paths in a first step
  through `$GITHUB_ENV`, so they stay defined once and stay out of the workspace.
- **No template granted `packages: read`, so no docs job could restore the CLI.** All six declare an explicit
  `permissions:` block, and an explicit block denies every scope it does not name. `dotnet tool restore`
  therefore reached GitHub Packages with no package access at all and died on `Unhandled exception: Response
  status code does not indicate success: 403 (Forbidden)`, which names neither the feed, nor the scope, nor
  DocuMe.
- **The advice for that 403 pointed at the wrong remedy.** The templates, the composite action and the README
  all said `GITHUB_TOKEN` "is enough from a repository in the same organisation as the package", offering a
  PAT for the cross-organisation case. A GitHub Packages package is scoped to the repository that *published*
  it, so no other repository's `GITHUB_TOKEN` can read it, same organisation or not. The remedy is a grant on
  the package — Packages → DocuMe.Cli → Manage Actions access — with the PAT as an alternative rather than
  the cure. Corrected wherever it was stated, `docs/wiki/30-automation/` included.
- **`DogfoodWikiTests` reded on any machine that had run the Playwright MCP server.** Its repo walk reads the
  filesystem, so the `.playwright-mcp/` directory that server leaves behind arrived as an undeclared
  top-level directory and failed `Every_top_level_directory_is_declared_shipped_or_not` — while CI, where it
  does not exist, stayed green. Added to the walk's skip list, and to `.gitignore`, which is worth doing and
  is not what fixes it.

### Added

- `WorkflowTemplateTests.No_template_reads_the_runner_context` and
  `Every_template_grants_the_packages_scope_its_restore_needs`: the two assertions that would have caught the
  above. `Every_template_is_a_yaml_workflow` could not, and that is not a gap in it — both files were valid
  YAML and invalid GitHub.

## [0.1.0] - 2026-08-12

First release. Everything below is new, so it is grouped by what it does rather than by added/changed.

### The CLI

- `docume init` scaffolds a consumer repo in thirteen targets: `docume.json`, a wiki skeleton with a root
  page and `_meta/STYLE.md`, an empty `_meta/state.json`, the six `docs-*` workflows, the
  `.config/dotnet-tools.json` version pin, the mermaid render script, and one `.gitignore` entry. Idempotent:
  files DocuMe owns are never overwritten, and the two it shares with you are merged.
- `docume init --adopt` builds the state file from a wiki the repo already has, seeding page ids from
  frontmatter or from a legacy `<path>: <id>` map, and refuses rather than guessing when it cannot.
- `docume convert <wiki-root>` converts every page and reports failures and degradations without publishing.
- `docume publish` runs the pipeline: link map, mermaid attachments, storage-format conversion, content
  hashing, upsert, approval banner, open-comment guard, approval invalidation, orphan report, and a
  child-order pass that makes Confluence match the source tree. With `--dry-run`, `--tree`,
  `--changed-since`, `--page`, `--force`, `--no-reorder`, `--block-on-open-comments`,
  `--no-comment-check`, `--notify-reviewers` (a footer comment asking for a re-review on every page whose
  approval the run revoked), `--prune` and `--allow-protected-space` — the one way into a space listed in
  `confluence.protectedSpaces`, good for a single run, with no config value that grants it standing.
- `docume sync` reconciles the `approved` and `stale` labels into state, ingests page and inline comments
  into the feedback inbox behind a per-page cursor, and with `--reply` posts answers and resolves the inline
  comments it answered.
- `docume drift` matches files changed between two revisions against every page's `sources` globs, in table,
  JSON or PR-comment form. `--mark` adds the `stale` label and updates state; it never edits a page body.
- `docume dashboard` regenerates the Documentation Status page from state plus the live labels.
- `docume status` reports what is published, what drifted, and whether the repo can publish at all, as a
  terminal table or as `--json` for a PR body.

### The converter

- A custom Markdig renderer that emits Confluence storage format directly, with no HTML step. Headings,
  paragraphs, inline formatting, lists (tight and loose), blockquotes, thematic breaks, GFM tables, fenced
  code blocks with language mapping and `title`/`collapse`/`linenumbers`/`firstline` attributes, GitHub alert
  panels, task lists, external and internal links, and mermaid diagrams rendered to SVG attachments.
- **Fail-loud by contract.** A construct the renderer cannot represent throws rather than being silently
  dropped or downgraded, so a page never publishes having quietly lost a section.
- A golden corpus of hand-reviewed `.md` → `.storage.xml` pairs is the converter's contract, with a coverage
  test mapping every documented construct to the case that pins it in both directions.

### Approval, feedback and drift

- Approval is a label a human adds in Confluence. Republishing a page whose content hash changed removes it
  and marks the page `needs-review`; the injected banner is excluded from the hash, so machine edits never
  cost an approval. Approval history is preserved rather than overwritten.
- `approvedBy` reads `unknown` by design: Confluence Cloud exposes no label author anywhere the tool can
  reach, and naming the account DocuMe authenticates as would put a fabricated approver in an audit trail.
- Reviewer comments travel back to the repo as inbox items, and every reply is stamped so a re-run cannot
  answer the same comment twice.

### The Claude Code plugin

- `/docs-loop` writes the pages. One unit per run, read from the code, with a citation behind every claim; it
  keeps an inventory in `_meta/PROGRESS.md`, sends what the code cannot settle to `_meta/GAPS.md`, and opens
  `docs/loop-<date>`. The first run builds the inventory and writes no page, so the list can be corrected
  before pages are generated against it.
- `/docs-refresh` rewrites the pages whose declared sources moved and opens `docs/refresh-<date>`.
- `/docs-feedback` verifies a reviewer's comment against the code and opens `docs/feedback-<date>`, or
  declines it with a citation.
- All three treat Confluence page bodies and comments as untrusted input: claims to verify against the code,
  never instructions to follow. None calls the Confluence API, and none publishes: the output is a pull
  request.
- The repository is its own plugin marketplace, so the plugin installs without waiting on the Moberg one.

### Packaging

- Eight workflow templates shipped by `init`: publish on merge, sync on a schedule, drift on a PR, drift after
  a deploy, nightly refresh, and feedback — the refresh and feedback jobs each in a claude and a copilot
  spelling, of which a repo receives the pair on its rail. The publish job closes the feedback loop: after republishing it
  runs `sync --reply`, then carries the state file and the `repliedAt`-stamped items into the `docs/sync` PR
  together, so a reviewer is answered once and only once.
- Every scaffolded workflow adds the GitHub Packages feed before restoring the pinned CLI. `DocuMe.Cli` is
  published there and not to nuget.org, and GitHub Packages authenticates every read including a public
  package, so a restore with no feed configured resolves the pin against a feed that has never held it.
  The templates read a `DOCUME_PACKAGES_TOKEN` secret and fall back to the job's own `GITHUB_TOKEN`, which
  is enough within the organisation that owns the package.
- Every `docume` step in the publish job holds its exit code and one final step turns a failure into a red
  check, after the state has been carried. A publish that fails half way still records the page ids it
  earned, so the next run continues instead of creating those pages a second time; a failed publish also
  skips the reply pass, because a reply must not claim a fix is live on a page the run never reached.
- A release workflow that fires on a `vX.Y.Z` tag: verify, restore, build, test, pack, push to GitHub
  Packages, cut the release. The verify step refuses the release unless the tag matches all three files
  carrying the version, and it runs first because a package on a feed cannot be unpublished.
- A composite action, `moberghr/docu-me/actions@v0`, for a consumer writing a docs job of their own: it
  installs the SDK, adds the feed, restores the pinned CLI, provisions the mermaid renderer when the run
  is a publish that needs one, and invokes `docume` with the arguments given. It names no DocuMe version
  itself — `.config/dotnet-tools.json` decides that — which is why it can float on a major ref. The
  release workflow's last step moves that ref, so `@v0` tracks the newest 0.x and `@v1` goes live at
  1.0.0; `@v0.1.0` is there for a consumer who wants no float at all. The six shipped templates call
  `dotnet tool restore` and `dotnet tool run docume` directly and do not go through it.
- Central Package Management, a pinned SDK, the four Moberg house analyzer packs as errors, and xUnit v3 on
  the Microsoft Testing Platform.

### Not in this release

- The entry in the Moberg plugin marketplace. §11 distributes the plugin from there eventually, but that
  marketplace lives in another repository and the entry has to be added by hand; until it is, this
  repository is its own marketplace and `/plugin marketplace add moberghr/docu-me` is the install.
- Validation against a large real wiki. The converter's contract is the hand-reviewed golden corpus,
  which proves it does not regress but cannot discover a markdown dialect nobody predicted. Running it
  over an existing wiki of some size, and finding out how much of it needs a shim, is deliberately still
  ahead: a construct the renderer does not recognise fails its page by name rather than mis-converting
  it quietly, so that discovery is late by design rather than dangerous.
