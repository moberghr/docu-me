---
sources:
  - templates/workflows/*.yml
---

# GitHub Workflows

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
