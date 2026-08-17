---
sources:
  - src/DocuMe.Cli/**
  - src/DocuMe.Core/**
  - README.md
---

# DocuMe

DocuMe keeps a Confluence wiki in step with the repository it documents. The markdown lives in the
repo, the CLI publishes it, and Confluence becomes a read surface with review on top: labels for
approval, comments for feedback, a dashboard for the state of the whole space.

It is a .NET tool (`docume`) plus a Claude Code plugin. The tool is the only thing that talks to
Confluence; the plugin's skills write markdown and open pull requests, and never touch the API.

## The one-way rule

**The repo is the source of truth.** A page edited by hand in Confluence is overwritten the next time
its markdown changes, and DocuMe never reads a page body back as content. Everything a reviewer adds
that is *not* body text — labels, comments — travels the other way and lands in the repo as state or
as an inbox item.

> [!NOTE]
> Two things follow from this that surprise people once: `publish --prune` deletes pages whose
> markdown is gone, and a space DocuMe writes into should hold nothing but DocuMe's output.

## What is in here

| Section | Read it for |
|---|---|
| [Concepts](10-concepts/README.md) | The lifecycle, and how approval and staleness are tracked |
| [Reference](20-reference/README.md) | Every command, every config field, the conversion contract |
| [Automation](30-automation/README.md) | The workflows and the Claude skills that drive it unattended |

## How a wiki grows, and why yours looks the way it does

Every generation skill reads `_meta/STYLE.md` before it writes a word, and writes to what that file
answers: who reads the wiki, how it should sound, how it is organised, how big it should be, whether
pages carry diagrams, whether a business tier exists, and how claims are verified. The consequence is
that the style guide is the lever for everything you might dislike about a generated wiki. Thin pages
and no diagrams mean the guide never asked for depth or diagrams, not that the generator cannot
produce them.

Generation is two skills, one per audience. `/docs-loop` writes the technical tier and `/docs-processes`
writes the business and process tier beside it, for readers who never open the code. Each keeps its own
inventory (`_meta/PROGRESS.md` and `_meta/PROGRESS-BUSINESS.md`), and each writes that inventory on its
first run instead of a page, so a human can reorder the list before pages are generated against it. A
wiki with no business tier is a wiki where `/docs-processes` has not run yet: nothing creates that tier
as a side effect. The skills are documented in [Claude Skills](30-automation/skills.md).

## Getting started

Installation and the first publish are in the repository `README.md`, not here: they change with the
release process, and duplicating them is how a wiki starts lying. Two steps in particular are left
there on purpose, because both have a caveat that would rot in a copy: DocuMe ships to GitHub Packages,
which authenticates every read, so the feed needs a token before the install line works at all.

Once `docume` is on the path, setting up a repository is one command, run from its root:

```bash
docume init --space MYSPACE --base-url https://example.atlassian.net/wiki
```

`init` is idempotent. It never overwrites a file that already exists, and reports what it skipped, so
running it again in a repo that already has a wiki is safe.

## Credentials

DocuMe reads exactly two environment variables and accepts credentials nowhere else — not in
`docume.json`, which is committed, and not on the command line, which lands in shell history:

```bash
export DOCUME_CONFLUENCE_EMAIL=you@example.com
export DOCUME_CONFLUENCE_TOKEN=<api-token-from-id.atlassian.com>
```

An expired token fails loud on the first request: 401 and 403 stop the run with a token-expiry
message instead of retrying, because retrying a rejected credential only wastes the rate limit.
Retry with backoff is for 429 and 5xx.
