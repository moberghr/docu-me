using System.Text;
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
/// Covered so far: headings (H1 dropped — it is the page title, §7); paragraphs
/// with inline text (literal, emphasis, inline code, line breaks); bullet/ordered/
/// nested lists; blockquotes; fenced code blocks (code macro); thematic breaks.
/// The rest of the construct table (tables, GitHub-alert panels, mermaid, links,
/// task lists, images) arrives in later M1 slices. Until a construct has a
/// dedicated renderer, <see cref="UnknownConstructRenderer"/> makes the converter
/// <em>fail loudly</em> rather than silently drop or mis-transform it (PLAN.md §7
/// acceptance: zero unknown-construct warnings). Output uses <c>\n</c> separators
/// unconditionally so golden files and content hashes stay stable across platforms.
/// </remarks>
public sealed class ConfluenceStorageRenderer : TextRendererBase<ConfluenceStorageRenderer>
{
    public ConfluenceStorageRenderer(TextWriter writer)
        : base(writer)
    {
        ObjectRenderers.Add(new HeadingRenderer());
        ObjectRenderers.Add(new ParagraphRenderer());
        ObjectRenderers.Add(new ListRenderer());
        ObjectRenderers.Add(new QuoteBlockRenderer());
        ObjectRenderers.Add(new FencedCodeBlockRenderer());
        ObjectRenderers.Add(new ThematicBreakRenderer());
        ObjectRenderers.Add(new EmphasisRenderer());
        ObjectRenderers.Add(new CodeInlineRenderer());
        ObjectRenderers.Add(new LiteralInlineRenderer());
        ObjectRenderers.Add(new LineBreakInlineRenderer());
        // Catch-all — MUST stay last so specific renderers win first-match.
        ObjectRenderers.Add(new UnknownConstructRenderer());
    }

    /// <summary>
    /// When set, paragraphs render their inline content without a <c>&lt;p&gt;</c>
    /// wrapper. Set for items of a <em>tight</em> list (CommonMark looseness), so
    /// <c>- a</c> becomes <c>&lt;li&gt;a&lt;/li&gt;</c> rather than
    /// <c>&lt;li&gt;&lt;p&gt;a&lt;/p&gt;&lt;/li&gt;</c>. Mirrors Markdig's own
    /// <c>HtmlRenderer.ImplicitParagraph</c>.
    /// </summary>
    public bool ImplicitParagraph { get; set; }

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
        if (renderer.ImplicitParagraph)
        {
            renderer.WriteLeafInline(obj);
            return;
        }

        renderer.Write("<p>");
        renderer.WriteLeafInline(obj);
        renderer.Write("</p>").Write('\n');
    }
}

/// <summary>
/// Bullet and ordered lists map to native <c>&lt;ul&gt;</c>/<c>&lt;ol&gt;</c> with
/// <c>&lt;li&gt;</c> items; nested lists render inside their parent item (§7). A
/// <em>tight</em> list (no blank lines) drops the item paragraph's <c>&lt;p&gt;</c>
/// wrapper via <see cref="ConfluenceStorageRenderer.ImplicitParagraph"/>, matching
/// Markdig's HTML renderer. Ordered-list start offset and bullet glyph are not
/// representable in storage format and are intentionally dropped.
/// </summary>
internal sealed class ListRenderer : MarkdownObjectRenderer<ConfluenceStorageRenderer, ListBlock>
{
    protected override void Write(ConfluenceStorageRenderer renderer, ListBlock obj)
    {
        var tag = obj.IsOrdered ? "ol" : "ul";
        renderer.Write('<').Write(tag).Write('>').Write('\n');

        foreach (var item in obj)
        {
            var previousImplicit = renderer.ImplicitParagraph;
            renderer.ImplicitParagraph = !obj.IsLoose;

            renderer.Write("<li>");
            renderer.WriteChildren((ListItemBlock)item);
            renderer.Write("</li>").Write('\n');

            renderer.ImplicitParagraph = previousImplicit;
        }

        renderer.Write("</").Write(tag).Write('>').Write('\n');
    }
}

/// <summary>
/// Blockquotes map to native <c>&lt;blockquote&gt;</c> (§7). GitHub alert syntax
/// (<c>&gt; [!NOTE]</c> …) parses as a plain blockquote in the default pipeline
/// but §7 maps it to a Confluence panel macro — a later M1 slice. Rather than
/// silently downgrade an alert to a quote, this fails loud until that slice lands.
/// </summary>
internal sealed class QuoteBlockRenderer : MarkdownObjectRenderer<ConfluenceStorageRenderer, QuoteBlock>
{
    private static readonly string[] AlertMarkers =
        ["[!NOTE]", "[!TIP]", "[!IMPORTANT]", "[!WARNING]", "[!CAUTION]"];

