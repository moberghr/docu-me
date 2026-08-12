---
sources:
  - templates/workflows/*.yml
  - plugin/skills/**
---

# Automation

Unattended, DocuMe is two halves: the CLI, the only thing that talks to Confluence, and the skills,
which write markdown and can only ever open a pull request.

- [GitHub Workflows](workflows.md) schedule the work. Four of the six run the CLI and nothing else.
- [Claude Skills](skills.md) write markdown. A model, always producing a pull request.

The other two workflows, `docs-refresh.yml` and `docs-feedback.yml`, are where the halves meet, and the
safety property is in *how* they meet. They invoke a skill, so they are the two that need
`ANTHROPIC_API_KEY`, and they are the two that deliberately do not carry the Confluence credentials. A
skill can be wrong and the worst case is a bad pull request that a human declines: nothing a model
writes reaches Confluence until someone merges it and the publish workflow runs.

`docume init` scaffolds all six into `.github/workflows/`. Each carries an `EDIT BEFORE USE` header for
what it cannot guess: your default branch, your wiki root, the name of your deploy workflow,
read access to the DocuMe.Cli package, `ANTHROPIC_API_KEY`, and the release to pin the plugin to.
