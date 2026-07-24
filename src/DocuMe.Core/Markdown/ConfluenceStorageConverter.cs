using Markdig;
using Markdig.Extensions.Tables;

namespace DocuMe.Core.Markdown;

/// <summary>
/// Converts frontmatter-free wiki markdown to Confluence storage format via
/// <see cref="ConfluenceStorageRenderer"/> (PLAN.md §7). This is the deterministic
/// half of the publish pipeline (§6.2 step 4); its output feeds the content hash
/// used for change detection and approval invalidation (§8, spike S6).
/// </summary>
public static class ConfluenceStorageConverter
{
    // UseYamlFrontMatter keeps a stray leading '---' block parsed as frontmatter
    // (and dropped — no renderer is registered for it) rather than as markup,
    // even though callers normally pass an already-stripped body.
    //
    // UseHeaderForColumnCount gives GFM's ragged-row semantics — short rows are
    // padded with empty cells and cells past the header width are dropped, which
    // is what GitHub shows the author. Markdig's default instead widens *every*
    // row to the widest one, so an over-wide body row would grow the header with
    // a blank <th> that exists in neither the source nor GitHub's rendering.
    //
    // FrontmatterParser.Pipeline must enable the same extensions: an extension
    // changes inline parsing, so a divergence would let the two disagree about
    // where the title's H1 is.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseYamlFrontMatter()
        .UsePipeTables(new PipeTableOptions { UseHeaderForColumnCount = true })
        .Build();

    /// <summary>Renders <paramref name="markdown"/> to a storage-format XHTML fragment.</summary>
    /// <param name="markdown">Frontmatter-free wiki markdown body.</param>
    /// <param name="linkResolver">
    /// Resolves relative <c>.md</c> link targets to Confluence page titles (§7). May be
    /// <c>null</c> when the body has no relative markdown links; if one is encountered
    /// without a resolver the converter fails loud rather than emitting a broken link.
    /// </param>
    public static string Convert(string markdown, PageLinkResolver? linkResolver = null)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var document = Markdig.Markdown.Parse(markdown, Pipeline);
        using var writer = new StringWriter();
        var renderer = new ConfluenceStorageRenderer(writer, linkResolver);
        renderer.Render(document);
        writer.Flush();
        return writer.ToString();
    }
}
