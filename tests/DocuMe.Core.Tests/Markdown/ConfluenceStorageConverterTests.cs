using DocuMe.Core.Markdown;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Shouldly;

namespace DocuMe.Core.Tests.Markdown;

/// <summary>
/// Direct converter assertions that a golden file cannot express — chiefly the
/// fail-loud contract for constructs the M1 seed does not yet render (PLAN.md §7).
/// Rendering behavior itself is pinned by the golden-file suite.
/// </summary>
public sealed class ConfluenceStorageConverterTests
{
    [Theory]

    // An indented CodeBlock (only fenced code is supported), then a raw HTML block.
    [InlineData("    indented code")]
    [InlineData("<div>raw html</div>")]
    public void Convert_throws_on_unsupported_block_construct(string markdown)
    {
        // Neither construct has a storage-format mapping, so conversion must throw rather
        // than silently drop or mis-transform it. The first reaches the catch-all; the
        // second is rejected by HtmlBlockRenderer, which admits comments only.
        var ex = Should.Throw<NotSupportedException>(() => ConfluenceStorageConverter.Convert(markdown));
        ex.Message.ShouldContain("No storage-format renderer");
    }

    [Fact]
    public void Convert_renders_external_link_without_a_resolver()
    {
        // External links carry their own href — no page-title resolver needed.
        var storage = ConfluenceStorageConverter.Convert("See [the docs](https://example.com).");

        storage.ShouldContain("<a href=\"https://example.com\">the docs</a>");
    }

    [Fact]
    public void Convert_renders_external_image_without_a_resolver()
    {
        // An external image carries its own URL — no attachment resolver needed,
        // mirroring how an external link needs no page-link resolver.
        var storage = ConfluenceStorageConverter.Convert("![a diagram](https://example.com/d.png)");

        storage.ShouldContain(
            "<ac:image ac:alt=\"a diagram\"><ri:url ri:value=\"https://example.com/d.png\"/></ac:image>");
    }

    [Fact]
    public void Convert_throws_on_local_image_without_an_attachment_resolver()
    {
        // A local image can only render if a resolver supplies the attachment
        // filename; emitting a dangling ri:filename would publish a broken image.
        var ex = Should.Throw<InvalidOperationException>(
            () => ConfluenceStorageConverter.Convert("![a diagram](diagram.png)"));
        ex.Message.ShouldContain("requires an attachment resolver");
    }

    [Fact]
    public void Convert_throws_on_local_image_the_resolver_does_not_resolve()
    {
        // A path the resolver returns null for is a broken image reference — the
        // same fail-loud contract as an unresolved relative .md link.
        var ex = Should.Throw<InvalidOperationException>(
            () => ConfluenceStorageConverter.Convert(
                "![a diagram](missing.png)",
                attachmentResolver: _ => null));
        ex.Message.ShouldContain("does not resolve to any known file");
    }

    [Fact]
    public void Convert_throws_on_image_followed_by_an_attribute_block()
    {
        // PLAN.md §7's '{width=300}' is not honored yet, and with no attributes
        // extension Markdig leaves it as a sibling literal — rendering the image
        // would publish the braces as visible text beside it.
        var ex = Should.Throw<NotSupportedException>(
            () => ConfluenceStorageConverter.Convert(
                "![a](p.png){width=300}",
                attachmentResolver: _ => "p.png"));
        ex.Message.ShouldContain("{width=300}");
    }

    [Fact]
    public void Convert_throws_on_data_uri_image()
    {
        // <ri:url> needs a fetchable URL; a base64 payload would publish broken.
        var ex = Should.Throw<NotSupportedException>(
            () => ConfluenceStorageConverter.Convert("![a](data:image/png;base64,iVBORw0K)"));
        ex.Message.ShouldContain("'data:' URI");
    }

    [Fact]
    public void Convert_throws_on_alt_text_carrying_a_nested_link()
    {
        // Flattening alt text to a plain ac:alt string would silently drop the
        // nested link's destination.
        var ex = Should.Throw<NotSupportedException>(
            () => ConfluenceStorageConverter.Convert(
                "![see [here](https://example.com)](p.png)",
                attachmentResolver: _ => "p.png"));
        ex.Message.ShouldContain("no plain-text projection");
    }

    [Fact]
    public void Convert_throws_on_a_mermaid_fence_without_a_diagram_resolver()
    {
        // Before this slice a mermaid fence silently published as a code block —
        // the diagram source where a picture belongs. Nothing may fall back to that.
        var ex = Should.Throw<InvalidOperationException>(
            () => ConfluenceStorageConverter.Convert("```mermaid\ngraph TD;\nA-->B;\n```"));
        ex.Message.ShouldContain("requires a mermaid diagram resolver");
    }

    [Fact]
    public void Convert_throws_when_a_mermaid_diagram_does_not_render()
    {
        // A null return means the renderer failed, either on bad diagram source or
        // because Node is missing. Publishing the source text instead would hide that.
        var ex = Should.Throw<InvalidOperationException>(
            () => ConfluenceStorageConverter.Convert(
                "```mermaid\nnot a diagram\n```",
                mermaidResolver: _ => null));
        ex.Message.ShouldContain("did not render to a diagram attachment");
    }

    [Fact]
    public void Convert_throws_on_a_mermaid_fence_carrying_code_macro_attributes()
    {
        // collapse/title/linenumbers have no counterpart on an <ac:image>, so
        // accepting them would drop what the author asked for.
        var ex = Should.Throw<NotSupportedException>(
            () => ConfluenceStorageConverter.Convert(
                "```mermaid collapse\ngraph TD;\nA-->B;\n```",
                mermaidResolver: _ => "d.svg"));
        ex.Message.ShouldContain("no code-macro parameters");
    }

    [Fact]
    public void Convert_throws_on_an_empty_mermaid_fence()
    {
        // Nothing to render, so there is no attachment the publisher could upload.
        var ex = Should.Throw<NotSupportedException>(
            () => ConfluenceStorageConverter.Convert(
                "```mermaid\n\n```",
                mermaidResolver: _ => "d.svg"));
        ex.Message.ShouldContain("Mermaid fence is empty");
    }

    [Theory]

