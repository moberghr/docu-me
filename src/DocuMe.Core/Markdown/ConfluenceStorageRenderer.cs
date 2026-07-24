using System.Text;
using Markdig.Extensions.Tables;
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
/// nested lists; blockquotes and GitHub-alert panel macros; fenced code blocks
/// (code macro); thematic breaks; links (external, relative .md page links,
/// autolinks); GFM tables. The rest of the construct table (mermaid, task lists,
/// strikethrough, images, <c>[TOC]</c>) arrives in later M1 slices. Until a construct has a
/// dedicated renderer, <see cref="UnknownConstructRenderer"/> makes the converter
/// <em>fail loudly</em> rather than silently drop or mis-transform it (PLAN.md §7
/// acceptance: zero unknown-construct warnings). Output uses <c>\n</c> separators
/// unconditionally so golden files and content hashes stay stable across platforms.
/// </remarks>
public sealed class ConfluenceStorageRenderer : TextRendererBase<ConfluenceStorageRenderer>
{
    public ConfluenceStorageRenderer(TextWriter writer, PageLinkResolver? linkResolver = null)
        : base(writer)
    {
        LinkResolver = linkResolver;

        ObjectRenderers.Add(new HeadingRenderer());
        ObjectRenderers.Add(new ParagraphRenderer());
        ObjectRenderers.Add(new ListRenderer());
        ObjectRenderers.Add(new QuoteBlockRenderer());
        ObjectRenderers.Add(new FencedCodeBlockRenderer());
        ObjectRenderers.Add(new ThematicBreakRenderer());
        ObjectRenderers.Add(new TableRenderer());
        ObjectRenderers.Add(new LinkReferenceDefinitionGroupRenderer());
        ObjectRenderers.Add(new EmphasisRenderer());
        ObjectRenderers.Add(new CodeInlineRenderer());
        ObjectRenderers.Add(new LinkInlineRenderer());
        ObjectRenderers.Add(new AutolinkInlineRenderer());
        ObjectRenderers.Add(new LiteralInlineRenderer());
        ObjectRenderers.Add(new LineBreakInlineRenderer());

        // Catch-all — MUST stay last so specific renderers win first-match.
        ObjectRenderers.Add(new UnknownConstructRenderer());
    }

    /// <summary>
    /// Resolves relative <c>.md</c> link targets to Confluence page titles (§7); may be
    /// <c>null</c>. <see cref="LinkInlineRenderer"/> fails loud on a relative link when
    /// this is <c>null</c> or the target does not resolve. External links and anchors
    /// need no resolver.
    /// </summary>
    public PageLinkResolver? LinkResolver { get; }

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

    /// <summary>Writes text escaped for a double-quoted XML attribute value (adds <c>&quot;</c> to <see cref="WriteEscaped"/>'s set).</summary>
    public ConfluenceStorageRenderer WriteAttributeEscaped(ReadOnlySpan<char> text)
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
                case '"':
                    Write("&quot;");
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
/// Blockquotes map to native <c>&lt;blockquote&gt;</c>, except that a GitHub alert
/// (<c>&gt; [!NOTE]</c> …) at the <em>root</em> blockquote level maps to a Confluence
/// panel macro instead (§7). The type mapping and the emitted macro shape follow
/// <c>kovetskiy/mark</c>, the behavioral reference §7 names for alerts
/// (<c>renderer/gh_alerts_blockquote.go</c>).
/// <para>
/// The marker line is the panel <em>type</em>, not content, so it is consumed: only
/// the author's remaining blocks land in the <c>rich-text-body</c>. A <em>nested</em>
/// alert stays a plain blockquote with its marker text visible (§7), matching both
/// mark's <c>quoteLevel == 0</c> rule and GitHub, which does not recognize an alert
/// inside another blockquote. "Root level" counts blockquote ancestors only, so an
/// alert inside a list item is still a panel.
/// </para>
/// <para>
/// Accepted loss (spec'd by §7): NOTE and IMPORTANT both map to <c>info</c>, so the
/// two are indistinguishable after conversion. mark compensates by injecting a
/// title paragraph naming the alert type; DocuMe deliberately does not, because
/// synthesizing body text the author never wrote would change the published content
/// (and its approval hash, §8) for a purely cosmetic gain.
/// </para>
/// </summary>
internal sealed class QuoteBlockRenderer : MarkdownObjectRenderer<ConfluenceStorageRenderer, QuoteBlock>
{
    private static readonly Dictionary<string, string> AlertPanelMacros = new(StringComparer.OrdinalIgnoreCase)
    {
        ["[!NOTE]"] = "info",
        ["[!TIP]"] = "tip",
        ["[!IMPORTANT]"] = "info",
        ["[!WARNING]"] = "note",
        ["[!CAUTION]"] = "warning",
    };

