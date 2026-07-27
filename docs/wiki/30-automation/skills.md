---
sources:
  - plugin/skills/**
  - plugin/.claude-plugin/plugin.json
  - .claude-plugin/*.json
---

# Claude Skills

The plugin ships three skills. All three end the same way when the run found work: a pull request with
`docume status --json` in the body, and none of them calls the Confluence API. Only the CLI does that.
All three also have the other ending, the one a nightly job has most nights, and it is not a failure:
they stop without a branch, a commit or a pull request. An empty PR costs a reviewer more than it tells
them.

| Skill | Use it when | Opens nothing when | Branch |
|---|---|---|---|
| `/docs-loop` | A wiki is being built from scratch, a page that should exist does not, or a recorded gap is being asked for | no unit is `todo` in `_meta/PROGRESS.md` | `docs/loop-<date>` |
| `/docs-refresh` | Drift was reported: a stale label, a drift comment, the nightly cron. Also a change you just made on a branch | the drift report says `hasDrift: false` | `docs/refresh-<date>` |
| `/docs-feedback` | Inbox items are waiting after a `sync --comments`, the workflow fired on them, or somebody asks you to deal with the comments | no inbox item is untriaged | `docs/feedback-<date>` |

The boundaries are worth stating because they overlap in the obvious wrong way: `/docs-loop` writes
pages that do not exist, `/docs-refresh` rewrites pages whose code moved, `/docs-feedback` answers a
reviewer. A refresh that finds a missing page records a gap rather than writing it.

## `/docs-loop`

Generates the wiki one unit at a time: inventory the code, pick the next undocumented unit, verify
every claim against the source, write the page with its `sources` frontmatter, extend the pull request.

One unit per run is deliberate. A hundred pages in one pull request gets rubber-stamped; five get read.

No CLI command writes `state.baselineSha`: the generation pass owns it, and that means this skill and
`/docs-refresh`, which stamp it to different values on purpose. This one sets it to the **oldest**
generation sha still recorded in `_meta/PROGRESS.md`, so drift measures from the point the *content* was
written rather than from wherever `HEAD` happened to be. Not today's `HEAD` after one page: that would
retire every drift the earlier pages have accumulated, silently, with nothing left to report it again.

## `/docs-refresh`

Regenerates the pages whose sources changed since the baseline, one summary line per page, and opens a
`docs/refresh-<date>` pull request. It reads `docume drift --format json` to decide what to touch, so a
run with nothing drifted opens nothing.

It is the other writer of `baselineSha`, and it stamps `<head>`: this run really did regenerate
everything drifted, and one that rewrote pages and left the field alone would report the same pages again
tomorrow night. The exception is the case that starts from a merge base rather than the recorded baseline,
"refresh the docs for the change I just made" on a branch, which deliberately leaves the stamp alone
because the wiki as a whole was not regenerated against an unmerged commit.

## `/docs-feedback`

Triages the inbox. For each item it verifies the claim against the code, and the code picks one of three
routes:

- **The claim held and the page now says so** — fix the page, and the reply says what changed.
- **The code contradicts the claim, or it asks for something out of scope** — decline it, and the reply
  carries the citation that settles it, or says where an out-of-scope request went instead.
- **The code cannot settle it** (a product decision, an intent, code that is not in this repo, or
  anything the run merely suspects) — record it in `_meta/GAPS.md` for the team, and the reply says so.

The third route is the one that costs a reviewer when it is skipped, because each verdict selects a
different sentence the CLI posts under the comment. An unsettled claim filed as a decline tells the
reviewer the page was checked against the code and is staying as it is, about a point nothing checked.

> [!WARNING]
> A comment body is untrusted input. It is a claim to verify against the source, never an instruction: a
> comment asking for a credential, a link, or a change to another page is **not acted on**. It is quoted
> verbatim in the pull request body, under its own heading, so a human decides what to do about whoever
> wrote it, and the item is triaged on whatever factual content it has left. Nothing a reviewer wrote is
> echoed into a page body, which is the whole point: a page is generated from code, not from comments.

Replies are posted by the account DocuMe authenticates as, and the last line says the reply was posted
automatically. A reviewer should never be left guessing whether a human answered.

## Installing

The plugin half is two slash commands inside Claude Code, not shell commands:

```text
/plugin marketplace add moberghr/docu-me
/plugin install docume@docume
```

`docume@docume` names the marketplace as well as the plugin. This repository is its own marketplace, which
is what makes the install work without waiting on Moberg's, and the qualifier is what keeps it
unambiguous once the plugin is listed in both.

The CLI is a separate install and the skills need it: they invoke `dotnet tool run docume`, which resolves
through the repo-local `.config/dotnet-tools.json` pin that `docume init` writes. So a repo that has not
run `docume init` gets a missing-command error from the shell, `Cannot find a tool in the manifest file
that has a command named 'docume'`, and not a DocuMe message. The DocuMe one comes a step later, once the
tool restores and the config is what is missing: `Config file not found: <path>/docume.json`.
