# Open questions for DocuMe's own wiki

Excluded from publishing: `wiki.exclude` defaults to `_meta/**`, and that is what `docume init` writes.
`/docs-feedback` records questions here rather than guessing at an answer. `/docs-loop` takes its next
unit from `_meta/PROGRESS.md`, the first `todo` in file order, and reads this file so it does not re-ask
a question an earlier run already failed to settle.

## Not documented yet, deliberately

- [ ] **The CLI install and the first publish.** Kept in the repository `README.md`: it changes with the
      release process, and two copies of an install story is how a wiki starts lying. The plugin half is
      the exception, on `30-automation/skills.md` under "Installing", where the same two slash commands
      appear a second time, so those two copies have to agree. Revisit once the published version is
      stable.
- [ ] **Whether an inline comment's anchor survives a body rewrite.** A sandbox observation rather
      than a code fact, and open as spike S4. There is no feedback page: it belongs in
      `10-concepts/lifecycle.md` under "4. Feedback" once observed against a real space, and an
      affirmative answer is what would change `20-reference/cli.md`'s `--block-on-open-comments` row,
      which today documents the guard that exists because the question is unsettled.

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
  "Mermaid": `pie`, a trailing semicolon on the header line, and YAML frontmatter. These were parked
  here as sandbox observations, and they are not. `MermaidAcceptanceTests` runs the real renderer over
  the golden corpus and asserts the first two refusals, and `templates/tools/render-mermaid.mjs`
  records them against the pinned version, so the wiki was withholding a fact the build already
  proved. All three render on GitHub, which is the case where a reader has no other signal.
  The page states the supported set as a closed list of six families rather than a denylist of
  spellings, because that is the shape of the parser's own dispatch and a denylist read as though
  `pie` were an exception. The semicolon is the uneven one: tolerated on `sequenceDiagram;` and
  `classDiagram;`, rejected on `graph TD;`.

- **Why `approvedBy` is always `unknown`.** Confluence Cloud exposes no label author through any API
  DocuMe can reach. Documented on `10-concepts/approval-and-drift.md`, under "How approval is
  recorded", rather than left as a puzzle, because it reads like a bug until you know.