    protected override void Write(ConfluenceStorageRenderer renderer, QuoteBlock obj)
    {
        ThrowIfGitHubAlert(obj);

        // A blockquote always wraps block-level content, so its paragraphs are
        // never implicit — reset the flag (which a tight enclosing list item may
        // have set) around the children, then restore it, so a quote serializes
        // identically whether or not it is nested in a list.
        var previousImplicit = renderer.ImplicitParagraph;
        renderer.ImplicitParagraph = false;

        renderer.Write("<blockquote>").Write('\n');
        renderer.WriteChildren(obj);
        renderer.Write("</blockquote>").Write('\n');

        renderer.ImplicitParagraph = previousImplicit;
    }

    private static void ThrowIfGitHubAlert(QuoteBlock quote)
    {
        if (quote.Count == 0 || quote[0] is not ParagraphBlock { Inline: { } inline })
        {
            return;
        }

        // Accumulate the first line's plain text: Markdig splits `[!NOTE]` into
        // several literal inlines (the unmatched `[` delimiter becomes its own
        // literal), so the marker is not necessarily the first child. Any
        // non-literal inline (emphasis, code, link) means it is not a bare marker.
        var firstLine = new StringBuilder();
        foreach (var child in inline)
        {
            if (child is LineBreakInline)
            {
                break;
            }
            if (child is not LiteralInline literal)
            {
                return;
            }
            firstLine.Append(literal.Content.AsSpan());
        }

        var marker = firstLine.ToString().Trim();
        // GitHub matches the alert keyword case-insensitively, so `[!note]` is
        // as much an alert as `[!NOTE]`; uppercase before the (uppercase) lookup
        // so a lowercase alert still fails loud rather than silently downgrading.
        if (Array.IndexOf(AlertMarkers, marker.ToUpperInvariant()) >= 0)
        {
            throw new NotSupportedException(
                $"GitHub alert '{marker}' maps to a Confluence panel macro (PLAN.md §7); " +
                "that renderer is a later M1 slice, so it is not rendered as a plain blockquote.");
        }
    }
}

/// <summary>
/// Fenced code blocks map to the Confluence <c>code</c> structured macro (§7). The
/// fence language is normalized to a Confluence brush via <see cref="LanguageMap"/>;
/// an unknown or absent language omits the <c>language</c> parameter (never throws).
/// The body is wrapped in CDATA; any literal <c>]]&gt;</c> is split so the fragment
/// stays well-formed XML.
/// </summary>
internal sealed class FencedCodeBlockRenderer : MarkdownObjectRenderer<ConfluenceStorageRenderer, FencedCodeBlock>
{
    private static readonly Dictionary<string, string> LanguageMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cs"] = "csharp", ["csharp"] = "csharp", ["c#"] = "csharp",
        ["sh"] = "bash", ["shell"] = "bash", ["bash"] = "bash", ["zsh"] = "bash",
        ["js"] = "javascript", ["javascript"] = "javascript",
        ["ts"] = "typescript", ["typescript"] = "typescript",
        ["py"] = "python", ["python"] = "python",
        ["rb"] = "ruby", ["ruby"] = "ruby",
        ["ps1"] = "powershell", ["pwsh"] = "powershell", ["powershell"] = "powershell",
        ["json"] = "json",
        ["xml"] = "xml", ["html"] = "html",
        ["yaml"] = "yaml", ["yml"] = "yaml",
        ["sql"] = "sql",
        ["java"] = "java",
        ["go"] = "go",
        ["php"] = "php",
        ["css"] = "css",
        ["diff"] = "diff",
        ["txt"] = "text", ["text"] = "text", ["plaintext"] = "text",
    };

    protected override void Write(ConfluenceStorageRenderer renderer, FencedCodeBlock obj)
    {
        renderer.Write("<ac:structured-macro ac:name=\"code\">");

        var language = MapLanguage(obj.Info);
        if (language is not null)
        {
            renderer.Write("<ac:parameter ac:name=\"language\">")
                .Write(language)
                .Write("</ac:parameter>");
        }

        renderer.Write("<ac:plain-text-body><![CDATA[");
        renderer.Write(ExtractCode(obj).Replace("]]>", "]]]]><![CDATA[>"));
        renderer.Write("]]></ac:plain-text-body></ac:structured-macro>").Write('\n');
    }

    private static string? MapLanguage(string? info)
    {
        if (string.IsNullOrWhiteSpace(info))
        {
            return null;
        }

        var token = info.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)[0];
        return LanguageMap.TryGetValue(token, out var language) ? language : null;
    }

    private static string ExtractCode(FencedCodeBlock obj)
    {
        var lines = obj.Lines;
        var slices = lines.Lines;
        var code = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                code.Append('\n');
            }
            code.Append(slices[i].Slice.AsSpan());
        }
        return code.ToString();
    }
}

/// <summary>Thematic breaks (<c>---</c>, <c>***</c>, <c>___</c>) map to <c>&lt;hr/&gt;</c>.</summary>
internal sealed class ThematicBreakRenderer : MarkdownObjectRenderer<ConfluenceStorageRenderer, ThematicBreakBlock>
{
    protected override void Write(ConfluenceStorageRenderer renderer, ThematicBreakBlock obj)
        => renderer.Write("<hr/>").Write('\n');
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
