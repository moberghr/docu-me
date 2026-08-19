---
sources:
  - src/DocuMe.Cli/Commands/*.cs
  - src/DocuMe.Cli/Program.cs
  - src/DocuMe.Core/Acceptance/*.cs
  - src/DocuMe.Core/Dashboard/*.cs
  - src/DocuMe.Core/Drift/*.cs
  - src/DocuMe.Core/Feedback/*.cs
  - src/DocuMe.Core/Git/GitRepository.cs
  - src/DocuMe.Core/Publishing/*.cs
  - src/DocuMe.Core/Scaffolding/*.cs
  - src/DocuMe.Core/State/DocumeState.cs
  - src/DocuMe.Core/Status/*.cs
  - src/DocuMe.Core/Sync/*.cs
---

# CLI Reference

[TOC]

Seven commands. Three of them can write to Confluence — `publish`, `dashboard`, and `sync --reply` —
and `drift --mark` writes labels. Everything else is read-only.

The root command carries nothing but `--help` and `--version`; every option below belongs to one
command. A bare invocation prints the command list rather than exiting silently:

```bash
dotnet tool run docume                    # the whole command list
dotnet tool run docume -- drift --help    # one command's options
```

> [!NOTE]
> `dotnet tool run` keeps `-?`, `-h` and `--help` for itself, so `dotnet tool run docume --help`
> prints the SDK's help for `dotnet tool run` and never reaches DocuMe. Put `--` ahead of the
> arguments to forward them through. `--version` is not one of the wrapper's options and needs no
> `--`: it reports the package version and the commit it was built from.

## Which commands write

| Command | Writes to Confluence | Writes to the repo |
|---|---|---|
| `init` | no | scaffolded files |
| `convert` | no | nothing |
| `publish` | pages, attachments, labels | `_meta/state.json` |
| `sync` | only with `--reply` | `_meta/state.json`, feedback inbox |
| `drift` | only with `--mark` | `_meta/state.json` with `--mark` |
| `dashboard` | the dashboard page | nothing |
| `status` | no | nothing |

Every write path in that table is gated on `confluence.protectedSpaces`: name a space there and
`publish`, `dashboard`, `drift --mark` and `sync --reply` all refuse it. The first three carry
`--allow-protected-space` to lift the refusal for one run without editing the list. `sync --reply`
deliberately does not: a reply is a comment posted into a space this repo was told to stay out of, so
there is no per-run override, and its read halves report the lock instead of refusing.

Every command that reads `docume.json` takes `--config` (default `docume.json`), and its directory is
the repo root that `wiki.root` and every `sources` glob resolve against. Every command that reads
state takes `--state`, defaulting to `<wiki.root>/_meta/state.json`.

## `docume init`

Scaffolds a consumer repo: `docume.json`, the `docs/wiki/` skeleton with `_meta/STYLE.md`, the mermaid
renderer, the workflow templates, a tool manifest, `.gitignore` entries.

The scaffolded `_meta/STYLE.md` is a questionnaire, not a style: seven bullets (audience, tone,
structure, scope, diagrams, business, verification) that every generation skill reads at the start of a
run. Answer them before the first run, because they decide the wiki you get. An unanswered guide leaves
the skill inferring your taxonomy from the code for that one run and saying so in the pull request.

| Option | Effect |
|---|---|
| `--space` | Confluence space key written into `docume.json` |
| `--base-url` | Confluence wiki base URL written into `docume.json` |
| `--adopt` | Build `_meta/state.json` from the wiki this repo already has, one entry per page, `pageId`s seeded from frontmatter |
| `--legacy-map` | Path to a JSON `page path → page id` map from whatever published the wiki before. Requires `--adopt` |
| `--agent` | Which agent the two model-running workflows are written for: `claude` or `copilot`. Recorded in `docume.json`; omitted, a recorded value wins and a fresh repo gets `claude` |

Idempotent: an existing file is never overwritten, and every skip is reported.

## `docume convert <wiki-root>`

Converts every page and reports what happened, grouped by construct and by dialect. No credentials, no
network, nothing written — not even the storage format it renders. Exit code 0 means the corpus clears
the acceptance bar.

| Option | Effect |
|---|---|
| `--accept <code>` | Treat a diagnostic code as an accepted loss: still reported, counted as a note instead of a warning. Repeatable |
| `--render-mermaid` | Also render every diagram through Node and report the ones that fail. Off by default: one process per diagram |
| `--renderer <path>` | Path to `render-mermaid.mjs`. Defaults to `tools/render-mermaid.mjs` |

This is the right pre-flight for CI on a pull request that touches the wiki: it catches an unclosed
construct or an unrenderable diagram before a publish run does.

## `docume publish`

Converts the wiki and publishes it. The interesting options are the ones that narrow the run or
change what it is allowed to do.

| Option | Effect |
|---|---|
| `--dry-run` | Plan and print, write nothing |
| `--tree` | Also print the page tree the run would build |
| `--force` | Republish every page even when nothing changed, re-uploading attachments |
| `--changed-since <sha>` | Write only the pages touched since a commit, including pages whose images changed. The whole tree is still loaded, converted and checked for orphans |
| `--page <path>` | Write only these pages. Repeatable; a path not in the tree is an error |
| `--prune` | Delete orphan pages after publishing. Asks first, needs a terminal, refuses to run in CI |
| `--no-reorder` | Skip the pass that puts each parent's children in source-tree order |
| `--block-on-open-comments` | Leave a page alone and exit non-zero when it has unresolved inline comments a body rewrite could strand. Default is to publish and warn |
| `--no-comment-check` | Skip that read entirely |
| `--notify-reviewers` | Post a footer comment asking for a re-review on every page whose `approved` label the run removes. One comment per revoked approval, so it notifies each page's watchers |
| `--allow-protected-space` | Write into a space listed in `confluence.protectedSpaces`, for one run |

> [!WARNING]
> `--prune` is the only destructive option. An orphan is a state entry whose markdown file is gone,
> which is also what a renamed file looks like before its new path is published, so prune after a
> successful full publish rather than alongside a partial one.

Every page the tool creates is stamped with a `docume` content property saying the page is
DocuMe-managed and which file owns it, and a body update stamps a page that predates the marker.
`--prune` reads that property live before each delete and refuses any orphan whose page is not
stamped: the page is reported, the run still exits 0, and nothing is deleted. Republish the page to
stamp it, or delete it by hand. The refusal exists because an id can reach `state.json` without
DocuMe having created the page (`init --adopt` seeds ids, and the file is hand-editable), and the
stamp is the proof the delete waits for.

## `docume sync`

Reads the labels and comments out of Confluence and reconciles them into the repo. Passing neither
half runs both read halves.

| Option | Effect |
|---|---|
| `--labels` | Reconcile the `approved`/`stale` labels into state |
| `--comments` | Ingest page comments into the feedback inbox |
| `--reply` | Post a reply under every triaged inbox item and resolve the inline comments it answers. The only half that writes to Confluence |
| `--output-dir` | Where to write inbox items. Defaults to `<wiki.root>/_meta/feedback/inbox` |
| `--dry-run` | Report what would change and write none of it |

Committing the result is the caller's job. In CI that means a `docs/sync` pull request, so a human sees
state changes before they land.

## `docume drift`

Reports which pages derive from code changed between two revisions.

| Option | Effect |
|---|---|
| `--baseline <rev>` | Revision to diff from. Defaults to `state.baselineSha` |
| `--head <rev>` | Revision to diff to. Defaults to `HEAD` |
| `--format <shape>` | `table`, `json`, or `github-comment`. `json` prints nothing else |
| `--fail-on-drift` | Exit 1 when any page is affected. Without it the command is advisory and exits 0 |
| `--mark` | Add the `stale` label to affected pages, set `stale: true` in state, refresh the dashboard |
| `--dry-run` | With `--mark`: report what would be labelled, write nothing. Needs no credentials |
| `--allow-protected-space` | With `--mark`: label pages in a space listed in `confluence.protectedSpaces`, for one run |

Drift can be told that a class of change never means the docs moved. An optional
`<wiki.root>/_meta/drift-ignore` holds one glob per line, matched against changed file paths the same
way `sources` globs are; a line whose first character is `#` is a comment (an indented `#` is an
error, not a comment), and a pattern may carry a reason after one (`src/gen/**/*.cs # generated`),
which the report quotes back. A changed file that any pattern matches never marks a page stale: every
format discloses it instead — the table's `EXEMPT` section, the JSON's `exempted` list, the PR
comment's exemption note — and neither the exit code nor `--mark` counts it. Mind the pattern's
width: an over-broad glob switches drift off for everything it covers, and the disclosure line is
the only trace. A malformed line fails the run with its line number, because an exemption list that
is silently half-read would silently un-report drift.

