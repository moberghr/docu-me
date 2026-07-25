# Open questions for DocuMe's own wiki

Excluded from publishing. `/docs-feedback` records questions here rather than guessing at an answer,
and `/docs-loop` reads it to find the next page worth writing.

## Not documented yet, deliberately

- [ ] **Installation and the first publish.** Kept in the repository `README.md` — it changes with the
      release process, and two copies of an install story is how a wiki starts lying. Revisit once the
      published version is stable.
- [ ] **Live-behaviour caveats.** Whether an inline comment's anchor survives a body rewrite, and which
      mermaid dialects the renderer rejects in practice, are sandbox observations rather than code
      facts. They belong on the conversion and feedback pages once observed against a real space.

## Shipped but no page describes it

Nothing today. The check that finds the next one is to compare what the repo ships against the union of
every page's `sources`: a shipped path no glob covers can never arrive as drift, so it stays undocumented
without anything reporting it.

## Answered, kept for the reasoning

- **The composite GitHub Action.** Documented on `30-automation/workflows.md`, under "Writing a docs job
  of your own", and `actions/*.yml` is in that page's `sources` so a change to it now surfaces as drift.
  It was invisible for exactly the reason above: the page declared only `templates/workflows/*.yml`, and
  no other page's globs reached `actions/`.

- **Why `approvedBy` is always `unknown`.** Confluence Cloud exposes no label author through any API
  DocuMe can reach. Documented on the approval page rather than left as a puzzle, because it reads like
  a bug until you know.
