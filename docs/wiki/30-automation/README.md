---
sources:
  - templates/workflows/*.yml
  - plugin/skills/**
---

# Automation

Unattended, DocuMe is two halves: the CLI, the only thing that talks to Confluence, and the skills,
which write markdown and can only ever open a pull request.

- [GitHub Workflows](workflows.md) schedule the work. Four of the eight shipped templates run the CLI
  and nothing else.
- [Claude Skills](skills.md) write markdown. A model, always producing a pull request.

The other four templates are where the halves meet, and the safety property is in *how* they meet. The
refresh and feedback jobs each ship in two spellings, `docs-refresh.claude.yml` and
`docs-feedback.claude.yml` for Claude Code, `docs-refresh.copilot.yml` and `docs-feedback.copilot.yml`
for the GitHub Copilot CLI, and a repo receives the pair matching its rail. They invoke a skill, so
they are the ones that need a model credential — `ANTHROPIC_API_KEY` on the claude rail,
`COPILOT_GITHUB_TOKEN` on the copilot rail — and they deliberately do not carry the Confluence
credentials. A skill can be wrong and the worst case is a bad pull request that a human declines:
nothing a model writes reaches Confluence until someone merges it and the publish workflow runs.

`docume init` scaffolds six files into `.github/workflows/`: the four CLI-only templates plus its
rail's model pair, landed under the bare names. Each carries an `EDIT BEFORE USE` header for
what it cannot guess: your default branch, your wiki root, the name of your deploy workflow,
read access to the DocuMe.Cli package, `ANTHROPIC_API_KEY`, and the release to pin the plugin to.