    // A comment sharing its closing line with author text, at every position CommonMark
    // can produce one: opening a document, interrupting a paragraph, closing a multi-line
    // comment, and with no separating space at all.
    [InlineData("<!-- c --> and then text.", "and then text.")]
    [InlineData("Before.\n<!-- c --> tail\nAfter.", "tail")]
    [InlineData("<!-- a\nb --> tail", "tail")]
    [InlineData("<!-- a -->text", "text")]
    public void Convert_throws_when_a_comment_block_carries_author_text(string markdown, string tail)
    {
        // THE reason this slice cannot drop on HtmlBlock.Type alone. CommonMark's comment
        // block ends at the line *containing* '-->', and the whole of that line belongs to
        // the block — so a blanket drop would silently delete the tail. It is also why this
        // fails loud instead of emitting the tail: the tail never went through inline
        // parsing, so a '**bold**' in it would publish as literal asterisks.
        var ex = Should.Throw<NotSupportedException>(() => ConfluenceStorageConverter.Convert(markdown));
        ex.Message.ShouldContain("shares its line with content that is not a comment");
        ex.Message.ShouldContain(tail);
    }

    [Theory]
    [InlineData("Before.\n\n<!-- oops\n\nAfter.")]
    [InlineData("<!-->")]
    public void Convert_throws_on_an_unterminated_comment(string markdown)
    {
        // An unclosed comment runs to the end of its container, so the first case has a
        // whole paragraph ("After.") inside the comment block — dropping it would delete
        // the rest of the page. The second is HTML5's abrupt-closing form, left to fail
        // rather than guessed at: its closer would have to reuse the opener's own '--'.
        var ex = Should.Throw<NotSupportedException>(() => ConfluenceStorageConverter.Convert(markdown));
        ex.Message.ShouldContain("Unterminated HTML comment");
    }

    [Fact]
    public void Convert_throws_on_inline_raw_html()
    {
        // Only comments are dropped; a real tag has no storage-format mapping, and
        // Confluence would reject or rewrite it on save.
        var ex = Should.Throw<NotSupportedException>(
            () => ConfluenceStorageConverter.Convert("Text with <b>bold</b>."));
        ex.Message.ShouldContain("inline raw HTML");
        ex.Message.ShouldContain("<b>");
    }

    [Fact]
    public void Convert_keeps_the_authors_spacing_when_it_drops_an_inline_comment()
    {
        // Deliberate: the comment node is dropped and nothing else is touched, leaving the
        // double space. Collapsing it would make the renderer an editor of the author's
        // whitespace (and would then owe the same answer for leading, trailing and heading
        // space) for no gain — XHTML collapses consecutive whitespace when rendered, so a
        // Confluence reader sees one space either way. Pinned because the double space looks
        // like a bug and invites a "fix" that would churn the §8 content hash.
        ConfluenceStorageConverter.Convert("Text with an <!-- c --> comment.")
            .ShouldBe("<p>Text with an  comment.</p>\n");
    }

    [Fact]
    public void Convert_drops_a_comment_block_without_leaving_a_blank_line()
    {
        // The renderer emits nothing at all, not even a newline, so a comment between two
        // paragraphs leaves neither a stray blank line nor an empty <p></p>.
        ConfluenceStorageConverter.Convert("Before.\n\n<!-- note -->\n\nAfter.")
            .ShouldBe("<p>Before.</p>\n<p>After.</p>\n");
    }

    [Fact]
    public void Convert_drops_the_hand_edited_marker_pair_and_keeps_what_it_wraps()
    {
        // The row PLAN.md §7 names explicitly. DocuMe's own refresh workflow writes these
        // markers into consumer wikis (§6, §9), so a page carrying one has to convert.
        ConfluenceStorageConverter.Convert(
            "<!-- HAND-EDITED START -->\n\nKept.\n\n<!-- HAND-EDITED END -->")
            .ShouldBe("<p>Kept.</p>\n");
    }

    [Fact]
    public void Convert_resolves_nbsp_to_a_non_breaking_space()
    {
        // The reason tests/golden/entities.md deliberately omits &nbsp;: U+00A0 is invisible
        // in a golden file, so a hand review (§4.3) could not tell it from a plain space.
        // Asserted here as an explicit '\u00A0' escape rather than a typed character: a
        // literal one in this file would be exactly as invisible as in the golden, and anyone
        // "tidying" it into a plain space would leave a test that passes against the wrong
        // output. What publishes is the character, not the name: '&nbsp;' is undefined in XML,
        // so echoing the name would invite Confluence to rewrite the body on save, churning
        // the §8 content hash and invalidating an approval nothing really changed (§9.2).
        ConfluenceStorageConverter.Convert("A price of 10&nbsp;000 kr.")
            .ShouldBe("<p>A price of 10\u00A0000 kr.</p>\n");
    }

    [Fact]
    public void Convert_does_not_double_escape_the_ampersand_entity()
    {
        // The escaper re-escapes what the entity resolves to, so '&amp;' transcodes to '&'
        // and is written back as '&amp;'. A renderer that emitted the source spelling
        // unescaped, or escaped the already-escaped text, would publish '&amp;amp;'.
        ConfluenceStorageConverter.Convert("Loans &amp; leases.")
            .ShouldBe("<p>Loans &amp; leases.</p>\n");
    }

    [Fact]
    public void Convert_does_not_let_an_entity_smuggle_markup_into_the_body()
    {
        // '&lt;' resolves to '<', which the escaper puts straight back. Publishing the
        // resolved character raw would turn author prose — or Confluence content echoed
        // back into a page (§0.2) — into live storage-format markup.
        ConfluenceStorageConverter.Convert("Beware &lt;script&gt;alert(1)&lt;/script&gt;.")
            .ShouldBe("<p>Beware &lt;script&gt;alert(1)&lt;/script&gt;.</p>\n");
    }

    [Fact]
    public void Convert_resolves_an_entity_in_image_alt_text_without_breaking_the_attribute()
    {
        // ac:alt is built by FlattenAltText, whose switch had no entity case and so threw.
        // '&quot;' is the case that matters: it resolves to a bare '"' inside a
        // double-quoted XML attribute, and only WriteAttributeEscaped putting it back as
        // '&quot;' keeps the attribute intact.
        ConfluenceStorageConverter.Convert(
            "![a &quot;quoted&quot; &copy; badge](images/badge.png)",
            attachmentResolver: _ => "badge.png")
            .ShouldBe(
                "<p><ac:image ac:alt=\"a &quot;quoted&quot; © badge\">"
                + "<ri:attachment ri:filename=\"badge.png\"/></ac:image></p>\n");
    }

