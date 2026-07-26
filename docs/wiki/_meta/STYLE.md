# Style guide for DocuMe's own wiki

This file is excluded from publishing (`wiki.exclude`). It tells `/docs-loop` and `/docs-refresh` how
to write here.

## Audience

A developer or tech lead evaluating DocuMe for their repo, or operating it in one. They know git,
CI and Confluence. They do not know DocuMe's internals and should not need to.

## Scope

Document the **product**, not the implementation. A reader of this wiki wants to know what a command
does, what it writes, and what will surprise them. Class names, file layout and milestone history
belong in `PLAN.md` and the code, not here.

The wiki is deliberately small. Six topic pages and three indexes cover the product; a seventh page
usually means something on an existing page should have been a table.

## Tone

- Explain the rule, then the consequence. "The repo is the source of truth" is worth nothing without
  "so a hand edit in Confluence is overwritten".
- State what will bite the reader. Every page that documents a destructive or irreversible option says
  so in an alert.
- No marketing. No "simply", "just", "powerful", "seamless".
- Avoid the em-dash as a comma substitute; prefer a comma, a colon, or a new sentence.

## Structure

- One H1 per page, which becomes the Confluence title and is dropped from the body.
- Numbered directory prefixes (`10-`, `20-`, `30-`) set the published order.
- Every directory has a `README.md`: it is the parent of that directory's pages in Confluence.
- `[TOC]` on pages longer than roughly one screen.

## Constraints that are checked

`DogfoodWikiTests` fails the build when any of these breaks, so they are contracts rather than
preferences:

- Every page declares at least one `sources` glob, and every glob matches a file that exists.
- Every shipped path is covered by some page's globs, so nothing DocuMe hands over is invisible to
  `docume drift`.
- The whole wiki converts with **zero failures and zero degradations** under the strict policy.
  DocuMe's own docs do not use constructs DocuMe converts badly.
- Titles are unique, and every relative link resolves to a page in the tree.

## Constructs to avoid

Not because they are wrong, but because they degrade and the strict bar rejects them:

- `> [!IMPORTANT]` — collapses into the same panel as `[!NOTE]`. Use one of the other four.
- Centered or right column alignment in tables (`:---:`, `---:`). An explicitly left `:---` column
  publishes the way GitHub renders it and is fine.
- Lists mixing task items and plain items.
- Same-page anchor links.
- Ordered lists starting at anything but 1.
- Fence languages outside the brush map, which takes both Atlassian's documented list and a Prism
  component id. Confluence documenting a language is not enough: `octave` is documented and still
  degrades. `bash`, `json`, `yaml`, `csharp`, `text`, `diff` and `xml` are all safe.
