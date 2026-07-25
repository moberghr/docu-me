using System.Globalization;
using DocuMe.Core.Markdown;

namespace DocuMe.Core.Publishing;

/// <summary>
/// Puts PLAN.md §7's <c>ac:width</c> on the <c>&lt;ac:image&gt;</c> a <c>```mermaid</c> fence rendered
/// to, in the body a publish is about to upload.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is not in the converter.</strong> The width being asked for is the width of the
/// <em>rendered SVG</em>, and the converter never renders: it is a filesystem-free, process-free text
/// transform, which is what makes it deterministic for the §8 content hash and testable with
/// hand-authored goldens (<see cref="MermaidDiagramResolver"/>). Only the publish path ever holds a
/// <see cref="MermaidDiagram"/> and its <see cref="MermaidDiagram.SvgWidth"/>, so only the publish path
/// can honor the attribute.
/// </para>
/// <para>
/// <strong>Why a substitution and not a second conversion.</strong> Re-converting the page with a
/// width-aware resolver would convert every page twice and let the write path re-decide what the plan
/// already decided, which is the invariant that keeps <c>--dry-run</c> honest
/// (<see cref="PublishExecutor"/>). Rendering at plan time is worse: it would start Node inside a dry
/// run. So the one element that needs an attribute gets it, over markup this codebase emitted itself
/// and can therefore match exactly. This is not the md→html→regex shortcut §7 forbids — nothing here
/// parses markdown.
/// </para>
/// <para>
/// <strong>The width is not in <c>contentHash</c>, and that is a trade worth naming.</strong> The hash
/// preimage is the converter's output, taken before the banner and before this (§8, rule §9.2). So a
/// diagram whose dimensions change while its source does not — a <c>beautiful-mermaid</c> upgrade —
/// keeps its published width until <c>--force</c> re-renders it. An ordinary edit to the page does not
/// refresh it either: that run has no reason to re-render an unchanged diagram, so it republishes the
/// remembered width (<see cref="State.PageState.DiagramWidths"/>). The alternative is worse: a width inside
/// the hash would revoke approval on every approved page in the wiki the day the renderer changed its
/// layout, for a change no author made and no reviewer needs to re-read.
/// </para>
/// <para>
/// A diagram whose SVG carries no usable width is left as it was before this existed: bare
/// <c>&lt;ac:image&gt;</c>, which Confluence scales natively. An omitted width beats a fabricated one,
/// the same call <see cref="Markdown.ConfluenceStorageRenderer"/> makes for an image's other
/// attributes.
/// </para>
/// </remarks>
public static class DiagramImageWidth
{
    /// <summary>
    /// Normalizes an SVG root <c>width</c> to the pixel count <c>ac:width</c> takes, or <c>null</c> when
    /// it is not a pixel count at all.
    /// </summary>
    /// <param name="svgWidth">
    /// <see cref="MermaidDiagram.SvgWidth"/> verbatim (e.g. <c>212.64</c>, <c>212.64px</c>), or
    /// <c>null</c>.
    /// </param>
    /// <remarks>
    /// <para>
    /// Rounded up to a whole pixel: Confluence's own editor writes integer widths, a fractional one is
    /// unverified against a real tenant, and rounding <em>up</em> cannot crop a diagram.
    /// </para>
    /// <para>
    /// A relative or unitful width (<c>100%</c>, <c>30em</c>, <c>auto</c>) answers <c>null</c> rather
    /// than being coerced. It is not a pixel count, and putting it in <c>ac:width</c> would be
    /// guessing at what the author's browser did with it.
    /// </para>
    /// </remarks>
    public static string? Pixels(string? svgWidth)
    {
        if (svgWidth is null)
        {
            return null;
        }

        var value = svgWidth.AsSpan().Trim();
        if (value.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^2].TrimEnd();
        }

        var parsed = double.TryParse(
            value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pixels);

        if (!parsed || !double.IsFinite(pixels) || pixels < 1)
        {
            return null;
        }

        return ((long)Math.Ceiling(pixels)).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Returns <paramref name="body"/> with <c>ac:width</c> added to each named diagram's image, or
    /// <paramref name="body"/> itself when there is nothing to add.
    /// </summary>
    /// <param name="body">
    /// The storage format about to be uploaded — <see cref="PlannedPage.UploadBody"/>, banner included.
    /// </param>
    /// <param name="pixelsByAttachment">
    /// Diagram attachment filename → the width to write, already through <see cref="Pixels"/>. Only
    /// diagrams belong here: an image an author placed carries its own width in the markdown (§7's
    /// images row), which is the converter's business, not this one's.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// A named diagram's image is not in the body. That means the attachment set and the body disagree
    /// — a bug in the plan rather than a run-time condition, and one that would otherwise publish a
    /// page silently missing the attribute (<see cref="PublishExecutor"/> throws for the same class of
    /// disagreement over attachment hashes).
    /// </exception>
    public static string Apply(string body, IReadOnlyDictionary<string, string> pixelsByAttachment)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(pixelsByAttachment);

        var widened = body;
        foreach (var (name, pixels) in pixelsByAttachment)
        {
            var image = ImageOf(name);
            if (!widened.Contains(image, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Diagram '{name}' has a width to publish but the page body does not reference it as "
                    + $"{image}. The plan's attachment set and its body disagree.");
            }

            widened = widened.Replace(image, WidenedImageOf(name, pixels), StringComparison.Ordinal);
        }

        return widened;
    }

    /// <summary>
    /// The exact element <see cref="Markdown.ConfluenceStorageRenderer"/> writes for a mermaid fence.
    /// </summary>
    /// <remarks>
    /// Written literally, with no escaping, because a diagram attachment name comes from
    /// <see cref="MermaidAttachmentName.ForSource"/> and nowhere else: <c>mermaid-</c>, sixteen hex
    /// characters, <c>.svg</c>. Nothing in that needs an XML character reference, so escaping it here
    /// would be a second, drifting copy of the converter's escaping rules. If the naming ever grows a
    /// character the converter escapes, <see cref="Apply"/> fails loud on the missing element rather
    /// than publishing a body it silently did not widen.
    /// </remarks>
    private static string ImageOf(string attachmentName) =>
        $"<ac:image><ri:attachment ri:filename=\"{attachmentName}\"/></ac:image>";

    private static string WidenedImageOf(string attachmentName, string pixels) =>
        $"<ac:image ac:width=\"{pixels}\"><ri:attachment ri:filename=\"{attachmentName}\"/></ac:image>";
}