    [Fact]
    public void Convert_keeps_a_comment_inside_a_fence_as_code()
    {
        // A fence body is CDATA and never sees inline parsing, so the comment is code the
        // author is showing, not a comment to strip. Free today, pinned so a future slice
        // cannot regress it.
        ConfluenceStorageConverter.Convert("```html\n<!-- shown -->\n```")
            .ShouldContain("<![CDATA[<!-- shown -->]]>");
    }

    [Theory]
    [InlineData("plantuml")]
    [InlineData("puml")]
    [InlineData("graphviz")]
    [InlineData("dot")]
    [InlineData("d2")]
    public void Convert_throws_on_a_diagram_dialect_it_cannot_render(string language)
    {
        // GitHub/GitLab render these as pictures, so degrading them to source text
        // loses the meaning, not just the styling. Mermaid is the only dialect
        // DocuMe has a render path for (PLAN.md §4).
        var ex = Should.Throw<NotSupportedException>(
            () => ConfluenceStorageConverter.Convert($"```{language}\na -> b\n```"));
        ex.Message.ShouldContain("diagram dialect DocuMe cannot render");
    }

    [Fact]
    public void Convert_degrades_an_unknown_programming_language_to_an_unlabelled_code_macro()
    {
        // The deliberate asymmetry against the diagram dialects above: an unmapped
        // language costs syntax highlighting and preserves every character, so
        // failing the page over it would buy nothing. Pinned so the decision cannot
        // drift by accident (it is what MapLanguage's null return means).
        ConfluenceStorageConverter.Convert("```rust\nlet x = 1;\n```").ShouldBe(
            "<ac:structured-macro ac:name=\"code\"><ac:plain-text-body><![CDATA[let x = 1;]]>"
            + "</ac:plain-text-body></ac:structured-macro>\n");
    }

    [Fact]
    public void Convert_throws_on_relative_non_md_link()
    {
        // §7 maps relative .md links and external URLs only; a link to a source
        // file or directory is unsupported and must surface, not be guessed at.
        var ex = Should.Throw<NotSupportedException>(
            () => ConfluenceStorageConverter.Convert("See [the service](../src/LoanService.cs)."));
        ex.Message.ShouldContain("neither a '.md' page link nor an external URL");
    }

    [Fact]
    public void Convert_throws_on_relative_md_link_without_a_resolver()
    {
        // A relative page link can only render if a resolver supplies the target
        // title; without one the converter must fail loud, not emit a broken link.
        var ex = Should.Throw<InvalidOperationException>(
            () => ConfluenceStorageConverter.Convert("See [Loans](domains/loans/README.md)."));
        ex.Message.ShouldContain("requires a page-link resolver");
    }

    [Fact]
    public void Convert_throws_when_a_relative_link_target_does_not_resolve()
    {
        // A resolver that returns null means the cross-reference is broken; that
        // must surface rather than silently producing an empty content-title.
        var ex = Should.Throw<InvalidOperationException>(
            () => ConfluenceStorageConverter.Convert("See [Gone](missing/page.md).", _ => null));
        ex.Message.ShouldContain("broken cross-reference");
    }

    [Fact]
    public void Convert_throws_on_a_page_link_title_rather_than_dropping_it()
    {
        // <ac:link> has no representable tooltip, so a title on an internal .md link
        // must fail loud rather than silently vanish (external <a> keeps its title).
        var ex = Should.Throw<NotSupportedException>(
            () => ConfluenceStorageConverter.Convert("See [Loans](loans.md \"tip\").", _ => "Loans"));
        ex.Message.ShouldContain("title tooltip");
    }

    [Theory]

    // §7's mapping (mark's): NOTE→info, TIP→tip, IMPORTANT→info, WARNING→note,
    // CAUTION→warning. NOTE and IMPORTANT sharing `info` is a spec'd accepted loss.
    [InlineData("> [!NOTE]\n> Heads up.", "info")]
    [InlineData("> [!TIP]\n> Heads up.", "tip")]
    [InlineData("> [!IMPORTANT]\n> Heads up.", "info")]
    [InlineData("> [!WARNING]\n> Heads up.", "note")]
    [InlineData("> [!CAUTION]\n> Heads up.", "warning")]

    // GitHub matches the keyword case-insensitively.
    [InlineData("> [!note]\n> Heads up.", "info")]
    public void Convert_renders_a_root_github_alert_as_a_panel_macro(string markdown, string macro)
    {
        // Whole-output equality also pins the marker-line consumption: the body is
        // the author's text only, with no leftover "[!NOTE]" and no leading space
        // from the line break that ended the marker line.
        ConfluenceStorageConverter.Convert(markdown).ShouldBe(
            $"<ac:structured-macro ac:name=\"{macro}\"><ac:parameter ac:name=\"icon\">true</ac:parameter>"
            + "<ac:rich-text-body>\n<p>Heads up.</p>\n</ac:rich-text-body></ac:structured-macro>\n");
    }

    [Fact]
    public void Convert_drops_the_marker_paragraph_when_the_marker_stands_alone()
    {
        // A blank quote line makes the marker its own paragraph rather than the first
        // line of the body's; it must be removed outright, not left as an empty <p>.
        ConfluenceStorageConverter.Convert("> [!NOTE]\n>\n> Heads up.").ShouldBe(
            "<ac:structured-macro ac:name=\"info\"><ac:parameter ac:name=\"icon\">true</ac:parameter>"
            + "<ac:rich-text-body>\n<p>Heads up.</p>\n</ac:rich-text-body></ac:structured-macro>\n");
    }

    [Fact]
    public void Convert_renders_an_alert_with_no_body_as_an_empty_panel()
    {
        // Degenerate but faithful — the author wrote an empty alert and GitHub renders
        // an empty callout. Pinned so the shape is a deliberate choice rather than a
        // surprise the first time M2 posts one to Confluence.
        ConfluenceStorageConverter.Convert("> [!NOTE]").ShouldBe(
            "<ac:structured-macro ac:name=\"info\"><ac:parameter ac:name=\"icon\">true</ac:parameter>"
            + "<ac:rich-text-body>\n</ac:rich-text-body></ac:structured-macro>\n");
    }

