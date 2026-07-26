---
sources:
  - templates/workflows/*.yml
  - actions/*.yml
---

# GitHub Workflows

[TOC]

Six templates, scaffolded by `docume init`. Each adds the DocuMe package feed, runs
`dotnet tool restore` and then the CLI through the repo-local tool manifest, so a workflow and a laptop
run the same pinned version.

The feed step is not optional plumbing. `DocuMe.Cli` is published to GitHub Packages rather than
nuget.org, and GitHub Packages authenticates every read including a public package, so a restore with no
feed configured resolves the pin against nuget.org alone and fails. Each template reads
`DOCUME_PACKAGES_TOKEN` and falls back to the workflow's own `GITHUB_TOKEN`, which is enough from a
repository in the same organisation as the package; from another org, add that secret holding a personal
access token with `read:packages`.

| Workflow | Fires on | Does |
|---|---|---|
| `docs-publish.yml` | push to the default branch, paths `docs/wiki/**` | `publish --changed-since` the last published sha, then refreshes the dashboard |
| `docs-drift-pr.yml` | `pull_request` | Comments on the pull request with the wiki pages its code changes affect |
| `docs-drift.yml` | `workflow_run` after your deploy workflow completes | `drift --mark`: labels the affected pages stale |
| `docs-sync.yml` | schedule, every six hours, plus manual dispatch | `sync` — reads labels and comments, opens a `docs/sync` pull request when state changed |
| `docs-refresh.yml` | schedule, nightly at 03:00, plus manual dispatch | Runs `/docs-refresh` when drift is reported, opening a `docs/refresh-<date>` pull request |
| `docs-feedback.yml` | push to the default branch, paths `docs/wiki/_meta/feedback/inbox/**`, plus manual dispatch | Runs `/docs-feedback` when new inbox items land |

## Why the two drift workflows differ

`docs-drift-pr.yml` is advisory. It comments and exits 0, because blocking a code pull request on
documentation is how teams learn to bypass documentation.

`docs-drift.yml` is the one that marks. It fires after a *deploy*, when the code is real, and it writes
labels only — never a page body. Marking on every pull request would label pages stale for changes that
never shipped.

## Secrets

Three of the six carry the credential variables, and they are the three that talk to Confluence:
`docs-publish.yml`, `docs-drift.yml`, `docs-sync.yml`.

```yaml
env:
  DOCUME_CONFLUENCE_EMAIL: ${{ secrets.DOCUME_CONFLUENCE_EMAIL }}
  DOCUME_CONFLUENCE_TOKEN: ${{ secrets.DOCUME_CONFLUENCE_TOKEN }}
```

The other three hold neither on purpose, and handing them the token would undo the reason they are
separate files. `docs-drift-pr.yml` runs on every contributor's branch, and its report is a `git diff`
plus a glob match, so the workflow with the widest trigger never sees the publishing token.
`docs-refresh.yml` and `docs-feedback.yml` are the unsupervised model runs, and a skill's output is a
pull request: only the CLI writes to Confluence, so the token stays out of the job a model drives. Those
two want `ANTHROPIC_API_KEY` instead, which no other template needs.

## Everything writes through a pull request

Four of the six change the repo, and none of them pushes to the default branch. `docs-publish.yml` and
`docs-sync.yml` commit the machine-owned state file to the shared `docs/sync` branch, opening that pull
request when it is not already open; `docs-refresh.yml` and `docs-feedback.yml` push a dated branch of
their own. So a human reviews every docs change before it publishes.

That is also why the publish workflow is the *only* one that writes page bodies: it runs on the default
branch, after the review.

> [!NOTE]
> A workflow that opened no pull request and one that failed look identical in a green run, and that is
> a live risk in exactly the two a model drives, because the skill is what decides whether to push at
> all. So `docs-refresh.yml` and `docs-feedback.yml` check `git ls-remote` for their own branch family
> before and after, and annotate the run with which branch they pushed: a refresh run that pushed
> nothing says so out loud. The other two need no such check — their branch is the fixed `docs/sync`,
> and `gh pr view` deciding whether to `gh pr create` cannot silently open nothing.

## Running the skills headlessly

The two model-driven workflows invoke Claude Code in headless mode:

```bash
claude -p "/docs-refresh" --permission-mode acceptEdits
```

The skill does its own verification against the code, writes the pages, and opens the pull request with
`gh`. `docume status --json` goes into the body, so the reviewer sees the state of the space next to
the diff.

## Writing a docs job of your own

Every template opens with the same three lines — install the SDK, add the package feed,
`dotnet tool restore` — and then runs `docume` through the manifest, which five do directly and
`docs-feedback.yml` does from inside the skill it launches. A composite action wraps all four, so a job
you write yourself does not have to get them right a seventh time.

```yaml
jobs:
  publish:
    runs-on: ubuntu-latest
    env:
      DOCUME_CONFLUENCE_EMAIL: ${{ secrets.DOCUME_CONFLUENCE_EMAIL }}
      DOCUME_CONFLUENCE_TOKEN: ${{ secrets.DOCUME_CONFLUENCE_TOKEN }}
    steps:
      - uses: actions/checkout@v4
      - uses: moberghr/docu-me/actions@v0
        with:
          args: publish --dry-run
          packages-token: ${{ secrets.GITHUB_TOKEN }}
```

| Input | Required | Does |
|---|---|---|
| `args` | yes | Everything after `docume`, as one string |
| `packages-token` | no | Token for the GitHub Packages feed, empty by default |
| `dotnet-version` | no | SDK to install, `10.0.x` by default |
| `mermaid` | no | Diagram renderer, `auto` by default |

The action names no DocuMe version. It restores your `.config/dotnet-tools.json` rather than installing
anything, so the CLI it runs is the one your repo pinned and a DocuMe release cannot change the version
your CI runs. A repo with no manifest stops with an error naming `docume init`, because the SDK's own
wording for a missing manifest does not mention DocuMe.

`packages-token` is optional in the schema and needed in practice. A composite action cannot read
`secrets` for itself, so the feed the CLI lives on has to be handed to it; leave it empty only when your
repo names that feed in a committed `NuGet.config`. Get it wrong either way and the restore stops with an
error naming the token, rather than with NuGet's own wording about a package it could not find on
nuget.org.

Two commands render diagrams, and both shell out to Node to do it: `publish` always, and `convert` when
you pass `--render-mermaid`. Neither Node nor `beautiful-mermaid` is on a runner by default, so
`mermaid: auto` installs both when `args` invokes either of those and skips them otherwise — a `drift` or
a `sync` pays for no toolchain, and neither does a bare `convert`, because conversion on its own never
renders. Set it to `false` if your wiki has no ` ```mermaid ` fences and you would rather not pay for the
install, or `true` to install regardless; a value the action does not recognise stops the job rather than
quietly leaving the renderer out.

> [!NOTE]
> `args` is split on whitespace into the argument list, which is what turns one input string into a
> command line. No single argument can carry a space of its own, so a run that needs one belongs in a
> plain `run:` step calling `dotnet tool run docume` directly.

The ref is a floating major tag, force-moved by the last step of each release: `@v0` tracks the newest
0.x, `@v1` takes over at 1.0.0, and a prerelease moves neither. Pin an exact release instead, as
`actions@v0.1.0`, if you would rather nothing moved under you.

None of the six templates uses the action. Five call the CLI directly and `docs-feedback.yml` reaches it
through the skill it runs, which keeps every scaffolded file readable without knowing what the action
does.
