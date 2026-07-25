---
sources:
  - src/DocuMe.Core/Config/*.cs
  - src/DocuMe.Core/State/*.cs
  - src/DocuMe.Core/Scaffolding/*.cs
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
`wiki.root`. Everything else has the default shown above.

Two are worth a second look:

- **`wiki.homePage`** names the index file of *every* directory, not only the root. `a/b/page.md` hangs
  under `a/b/README.md`, which hangs under `a/README.md`. A directory with no index page is skipped
  rather than synthesized: its children hang from the nearest index above it, because inventing a page
  no author wrote would break the one-way rule.
- **`confluence.protectedSpaces`** is a write lock. A space listed here is refused, and the only way
  past it is `--allow-protected-space` for a single run. There is no config value that grants it
  permanently, so removing the lock is a reviewed commit rather than a flag somebody stops reading.

## Page frontmatter

```yaml
---
sources:
  - src/DocuMe.Core/Publishing/*.cs
  - src/DocuMe.Core/State/StateStore.cs
title: Publish Pipeline
pageId: "123456"
---
```

| Key | Required | Meaning |
|---|---|---|
| `sources` | no, but drift needs it | Code paths the page derives from, as globs relative to the repo root |
| `title` | no | Overrides the title. Defaults to the first H1, which is dropped from the body because Confluence renders the page title itself |
| `pageId` | no | Set by publish. Pre-seed it to adopt a page that already exists in Confluence |

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
      "approval": { "status": "approved", "approvedVersion": 6 },
      "stale": false,
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

## Excluding files

`wiki.exclude` defaults to `["_meta/**"]`, which keeps the style guide, the gaps list and the state
file out of Confluence. `wiki.extraPages` re-includes a single excluded file under a title of your
choosing — the usual case being a gaps page you *do* want the team to see.
