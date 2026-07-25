---
sources:
  - src/DocuMe.Core/**
---

# Concepts

The two ideas that explain most of DocuMe's behaviour.

- [The Documentation Lifecycle](lifecycle.md) — the five stages a page moves through, and which
  command or skill owns each one.
- [Approval and Drift](approval-and-drift.md) — how a page is marked reviewed, what silently
  invalidates that, and how DocuMe knows a page has fallen behind its code.

Both pages assume the [one-way rule](../README.md): content flows repo → Confluence, review signals
flow Confluence → repo.