Some sweeps touch the very files the docs describe, where a path glob would switch drift off for
those files forever. An optional `<wiki.root>/_meta/drift-ignore-revs` exempts by commit instead:
one full 40-character sha per line, comments behind `#` (leading or trailing), case-insensitive —
git's `blame.ignoreRevsFile` format as git itself reads it, so one file can serve both tools. When
a listed commit is actually in the compared range the diff is attributed per commit, so a file
counts as changed only when a commit that is not listed touched it; a file touched by both a
listed and an unlisted commit still drifts, and a merge commit carries no file list of its own
under this attribution. A file that names nothing in the range changes nothing: the run answers
from the ordinary diff, exactly as if the file were absent. The two lists compose in order: commit
exemptions narrow the diff first, then the path globs above apply to what is left. Every format
discloses the held-out commits (the table's `IGNORED COMMITS` line, the JSON's
`ignoredCommitCount`, the PR comment's provenance), and a malformed line fails the run naming its
line, for the reason a malformed glob does.

## `docume dashboard`

Regenerates the status page from state plus the live labels.

| Option | Effect |
|---|---|
| `--title` | Title of the dashboard page. Defaults to `dashboard.title` |
| `--dry-run` | Print the storage format it would publish and write nothing |
| `--allow-protected-space` | Publish the dashboard into a space listed in `confluence.protectedSpaces`, for one run |

The labels are reconciled in memory here; `docume sync --labels` is what writes them into state.

## `docume status`

Reports what is published, what drifted, and whether this repo is set up to publish at all.

| Option | Effect |
|---|---|
| `--json` | Print the report as JSON and nothing else, for a pull-request body or a CI step |
| `--offline` | Skip the single Confluence request — the token and space probe |
| `--fail-on-drift` | Exit non-zero when the published wiki differs from the repo |

`docume status --json` is what every skill appends to its pull-request body, so a reviewer sees the
state of the space next to the diff.