    protected override void Write(ConfluenceStorageRenderer renderer, QuoteBlock obj)
    {
        var alert = IsNestedInQuote(obj) ? null : MatchAlertPanel(obj);
        if (alert is null)
        {
            WriteBody(renderer, obj, "<blockquote>", "</blockquote>");
            return;
        }

        var (macro, markerLine) = alert.Value;
        ConsumeMarkerLine(obj, markerLine);

        // `icon` is Confluence's own default for these panels; mark writes it
        // explicitly and this follows it verbatim rather than relying on a
        // server-side default that a site could in principle change.
        var panelOpen = $"<ac:structured-macro ac:name=\"{macro}\">"
            + "<ac:parameter ac:name=\"icon\">true</ac:parameter><ac:rich-text-body>";

        WriteBody(renderer, obj, panelOpen, "</ac:rich-text-body></ac:structured-macro>");
    }

    private static void WriteBody(ConfluenceStorageRenderer renderer, QuoteBlock quote, string open, string close)
    {
        // A blockquote and a panel body both wrap block-level content, so their
        // paragraphs are never implicit — reset the flag (which a tight enclosing
        // list item may have set) around the children, then restore it, so either
        // block serializes identically whether or not it is nested in a list.
        var previousImplicit = renderer.ImplicitParagraph;
        renderer.ImplicitParagraph = false;

        renderer.Write(open).Write('\n');
        renderer.WriteChildren(quote);
        renderer.Write(close).Write('\n');

        renderer.ImplicitParagraph = previousImplicit;
    }

    /// <summary>True when this quote has a <see cref="QuoteBlock"/> ancestor, i.e. mark's <c>quoteLevel &gt; 0</c>.</summary>
    private static bool IsNestedInQuote(QuoteBlock quote)
    {
        for (var parent = quote.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is QuoteBlock)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the panel macro name and the marker paragraph's inline container when
    /// the quote's first line is a bare alert marker, else <c>null</c>.
    /// </summary>
    private static (string Macro, ContainerInline MarkerLine)? MatchAlertPanel(QuoteBlock quote)
    {
        if (quote.Count == 0 || quote[0] is not ParagraphBlock { Inline: { } inline })
        {
            return null;
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
                return null;
            }

            firstLine.Append(literal.Content.AsSpan());
        }

        // GitHub requires the marker to occupy its whole first line (`> [!NOTE] text`
        // is not an alert) and matches the keyword case-insensitively, so `[!note]`
        // is as much an alert as `[!NOTE]`.
        var macro = AlertPanelMacros.GetValueOrDefault(firstLine.ToString().Trim());
        return macro is null ? null : (macro, inline);
    }

