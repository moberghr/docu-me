using DocuMe.Core.Publishing;
using Shouldly;

namespace DocuMe.Core.Tests.Publishing;

/// <summary>
/// §7's <c>ac:width</c> on a mermaid diagram's image: which SVG widths become a pixel count, and the
/// substitution that puts them on the body a publish uploads. Both are pure.
/// </summary>
public sealed class DiagramImageWidthTests
{
    private const string Diagram = "mermaid-3f2a1c9d0b8e7f65.svg";

    [Fact]
    public void Widens_a_diagram_image_and_leaves_every_other_image_alone()
    {
        var body = $"""
            <p>Before.</p>
            <p><ac:image><ri:attachment ri:filename="{Diagram}"/></ac:image></p>
            <p><ac:image ac:alt="Logo"><ri:attachment ri:filename="logo.png"/></ac:image></p>
            <p><ac:image><ri:attachment ri:filename="spacer.png"/></ac:image></p>
            """;

        var widened = DiagramImageWidth.Apply(body, new Dictionary<string, string> { [Diagram] = "213" });

        widened.ShouldContain(
            $"<ac:image ac:width=\"213\"><ri:attachment ri:filename=\"{Diagram}\"/></ac:image>");

        // An author's image is the converter's business (§7's images row), so an attachment nobody named
        // keeps the shape the converter gave it — including the bare one that looks just like a diagram's.
        widened.ShouldContain("<ac:image ac:alt=\"Logo\"><ri:attachment ri:filename=\"logo.png\"/></ac:image>");
        widened.ShouldContain("<ac:image><ri:attachment ri:filename=\"spacer.png\"/></ac:image>");
    }

    /// <summary>
    /// One diagram source, two fences: the pipeline dedups the attachment to one name, so the body carries
    /// the same element twice and both have to be widened. A page showing one image widened and the other
    /// not is the visible half of this bug.
    /// </summary>
    [Fact]
    public void Widens_every_occurrence_of_a_diagram_repeated_on_one_page()
    {
        var image = $"<p><ac:image><ri:attachment ri:filename=\"{Diagram}\"/></ac:image></p>";
        var body = $"{image}\n<p>Between.</p>\n{image}";

        var widened = DiagramImageWidth.Apply(body, new Dictionary<string, string> { [Diagram] = "88" });

        widened.ShouldNotContain($"<ac:image><ri:attachment ri:filename=\"{Diagram}\"/>");
        Occurrences(widened, "ac:width=\"88\"").ShouldBe(2);
    }

    [Fact]
    public void Returns_the_body_untouched_when_no_diagram_has_a_width()
    {
        const string body = "<p>No diagrams here.</p>";

        DiagramImageWidth.Apply(body, new Dictionary<string, string>()).ShouldBeSameAs(body);
    }

    /// <summary>
    /// The plan's attachment set comes from the converter's own walk over this body, so a diagram that has
    /// a width but no image is the two disagreeing — a bug, and one that would otherwise publish a page
    /// quietly missing the attribute.
    /// </summary>
    [Fact]
    public void Throws_when_a_named_diagram_is_not_referenced_by_the_body()
    {
        var thrown = Should.Throw<InvalidOperationException>(() => DiagramImageWidth.Apply(
            "<p>A page that shows no diagram.</p>",
            new Dictionary<string, string> { [Diagram] = "213" }));

        thrown.Message.ShouldContain(Diagram);
        thrown.Message.ShouldContain("disagree");
    }

    /// <summary>
    /// Rounded up to a whole pixel: Confluence's editor writes integers, and rounding up cannot crop a
    /// diagram.
    /// </summary>
    [Theory]
    [InlineData("212.64", "213")]
    [InlineData("212", "212")]
    [InlineData("212.64px", "213")]
    [InlineData("  212.4  ", "213")]
    [InlineData("1.2", "2")]
    [InlineData("1", "1")]
    public void Reads_a_pixel_width_as_a_whole_number_of_pixels(string svgWidth, string expected)
        => DiagramImageWidth.Pixels(svgWidth).ShouldBe(expected);

    /// <summary>
    /// Everything that is not a pixel count answers null, which publishes a bare
    /// <c>&lt;ac:image&gt;</c> — what the body carried before this existed, and what Confluence scales
    /// natively. Coercing a relative width would be guessing at what a browser did with it.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("auto")]
    [InlineData("100%")]
    [InlineData("30em")]
    [InlineData("0")]
    [InlineData("0.4")]
    [InlineData("-212")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("212,64")]
    [InlineData("2e400")]
    public void Refuses_a_width_that_is_not_a_pixel_count(string? svgWidth)
        => DiagramImageWidth.Pixels(svgWidth).ShouldBeNull();

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        var at = text.IndexOf(value, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
