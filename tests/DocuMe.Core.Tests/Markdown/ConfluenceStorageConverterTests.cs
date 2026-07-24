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
    [InlineData("    indented code")]     // CodeBlock (indented — only fenced is supported)
    [InlineData("<div>raw html</div>")]   // HtmlBlock
    public void Convert_throws_on_unsupported_block_construct(string markdown)
    {
        // Every construct above lacks a dedicated renderer, so the catch-all must
        // throw rather than silently drop or mis-transform it.
        var ex = Should.Throw<NotSupportedException>(() => ConfluenceStorageConverter.Convert(markdown));
        ex.Message.ShouldContain("No storage-format renderer");
    }

    [Fact]
    public void Convert_throws_on_unsupported_inline_link()
    {
        // Links are a later M1 slice; until then they must fail loudly, not
        // silently lose their href/text.
        var ex = Should.Throw<NotSupportedException>(
            () => ConfluenceStorageConverter.Convert("See [the docs](https://example.com)."));
        ex.Message.ShouldContain("No storage-format renderer");
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
        // A literal ]]> inside the code would prematurely close the CDATA section;
        // it must be split so the storage fragment stays parseable XML.
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