    /// <summary>
    /// Drops the marker line — every inline up to and including the line break that
    /// ends it — and the now-empty paragraph if the marker stood alone, so the panel
    /// body starts at the author's first real content. Mirrors mark's
    /// <c>GHAlertsTransformer.splitAlertParagraph</c>. Mutating the tree is safe here:
    /// <see cref="ConfluenceStorageConverter"/> parses a fresh document per call and
    /// discards it after rendering.
    /// </summary>
    private static void ConsumeMarkerLine(QuoteBlock quote, ContainerInline markerLine)
    {
        var child = markerLine.FirstChild;
        while (child is not null)
        {
            var next = child.NextSibling;
            var endsLine = child is LineBreakInline;
            child.Remove();

            if (endsLine)
            {
                break;
            }

            child = next;
        }

        if (markerLine.FirstChild is null)
        {
            quote.RemoveAt(0);
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
        renderer.Write(ExtractCode(obj).Replace("]]>", "]]]]><![CDATA[>", StringComparison.Ordinal));
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

/// <summary>
/// GFM pipe tables map to a native storage-format table (§7). Confluence's own
/// shape carries the header row <em>inside</em> <c>&lt;tbody&gt;</c> as
/// <c>&lt;th&gt;</c> cells rather than in a <c>&lt;thead&gt;</c>
/// (confmark <c>docs/MAPPING.md</c>), so no <c>&lt;thead&gt;</c> is emitted.
/// <para>
/// Two properties of <c>Table.ColumnDefinitions</c> are deliberately dropped, both
/// accepted losses. <em>Alignment</em> (<c>|:---:|</c>) has no storage-format
/// representation (§7, confmark's "known lossy points"). <em>Width</em> is Markdig's
/// raw header dash count, which is source formatting rather than layout intent — a
/// hand-aligned <c>|------|--|</c> would otherwise emit a lopsided column grid the
/// author never asked for.
/// </para>
/// </summary>
internal sealed class TableRenderer : MarkdownObjectRenderer<ConfluenceStorageRenderer, Table>
{
    protected override void Write(ConfluenceStorageRenderer renderer, Table obj)
    {
        renderer.Write("<table>").Write('\n');
        renderer.Write("<tbody>").Write('\n');

        foreach (var rowObj in obj)
        {
            var row = (TableRow)rowObj;
            var cellTag = row.IsHeader ? "th" : "td";

            renderer.Write("<tr>").Write('\n');
            foreach (var cellObj in row)
            {
                WriteCell(renderer, (TableCell)cellObj, cellTag);
            }

            renderer.Write("</tr>").Write('\n');
        }

        renderer.Write("</tbody>").Write('\n');
        renderer.Write("</table>").Write('\n');
    }

    private static void WriteCell(ConfluenceStorageRenderer renderer, TableCell cell, string tag)
    {
        // Unreachable for pipe tables (a GFM cell is always 1x1) — it guards the
        // day someone enables .UseGridTables(), where a dropped span would quietly
        // reshape the grid instead of failing.
        if (cell.ColumnSpan != 1 || cell.RowSpan != 1)
        {
            throw new NotSupportedException(
                $"Table cell spans {cell.ColumnSpan} column(s) and {cell.RowSpan} row(s); " +
                "merged cells are not supported by the M1 converter (GFM pipe tables cannot " +
                "express them).");
        }

        renderer.Write('<').Write(tag).Write('>');

        // A GFM cell holds inline content, which Markdig models as one paragraph;
        // drop its <p> wrapper so the cell reads <td>text</td>. Mirrors Markdig's
        // own HtmlTableRenderer, which only implies the paragraph for a single
        // block so a multi-block cell still gets real <p> elements.
        var previousImplicit = renderer.ImplicitParagraph;
        renderer.ImplicitParagraph = cell.Count == 1;

        renderer.WriteChildren(cell);

        renderer.ImplicitParagraph = previousImplicit;
        renderer.Write("</").Write(tag).Write('>').Write('\n');
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

/// <summary>
/// Inline links (§7). An <em>external</em> URL (has a URI scheme, or is
/// protocol-relative <c>//host</c>) renders as <c>&lt;a href&gt;</c>. A relative
/// <c>.md</c> path renders as a Confluence page link
/// (<c>&lt;ac:link&gt;&lt;ri:page ri:content-title="…"/&gt;…&lt;/ac:link&gt;</c>),
/// its title resolved through <see cref="ConfluenceStorageRenderer.LinkResolver"/>.
/// Per spike S2's default (heading anchors are not assumed to survive the new
/// editor) a <c>#fragment</c> is dropped: a fragment on a page link is stripped
/// and a same-page anchor (<c>#foo</c>) degrades to its link text with no link.
/// Images (<c>![alt](src)</c> — also a <see cref="LinkInline"/>) and relative
/// non-<c>.md</c> targets are not yet supported and fail loud.
/// </summary>
internal sealed class LinkInlineRenderer : MarkdownObjectRenderer<ConfluenceStorageRenderer, LinkInline>
{
    protected override void Write(ConfluenceStorageRenderer renderer, LinkInline obj)
    {
        if (obj.IsImage)
        {
            throw new NotSupportedException(
                "Image syntax '![alt](src)' maps to a Confluence attachment + <ac:image> " +
                "(PLAN.md §7); that renderer is a later M1 slice, so images are not yet converted.");
        }

        var url = obj.Url ?? string.Empty;

        // Same-page anchor (spike S2 default): drop the anchor, keep the link text —
        // there is no destination page and heading anchors may not survive.
        if (url.StartsWith('#'))
        {
            renderer.WriteChildren(obj);
            return;
        }

        if (IsExternal(url))
        {
            renderer.Write("<a href=\"").WriteAttributeEscaped(url).Write('"');

            // A markdown link title ([text](url "tip")) is a tooltip — representable
            // on an <a>, so preserve it rather than silently dropping it.
            if (!string.IsNullOrEmpty(obj.Title))
            {
                renderer.Write(" title=\"").WriteAttributeEscaped(obj.Title).Write('"');
            }

            renderer.Write('>');
            renderer.WriteChildren(obj);
            renderer.Write("</a>");
            return;
        }

        // Relative link: strip any #fragment (S2 default — dropped, not preserved).
        var fragment = url.IndexOf('#', StringComparison.Ordinal);
        var path = fragment < 0 ? url : url[..fragment];
        if (path.Length == 0)
        {
            renderer.WriteChildren(obj);
            return;
        }

        if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Relative link '{url}' is neither a '.md' page link nor an external URL. " +
                "PLAN.md §7 maps only relative .md links and external links; other relative " +
                "targets (source files, directories) are not supported by the M1 converter.");
        }

        if (!string.IsNullOrEmpty(obj.Title))
        {
            // A page link (<ac:link>) has no representable tooltip in storage format,
            // so a title on an internal .md link would be a silent loss. Fail loud
            // rather than drop it (it is vanishingly rare in practice).
            throw new NotSupportedException(
                $"Relative markdown link '{url}' carries a title tooltip, which has no " +
                "representation on a Confluence page link (<ac:link>) in storage format.");
        }

        var resolver = renderer.LinkResolver
            ?? throw new InvalidOperationException(
                $"Relative markdown link '{url}' requires a page-link resolver, but none was " +
                "supplied to ConfluenceStorageConverter.Convert.");
        var title = resolver(path)
            ?? throw new InvalidOperationException(
                $"Relative markdown link '{url}' does not resolve to any known wiki page " +
                "(broken cross-reference). Fix the link or the target page's title.");

        renderer.Write("<ac:link><ri:page ri:content-title=\"")
            .WriteAttributeEscaped(title)
            .Write("\"/><ac:link-body>");
        renderer.WriteChildren(obj);
        renderer.Write("</ac:link-body></ac:link>");
    }

    /// <summary>True when the URL carries a URI scheme (<c>https:</c>, <c>mailto:</c>, …) or is protocol-relative (<c>//host</c>).</summary>
    private static bool IsExternal(string url)
    {
        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            return true;
        }

        var colon = url.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return false;
        }

        for (var i = 0; i < colon; i++)
        {
            var c = url[i];
            var ok = i == 0
                ? char.IsAsciiLetter(c)
                : char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Autolinks (<c>&lt;https://example.com&gt;</c>, <c>&lt;me@example.com&gt;</c>) are
/// external links (§7) — Markdig models them separately from <see cref="LinkInline"/>.
/// The visible text is the URL itself; an email autolink gets a <c>mailto:</c> href.
/// </summary>
internal sealed class AutolinkInlineRenderer : MarkdownObjectRenderer<ConfluenceStorageRenderer, AutolinkInline>
{
    protected override void Write(ConfluenceStorageRenderer renderer, AutolinkInline obj)
    {
        var href = obj.IsEmail ? "mailto:" + obj.Url : obj.Url;
        renderer.Write("<a href=\"").WriteAttributeEscaped(href).Write("\">");
        renderer.WriteEscaped(obj.Url.AsSpan());
        renderer.Write("</a>");
    }
}

/// <summary>
/// Link reference definitions (<c>[ref]: url</c>) are invisible metadata: Markdig
/// resolves the <c>[text][ref]</c> usages into ordinary <see cref="LinkInline"/>s
/// and collects the definitions into this group block. It renders to nothing
/// (matching Markdig's own HtmlRenderer) — a no-op keeps reference-style links from
/// tripping the fail-loud catch-all on a fully-representable construct.
/// </summary>
internal sealed class LinkReferenceDefinitionGroupRenderer
    : MarkdownObjectRenderer<ConfluenceStorageRenderer, LinkReferenceDefinitionGroup>
{
    protected override void Write(ConfluenceStorageRenderer renderer, LinkReferenceDefinitionGroup obj)
    {
        // Intentionally emits nothing.
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
            // subclasses like AutolinkInline, which must fail until supported).
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