    [Theory]

    // Not one of GitHub's five keywords.
    [InlineData("> [!FOO]\n> Heads up.")]

    // GitHub requires the marker to occupy its whole first line, so this is a quote
    // whose text happens to start with brackets — converting it would invent a panel.
    [InlineData("> [!NOTE] Heads up.")]

    // The marker is emphasized, not bare, so the first line is not a marker at all.
    [InlineData("> **[!NOTE]**\n> Heads up.")]
    public void Convert_renders_a_quote_that_is_not_a_bare_alert_marker_as_a_blockquote(string markdown)
    {
        var storage = ConfluenceStorageConverter.Convert(markdown);

        storage.ShouldStartWith("<blockquote>");
        storage.ShouldNotContain("ac:structured-macro");
    }

    [Fact]
    public void Convert_renders_a_nested_alert_as_a_plain_blockquote()
    {
        // §7: only a root-level alert becomes a panel. GitHub does not recognize an
        // alert nested in another blockquote either, so its marker text stays visible
        // instead of being consumed as a panel type.
        var storage = ConfluenceStorageConverter.Convert("> [!NOTE]\n> Outer.\n>\n> > [!WARNING]\n> > Inner.");

        storage.ShouldContain("<blockquote>\n<p>[!WARNING] Inner.</p>\n</blockquote>");
        storage.ShouldContain("ac:name=\"info\"");
        storage.ShouldNotContain("ac:name=\"note\"");
    }

    [Fact]
    public void Convert_renders_an_alert_in_a_tight_list_item_without_leaking_the_implicit_paragraph()
    {
        // A tight list sets ImplicitParagraph for its items; a panel body always wraps
        // block content, so the renderer must reset and restore the flag or the body
        // loses its <p>. Same nesting bug class that bit the blockquote in M1 slice 2.
        var storage = ConfluenceStorageConverter.Convert("- item with an alert\n  > [!CAUTION]\n  > Careful.\n- plain item\n");

        storage.ShouldBe(
            "<ul>\n<li>item with an alert<ac:structured-macro ac:name=\"warning\">"
            + "<ac:parameter ac:name=\"icon\">true</ac:parameter><ac:rich-text-body>\n"
            + "<p>Careful.</p>\n</ac:rich-text-body></ac:structured-macro>\n</li>\n"
            + "<li>plain item</li>\n</ul>\n");
    }

    [Fact]
    public void Convert_keeps_the_code_macro_well_formed_when_the_body_contains_the_cdata_terminator()
    {
        // A literal CDATA terminator inside the code would prematurely close the
        // section, so it must be split to keep the storage fragment parseable XML.
        var storage = ConfluenceStorageConverter.Convert("```\na]]>b\n```");

        storage.ShouldContain("<![CDATA[a]]]]><![CDATA[>b]]>");
        storage.ShouldNotContain("[CDATA[a]]>b");
    }

    [Theory]

    // Enabling the pipe-table extension makes '|' significant during inline
    // parsing, so a paragraph that merely contains pipes must still round-trip as
    // text: Markdig turns the unused delimiters back into literals, and if one
    // ever leaked through it would hit the fail-loud catch-all and fail the page.
    [InlineData("Run `ls | wc -l` to count.", "Run <code>ls | wc -l</code> to count.")]
    [InlineData("Either a | b applies.", "Either a | b applies.")]

    // No header separator (`|---|`), so this is not a table — RequireHeaderSeparator
    // defaults to true, keeping GFM semantics.
    [InlineData("| a | b |", "| a | b |")]
    public void Convert_keeps_pipes_in_a_non_table_paragraph_as_text(string markdown, string expectedInner)
    {
        ConfluenceStorageConverter.Convert(markdown).ShouldBe($"<p>{expectedInner}</p>\n");
    }

    [Fact]
    public void Convert_renders_a_table_nested_in_a_tight_list_item_without_leaking_the_implicit_paragraph()
    {
        // A tight list sets ImplicitParagraph for its items; the table's cells must
        // restore it per cell so the shape is identical to a top-level table. This
        // is the nesting class of bug that bit the blockquote renderer in M1 slice 2.
        var storage = ConfluenceStorageConverter.Convert("- Codes:\n\n  | A | B |\n  |---|---|\n  | 1 | 2 |\n");

        storage.ShouldContain("<tr>\n<th>A</th>\n<th>B</th>\n</tr>");
        storage.ShouldContain("<tr>\n<td>1</td>\n<td>2</td>\n</tr>");
        storage.ShouldNotContain("<th><p>");
        storage.ShouldNotContain("<td><p>");
    }

    [Fact]
    public void Convert_leaves_a_pipe_inside_a_fenced_code_block_alone()
    {
        // Shell pipelines in code samples are everywhere in this wiki; block-level
        // parsing claims the fence before the table parser sees the '|', but pin it
        // so enabling a future inline extension cannot quietly turn code into a table.
        var storage = ConfluenceStorageConverter.Convert("```sh\nls | wc -l\n```\n");

        storage.ShouldContain("<![CDATA[ls | wc -l]]>");
        storage.ShouldNotContain("<table>");
    }

    [Fact]
    public void Convert_renders_a_header_only_table()
    {
        // A header row with no body rows is still a valid table; it must not emit an
        // empty <tbody> pair or trip the fail-loud catch-all.
        var storage = ConfluenceStorageConverter.Convert("| A | B |\n|---|---|\n");

        storage.ShouldBe("<table>\n<tbody>\n<tr>\n<th>A</th>\n<th>B</th>\n</tr>\n</tbody>\n</table>\n");
    }

    [Fact]
    public void Convert_lets_a_table_interrupt_a_paragraph()
    {
        // KNOWN DIVERGENCE from GitHub: GFM requires a table's header to start its
        // own block, so GitHub renders this as one paragraph of literal pipes, while
        // Markdig splits the paragraph and builds the table. Markdig's reading is
        // what the author meant, so it is kept rather than worked around — but it is
        // pinned here so the difference is asserted instead of discovered on a page.
        var storage = ConfluenceStorageConverter.Convert("Some text\n| a | b |\n|---|---|\n| 1 | 2 |\n");

        storage.ShouldBe(
            "<p>Some text </p>\n<table>\n<tbody>\n<tr>\n<th>a</th>\n<th>b</th>\n</tr>\n"
            + "<tr>\n<td>1</td>\n<td>2</td>\n</tr>\n</tbody>\n</table>\n");
    }

