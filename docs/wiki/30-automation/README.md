---
sources:
  - templates/workflows/*.yml
  - plugin/skills/**
---

# Automation

Unattended, DocuMe is two halves that never overlap.

- [GitHub Workflows](workflows.md) run the CLI. Deterministic, no model, no judgement.
- [Claude Skills](skills.md) write markdown. A model, always producing a pull request.

The split is the safety property: a skill can be wrong and the worst case is a bad pull request that a
human declines. A workflow cannot be wrong about *content* because it never writes any.

`docume init` scaffolds the workflows into `.github/workflows/`, commented for the two things they
cannot guess — your default branch and the name of your deploy workflow.
