---
sources:
  - templates/workflows/*.yml
  - actions/*.yml
---

# GitHub Workflows

[TOC]

Six templates, scaffolded by `docume init`. Each runs `dotnet tool restore` and then the CLI through
the repo-local tool manifest, so a workflow and a laptop run the same pinned version.

| Workflow | Fires on | Does |
|---|---|---|
| `docs-publish.yml` | push to the default branch, paths `docs/wiki/**` | `publish --changed-since` the last published sha, then refreshes the dashboard |
| `docs-drift-pr.yml` | `pull_request` | Comments on the pull request with the wiki pages its code changes affect |
| `docs-drift.yml` | `workflow_run` after your deploy workflow completes | `drift --mark`: labels the affected pages stale |
| `docs-sync.yml` | schedule, every six hours, plus manual dispatch | `sync` — reads labels and comments, opens a `docs/sync` pull request when state changed |
| `docs-refresh.yml` | schedule, nightly at 03:00, plus manual dispatch | Runs `/docs-refresh` when drift is reported, opening a `docs/refresh-<date>` pull request |
| `docs-feedback.yml` | push to the default branch, paths `docs/wiki/_meta/feedback/inbox/**` | Runs `/docs-feedback` when new inbox items land |

## Why the two drift workflows differ

`docs-drift-pr.yml` is advisory. It comments and exits 0, because blocking a code pull request on
documentation is how teams learn to bypass documentation.

`docs-drift.yml` is the one that marks. It fires after a *deploy*, when the code is real, and it writes
labels only — never a page body. Marking on every pull request would label pages stale for changes that
never shipped.

## Secrets

Every workflow needs the two credential variables, and the ones that run a skill need a model key as
well:

```yaml
env:
  DOCUME_CONFLUENCE_EMAIL: ${{ secrets.DOCUME_CONFLUENCE_EMAIL }}
  DOCUME_CONFLUENCE_TOKEN: ${{ secrets.DOCUME_CONFLUENCE_TOKEN }}
```

## Everything writes through a pull request

The two workflows that produce repo changes — `docs-sync.yml` and `docs-refresh.yml` — commit to a
branch and open a pull request. Neither pushes to the default branch, so a human reviews every docs
change before it publishes.

That is also why the publish workflow is the *only* one that writes page bodies: it runs on the default
branch, after the review.

> [!NOTE]
> A workflow that opened no pull request and one that failed look identical in a green run, so each
> template checks `git ls-remote` for its own branch family before and after, and annotates the run
> with which branch it pushed. A refresh run that pushed nothing says so out loud.

## Running the skills headlessly

The two model-driven workflows invoke Claude Code in headless mode:

```bash
claude -p "/docs-refresh" --permission-mode acceptEdits
```

The skill does its own verification against the code, writes the pages, and opens the pull request with
`gh`. `docume status --json` goes into the body, so the reviewer sees the state of the space next to
the diff.

## Writing a docs job of your own

Every template opens with the same three lines: install the SDK, `dotnet tool restore`, run `docume`
through the manifest. A composite action wraps them, so a job you write yourself does not have to get
them right a seventh time.

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
```

| Input | Required | Does |
|---|---|---|
| `args` | yes | Everything after `docume`, as one string |
| `dotnet-version` | no | SDK to install, `10.0.x` by default |

The action names no DocuMe version. It restores your `.config/dotnet-tools.json` rather than installing
anything, so the CLI it runs is the one your repo pinned and a DocuMe release cannot change the version
your CI runs. A repo with no manifest stops with an error naming `docume init`, because the SDK's own
wording for a missing manifest does not mention DocuMe.

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
