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
    public static string Convert(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var document = Markdig.Markdown.Parse(markdown, Pipeline);
        using var writer = new StringWriter();
        var renderer = new ConfluenceStorageRenderer(writer);
        renderer.Render(document);
        writer.Flush();
        return writer.ToString();
    }
}