    [Fact]
    public void Convert_drops_column_alignment_rather_than_emitting_a_style_attribute()
    {
        // §7 accepted loss: storage format has no per-column alignment. Markdig's
        // own HTML renderer would emit style="text-align: …"; ours must not, since
        // Confluence would not honor it anyway.
        var storage = ConfluenceStorageConverter.Convert("| A | B |\n|:-:|--:|\n| 1 | 2 |\n");

        storage.ShouldNotContain("text-align");
        storage.ShouldNotContain("style=");
        storage.ShouldNotContain("<col");
    }

    [Theory]

    // A language alone stays exactly as it was before fence attributes existed.
    [InlineData("cs", "<ac:parameter ac:name=\"language\">csharp</ac:parameter>")]

    // Each attribute emits its parameter, in mark's ac:code order (language,
    // collapse, linenumbers, firstline, title).
    [InlineData("cs collapse", "<ac:parameter ac:name=\"language\">csharp</ac:parameter><ac:parameter ac:name=\"collapse\">true</ac:parameter>")]
    [InlineData("cs linenumbers", "<ac:parameter ac:name=\"language\">csharp</ac:parameter><ac:parameter ac:name=\"linenumbers\">true</ac:parameter>")]

    // A bare number is firstline, and implies line numbering (mark's README).
    [InlineData("cs 5", "<ac:parameter ac:name=\"language\">csharp</ac:parameter><ac:parameter ac:name=\"linenumbers\">true</ac:parameter><ac:parameter ac:name=\"firstline\">5</ac:parameter>")]

    // An off switch, and a repeat, both resolve to "not requested" => omitted.
    [InlineData("cs nocollapse", "<ac:parameter ac:name=\"language\">csharp</ac:parameter>")]
    [InlineData("cs collapse nocollapse", "<ac:parameter ac:name=\"language\">csharp</ac:parameter>")]

    // `-` is mark's "no language, but I want the attributes" placeholder, and an
    // attribute in first position means the same thing.
    [InlineData("- collapse", "<ac:parameter ac:name=\"collapse\">true</ac:parameter>")]
    [InlineData("collapse", "<ac:parameter ac:name=\"collapse\">true</ac:parameter>")]
    [InlineData("linenumbers", "<ac:parameter ac:name=\"linenumbers\">true</ac:parameter>")]
    [InlineData("7", "<ac:parameter ac:name=\"linenumbers\">true</ac:parameter><ac:parameter ac:name=\"firstline\">7</ac:parameter>")]

    // The title is the rest of the line, unquoted — mark's syntax, and it keeps its
    // internal spacing. A title in first position needs no language either.
    [InlineData("cs title Program.cs", "<ac:parameter ac:name=\"language\">csharp</ac:parameter><ac:parameter ac:name=\"title\">Program.cs</ac:parameter>")]
    [InlineData("cs title A b c", "<ac:parameter ac:name=\"language\">csharp</ac:parameter><ac:parameter ac:name=\"title\">A b c</ac:parameter>")]
    [InlineData("title A b c", "<ac:parameter ac:name=\"title\">A b c</ac:parameter>")]

    // Keywords are matched case-insensitively, unlike mark — which only ever widens
    // what is understood, and never silently misreads a token.
    [InlineData("cs COLLAPSE TITLE Foo", "<ac:parameter ac:name=\"language\">csharp</ac:parameter><ac:parameter ac:name=\"collapse\">true</ac:parameter><ac:parameter ac:name=\"title\">Foo</ac:parameter>")]

    // An unknown language still omits the parameter without throwing (fail-safe),
    // even when attributes follow it.
    [InlineData("brainfuck collapse", "<ac:parameter ac:name=\"collapse\">true</ac:parameter>")]
    public void Convert_maps_code_fence_attributes_to_macro_parameters(string infoLine, string expectedParameters)
    {
        var storage = ConfluenceStorageConverter.Convert($"```{infoLine}\nbody\n```\n");

        storage.ShouldBe(
            $"<ac:structured-macro ac:name=\"code\">{expectedParameters}"
            + "<ac:plain-text-body><![CDATA[body]]></ac:plain-text-body></ac:structured-macro>\n");
    }

    [Fact]
    public void Convert_escapes_a_code_fence_title_as_element_content()
    {
        // The title is author text on the fence line, so it can carry XML specials.
        var storage = ConfluenceStorageConverter.Convert("```sh title Pipe <in> & out\nbody\n```\n");

        storage.ShouldContain("<ac:parameter ac:name=\"title\">Pipe &lt;in&gt; &amp; out</ac:parameter>");
    }

    [Theory]

    // The whole point of the slice: an attribute this converter does not understand
    // must fail the page rather than vanish from it.
    [InlineData("cs mysteryflag")]
    [InlineData("cs colapse")]

    // PLAN.md §7's illustrative `title=Foo` spelling is NOT mark's syntax, and mark
    // itself would silently turn it into a bogus `theme` parameter. Fail instead.
    [InlineData("cs title=Foo")]
    [InlineData("cs collapse=true")]

    // A theme name is a Confluence Server concept the Cloud macro no longer
    // documents; accepting arbitrary tokens as themes is exactly what makes mark
    // swallow the typos above.
    [InlineData("cs midnight")]

    // `title` with nothing after it asks for a title we cannot produce.
    [InlineData("cs title")]
    [InlineData("cs collapse title")]

    // Confluence numbers lines from 1, so 0 and negatives are not firstline values.
    [InlineData("cs 0")]
    [InlineData("cs -1")]

