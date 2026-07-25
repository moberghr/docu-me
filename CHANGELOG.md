# Changelog

All notable changes to DocuMe are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

One version covers everything: the `DocuMe.Cli` and `DocuMe.Core` packages and the Claude Code plugin all
ship off a single `vX.Y.Z` tag, so a heading here describes all three. The version at the top of this file
is the one `Directory.Build.props` declares, whether or not its tag has been pushed yet.

## [0.1.0] - unreleased

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
  child-order pass that makes Confluence match the source tree. With `--dry-run`, `--changed-since`,
  `--page`, `--force`, `--no-reorder`, `--block-on-open-comments` and `--prune`.
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
- A release workflow that fires on a `vX.Y.Z` tag: verify, restore, build, test, pack, push to GitHub
  Packages, cut the release. The verify step refuses the release unless the tag matches all three files
  carrying the version, and it runs first because a package on a feed cannot be unpublished.
- Central Package Management, a pinned SDK, the four Moberg house analyzer packs as errors, and xUnit v3 on
  the Microsoft Testing Platform.

### Not in this release

- The composite GitHub Action wrapping install-and-run. The shipped workflow templates call
  `dotnet tool restore` and `dotnet tool run docume` directly and need no action.
