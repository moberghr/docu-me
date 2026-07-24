using DocuMe.Core.Markdown;
using Shouldly;

namespace DocuMe.Core.Tests.Markdown;

/// <summary>
/// The attachment name is a pure function of the diagram source (PLAN.md §8, rule §9.2). These
/// tests exist to stop a future "improvement" — a counter, a timestamp, a per-run salt — from
/// silently churning every page's <c>contentHash</c> and revoking approvals nothing changed.
/// </summary>
public sealed class MermaidAttachmentNameTests
{
    private const string Source = "graph TD\n  A --> B";

    [Fact]
    public void Same_source_always_gets_the_same_name()
    {
        MermaidAttachmentName.ForSource(Source).ShouldBe(MermaidAttachmentName.ForSource(Source));
    }

    [Fact]
    public void Different_sources_get_different_names()
    {
        MermaidAttachmentName.ForSource(Source)
            .ShouldNotBe(MermaidAttachmentName.ForSource("graph TD\n  A --> C"));
    }

    [Fact]
    public void Name_is_hex_hash_under_the_mermaid_prefix_and_the_single_extension()
    {
        var name = MermaidAttachmentName.ForSource(Source);

        // Pinned verbatim: the name lands in the published body, so its exact shape is part of
        // the content hash and cannot drift silently.
        name.ShouldBe("mermaid-1eba6c28fd9d3a91.svg");
        name.ShouldEndWith(MermaidAttachmentName.Extension);
    }

    [Fact]
    public void Line_ending_style_does_not_change_the_name()
    {
        // A Windows checkout of the same wiki must not produce a second attachment for the
        // same diagram — that would be pure hash churn on every page holding a diagram.
        var lf = MermaidAttachmentName.ForSource("graph TD\n  A --> B");
        var crlf = MermaidAttachmentName.ForSource("graph TD\r\n  A --> B");
        var cr = MermaidAttachmentName.ForSource("graph TD\r  A --> B");

        crlf.ShouldBe(lf);
        cr.ShouldBe(lf);
    }

    [Fact]
    public void Surrounding_whitespace_does_not_change_the_name()
    {
        MermaidAttachmentName.ForSource("\n  " + Source + "  \n\n").ShouldBe(
            MermaidAttachmentName.ForSource(Source));
    }

    [Fact]
    public void Interior_whitespace_does_change_the_name()
    {
        // Only the edges are normalized. Indentation inside a diagram is meaningful to
        // mermaid, so two differently-indented diagrams are two different attachments.
        MermaidAttachmentName.ForSource("graph TD\n    A --> B")
            .ShouldNotBe(MermaidAttachmentName.ForSource("graph TD\n  A --> B"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public void Empty_source_is_refused_rather_than_named(string source)
    {
        Should.Throw<ArgumentException>(() => MermaidAttachmentName.ForSource(source));
    }
}
