---
sources:
  - src/DocuMe.Cli/**
  - src/DocuMe.Core/**
---

# Reference

- [CLI Reference](cli.md) — every command and the options that change what it writes.
- [Configuration and State](configuration.md) — `docume.json`, page frontmatter, `_meta/state.json`.
- [Markdown to Confluence Storage Format](conversion.md) — which constructs convert, which degrade,
  and which fail loud.

Each page derives from the code it documents: `src/DocuMe.Cli` for the commands, `src/DocuMe.Core` for
config, state and the converter. The conversion page also tracks the shipped mermaid renderer and the
golden corpus, which are the contract for what it claims. Every page names its own set in `sources`,
and that frontmatter, not this sentence, is what `docume drift` reads.

When an option's help text and this wiki disagree, the help text is right and this page has drifted.
