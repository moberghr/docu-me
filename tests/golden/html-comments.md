## HTML comments

Prose before a standalone comment.

<!-- A note to editors that must never reach Confluence. -->

Prose after it: no blank line and no empty paragraph is left behind.

Prose whose citation sits directly underneath it, no blank line before the comment.
<!-- cites: src/Example.cs:10 -->

<!-- A comment may span
     more than one line, and all of it goes. -->

<!-- HAND-EDITED START -->

Content wrapped by DocuMe's own refresh markers survives; the markers do not.

<!-- HAND-EDITED END -->

Text with an <!-- inline --> comment keeps the author's spacing verbatim.

A comment may also trail a sentence. <!-- trailing -->

One <!-- a --> two <!-- b --> three.

### Heading with a trailing comment <!-- h -->

- one <!-- inline --> item
- two
  <!-- a block comment inside a tight list item -->
- three

| Column | Notes |
|---|---|
| <!-- leading --> cell | mid <!-- cell --> cell |

> Quoted prose stays quoted.
>
> <!-- a comment inside a blockquote -->

```html
<!-- Inside a fence a comment is code, so it survives verbatim. -->
```

<!---->

<!-- two --><!-- on one line -->

   <!-- indented up to three spaces is still a comment block -->

Last paragraph.
