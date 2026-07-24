using Markdig;

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
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseYamlFrontMatter()
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
