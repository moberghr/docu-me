# Changelog

All notable changes to DocuMe are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

One version covers everything: the `DocuMe.Cli` and `DocuMe.Core` packages and the Claude Code plugin all
ship off a single `vX.Y.Z` tag, so a heading here describes all three. The version at the top of this file
is the one `Directory.Build.props` declares, whether or not its tag has been pushed yet.

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

- Six workflow templates shipped by `init`: publish on merge, sync on a schedule, drift on a PR, drift after
  a deploy, nightly refresh, and feedback. The publish job closes the feedback loop: after republishing it
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
