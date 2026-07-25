---
sources:
  - src/DocuMe.Core/Markdown/*.cs
  - src/DocuMe.Core/Acceptance/*.cs
  - tests/golden/**
---

# Markdown to Confluence Storage Format

[TOC]

The converter is a custom Markdig renderer that emits Confluence storage format directly. Not
markdown → HTML → regex: storage format is not HTML, and every layer of that pipeline loses something
the next one cannot recover.

Its contract is the golden-file suite. Each case is a markdown file and the storage format it must
produce, reviewed by hand once and asserted forever. A golden is never regenerated to make a failing
test pass — the diff goes to a human, because a converter that rewrites its own expectations has no
contract at all.

## What converts

| Markdown | Confluence |
|---|---|
| Headings h1–h4 | `<h1>`–`<h4>`, with the page's leading h1 dropped — Confluence renders the title itself |
| GFM tables | `<table>` with a header row |
| Fenced code blocks | The code macro, with the language mapped where Confluence knows it |
| GitHub alerts | Panel macros: NOTE and IMPORTANT to info, TIP to tip, WARNING to note, CAUTION to warning |
| Mermaid fences | Rendered to SVG, attached to the page, referenced as an image |
| Relative `.md` links | A Confluence page link, resolved by the linked page's title |
| External links | A plain anchor |
| `[TOC]` alone on a line | The table-of-contents macro |
| Images | Attachment plus image tag; `{width=300}` becomes the rendered width |
| Task lists | The native task-list macro |
| Inline code, bold, italic, strikethrough, blockquotes, rules, nested lists | Native markup |

## Code fence attributes

Fence attributes follow mark's syntax, which is space-separated with no `=` anywhere:

```text
```bash collapse linenumbers firstline 10 title Publishing a single page
```

`title` takes the rest of the line, unquoted. `collapse`, `nocollapse`, `linenumbers` and a bare
positive integer for `firstline` are the other four. An **unrecognized attribute fails the page loud**,
including the `title=Foo` spelling, which is a different tool's syntax.

That asymmetry against the language token is deliberate. An unknown language costs syntax
highlighting and is reported as a degradation; a silently dropped attribute publishes a page the
author did not ask for.

## The three severities

Every construct lands in one of three buckets, and which bucket matters more than the individual
mappings.

1. **Converts** — silent. The overwhelming majority.
2. **Degrades** — converts, with something lost, and says so. Storage format cannot express the
   construct, so the alternative to a warning is a quiet difference between the markdown and the page.
3. **Fails** — the page does not convert at all. Reserved for constructs where guessing would publish
   something wrong: an unknown diagram dialect, raw inline HTML, an unrecognized fence attribute.

The degradation codes, all reported per page with the dialect that triggered them:

| Code | What is lost |
|---|---|
| `unknown-fence-language` | Highlighting for a language Confluence does not document |
| `mixed-task-list` | A list mixing task and plain items falls back to a plain list, echoing the literal `[x]` text |
| `same-page-anchor-link` | An anchor link within one page has no storage-format equivalent |
| `table-alignment-dropped` | GFM column alignment is not representable |
| `ordered-list-start-dropped` | A list starting at something other than 1 |
| `alert-type-collapsed` | Two alert types mapping to one panel, so the distinction is gone |
| `task-list-numbering-dropped` | An ordered task list, which the macro cannot number |

## Checking a wiki

```bash
dotnet tool run docume convert docs/wiki
```

Exit code 0 means zero failures and zero warnings. A degradation your team has decided to live with is
promoted to a note with `--accept`, which keeps the count and the dialect breakdown in the report
rather than hiding them:

```bash
dotnet tool run docume convert docs/wiki --accept table-alignment-dropped
```

> [!NOTE]
> DocuMe's own wiki is held to the strict bar: zero failures and zero degradations, no `--accept`.
> A tool that documents itself using constructs it converts badly is not making a good argument.

## Mermaid

A mermaid fence is rendered to SVG through Node and uploaded as an attachment, named from a hash of
the diagram source. That naming is a pure function of the source, so an unrelated edit elsewhere on
the page never renames the attachment and never churns the content hash.

Rendering happens at publish. `convert` counts diagrams without starting a process unless
`--render-mermaid` is passed, which is what makes a conversion check cheap enough to run on every
pull request.

> [!WARNING]
> Not every mermaid dialect renders. A diagram the renderer rejects fails its page rather than
> publishing a broken image reference, so a new diagram is worth a `convert --render-mermaid` before
> the pull request.