    // Other toolchains spell fence attributes differently (Docusaurus/VitePress
    // quoted titles, Prism line-range highlighting). DocuMe speaks mark's syntax
    // only, and says so loudly instead of half-honoring a foreign dialect — note
    // that the quoted forms never even reach the title branch, since `title="x"`
    // tokenizes as one unknown word rather than the bare `title` keyword.
    [InlineData("js {1,3-4}")]
    [InlineData("bash title=\"Deploy\"")]
    [InlineData("bash title=\"Deploy to prod\"")]
    public void Convert_fails_loud_on_an_unsupported_code_fence_attribute(string infoLine)
    {
        Should.Throw<NotSupportedException>(() => ConfluenceStorageConverter.Convert($"```{infoLine}\nbody\n```\n"));
    }

    [Fact]
    public void Convert_does_not_throw_for_an_unknown_language_the_way_it_does_for_an_unknown_attribute()
    {
        // The asymmetry is deliberate and worth pinning: an unmapped language costs
        // only syntax highlighting, while a dropped attribute would publish
        // something the author did not ask for.
        ConfluenceStorageConverter.Convert("```brainfuck\nbody\n```\n")
            .ShouldBe("<ac:structured-macro ac:name=\"code\"><ac:plain-text-body><![CDATA[body]]></ac:plain-text-body></ac:structured-macro>\n");

        Should.Throw<NotSupportedException>(() => ConfluenceStorageConverter.Convert("```brainfuck mysteryflag\nbody\n```\n"));
    }

    [Fact]
    public void Convert_does_not_read_the_code_body_as_fence_attributes()
    {
        // Only the info line carries attributes; a body line that happens to read
        // like one must stay code.
        var storage = ConfluenceStorageConverter.Convert("```sh\ntitle Not A Title\ncollapse\n```\n");

        storage.ShouldBe(
            "<ac:structured-macro ac:name=\"code\"><ac:parameter ac:name=\"language\">bash</ac:parameter>"
            + "<ac:plain-text-body><![CDATA[title Not A Title\ncollapse]]></ac:plain-text-body></ac:structured-macro>\n");
    }

    [Theory]

    // Confluence's own status strings, and GitHub's case-insensitive `[X]`.
    [InlineData("- [x] done", "complete")]
    [InlineData("- [X] done", "complete")]
    [InlineData("- [ ] done", "incomplete")]
    public void Convert_renders_an_all_task_list_as_a_confluence_task_list(string markdown, string status)
    {
        // Whole-output equality also pins marker consumption: the body is the author's
        // text with no leftover "[x]" and no leading space — Markdig leaves the
        // marker's separating space on the following literal, unlike goldmark/mark.
        ConfluenceStorageConverter.Convert(markdown).ShouldBe(
            "<ac:task-list>\n<ac:task>\n<ac:task-id>1</ac:task-id>\n"
            + $"<ac:task-status>{status}</ac:task-status>\n"
            + "<ac:task-body>done</ac:task-body>\n</ac:task>\n</ac:task-list>\n");
    }

    [Theory]
    [InlineData("- see [x](https://example.com) here", "see <a href=\"https://example.com\">x</a> here")]
    [InlineData("- see [ ](https://example.com) here", "see <a href=\"https://example.com\"> </a> here")]
    public void Convert_keeps_a_bracketed_link_in_a_list_item_a_link(string markdown, string expectedInner)
    {
        // The regression this slice's custom parser exists to prevent. Markdig's stock
        // TaskListInlineParser matches [x]/[ ] anywhere inside a list item and is
        // installed ahead of the link parser, so it eats the label of [x](url) and
        // leaves "(url)" as text — a silently broken link. GfmTaskListInlineParser
        // requires the marker to open the item, which is also GitHub's rule.
        ConfluenceStorageConverter.Convert(markdown).ShouldBe($"<ul>\n<li>{expectedInner}</li>\n</ul>\n");
    }

    [Theory]

    // Mid-item text, and a marker protected by a code span (a wiki documenting the
    // task-list syntax itself). Both render exactly as GitHub renders them.
    [InlineData("- mark the box [x] before signing", "mark the box [x] before signing")]
    [InlineData("- `[x]` means done", "<code>[x]</code> means done")]
    public void Convert_keeps_a_marker_that_does_not_open_the_item_as_text(string markdown, string expectedInner)
    {
        ConfluenceStorageConverter.Convert(markdown).ShouldBe($"<ul>\n<li>{expectedInner}</li>\n</ul>\n");
    }

    [Fact]
    public void Convert_does_not_treat_a_marker_opening_a_later_paragraph_as_a_task()
    {
        // GFM recognizes a marker only where it opens the item, meaning the first
        // paragraph; Markdig's stock parser accepts any paragraph in the item.
        ConfluenceStorageConverter.Convert("- first\n\n  [x] not a marker\n").ShouldBe(
            "<ul>\n<li><p>first</p>\n<p>[x] not a marker</p>\n</li>\n</ul>\n");
    }

    [Fact]
    public void Convert_leaves_a_bracketed_x_outside_a_list_alone()
    {
        // Enabling the extension must not make "[x]" significant in ordinary prose.
        ConfluenceStorageConverter.Convert("Tick [x] in the form.").ShouldBe("<p>Tick [x] in the form.</p>\n");
    }

    [Fact]
    public void Convert_keeps_the_paragraph_in_a_loose_task_list_body()
    {
        // Looseness is honored exactly as for a plain list, so a task body is bare
        // inline content when tight and keeps its <p> when loose.
        ConfluenceStorageConverter.Convert("- [x] done\n\n- [ ] todo\n").ShouldBe(
            "<ac:task-list>\n<ac:task>\n<ac:task-id>1</ac:task-id>\n<ac:task-status>complete</ac:task-status>\n"
            + "<ac:task-body><p>done</p>\n</ac:task-body>\n</ac:task>\n"
            + "<ac:task>\n<ac:task-id>2</ac:task-id>\n<ac:task-status>incomplete</ac:task-status>\n"
            + "<ac:task-body><p>todo</p>\n</ac:task-body>\n</ac:task>\n</ac:task-list>\n");
    }

    [Fact]
    public void Convert_numbers_task_ids_uniquely_per_page_and_identically_on_every_render()
    {
        // Confluence tracks task completion by id, so two lists on one page must not
        // repeat ids (mark's per-list counter does). Re-rendering the same source must
        // also produce the same ids, or the content hash (§8) would churn — hence a
        // counter on the per-call renderer rather than a static one.
        const string markdown = "- [x] a\n\ntext\n\n- [ ] b\n";

        var first = ConfluenceStorageConverter.Convert(markdown);

        first.ShouldContain("<ac:task-id>1</ac:task-id>");
        first.ShouldContain("<ac:task-id>2</ac:task-id>");
        ConfluenceStorageConverter.Convert(markdown).ShouldBe(first);
    }

