# Open questions for DocuMe's own wiki

Excluded from publishing. `/docs-feedback` records questions here rather than guessing at an answer,
and `/docs-loop` reads it to find the next page worth writing.

## Not documented yet, deliberately

- [ ] **Installation and the first publish.** Kept in the repository `README.md` — it changes with the
      release process, and two copies of an install story is how a wiki starts lying. Revisit once the
      published version is stable.
- [ ] **The composite GitHub Action.** `PLAN.md` §12 promises `actions@v1` wrapping install and run.
      It does not exist and its versioning is an open decision (a floating major tag has to be
      force-moved on every release). All six shipped workflows call `dotnet tool run docume` directly
      and need no action, so nothing here is blocked.
- [ ] **Live-behaviour caveats.** Whether an inline comment's anchor survives a body rewrite, and which
      mermaid dialects the renderer rejects in practice, are sandbox observations rather than code
      facts. They belong on the conversion and feedback pages once observed against a real space.

## Answered, kept for the reasoning

- **Why `approvedBy` is always `unknown`.** Confluence Cloud exposes no label author through any API
  DocuMe can reach. Documented on the approval page rather than left as a puzzle, because it reads like
  a bug until you know.
