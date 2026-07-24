## Character references &copy; 2026

Named references publish as the character they resolve to: &copy; &reg; &trade; &mdash; &rarr; &hellip;

XML predefines five entity names. &amp; &lt; and &gt; round-trip unchanged, because the escaper writes them back.

The other two resolve to characters that need no escaping in element content: &quot; and &apos;.

A bare & that is part of no reference is escaped the same way.

Numeric references resolve as well: decimal &#169;, hexadecimal &#xA9;, and an astral one &#x1F600;.

An unrecognized reference stays the literal text the author typed: &nosuchthing; and &copy with no semicolon.

<!--
  &nbsp; is deliberately absent from this golden. It resolves to U+00A0, which is
  invisible both here and in the .storage.xml beside it, so a hand review (§4.3)
  could not tell it from an ordinary space. Its codepoint is pinned instead by
  ConfluenceStorageConverterTests.Convert_resolves_nbsp_to_a_non_breaking_space.
-->

Inside a code span the source spelling survives, because a span is never entity-parsed: `&copy;` and `&amp;`.

```text
literal &copy; and &amp; inside a fence
```

- A list item with &copy; in it
- A list item with an &mdash; dash

| Symbol | Meaning &amp; note |
|---|---|
| &copy; | copyright |
| &rarr; | x &amp; y |

> A quoted &copy; line.

A [link with &copy; in its text](https://example.com/terms) and a [titled link](https://example.com/t "Terms &copy; 2026").

![alt text with &copy; inside](images/badge.png)

**Bold &copy;** and *italic &mdash;*.