    [Fact]
    public void Convert_renders_a_task_list_nested_in_a_tight_list_item()
    {
        // The nesting-bug class from M1 slice 2: a tight outer item sets
        // ImplicitParagraph, and the task body must honor its own list's looseness
        // instead of inheriting the outer item's.
        ConfluenceStorageConverter.Convert("- steps\n  - [x] done\n").ShouldBe(
            "<ul>\n<li>steps<ac:task-list>\n<ac:task>\n<ac:task-id>1</ac:task-id>\n"
            + "<ac:task-status>complete</ac:task-status>\n<ac:task-body>done</ac:task-body>\n"
            + "</ac:task>\n</ac:task-list>\n</li>\n</ul>\n");
    }

    [Fact]
    public void Convert_renders_an_ordered_task_list_as_a_task_list()
    {
        // Storage format has no numbered task list, so ordering is dropped rather
        // than emitting <ol> with the markers demoted to text.
        var storage = ConfluenceStorageConverter.Convert("1. [x] done\n2. [ ] todo\n");

        storage.ShouldStartWith("<ac:task-list>");
        storage.ShouldNotContain("<ol>");
    }

    [Theory]
    [InlineData("~~gone~~", "<p><span style=\"text-decoration: line-through;\">gone</span></p>\n")]
    [InlineData("a ~~b~~ c", "<p>a <span style=\"text-decoration: line-through;\">b</span> c</p>\n")]
    [InlineData(
        "~~a~~ and ~~b~~",
        "<p><span style=\"text-decoration: line-through;\">a</span> and <span style=\"text-decoration: line-through;\">b</span></p>\n")]
    public void Convert_renders_strikethrough_as_a_line_through_span(string markdown, string expected)
    {
        // Atlassian's documented spelling and the shape the Confluence editor itself
        // emits — deliberately not mark's <del>, which Confluence would rewrite on
        // save and so churn the approval content hash (§8) on every republish.
        ConfluenceStorageConverter.Convert(markdown).ShouldBe(expected);
    }

    [Theory]
    [InlineData("~x~", "<p>~x~</p>\n")]
    [InlineData("^sup^", "<p>^sup^</p>\n")]
    [InlineData("++ins++", "<p>++ins++</p>\n")]
    [InlineData("==mark==", "<p>==mark==</p>\n")]
    [InlineData("approx ~5 minutes and ~10 more", "<p>approx ~5 minutes and ~10 more</p>\n")]
    [InlineData("cd ~/projects then ~/tmp", "<p>cd ~/projects then ~/tmp</p>\n")]
    public void Convert_leaves_the_other_emphasis_extras_as_literal_text(string markdown, string expected)
    {
        // THE regression pin for passing EmphasisExtraOptions.Strikethrough explicitly.
        // Markdig's argless UseEmphasisExtras() defaults to all five extras, which would
        // make each of these an EmphasisInline with no storage-format mapping — so a bare
        // '^' or '~' anywhere in a service wiki's prose would fail the whole page.
        ConfluenceStorageConverter.Convert(markdown).ShouldBe(expected);
    }

    [Theory]
    [InlineData(
        "~~struck **bold**~~",
        "<p><span style=\"text-decoration: line-through;\">struck <strong>bold</strong></span></p>\n")]
    [InlineData(
        "**bold ~~struck~~**",
        "<p><strong>bold <span style=\"text-decoration: line-through;\">struck</span></strong></p>\n")]
    [InlineData(
        "*italic ~~struck~~*",
        "<p><em>italic <span style=\"text-decoration: line-through;\">struck</span></em></p>\n")]
    public void Convert_nests_strikethrough_with_the_other_emphasis(string markdown, string expected)
    {
        ConfluenceStorageConverter.Convert(markdown).ShouldBe(expected);
    }

    [Fact]
    public void Convert_escapes_markup_characters_inside_struck_text()
    {
        ConfluenceStorageConverter.Convert("~~AT&T > B~~")
            .ShouldBe("<p><span style=\"text-decoration: line-through;\">AT&amp;T &gt; B</span></p>\n");
    }

    [Fact]
    public void Convert_treats_a_triple_tilde_as_a_code_fence_not_strikethrough()
    {
        // '~~~' is CommonMark's other fence marker. Enabling '~' as an emphasis
        // delimiter must not shadow it, or every tilde-fenced sample on a page
        // would be re-read as prose.
        var storage = ConfluenceStorageConverter.Convert("~~~\nplain fence\n~~~\n");

        storage.ShouldStartWith("<ac:structured-macro ac:name=\"code\">");
        storage.ShouldContain("plain fence");
        storage.ShouldNotContain("line-through");
    }

    [Fact]
    public void Convert_does_not_strike_a_tilde_pair_inside_a_code_span()
    {
        ConfluenceStorageConverter.Convert("`~~kept~~`").ShouldBe("<p><code>~~kept~~</code></p>\n");
    }

    [Fact]
    public void Convert_leaves_an_unmatched_tilde_pair_as_literal_text()
    {
        // '~' allows intra-word emphasis, so an opener with no closer must degrade
        // to text rather than swallowing the rest of the paragraph.
        ConfluenceStorageConverter.Convert("range 5~~10 units").ShouldBe("<p>range 5~~10 units</p>\n");
    }

    [Fact]
    public void Render_fails_loud_on_an_emphasis_delimiter_it_has_no_mapping_for()
    {
        // Unreachable through the converter's own pipeline, so the guard is asserted
        // against a hand-built tree: if a later pipeline ever enables Markdig's other
        // emphasis extras, '^sup^' must fail loudly instead of rendering as <em>.
        var document = new MarkdownDocument();
        var paragraph = new ParagraphBlock { Inline = new ContainerInline() };
        var emphasis = new EmphasisInline { DelimiterChar = '^', DelimiterCount = 1 };
        emphasis.AppendChild(new LiteralInline("sup"));
        paragraph.Inline.AppendChild(emphasis);
        document.Add(paragraph);

        using var writer = new StringWriter();
        var renderer = new ConfluenceStorageRenderer(writer);

        var ex = Should.Throw<NotSupportedException>(() => renderer.Render(document));
        ex.Message.ShouldContain("emphasis delimiter '^'");
    }

