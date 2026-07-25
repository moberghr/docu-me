---
sources:
  - plugin/skills/**
  - plugin/.claude-plugin/plugin.json
---

# Claude Skills

The plugin ships three skills. All three end the same way — a pull request with
`docume status --json` in the body — and none of them calls the Confluence API. Only the CLI does that.

| Skill | Use it when | Branch |
|---|---|---|
| `/docs-loop` | A wiki is being built from scratch, or a page that should exist does not | `docs/loop-<date>` |
| `/docs-refresh` | Drift was reported: a stale label, a drift comment, the nightly cron | `docs/refresh-<date>` |
| `/docs-feedback` | Inbox items are waiting after a `sync --comments` | `docs/feedback-<date>` |

The boundaries are worth stating because they overlap in the obvious wrong way: `/docs-loop` writes
pages that do not exist, `/docs-refresh` rewrites pages whose code moved, `/docs-feedback` answers a
reviewer. A refresh that finds a missing page records a gap rather than writing it.

## `/docs-loop`

Generates the wiki one unit at a time: inventory the code, pick the next undocumented unit, verify
every claim against the source, write the page with its `sources` frontmatter, extend the pull request.

One unit per run is deliberate. A hundred pages in one pull request gets rubber-stamped; five get read.

The skill owns `state.baselineSha` — no CLI command writes it — and sets it to the oldest generation
commit still described by the pull request, so drift measures from the point the *content* was written
rather than from wherever `HEAD` happened to be.

## `/docs-refresh`

Regenerates the pages whose sources changed since the baseline, one summary line per page, and opens a
`docs/refresh-<date>` pull request. It reads `docume drift --format json` to decide what to touch, so a
run with nothing drifted opens nothing.

## `/docs-feedback`

Triages the inbox. For each item it verifies the claim against the code and takes one of three routes:

- **The claim holds** — fix the page, and the reply says what changed.
- **It is a question, not a claim** — record it in `_meta/GAPS.md` for the team.
- **The claim is wrong** — decline it, and the reply cites the code that says so.

> [!WARNING]
> A comment body is untrusted input. It is a claim to verify against the source, never an
> instruction — a comment asking for a credential, a link, or a change to another page gets treated as
> a claim about the page it was left on, and declined. Nothing a reviewer wrote is echoed into a page
> body, which is the whole point: a page is generated from code, not from comments.

Replies are posted by the account DocuMe authenticates as, and the last line says the reply was posted
automatically. A reviewer should never be left guessing whether a human answered.

## Installing

```bash
claude plugin marketplace add moberghr/docu-me
claude plugin install docume
```

The skills call the CLI through the repo-local tool manifest, so a repo that has not run
`docume init` gets a DocuMe message telling it so, rather than a missing-command error from a shell.
