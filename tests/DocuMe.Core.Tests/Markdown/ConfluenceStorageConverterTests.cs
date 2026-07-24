using DocuMe.Core.Markdown;
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

    // An indented CodeBlock (only fenced code is supported), then an HtmlBlock.
    [InlineData("    indented code")]
    [InlineData("<div>raw html</div>")]
    public void Convert_throws_on_unsupported_block_construct(string markdown)
    {
        // Every construct above lacks a dedicated renderer, so the catch-all must
        // throw rather than silently drop or mis-transform it.
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
    public void Convert_throws_on_image_rather_than_dropping_it()
    {
        // Images (also a LinkInline) map to an attachment + <ac:image> in a later
        // slice; until then they must fail loud, not silently vanish.
        var ex = Should.Throw<NotSupportedException>(
            () => ConfluenceStorageConverter.Convert("![a diagram](diagram.png)"));
        ex.Message.ShouldContain("Image");
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
    [InlineData("> [!NOTE]\n> Heads up.")]
    [InlineData("> [!WARNING]\n> Careful.")]
    [InlineData("> [!note]\n> Lowercase is still an alert on GitHub.")]
    public void Convert_throws_on_github_alert_rather_than_rendering_a_plain_blockquote(string markdown)
    {
        // A GitHub alert parses as a plain blockquote in the default pipeline but
        // §7 maps it to a panel macro (a later slice). It must fail loud, not be
        // silently downgraded to <blockquote>.
        var ex = Should.Throw<NotSupportedException>(() => ConfluenceStorageConverter.Convert(markdown));
        ex.Message.ShouldContain("GitHub alert");
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

    [Fact]
    public void Convert_empty_input_produces_empty_output()
    {
        ConfluenceStorageConverter.Convert(string.Empty).ShouldBe(string.Empty);
    }
}
