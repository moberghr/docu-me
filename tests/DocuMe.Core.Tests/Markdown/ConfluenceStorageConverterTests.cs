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
    [InlineData("- one\n- two")]        // ListBlock
    [InlineData("> quoted")]             // QuoteBlock
    [InlineData("```\ncode\n```")]       // FencedCodeBlock
    [InlineData("---")]                  // ThematicBreak
    public void Convert_throws_on_unsupported_block_construct(string markdown)
    {
        // Every construct above lacks a dedicated renderer in the seed, so the
        // catch-all must throw rather than silently drop or mis-transform it.
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

    [Fact]
    public void Convert_empty_input_produces_empty_output()
    {
        ConfluenceStorageConverter.Convert(string.Empty).ShouldBe(string.Empty);
    }
}
