using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Parsers.Inlines;
using Markdig.Renderers;
using Markdig.Syntax;

namespace DocuMe.Core.Markdown;

/// <summary>
/// Enables GFM task lists (<c>- [x] done</c>) using <see cref="GfmTaskListInlineParser"/>
/// instead of Markdig's stock <see cref="TaskListExtension"/>, whose parser is looser
/// than GitHub's. Rendering is wired up by <see cref="ConfluenceStorageRenderer"/>'s
/// own object renderers, so this extension only installs the parser.
/// </summary>
internal sealed class GfmTaskListExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        if (!pipeline.InlineParsers.Contains<GfmTaskListInlineParser>())
        {
            // The same slot Markdig's own TaskListExtension uses: ahead of the link
            // parser, so `[x]` is considered before `[text](url)`.
            pipeline.InlineParsers.InsertBefore<LinkInlineParser>(new GfmTaskListInlineParser());
        }
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        // Intentionally empty — see the type remarks.
    }
}

/// <summary>
/// Narrows Markdig's <see cref="TaskListInlineParser"/> to GitHub's positional rule:
/// a task marker must <em>open the list item</em>, i.e. be the first thing in the
/// item's first paragraph.
/// </summary>
/// <remarks>
/// Markdig's parser checks only that the enclosing block sits in a list item, so it
/// matches <c>[x]</c>/<c>[ ]</c>/<c>[X]</c> anywhere inside one. Verified against
/// Markdig 1.3.2 by probing the parsed tree, that costs two things GitHub does not:
/// <c>- see [x](https://example.com) here</c> loses the <em>link</em> (the marker
/// parser runs before the link parser and eats <c>[x]</c>, leaving <c>(url)</c> as
/// text), and a marker opening a list item's <em>second</em> paragraph is treated as
/// a marker as well. Both are silent corruption of author intent, so the position is
/// checked before deferring to the base parser; a non-opening <c>[x]</c> stays
/// ordinary text, exactly as GitHub renders it.
/// </remarks>
internal sealed class GfmTaskListInlineParser : TaskListInlineParser
{
    public override bool Match(InlineProcessor processor, ref StringSlice slice)
        => OpensListItem(processor, slice.Start) && base.Match(processor, ref slice);

    private static bool OpensListItem(InlineProcessor processor, int sliceStart)
    {
        // A heading or table cell in a list item is not a task item either, so the
        // block must be the item's *first paragraph* — not merely inside the item.
        if (processor.Block is not ParagraphBlock paragraph
            || paragraph.Parent is not ListItemBlock item
            || item.Count == 0
            || !ReferenceEquals(item[0], paragraph))
        {
            return false;
        }

        // The paragraph's span starts at its first content character, so an opening
        // marker's source position is exactly that offset.
        return processor.GetSourcePosition(sliceStart) == paragraph.Span.Start;
    }
}
