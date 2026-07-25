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

- [ ] **The composite GitHub Action.** `actions/action.yml` exists now: it installs the repo-pinned CLI
      (`dotnet tool restore` over `.config/dotnet-tools.json`, erroring by name when that manifest is
      missing) and runs `docume` with an `args` string. No page mentions it. The versioning question
      that kept it out is settled in `PLAN.md` §12: a floating major tag, force-moved by the release
      workflow's last step, `@v0` until 1.0.0 ships, and a prerelease tag moves nothing. Not urgent,
      because none of the six scaffolded workflows uses it: five call `dotnet tool run docume`
      directly and `docs-feedback.yml` reaches the CLI through the skill it runs. It will not arrive
      as drift either, since `30-automation/workflows.md` declares only `templates/workflows/*.yml`
      and no page's `sources` cover `actions/`.

## Answered, kept for the reasoning

- **Why `approvedBy` is always `unknown`.** Confluence Cloud exposes no label author through any API
  DocuMe can reach. Documented on the approval page rather than left as a puzzle, because it reads like
  a bug until you know.
