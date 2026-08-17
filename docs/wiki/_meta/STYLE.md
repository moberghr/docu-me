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

The wiki is deliberately small: 7 topic pages and 4 indexes cover the product. Another topic page
usually means something on an existing page should have been a table.

## Tone

- Explain the rule, then the consequence. "The repo is the source of truth" is worth nothing without
  "so a hand edit in Confluence is overwritten".
- State what will bite the reader. Every page that documents a destructive or irreversible option says
  so in an alert.
- No marketing. No "simply", "just", "powerful", "seamless".
- Avoid the em-dash as a comma substitute; prefer a comma, a colon, or a new sentence.

## Diagrams

A page whose subject is a flow or a state machine opens with one mermaid fence that shows it: the
lifecycle gets a flowchart, approval gets a state diagram. Reference pages draw only what a table
cannot carry. Stay inside the supported families — flowchart and graph, sequenceDiagram,
stateDiagram-v2, classDiagram, erDiagram, xychart-beta — and avoid `pie`, YAML frontmatter and a `;`
after `graph TD`, all of which reject (the conversion reference names them). Check an added or edited
fence by running `docume convert docs/wiki` with `--render-mermaid` before opening the pull request: a
diagram the renderer refuses fails that page's publish.

## Structure

- One H1 per page, which becomes the Confluence title and is dropped from the body. *Every* H1 is
  dropped, not just the first, so a second one is a heading whose text disappears from the published
  page with no warning anywhere.
- Numbered directory prefixes (`10-`, `20-`, `30-`) set the published order.
- Every directory has a `README.md`: it is the parent of that directory's pages in Confluence. A
  directory without one loses its level in the published tree, and its pages hang from the index
  above instead.
- `[TOC]` on pages longer than roughly one screen.

## Verification

Every factual sentence carries a `path/file.cs:line` or names the symbol a reader can grep. What the
code cannot settle either stays off the page and goes to `_meta/GAPS.md`, or carries a marker when a
reader genuinely needs the sentence:

| Marker | When | What follows it |
|---|---|---|
| `⚠️ UNVERIFIED` | you believe it, the code neither confirms nor contradicts it | what would settle it |
| `⚠️ AMBIGUOUS` | the code supports two readings | both readings, and the citation for each |

Both pass through the converter as plain text (`PLAN.md` §7, golden case `markers`), so a reader sees
them in Confluence rather than only in the repo. That is the point of using them instead of a code
comment, and also the reason to use them sparingly: a page speckled with markers has stopped being
documentation and become a list of open questions, and that list belongs in `GAPS.md`.

## Constraints that are checked

`DogfoodWikiTests` and `StyleGuidePageTests` fail the build when any of these breaks, so they are
contracts rather than preferences:

- Every page declares at least one `sources` glob, and every glob matches a file that exists.
- Every shipped path reaches some page's globs, so nothing DocuMe hands over is invisible to
  `docume drift`. Shipped means `.claude-plugin/`, `actions/`, `plugin/`, `schema/`, `src/` and
  `templates/`: how DocuMe is made (its tests, its CI, the build loop) is not what it hands over, and
  this wiki documents the product. A shipped file that genuinely needs no page is listed in the test
  with its reason.
- Every top-level directory is declared shipped or not, so a new one arrives as a decision. The list
  above is what bounds the shipped sweep and the rule §9.5 knowledge scan, and both pass over anything
  outside it without a word — an unclassified directory would be exempt from both in silence.
- Every directory publishes through its own `README.md`, and exactly one page sits at the tree root.
- The whole wiki converts with **zero failures and zero degradations** under the strict policy.
  DocuMe's own docs do not use constructs DocuMe converts badly.
- Titles are unique, and every relative link resolves to a page in the tree.
- This guide describes the above accurately: the page count under **Scope**, the markers under
  **Verification**, and one bullet per degradation code under **Constructs to avoid**. A guide that
  `/docs-loop` reads every run is instruction, so a stale sentence here is written into pages rather
  than merely misread.

## Constructs to avoid

Not because they are wrong, but because they degrade and the strict bar rejects them. Each bullet ends
with the code `docume convert` prints, so a warning leads back to the rule it broke:

- `> [!IMPORTANT]` — collapses into the same panel as `[!NOTE]`. Use one of the other four.
  (`alert-type-collapsed`)
- Centered or right column alignment in tables (`:---:`, `---:`). An explicitly left `:---` column
  publishes the way GitHub renders it and is fine. (`table-alignment-dropped`)
- Lists mixing task items and plain items. (`mixed-task-list`)
- Ordered lists whose every item is a task (`1. [x] done`). They publish as a native task list, which
  has no numbered form, so the checkboxes stay and the numbers go. (`task-list-numbering-dropped`)
- Same-page anchor links, which publish as their link text with the destination gone.
  (`same-page-anchor-link`)
- Ordered lists starting at anything but 1. (`ordered-list-start-dropped`)
- Fence languages outside the brush map, which takes both Atlassian's documented list and a Prism
  component id. Confluence documenting a language is not enough: `octave` is documented and still
  degrades. `bash`, `json`, `yaml`, `csharp`, `text`, `diff` and `xml` are all safe.
  (`unknown-fence-language`)