    [Theory]
    [InlineData("[TOC]")]
    [InlineData("[TOC]\n")]
    [InlineData("[TOC] ")] // trailing whitespace is invisible and must not defeat the match
    [InlineData("  [TOC]")] // up to 3 spaces is still a paragraph, not indented code
    [InlineData("   [TOC]")]
    public void Convert_renders_a_root_level_toc_marker_as_the_toc_macro(string markdown)
    {
        ConfluenceStorageConverter.Convert(markdown)
            .ShouldBe("<ac:structured-macro ac:name=\"toc\"></ac:structured-macro>\n");
    }

    [Fact]
    public void Convert_renders_the_toc_macro_in_document_order_among_other_blocks()
    {
        ConfluenceStorageConverter.Convert("[TOC]\n\n## Section\n\nBody.")
            .ShouldBe("<ac:structured-macro ac:name=\"toc\"></ac:structured-macro>\n<h2>Section</h2>\n<p>Body.</p>\n");
    }

    [Fact]
    public void Convert_renders_every_toc_marker_on_a_page_and_does_so_stably()
    {
        // No counter or other per-page state backs the macro, so re-rendering the same
        // source must be byte-identical (§8: the approval content hash must not churn).
        const string markdown = "[TOC]\n\n## A\n\n[TOC]";
        const string expected = "<ac:structured-macro ac:name=\"toc\"></ac:structured-macro>\n"
            + "<h2>A</h2>\n"
            + "<ac:structured-macro ac:name=\"toc\"></ac:structured-macro>\n";

        ConfluenceStorageConverter.Convert(markdown).ShouldBe(expected);
        ConfluenceStorageConverter.Convert(markdown).ShouldBe(expected);
    }

    [Fact]
    public void Convert_keeps_an_escaped_toc_marker_as_literal_text()
    {
        // THE discriminator this slice turns on. '\[TOC]' is the one spelling an author uses
        // to *prevent* expansion, and it accumulates to exactly the same text as the bare
        // marker — Markdig only distinguishes them by shape (one literal vs. two). Together
        // with Convert_renders_a_root_level_toc_marker_as_the_toc_macro this pins that shape
        // from both sides, so a Markdig upgrade that changed it fails here rather than
        // silently eating author text.
        ConfluenceStorageConverter.Convert("\\[TOC]").ShouldBe("<p>[TOC]</p>\n");
    }

    [Theory]
    [InlineData("[toc]", "[toc]")] // the match is case-sensitive by design
    [InlineData("[Toc]", "[Toc]")]
    [InlineData("[TOC] and more", "[TOC] and more")]
    [InlineData("leading [TOC]", "leading [TOC]")]
    [InlineData("[[TOC]]", "[[TOC]]")]
    [InlineData("**[TOC]**", "<strong>[TOC]</strong>")]
    [InlineData("`[TOC]`", "<code>[TOC]</code>")]
    [InlineData("[TOC]\n[TOC]", "[TOC] [TOC]")] // two on one paragraph via a soft break
    public void Convert_keeps_a_toc_marker_that_is_not_alone_on_its_line_as_text(string markdown, string expectedInner)
    {
        ConfluenceStorageConverter.Convert(markdown).ShouldBe($"<p>{expectedInner}</p>\n");
    }

    [Theory]
    [InlineData("- [TOC]", "<ul>\n<li>[TOC]</li>\n</ul>\n")]
    [InlineData("> [TOC]", "<blockquote>\n<p>[TOC]</p>\n</blockquote>\n")]
    [InlineData(
        "| a |\n|---|\n| [TOC] |",
        "<table>\n<tbody>\n<tr>\n<th>a</th>\n</tr>\n<tr>\n<td>[TOC]</td>\n</tr>\n</tbody>\n</table>\n")]
    public void Convert_keeps_a_nested_toc_marker_as_text(string markdown, string expected)
    {
        // Root-level only: a TOC is a page-level construct, and a marker inside a list item,
        // quote or table cell is far more likely prose about the syntax. Degrading to visible
        // text (rather than failing loud) keeps a page that documents the syntax publishable.
        ConfluenceStorageConverter.Convert(markdown).ShouldBe(expected);
    }

    [Fact]
    public void Convert_leaves_a_toc_marker_inside_a_fenced_code_block_alone()
    {
        ConfluenceStorageConverter.Convert("```\n[TOC]\n```")
            .ShouldBe("<ac:structured-macro ac:name=\"code\">"
                + "<ac:plain-text-body><![CDATA[[TOC]]]></ac:plain-text-body></ac:structured-macro>\n");
    }

    [Fact]
    public void Convert_keeps_a_toc_marker_backed_by_a_reference_definition_as_a_link()
    {
        // The author defined a real link target, so honoring it beats overriding them.
        ConfluenceStorageConverter.Convert("[TOC]: https://example.com/toc\n\n[TOC]")
            .ShouldBe("<p><a href=\"https://example.com/toc\">TOC</a></p>\n");
    }

    [Fact]
    public void Convert_fails_loud_on_an_indented_toc_marker()
    {
        // 4 spaces makes it an indented code block, which the converter does not support.
        // Pinned because indenting the marker is a realistic authoring mistake.
        var ex = Should.Throw<NotSupportedException>(() => ConfluenceStorageConverter.Convert("    [TOC]"));
        ex.Message.ShouldContain("CodeBlock");
    }

    [Fact]
    public void Convert_fails_loud_on_two_adjacent_toc_markers()
    {
        // '[TOC][TOC]' parses as an unresolved reference with a label, leaving a
        // LinkDelimiterInline in the tree. Pinned so the behavior is known here rather
        // than discovered on a live page.
        Should.Throw<NotSupportedException>(() => ConfluenceStorageConverter.Convert("[TOC][TOC]"));
    }

    [Fact]
    public void Convert_empty_input_produces_empty_output()
    {
        ConfluenceStorageConverter.Convert(string.Empty).ShouldBe(string.Empty);
    }
}
