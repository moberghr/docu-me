using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace DocuMe.Core.Markdown;

/// <summary>
/// Renders a parsed markdown document to Confluence <em>storage format</em>
/// (XHTML) — the custom Markdig renderer of PLAN.md §7, written directly rather
/// than via markdown→HTML→regex so every construct maps deliberately.
/// </summary>
/// <remarks>
/// This is the M1 seed: it covers headings (H1 dropped — it is the page title,
/// §7) and paragraphs with inline text (literal, emphasis, inline code, line
/// breaks). The rest of the construct table (tables, code macros, panels,
/// mermaid, links, task lists) arrives in later M1 slices. Until a construct
/// has a dedicated renderer, <see cref="UnknownConstructRenderer"/> makes the
/// converter <em>fail loudly</em> rather than silently drop or mis-transform it
/// (PLAN.md §7 acceptance: zero unknown-construct warnings). Output uses
/// <c>\n</c> separators unconditionally so golden files and content hashes stay
/// stable across platforms.
/// </remarks>
public sealed class ConfluenceStorageRenderer : TextRendererBase<ConfluenceStorageRenderer>
{
    public ConfluenceStorageRenderer(TextWriter writer)
        : base(writer)
    {
        ObjectRenderers.Add(new HeadingRenderer());
        ObjectRenderers.Add(new ParagraphRenderer());
        ObjectRenderers.Add(new EmphasisRenderer());
        ObjectRenderers.Add(new CodeInlineRenderer());
        ObjectRenderers.Add(new LiteralInlineRenderer());
        ObjectRenderers.Add(new LineBreakInlineRenderer());
        // Catch-all — MUST stay last so specific renderers win first-match.
        ObjectRenderers.Add(new UnknownConstructRenderer());
    }

    /// <summary>Writes text with XML character references so it is safe in storage-format markup.</summary>
    public ConfluenceStorageRenderer WriteEscaped(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            switch (c)
            {
                case '&':
                    Write("&amp;");
                    break;
                case '<':
                    Write("&lt;");
                    break;
                case '>':
                    Write("&gt;");
                    break;
                default:
                    Write(c);
                    break;
            }
        }
        return this;
    }
}

/// <summary>H1 is dropped (it is the page title, §7); H2–H6 map to <c>&lt;hN&gt;</c>.</summary>
internal sealed class HeadingRenderer : MarkdownObjectRenderer<ConfluenceStorageRenderer, HeadingBlock>
{
    protected override void Write(ConfluenceStorageRenderer renderer, HeadingBlock obj)
    {
        if (obj.Level <= 1)
        {
            return;
        }

        var tag = "h" + obj.Level;
        renderer.Write('<').Write(tag).Write('>');
        renderer.WriteLeafInline(obj);
        renderer.Write("</").Write(tag).Write('>').Write('\n');
    }
}

internal sealed class ParagraphRenderer : MarkdownObjectRenderer<ConfluenceStorageRenderer, ParagraphBlock>
{
    protected override void Write(ConfluenceStorageRenderer renderer, ParagraphBlock obj)
    {
        renderer.Write("<p>");
        renderer.WriteLeafInline(obj);
        renderer.Write("</p>").Write('\n');
    }
}

/// <summary><c>**bold**</c>/<c>__bold__</c> → <c>&lt;strong&gt;</c>; <c>*italic*</c>/<c>_italic_</c> → <c>&lt;em&gt;</c>.</summary>
internal sealed class EmphasisRenderer : MarkdownObjectRenderer<ConfluenceStorageRenderer, EmphasisInline>
{
    protected override void Write(ConfluenceStorageRenderer renderer, EmphasisInline obj)
    {
        var tag = obj.DelimiterCount >= 2 ? "strong" : "em";
        renderer.Write('<').Write(tag).Write('>');
        renderer.WriteChildren(obj);
        renderer.Write("</").Write(tag).Write('>');
    }
}

/// <summary>Inline code spans map to <c>&lt;code&gt;</c> (mark/confmark mapping, §7).</summary>
internal sealed class CodeInlineRenderer : MarkdownObjectRenderer<ConfluenceStorageRenderer, CodeInline>
{
    protected override void Write(ConfluenceStorageRenderer renderer, CodeInline obj)
    {
        renderer.Write("<code>");
        renderer.WriteEscaped(obj.Content.AsSpan());
        renderer.Write("</code>");
    }
}

internal sealed class LiteralInlineRenderer : MarkdownObjectRenderer<ConfluenceStorageRenderer, LiteralInline>
{
    protected override void Write(ConfluenceStorageRenderer renderer, LiteralInline obj)
        => renderer.WriteEscaped(obj.Content.AsSpan());
}

/// <summary>Soft breaks become a space; hard breaks (two trailing spaces / <c>\</c>) become <c>&lt;br/&gt;</c>.</summary>
internal sealed class LineBreakInlineRenderer : MarkdownObjectRenderer<ConfluenceStorageRenderer, LineBreakInline>
{
    protected override void Write(ConfluenceStorageRenderer renderer, LineBreakInline obj)
    {
        if (obj.IsHard)
        {
            renderer.Write("<br/>");
        }
        else
        {
            renderer.Write(' ');
        }
    }
}

/// <summary>
/// Registered last as a catch-all. Recurses the two structural wrappers we rely
/// on — the document root and a leaf block's implicit inline container — and
/// throws <see cref="NotSupportedException"/> for any content construct that has
/// no dedicated renderer yet. This is the fail-loud contract: an unsupported
/// construct never silently drops or mis-transforms into wrong-but-plausible
/// markup. Later M1 slices retire cases from here by registering real renderers.
/// </summary>
internal sealed class UnknownConstructRenderer : MarkdownObjectRenderer<ConfluenceStorageRenderer, MarkdownObject>
{
    protected override void Write(ConfluenceStorageRenderer renderer, MarkdownObject obj)
    {
        switch (obj)
        {
            case MarkdownDocument document:
                renderer.WriteChildren(document);
                return;
            // The exact-type root inline container of a leaf block (NOT its
            // subclasses like LinkInline, which must fail until supported).
            case ContainerInline container when obj.GetType() == typeof(ContainerInline):
                renderer.WriteChildren(container);
                return;
            default:
                throw new NotSupportedException(
                    $"No storage-format renderer for markdown construct '{obj.GetType().Name}'. " +
                    "It is not yet supported by the M1 converter.");
        }
    }
}
