---
sources:
  - plugin/skills/**
  - plugin/.claude-plugin/plugin.json
  - .claude-plugin/*.json
---

# Claude Skills

The plugin ships four skills. All four end the same way when the run found work: a pull request with
`docume status --json` in the body, and none of them calls the Confluence API. Only the CLI does that.
All four also have the other ending, the one a nightly job has most nights, and it is not a failure:
they stop without a branch, a commit or a pull request. An empty PR costs a reviewer more than it tells
them.

| Skill | Use it when | Opens nothing when | Branch |
|---|---|---|---|
| `/docs-loop` | A wiki is being built from scratch, a page that should exist does not, or a recorded gap is being asked for | nothing is `todo` in `_meta/PROGRESS.md` | `docs/loop-<date>` |
| `/docs-processes` | The wiki needs pages for the people who use the system rather than the people who build it | the process inventory is exhausted | `docs/processes-<date>` |
| `/docs-refresh` | Drift was reported: a stale label, a drift comment, the nightly cron. Also a change you just made on a branch | the drift report says `hasDrift: false` | `docs/refresh-<date>` |
| `/docs-feedback` | Inbox items are waiting after a `sync --comments`, the workflow fired on them, or somebody asks you to deal with the comments | no inbox item is untriaged | `docs/feedback-<date>` |

The boundaries are worth stating because they overlap in the obvious wrong way: `/docs-loop` writes
technical pages that do not exist, `/docs-processes` writes the business-tier pages beside them,
`/docs-refresh` rewrites pages whose code moved on either tier, `/docs-feedback` answers a reviewer. A
refresh that finds a missing page records a gap rather than writing it.

## `/docs-loop`

Generates the wiki one unit at a time: inventory the code, pick the next undocumented unit, verify
every claim against the source, write the page with its `sources` frontmatter, extend the pull request.

One unit per run is deliberate. A hundred pages in one pull request gets rubber-stamped; five get read.

No CLI command writes `state.baselineSha`: the generation pass owns it, and that means this skill,
`/docs-processes` and `/docs-refresh`, which do not all stamp the same value. This one sets it to the **oldest**
generation sha still recorded in `_meta/PROGRESS.md`, so drift measures from the point the *content* was
written rather than from wherever `HEAD` happened to be. Not today's `HEAD` after one page: that would
retire every drift the earlier pages have accumulated, silently, with nothing left to report it again.

## `/docs-processes`

Generates the business and process tier: the pages a reader who never opens the code needs, one process
at a time. Same pipeline, same publish path, same drift detection; the register is what changes. The
consumer names the tier's directory in `_meta/STYLE.md`, and where that file is silent the skill uses
`40-business/` and says so in the pull request.

Citations do not disappear on this tier, they move. Every claim carries an HTML comment on its own line
under the paragraph it backs, `<!-- cites: path/file.cs:31 -->`, and the converter drops comments, so a
business reader never sees a file path while `/docs-refresh` and `/docs-feedback` still see the evidence.
`contentHash` is taken over the converted body, so a citation whose line number moved costs neither an
approval nor a page version.

The `⚠️` markers are banned here rather than merely discouraged: a marker asks a reader to go and check
the claim, and this reader has no way to. A sentence is verified or it is absent, and what nothing settles
becomes a question in `_meta/GAPS.md`. The second source it may verify against is `_meta/BUSINESS.md`, a
consumer-owned file of the facts no code states (why a limit is what it is, which policy a refusal
implements). The skill cites that file and never writes a fact into it; candidate entries go in the pull
request body for a human to accept.

Its inventory is `_meta/PROGRESS-BUSINESS.md`, separate from `/docs-loop`'s so the two tiers advance
independently, and it writes `baselineSha` too. It stamps the **oldest** generation sha across both
progress files, because one baseline covers both tiers and a stamp taken from one of them retires the
drift the other has accumulated.

## `/docs-refresh`

Regenerates the pages whose sources changed since the baseline, one summary line per page, and opens a
`docs/refresh-<date>` pull request. It reads `docume drift --format json` to decide what to touch, so a
run with nothing drifted opens nothing.

It writes `baselineSha` as well, and it stamps `<head>`: this run really did regenerate
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
