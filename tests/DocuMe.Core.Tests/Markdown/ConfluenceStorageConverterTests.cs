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

    [Fact]
    public void Convert_empty_input_produces_empty_output()
    {
        ConfluenceStorageConverter.Convert(string.Empty).ShouldBe(string.Empty);
    }
}
