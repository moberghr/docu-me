---
sources:
  - src/DocuMe.Core/Config/*.cs
  - src/DocuMe.Core/State/*.cs
  - src/DocuMe.Core/Scaffolding/*.cs
  - src/DocuMe.Core/Drift/DriftPlanner.cs
  - schema/*.json
---

# Configuration and State

Three files. One is written by hand and committed, one is written per page by an author, one is written
by the tool and committed anyway.

| File | Owner | Committed |
|---|---|---|
| `docume.json` | humans | yes |
| page frontmatter | whoever writes the page | yes |
| `_meta/state.json` | the tool | yes |

State is committed on purpose. It carries page ids, content hashes and approvals, so publishing from a
different machine or a CI runner has to produce the same plan as publishing from a laptop.

## `docume.json`

Lives at the repo root. Committed, and therefore contains **no secrets** — credentials come from the
environment and from nowhere else.

```json
{
  "$schema": "https://raw.githubusercontent.com/moberghr/docu-me/main/schema/docume.schema.json",
  "agent": "claude",
  "confluence": {
    "baseUrl": "https://example.atlassian.net/wiki",
    "spaceKey": "DOCS",
    "spaceId": "2431647748",
    "rootPageId": "4512153602",
    "protectedSpaces": ["PROD"]
  },
  "wiki": {
    "root": "docs/wiki",
    "exclude": ["_meta/**"],
    "extraPages": [
      { "path": "_meta/GAPS.md", "title": "Open Questions for the Team" }
    ],
    "homePage": "README.md"
  },
  "labels": { "approved": "approved", "stale": "stale" },
  "dashboard": { "title": "Documentation Status" },
  "drift": { "defaultBranch": "dev" },
  "links": { "repoBlobUrl": null },
  "mermaid": { "renderer": "tools/render-mermaid.mjs" }
}
```

Three fields are required and validated on load: `confluence.baseUrl`, `confluence.spaceKey` and
`wiki.root`. `docume init` writes a working file with all three set.

**The block above is an example, not a list of defaults.** Four of its values are illustrations, and
the real default for each is empty:

| Field | Actual default | What the example shows instead |
|---|---|---|
| `confluence.spaceId` | absent — resolved from `spaceKey` on first use | a numeric id |
| `confluence.rootPageId` | absent — publish under the space root | a numeric id |
| `confluence.protectedSpaces` | `[]` — **no space is write-locked** | one locked space |
| `wiki.extraPages` | `[]` | one re-included file |

Every other field does default to the value shown.

**`agent`** is read by `docume init` and by nothing else: it records which agent the two model-running
workflows were scaffolded for (`claude` or `copilot`), so a re-run without `--agent` keeps the repo's
choice. Absent also means `claude`, which is the rail every repo scaffolded before the copilot rail
existed is on.

Two are worth a second look:

- **`wiki.homePage`** names the index file of *every* directory, not only the root. `a/b/page.md` hangs
  under `a/b/README.md`, which hangs under `a/README.md`. A directory with no index page is skipped
  rather than synthesized: its children hang from the nearest index above it, because inventing a page
  no author wrote would break the one-way rule.
- **`confluence.protectedSpaces`** is a write lock, and it starts empty: a fresh `docume init` locks
  nothing. A space listed here is refused, and the only way past it is `--allow-protected-space` for a
  single run. There is no config value that grants it permanently, so removing the lock is a reviewed
  commit rather than a flag somebody stops reading.

### Two fields are inert in this version

`drift.defaultBranch` and `links.repoBlobUrl` load, validate and round-trip like every other field, and no
command reads either one. Setting them changes nothing:

- **`drift.defaultBranch`** — `docume drift` with no `--baseline` diffs from `state.json`'s `baselineSha`,
  and the PR job in `docs-drift-pr.yml` computes a merge base from the pull request itself. Neither asks
  this value.
- **`links.repoBlobUrl`** — source refs publish as plain text. Nothing turns them into blob links.

They are documented rather than removed because both are declared in the build plan and one has a
specified behaviour, so building them and dropping them are both open. The section is held to the truth
from the other side too: the suite fails if either field acquires a reader while this list still names it.

### `$schema` is the only check on a misspelled key

`docume init` writes the `$schema` line, and it earns its place the moment you hand-edit the file. The
loader binds the keys it recognizes and drops the rest without a word: write `protectedSpace` for
`protectedSpaces` and the write lock is simply off, on every run, with nothing printed. The schema sets
`additionalProperties: false` on every object, so a schema-aware editor flags the typo as you type it.
That is the only place a misspelled key is ever reported.

## Page frontmatter

```yaml
---
sources:
  - src/DocuMe.Core/Publishing/*.cs
  - src/DocuMe.Core/State/StateStore.cs
title: Publish Pipeline
pageId: "123456"
owner: "@moberghr/docs"
---
```

| Key | Required | Meaning |
|---|---|---|
| `sources` | no, but drift needs it | Code paths the page derives from, as globs relative to the repo root |
| `title` | no | Overrides the title. Defaults to the first H1, which is dropped from the body because Confluence renders the page title itself |
| `pageId` | no | Set by publish. Pre-seed it to adopt a page that already exists in Confluence |
| `owner` | no | A single string naming who owns the page. `docume drift` groups the affected pages by it in the PR comment, and the dashboard shows it in a column |
| `publish` | no | Defaults to `true`. Set `publish: false` to hold the page back as a draft: the publish plan reports it but never writes it, drift ignores it, and a previously published copy stays frozen in Confluence until the flag flips back. Not an orphan, because the file still exists |

**`owner` is carried verbatim.** DocuMe never prepends `@`, never changes the case and never resolves
the value against a forge or a directory, so **write the handle the way your forge mentions people**.
The refusal is deliberate: a tool that turned `alice` into `@alice` would notify whichever account
happens to hold that name, a stranger in the ordinary case where a repo's convention is an email or a
display name. An owner written without the mention syntax reaches the PR comment as plain text, pings
nobody, and is visibly wrong to the person reading it. What that syntax is, and who owns which page,
is your repo's knowledge rather than the tool's. Verbatim stops where a handle stops: the PR comment
collapses line endings and neutralizes `<`, `[` and `]` before it prints the handle, because each of the
three would let a crafted `owner` forge a verdict, hide the report behind a `<details>`, or post a
clickable link to anywhere, inside a comment the bot signs. No forge handle contains any of them, and
that is the whole test — `_` and `*` are left alone precisely because `@my_org/team` and `_platform_` are
made of them, so your mention is unaffected.

Verbatim goes for the grouping too: owners are compared byte for byte, so `owner: "@alice "` and
`owner: "@alice"` are two owners and get two identical-looking headings in the same comment. Quote a
handle only as tightly as you mean it.

[Approval and drift](../10-concepts/approval-and-drift.md) covers what the owner then does: the
grouped PR comment, the count of affected pages that carry none, and the dashboard column.

Frontmatter is stripped before conversion, so none of it appears in the published body or in the
content hash.

> [!TIP]
> Titles must be unique across the space, because a relative `.md` link converts to a Confluence
> link *by title*. Two pages claiming one title fail the whole load with both paths named, rather
> than publishing a tree with an ambiguous link in it.

## `_meta/state.json`

Machine-owned, one entry per page:

```json
{
  "version": 1,
  "baselineSha": "98c6df844",
  "lastPublishedSha": "1f4a9c02b",
  "pages": {
    "10-concepts/lifecycle.md": {
      "pageId": "123456",
      "title": "The Documentation Lifecycle",
      "parentPageId": "123400",
      "contentHash": "sha256:...",
      "publishedVersion": 6,
      "attachments": { "mermaid-abc123.svg": "sha256:..." },
      "diagramWidths": { "mermaid-abc123.svg": "241" },
      "verdict": {
        "sourcesHash": "sha256:...",
        "sealedAt": "2026-08-19T09:12:44Z",
        "repoSha": "6accfb8"
      },
      "approval": { "status": "approved", "approvedVersion": 6 },
      "stale": false,
      "marked": true,
      "feedbackCursor": "2026-08-01T10:00:00Z"
    }
  }
}
```

The two top-level shas answer different questions and are set by different things:

- **`baselineSha`** — the commit the wiki content was last *generated* against. Drift measures from
  here. No CLI command writes it; the generation pass does, which is `/docs-loop` or `/docs-refresh`.
- **`lastPublishedSha`** — the commit of the last publish run, written by `publish`. This is what
  `publish --changed-since` reads.

Using the publish sha as a drift baseline would report zero drift on a repo that publishes on every
merge, which is every repo that has automated this properly.

`diagramWidths` is the one per-page field that is not bookkeeping about the page itself: it records the
pixel width measured from each rendered diagram, which is the `ac:width` its image carries in Confluence.
It is remembered rather than re-derived because a publish re-renders only the diagrams whose bytes it
uploads, so a run that edits a page's *text* would otherwise republish its unchanged diagram without a
width, and the image would fall back to Confluence's native scaling. `docume publish --force` re-renders
everything and re-measures, which is also how a page picks a width up after a renderer upgrade changed
the layout without changing the diagram source.

`verdict` is the seal a publish takes over the code the page derives from. A run that writes a page body
also fingerprints every file that page's `sources` globs match, across the files git tracks in the repo,
and records that hash in `sourcesHash` with the moment it was taken in `sealedAt` and the commit the run
was publishing in `repoSha`. `docume drift` recomputes the fingerprint for the pages a diff flagged and
reports the ones that still match as sealed rather than drifted. A `verdict` is always evidence about at
least one file: a page whose globs matched no tracked file gets none, because the fingerprint of no files
is the same value under every condition that produces it and would match itself forever. A page published
before this existed carries no `verdict` either, and both answer drift from the commit range exactly as
they always did until a publish that can seal them.
[Approval and drift](../10-concepts/approval-and-drift.md) covers what the seal does and does not claim.

`approval` carries three more keys than the example shows: `approvedBy`, `approvedAt` and a `history`
array. [Approval and drift](../10-concepts/approval-and-drift.md) covers what each one means, including
why the first of them always reads `unknown`.

`marked` records that the page carries DocuMe's managed content property, stamped at create and healed
on the first body update of a page published before the marker existed. It is a cache, never the
authority: `publish --prune` reads the property live and refuses to delete a page that does not carry
it, whatever state says.

## Excluding files

`wiki.exclude` defaults to `["_meta/**"]`, which keeps the style guide, the gaps list and the state
file out of Confluence. `wiki.extraPages` re-includes a single excluded file under a title of your
choosing — the usual case being a gaps page you *do* want the team to see.

Dot-paths are excluded structurally rather than by that default. Any file with a path segment
beginning with `.` (`.claude/`, `.github/`, `.vscode/`, a bare `.editorconfig`) is tooling metadata
rather than wiki content, so it stays out of scope even if you replace `wiki.exclude` entirely. That
matters most when `wiki.root` is the repo root, where `docume init`'s own scaffolding lands: an
untitled `.github/PULL_REQUEST_TEMPLATE.md` in scope would fail the whole publish, because a page
with no title is a whole-tree error. `wiki.extraPages` re-includes a dot-path as well, on the rare
occasion you mean to publish one.
