# Open questions for DocuMe's own wiki

Excluded from publishing. `/docs-feedback` records questions here rather than guessing at an answer,
and `/docs-loop` reads it to find the next page worth writing.

## Not documented yet, deliberately

- [ ] **Installation and the first publish.** Kept in the repository `README.md` — it changes with the
      release process, and two copies of an install story is how a wiki starts lying. Revisit once the
      published version is stable.
- [ ] **Whether an inline comment's anchor survives a body rewrite.** A sandbox observation rather
      than a code fact, and open as spike S4. It belongs on the feedback page once observed against a
      real space.

## Shipped but no page describes it

Nothing today, and it is now checked rather than asserted. `DogfoodWikiTests` runs every shipped path
through the same globbing a real drift run uses and fails on one no page's `sources` covers, because a
shipped path no glob covers can never arrive as drift: the page describing it goes stale with nothing
reporting it. An artifact that genuinely needs no page is listed in that test with its reason, so the
decision is written down instead of being an empty result.

## Answered, kept for the reasoning

- **The composite GitHub Action.** Documented on `30-automation/workflows.md`, under "Writing a docs job
  of your own", and `actions/*.yml` is in that page's `sources` so a change to it now surfaces as drift.
  It was invisible for exactly the reason above: the page declared only `templates/workflows/*.yml`, and
  no other page's globs reached `actions/`.

- **Which mermaid dialects the renderer rejects.** Named on `20-reference/conversion.md`, under
  "Mermaid": `pie` and a trailing semicolon on the header line. These were parked here as sandbox
  observations, and they are not. `MermaidAcceptanceTests` runs the real renderer over the golden
  corpus and asserts both refusals, and `templates/tools/render-mermaid.mjs` records them against the
  pinned version, so the wiki was withholding a fact the build already proved. Both spellings render on
  GitHub, which is the case where a reader has no other signal.

- **Why `approvedBy` is always `unknown`.** Confluence Cloud exposes no label author through any API
  DocuMe can reach. Documented on the approval page rather than left as a puzzle, because it reads like
  a bug until you know.
