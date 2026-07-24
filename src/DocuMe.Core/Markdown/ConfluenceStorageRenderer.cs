using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
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
/// with inline text (literal, emphasis incl. strikethrough, inline code, line
/// breaks); bullet/ordered/nested lists and task lists; blockquotes and
/// GitHub-alert panel macros; fenced code blocks (code macro); thematic breaks;
/// links (external, relative .md page links, autolinks); GFM tables; a root-level
/// <c>[TOC]</c> line (table-of-contents macro); images (attachment or external URL);
/// <c>```mermaid</c> fences (rendered-diagram attachment); HTML comments in both the
/// block and inline form (dropped).
/// That completes the §7 construct table. Until a construct has a
/// dedicated renderer, <see cref="UnknownConstructRenderer"/> makes the converter
/// <em>fail loudly</em> rather than silently drop or mis-transform it (PLAN.md §7
/// acceptance: zero unknown-construct warnings). Output uses <c>\n</c> separators
/// unconditionally so golden files and content hashes stay stable across platforms.
/// <para>
/// The handful of constructs that deliberately <em>degrade</em> instead of failing — an
/// unmapped fence language, a mixed task list, a same-page anchor — report a
/// <see cref="ConversionDiagnostic"/> through <see cref="Report"/> so §4.4's second clause
/// ("zero unknown-construct warnings") is measurable. Reporting never changes what is written.
/// </para>
/// </remarks>
public sealed class ConfluenceStorageRenderer : TextRendererBase<ConfluenceStorageRenderer>
{
    private int _taskId;

    public ConfluenceStorageRenderer(
        TextWriter writer,
        PageLinkResolver? linkResolver = null,
        AttachmentResolver? attachmentResolver = null,
        MermaidDiagramResolver? mermaidResolver = null,
        ICollection<ConversionDiagnostic>? diagnostics = null)
        : base(writer)
    {
        LinkResolver = linkResolver;
        AttachmentResolver = attachmentResolver;
        MermaidResolver = mermaidResolver;
        Diagnostics = diagnostics;

        ObjectRenderers.Add(new HeadingRenderer());
        ObjectRenderers.Add(new ParagraphRenderer());
        ObjectRenderers.Add(new ListRenderer());
        ObjectRenderers.Add(new QuoteBlockRenderer());
        ObjectRenderers.Add(new FencedCodeBlockRenderer());
        ObjectRenderers.Add(new ThematicBreakRenderer());
        ObjectRenderers.Add(new TableRenderer());
        ObjectRenderers.Add(new LinkReferenceDefinitionGroupRenderer());
        ObjectRenderers.Add(new HtmlBlockRenderer());
        ObjectRenderers.Add(new EmphasisRenderer());
        ObjectRenderers.Add(new CodeInlineRenderer());
        ObjectRenderers.Add(new LinkInlineRenderer());
        ObjectRenderers.Add(new AutolinkInlineRenderer());
        ObjectRenderers.Add(new TaskListRenderer());
        ObjectRenderers.Add(new LiteralInlineRenderer());
        ObjectRenderers.Add(new HtmlEntityInlineRenderer());
        ObjectRenderers.Add(new LineBreakInlineRenderer());
        ObjectRenderers.Add(new HtmlInlineRenderer());

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
    /// Resolves relative image paths to their final Confluence attachment filenames (§7);
    /// may be <c>null</c>. <see cref="LinkInlineRenderer"/> fails loud on a local image when
    /// this is <c>null</c> or the path does not resolve. External image URLs need no
    /// resolver.
    /// </summary>
    public AttachmentResolver? AttachmentResolver { get; }

    /// <summary>
    /// Resolves a <c>```mermaid</c> fence body to the attachment filename its rendered
    /// diagram will be uploaded under (§7); may be <c>null</c>.
    /// <see cref="FencedCodeBlockRenderer"/> fails loud on a mermaid fence when this is
    /// <c>null</c> or the diagram does not render, rather than publishing the diagram
    /// source as a code block.
    /// </summary>
    public MermaidDiagramResolver? MermaidResolver { get; }

    /// <summary>
    /// Sink for the deliberate degradations this render applied (§4.4); may be <c>null</c>,
    /// in which case <see cref="Report"/> is a no-op. Never consulted while writing, so the
    /// emitted storage format is identical whether or not a caller passes one — which is what
    /// keeps the 27 hand-reviewed golden files (§4.3) valid.
    /// </summary>
    public ICollection<ConversionDiagnostic>? Diagnostics { get; }

    /// <summary>
    /// When set, paragraphs render their inline content without a <c>&lt;p&gt;</c>
    /// wrapper. Set for items of a <em>tight</em> list (CommonMark looseness), so
    /// <c>- a</c> becomes <c>&lt;li&gt;a&lt;/li&gt;</c> rather than
    /// <c>&lt;li&gt;&lt;p&gt;a&lt;/p&gt;&lt;/li&gt;</c>. Mirrors Markdig's own
    /// <c>HtmlRenderer.ImplicitParagraph</c>.
    /// </summary>
    public bool ImplicitParagraph { get; set; }

    /// <summary>
    /// Issues the next <c>&lt;ac:task-id&gt;</c>, numbering the page's tasks from 1 in
    /// document order. The counter lives on the renderer instance, which
    /// <see cref="ConfluenceStorageConverter"/> creates per call, so ids are unique
    /// within a page and identical on every re-render of the same source (§8: the
    /// content hash must not churn).
    /// <para>
    /// Deliberate deviation from mark, which restarts at 1 for every list and so
    /// repeats ids when a page has more than one task list: Confluence tracks task
    /// completion by id, and duplicates invite it to conflate two distinct tasks.
    /// </para>
    /// </summary>
    public int NextTaskId() => ++_taskId;

    /// <summary>
    /// Records that a construct converted but degraded (see <see cref="ConversionDiagnostic"/>).
    /// A no-op when no sink was supplied, so a reporting site costs nothing and never changes
    /// output. Call it only where the degradation is a real loss against what the author sees
    /// on GitHub: a diagnostic that fires on a lossless construct would make §4.4's
    /// "zero unknown-construct warnings" unreachable for reasons that are not losses.
    /// </summary>
    public void Report(string code, string construct, string message)
        => Diagnostics?.Add(new ConversionDiagnostic(code, construct, message));

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

/// <summary>
/// Paragraphs map to <c>&lt;p&gt;</c>, except a root-level paragraph that is exactly
/// <c>[TOC]</c>, which becomes Confluence's table-of-contents macro (§7).
/// <para>
/// The <c>[TOC]</c> spelling is DocuMe's own: mark has no such shorthand (it requires an
/// explicit <c>&lt;!-- Include: ac:toc --&gt;</c> directive), so §7's source syntax follows
/// the Python-Markdown/MultiMarkdown convention instead. No parameters are emitted, unlike
/// mark's template which writes all ten Confluence defaults: the shorthand carries none, and
/// writing defaults would churn every published body and the §8 approval hash for zero
/// rendering difference (same reasoning as the code macro's omitted <c>collapse</c>). The
/// open/close spelling over a self-closing tag matches confmark, §7's round-trip reference,
/// and needs no reshaping if a parameterized shorthand is ever added.
/// </para>
/// <para>
/// Matching is deliberately <em>narrower</em> than the reference tools, because widening it
/// silently eats author text while narrowing it only leaves a visible literal the author can
/// fix. Three rules follow from that: the match is structural, root-level only, and
/// case-sensitive. Structural, because Markdig parses an unresolved <c>[TOC]</c> reference
/// into two literals (<c>[</c> then <c>TOC]</c>) whereas the escaped <c>\[TOC]</c> — the one
/// spelling an author uses precisely to prevent expansion — collapses to a <em>single</em>
/// literal with identical text; comparing accumulated text would hijack it, comparing shape
/// cannot. Root-level only (mirroring the alert panels' blockquote-depth rule), since a TOC
/// is a page-level construct and a <c>[TOC]</c> in a list item, quote or table cell is far
/// more likely prose about the syntax than a request for one. Case-sensitive, so
/// <c>[toc]</c> stays text.
/// </para>
/// <para>
/// A non-matching <c>[TOC]</c> degrades to visible literal text rather than failing loud, so
/// a page documenting the syntax stays publishable.
/// </para>
/// </summary>
internal sealed class ParagraphRenderer : MarkdownObjectRenderer<ConfluenceStorageRenderer, ParagraphBlock>
{
    private const string TocMacro = "<ac:structured-macro ac:name=\"toc\"></ac:structured-macro>";

    protected override void Write(ConfluenceStorageRenderer renderer, ParagraphBlock obj)
    {
        if (IsTocMarker(obj))
        {
            renderer.Write(TocMacro).Write('\n');
            return;
        }

        if (renderer.ImplicitParagraph)
        {
            renderer.WriteLeafInline(obj);
            return;
        }

        renderer.Write("<p>");
        renderer.WriteLeafInline(obj);
        renderer.Write("</p>").Write('\n');
    }

    /// <summary>
    /// True when <paramref name="obj"/> is a root-level paragraph holding nothing but the
    /// literal <c>[TOC]</c>, in the exact two-literal shape Markdig produces for an
    /// unresolved shortcut reference. A Markdig upgrade that changed that shape would make
    /// this return false, degrading to visible <c>[TOC]</c> text rather than to a wrong
    /// macro; a test pins the shape so the change surfaces there instead.
    /// </summary>
    private static bool IsTocMarker(ParagraphBlock obj)
    {
        if (obj.Parent is not MarkdownDocument || obj.Inline is null)
        {
            return false;
        }

        if (obj.Inline.FirstChild is not LiteralInline first
            || !first.Content.AsSpan().SequenceEqual("["))
        {
            return false;
        }

        if (first.NextSibling is not LiteralInline second || second.NextSibling is not null)
        {
            return false;
        }

        return second.Content.AsSpan().SequenceEqual("TOC]");
    }
}

/// <summary>
/// Bullet and ordered lists map to native <c>&lt;ul&gt;</c>/<c>&lt;ol&gt;</c> with
/// <c>&lt;li&gt;</c> items; nested lists render inside their parent item (§7). A
/// <em>tight</em> list (no blank lines) drops the item paragraph's <c>&lt;p&gt;</c>
/// wrapper via <see cref="ConfluenceStorageRenderer.ImplicitParagraph"/>, matching
/// Markdig's HTML renderer. Ordered-list start offset and bullet glyph are not
/// representable in storage format and are intentionally dropped.
/// <para>
/// A list whose items are <em>all</em> task items becomes a native Confluence task
/// list instead (§7) — see <see cref="WriteTaskList"/>.
/// </para>
/// </summary>
internal sealed class ListRenderer : MarkdownObjectRenderer<ConfluenceStorageRenderer, ListBlock>
{
    protected override void Write(ConfluenceStorageRenderer renderer, ListBlock obj)
    {
        if (TryGetTaskMarkers(obj, out var markers))
        {
            WriteTaskList(renderer, obj, markers);
            return;
        }

        var tag = obj.IsOrdered ? "ol" : "ul";
        ReportMixedTaskList(renderer, obj, tag);

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

    /// <summary>
    /// Collects one task marker per item, in item order, or fails when any item lacks
    /// one. A <em>mixed</em> list is deliberately not a task list: storage format has
    /// no element for a plain item inside <c>&lt;ac:task-list&gt;</c>, so mixing would
    /// produce invalid markup. Such a list degrades to <c>&lt;ul&gt;</c>/<c>&lt;ol&gt;</c>
    /// with the markers kept as text by <see cref="TaskListRenderer"/> — mark's rule
    /// (its <c>isTaskList</c>), and the fallback PLAN.md §7 calls for.
    /// </summary>
    private static bool TryGetTaskMarkers(ListBlock list, [NotNullWhen(true)] out TaskList[]? markers)
    {
        markers = null;
        if (list.Count == 0)
        {
            return false;
        }

        var found = new TaskList[list.Count];
        for (var i = 0; i < list.Count; i++)
        {
            // GfmTaskListInlineParser only ever produces a marker in this position,
            // so "first inline of the first block" is the whole test.
            if (list[i] is not ListItemBlock { Count: > 0 } item
                || item[0] is not ParagraphBlock { Inline.FirstChild: TaskList marker })
            {
                return false;
            }

            found[i] = marker;
        }

        markers = found;
        return true;
    }

    /// <summary>
    /// Reports the mixed-task-list degradation (§4.4) when this plain list carries <em>some</em>
    /// task markers — the items that had one publish as literal <c>[x]</c>/<c>[ ]</c> text
    /// instead of trackable Confluence tasks. Silent when no item carries a marker, which is an
    /// ordinary list and no loss at all.
    /// </summary>
    private static void ReportMixedTaskList(ConfluenceStorageRenderer renderer, ListBlock list, string tag)
    {
        var taskItems = CountTaskItems(list);
        if (taskItems == 0)
        {
            return;
        }

        var message =
            $"{taskItems} of {list.Count} items in this list open with a task marker. Storage "
            + "format has no plain item inside <ac:task-list>, so the whole list degrades to "
            + $"<{tag}> and the markers stay as literal '[x]'/'[ ]' text: completion state is "
            + "readable but Confluence no longer tracks it. Split the plain items into their own "
            + "list to get native tasks.";
        renderer.Report(ConversionDiagnosticCodes.MixedTaskList, tag, message);
    }

    /// <summary>
    /// Counts items whose first inline is a task marker, i.e. the ones
    /// <see cref="TryGetTaskMarkers"/> demands of <em>every</em> item. A <c>[x]</c> that does
    /// not open its item is ordinary text and never becomes a <see cref="TaskList"/> inline
    /// (see <see cref="GfmTaskListExtension"/>), so it is not counted here either.
    /// </summary>
    private static int CountTaskItems(ListBlock list) =>
        list.Count(item => item is ListItemBlock { Count: > 0 } block
            && block[0] is ParagraphBlock { Inline.FirstChild: TaskList });

    /// <summary>
    /// Writes the Confluence task list (<c>&lt;ac:task-list&gt;</c> /
    /// <c>&lt;ac:task&gt;</c> / <c>&lt;ac:task-id&gt;</c> / <c>&lt;ac:task-status&gt;</c> /
    /// <c>&lt;ac:task-body&gt;</c>), the shape read from mark's
    /// <c>renderer/tasklist.go</c> and its <c>testdata/tasklists.html</c> fixture; the
    /// status strings are Confluence's <c>complete</c>/<c>incomplete</c>.
    /// <para>
    /// Ordered-ness is dropped: <c>1. [x] done</c> is a task list too, and a task list
    /// has no numbered variant. Tight/loose is honored exactly as for a plain list, so
    /// a tight item's body is bare inline content and a loose one keeps its
    /// <c>&lt;p&gt;</c>. A nested task list lands inside its parent's task body, which
    /// is how Confluence nests tasks.
    /// </para>
    /// </summary>
    private static void WriteTaskList(ConfluenceStorageRenderer renderer, ListBlock obj, TaskList[] markers)
    {
        renderer.Write("<ac:task-list>").Write('\n');

        for (var i = 0; i < obj.Count; i++)
        {
            var item = (ListItemBlock)obj[i];
            var status = markers[i].Checked ? "complete" : "incomplete";
            ConsumeMarker(markers[i]);

            renderer.Write("<ac:task>").Write('\n');
            renderer.Write("<ac:task-id>")
                .Write(renderer.NextTaskId().ToString(CultureInfo.InvariantCulture))
                .Write("</ac:task-id>")
                .Write('\n');
            renderer.Write("<ac:task-status>").Write(status).Write("</ac:task-status>").Write('\n');
            renderer.Write("<ac:task-body>");

            var previousImplicit = renderer.ImplicitParagraph;
            renderer.ImplicitParagraph = !obj.IsLoose;

            renderer.WriteChildren(item);

            renderer.ImplicitParagraph = previousImplicit;
            renderer.Write("</ac:task-body>").Write('\n');
            renderer.Write("</ac:task>").Write('\n');
        }

        renderer.Write("</ac:task-list>").Write('\n');
    }

    /// <summary>
    /// Drops the marker and the one space separating it from the body, so the task
    /// body starts at the author's own text. GFM spells the marker <c>[x] </c> with
    /// that separator included, and goldmark (so also mark) consumes it in the parser,
    /// whereas Markdig leaves it on the following literal. Mutating the tree is safe
    /// here for the same reason as in <see cref="QuoteBlockRenderer"/>:
    /// <see cref="ConfluenceStorageConverter"/> parses a fresh document per call and
    /// discards it after rendering.
    /// </summary>
    private static void ConsumeMarker(TaskList marker)
    {
        if (marker.NextSibling is LiteralInline literal && literal.Content.CurrentChar == ' ')
        {
            literal.Content.SkipChar();
        }

        marker.Remove();
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
/// alert inside a list item is still a panel. That nested case emits no
/// <see cref="ConversionDiagnostic"/> precisely <em>because</em> it matches GitHub: the reader
/// sees the same quoted <c>[!NOTE]</c> line either way, so nothing is lost against the source.
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
/// Fenced code blocks map to the Confluence <c>code</c> structured macro (§7),
/// including mark's fence-attribute syntax on the info line:
/// <c>```lang linenumbers collapse title Some Title</c>.
/// </summary>
/// <remarks>
/// <para>
/// A <c>```mermaid</c> fence is not code at all: it becomes the rendered-diagram
/// attachment of §7 rather than a code macro — see <see cref="TryWriteDiagram"/>.
/// </para>
/// <para>
/// The first token is the language, normalized to a Confluence brush via
/// <see cref="LanguageMap"/>. An unknown or absent language omits the
/// <c>language</c> parameter and never throws — losing syntax highlighting is
/// cosmetic and Confluence renders unhighlighted code fine. Write <c>-</c> as the
/// language (mark's convention) to use attributes without one.
/// </para>
/// <para>
/// <strong>The inclusion rule for <see cref="LanguageMap"/>:</strong> a brush is mapped
/// only when it is confirmed by <em>two independent sources</em> — it appears in
/// Atlassian's own documented Code Block language list, <em>and</em> it is a Prism
/// component id, which is what the ADF <c>codeBlock</c> node documents its
/// <c>language</c> attribute to be (Confluence Cloud converts storage format to ADF, so
/// Prism's vocabulary is what the value is finally read against). Both checks matter
/// because the two lists disagree: the Cloud support page still shows the ~23-brush
/// legacy set, and the UI's display names (<c>CSharp</c>, <c>Objective-C</c>,
/// <c>TeX</c>) are not the storage values (<c>csharp</c>, <c>objectivec</c>,
/// <c>latex</c>).
/// </para>
/// <para>
/// Anything confirmed by only one source stays <em>unmapped on purpose</em>, because a
/// wrong brush is a <em>silent</em> no-highlight — indistinguishable in the published
/// page from omitting the parameter, but it also suppresses the
/// <see cref="ConversionDiagnosticCodes.UnknownFenceLanguage"/> diagnostic, turning a
/// reported cosmetic loss into an unreported one. So Atlassian-documented languages with
/// no Prism component at all (CUDA, FoxPro, JavaFX, Objective-J, Octave) are deliberately
/// absent and keep reporting, and so is any language Prism supports that Atlassian does
/// not document (Nim, Brainfuck).
/// </para>
/// <para>
/// Where Atlassian's display name is not itself the Prism key the <em>pairing</em> is an
/// inference, not a confirmation — <c>CSharp</c>/<c>csharp</c> (long since verified),
/// <c>Objective-C</c>/<c>objectivec</c>, <c>TeX</c>/<c>latex</c>,
/// <c>Dockerfile</c>/<c>docker</c>, <c>ColdFusion</c>/<c>cfscript</c>,
/// <c>reStructuredText</c>/<c>rest</c>, <c>StandardML</c>/<c>sml</c>,
/// <c>Mathematica</c>/Prism's <c>wolfram</c> alias. The residual unknown is whether
/// Cloud's storage-to-ADF conversion honors every Prism id or validates against a
/// narrower internal table; that is a sandbox observation, not a documentation one, and
/// its worst case is the unhighlighted block we already publish today.
/// </para>
/// <para>
/// The remaining tokens are attributes: <c>collapse</c>/<c>nocollapse</c>,
/// <c>linenumbers</c>, a positive integer (Confluence's <c>firstline</c>, which also
/// turns line numbers on), and <c>title</c>, whose value is the rest of the line
/// unquoted — exactly how mark reads it, confirmed against its own
/// <c>testdata/codes.html</c> fixture, where <c>```sh title A b c</c> yields
/// <c>&lt;ac:parameter ac:name="title"&gt;A b c&lt;/ac:parameter&gt;</c>. Keyword
/// matching is case-insensitive (mark's is not), which only ever widens what is
/// understood. A repeated attribute takes its last value.
/// </para>
/// <para>
/// An <em>unrecognized</em> attribute fails loud with
/// <see cref="NotSupportedException"/>. That asymmetry against the language token is
/// deliberate: an unknown language costs highlighting, whereas a dropped attribute
/// publishes a page the author did not ask for. It is also a deliberate deviation
/// from mark, which treats any unknown token as a Confluence Server <c>theme</c>
/// name — so mark silently turns a typo (<c>colapse</c>) or the <c>title=Foo</c>
/// spelling into a bogus <c>theme</c> parameter. Confluence Cloud's Code Block macro
/// documents only <c>language</c>, <c>title</c>, <c>collapse</c>,
/// <c>linenumbers</c> and <c>firstline</c> (theme is an admin-level default, not a
/// per-macro parameter), so those five are what DocuMe emits.
/// </para>
/// <para>
/// Parameter order mirrors mark's <c>ac:code</c> template so a DocuMe page and a
/// mark page produce comparable storage. Unlike mark, an absent parameter is
/// omitted rather than written with its Confluence default: an unconditional
/// <c>collapse=false</c> on every code block would be churn in the published body
/// and in the approval hash (§8) for no rendering difference.
/// </para>
/// The body is wrapped in CDATA; any literal <c>]]&gt;</c> is split so the fragment
/// stays well-formed XML.
/// </remarks>
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

        // Systems and application languages.
        ["rust"] = "rust", ["rs"] = "rust",
        ["c"] = "c",
        ["cpp"] = "cpp", ["c++"] = "cpp",
        ["objectivec"] = "objectivec", ["objective-c"] = "objectivec", ["objc"] = "objectivec",
        ["swift"] = "swift",
        ["d"] = "d",
        ["pascal"] = "pascal", ["objectpascal"] = "pascal",
        ["ada"] = "ada",
        ["fortran"] = "fortran",
        ["vala"] = "vala",
        ["haxe"] = "haxe",

        // JVM and .NET family.
        ["kotlin"] = "kotlin", ["kt"] = "kotlin", ["kts"] = "kotlin",
        ["scala"] = "scala",
        ["groovy"] = "groovy",
        ["vbnet"] = "vbnet", ["vb.net"] = "vbnet",
        ["vb"] = "visual-basic", ["visualbasic"] = "visual-basic", ["vba"] = "visual-basic",

        // Scripting languages.
        ["perl"] = "perl", ["pl"] = "perl", ["pm"] = "perl",
        ["lua"] = "lua",
        ["r"] = "r",
        ["dart"] = "dart",
        ["julia"] = "julia", ["jl"] = "julia",
        ["matlab"] = "matlab",
        ["tcl"] = "tcl",
        ["coffeescript"] = "coffeescript", ["coffee"] = "coffeescript",
        ["livescript"] = "livescript",
        ["actionscript"] = "actionscript",
        ["applescript"] = "applescript",
        ["autoit"] = "autoit",
        ["coldfusion"] = "cfscript", ["cfscript"] = "cfscript", ["cfc"] = "cfscript",

        // Functional languages.
        ["haskell"] = "haskell", ["hs"] = "haskell",
        ["erlang"] = "erlang", ["erl"] = "erlang",
        ["elixir"] = "elixir",
        ["ocaml"] = "ocaml",
        ["clojure"] = "clojure", ["clj"] = "clojure", ["cljs"] = "clojure",
        ["scheme"] = "scheme",
        ["racket"] = "racket", ["rkt"] = "racket",
        ["sml"] = "sml", ["standardml"] = "sml", ["smlnj"] = "sml",
        ["prolog"] = "prolog",
        ["smalltalk"] = "smalltalk",

        // Web and markup adjacent.
        ["jsx"] = "jsx",
        ["tsx"] = "tsx",
        ["sass"] = "sass", ["scss"] = "scss",
        ["graphql"] = "graphql", ["gql"] = "graphql",
        ["xquery"] = "xquery",
        ["rest"] = "rest", ["rst"] = "rest", ["restructuredtext"] = "rest",
        ["tex"] = "latex", ["latex"] = "latex",

        // Infrastructure and configuration.
        ["dockerfile"] = "docker", ["docker"] = "docker",
        ["hcl"] = "hcl", ["terraform"] = "hcl", ["tf"] = "hcl",
        ["nginx"] = "nginx",
        ["protobuf"] = "protobuf", ["proto"] = "protobuf",

        // Hardware description and enterprise.
        ["verilog"] = "verilog",
        ["vhdl"] = "vhdl",
        ["abap"] = "abap",
        ["puppet"] = "puppet",
        ["qml"] = "qml",
        ["splunk-spl"] = "splunk-spl", ["splunkspl"] = "splunk-spl",
        ["mathematica"] = "mathematica",
    };

    /// <summary>
    /// Diagram-as-code fence languages that render to a picture on GitHub or GitLab but that
    /// DocuMe has no render path for. See <see cref="RejectUnrenderedDiagramDialect"/> for why
    /// these fail loud while an unknown <em>programming</em> language does not.
    /// </summary>
    private static readonly HashSet<string> DiagramDialects = new(StringComparer.OrdinalIgnoreCase)
    {
        "plantuml", "puml", "graphviz", "dot", "d2",
    };

    protected override void Write(ConfluenceStorageRenderer renderer, FencedCodeBlock obj)
    {
        var infoLine = ReadInfoLine(obj);

        if (TryWriteDiagram(renderer, obj, infoLine))
        {
            return;
        }

        var attributes = ParseInfoLine(infoLine);

        if (attributes.UnknownLanguage is { } unknownLanguage)
        {
            var message =
                $"Fence language '{unknownLanguage}' has no Confluence brush in the renderer's "
                + "language map, so the code macro is emitted without a 'language' parameter and "
                + "the block publishes unhighlighted. Every character the author wrote is "
                + "preserved; only syntax colouring is lost.";
            renderer.Report(ConversionDiagnosticCodes.UnknownFenceLanguage, unknownLanguage, message);
        }

        renderer.Write("<ac:structured-macro ac:name=\"code\">");

        if (attributes.Language is { } language)
        {
            WriteParameter(renderer, "language", language);
        }

        if (attributes.Collapse)
        {
            WriteParameter(renderer, "collapse", "true");
        }

        if (attributes.LineNumbers)
        {
            WriteParameter(renderer, "linenumbers", "true");
        }

        if (attributes.FirstLine is { } firstLine)
        {
            WriteParameter(renderer, "firstline", firstLine.ToString(CultureInfo.InvariantCulture));
        }

        if (attributes.Title is { } title)
        {
            WriteParameter(renderer, "title", title);
        }

        renderer.Write("<ac:plain-text-body><![CDATA[");
        renderer.Write(ExtractCode(obj).Replace("]]>", "]]]]><![CDATA[>", StringComparison.Ordinal));
        renderer.Write("]]></ac:plain-text-body></ac:structured-macro>").Write('\n');
    }

    /// <summary>Writes one macro parameter, escaping the value as element content.</summary>
    private static void WriteParameter(ConfluenceStorageRenderer renderer, string name, string value)
    {
        renderer.Write("<ac:parameter ac:name=\"")
            .Write(name)
            .Write("\">")
            .WriteEscaped(value)
            .Write("</ac:parameter>");
    }

    /// <summary>
    /// Renders a <c>```mermaid</c> fence as the diagram attachment of PLAN.md §7 and
    /// returns <c>true</c>; returns <c>false</c> for a fence that really is code, which the
    /// caller then renders as a code macro.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shape is the standalone-image shape the rest of the converter already produces
    /// for <c>![alt](file.svg)</c> alone on a line — <c>&lt;p&gt;</c> wrapping an
    /// <c>&lt;ac:image&gt;</c> wrapping an <c>&lt;ri:attachment&gt;</c> — because a mermaid
    /// fence <em>is</em> a standalone diagram and two spellings of the same thing would be
    /// gratuitous. §7 line 281 additionally shows an <c>ac:width</c>, which is
    /// <strong>omitted</strong>: measuring the rendered SVG means opening it, and this
    /// converter never touches the filesystem. Confluence scales an attached SVG natively,
    /// so an omitted width beats a fabricated one.
    /// </para>
    /// <para>
    /// No <c>ac:alt</c> or <c>ac:title</c> either: a fence carries no alt text, and
    /// synthesizing one ("Mermaid diagram") would put words the author never wrote into the
    /// body and therefore into the approval hash (§8), the same reason the images slice
    /// refused to fabricate attributes.
    /// </para>
    /// <para>
    /// Everything here fails loud rather than falling back to a code macro. Publishing the
    /// diagram <em>source</em> where a picture belongs is precisely the silent wrongness §7's
    /// fail-loud contract exists to prevent — and it is what this renderer did before this
    /// slice, because <see cref="MapLanguage"/> returns <c>null</c> for any language outside
    /// <see cref="LanguageMap"/> and a null language merely omits the parameter.
    /// </para>
    /// </remarks>
    private static bool TryWriteDiagram(ConfluenceStorageRenderer renderer, FencedCodeBlock obj, string infoLine)
    {
        var language = FirstToken(infoLine, out var rest);

        if (!string.Equals(language, "mermaid", StringComparison.OrdinalIgnoreCase))
        {
            RejectUnrenderedDiagramDialect(language);
            return false;
        }

        if (rest.Length > 0)
        {
            // The code macro's attributes (collapse, title, …) have no counterpart on an
            // <ac:image>, so accepting them here would silently drop what the author asked
            // for. Kept symmetric with the unknown-attribute rejection below.
            throw new NotSupportedException(
                $"Mermaid fence carries '{rest}' on its info line. A mermaid fence renders to a "
                + "diagram attachment, which has no code-macro parameters (collapse, linenumbers, "
                + "title, firstline) to apply. Write ```mermaid on its own.");
        }

        var source = ExtractCode(obj);
        if (source.AsSpan().Trim().IsEmpty)
        {
            throw new NotSupportedException(
                "Mermaid fence is empty, so there is no diagram to render. Remove the fence or "
                + "add diagram source.");
        }

        var resolver = renderer.MermaidResolver
            ?? throw new InvalidOperationException(
                "A ```mermaid fence requires a mermaid diagram resolver, but none was supplied "
                + "to ConfluenceStorageConverter.Convert. Without one the diagram source would "
                + "publish as a code block instead of a picture.");
        var filename = resolver(source)
            ?? throw new InvalidOperationException(
                "A ```mermaid fence did not render to a diagram attachment (the renderer "
                + "reported failure). Fix the diagram source, or check that Node and "
                + "render-mermaid.mjs are available (PLAN.md §4).");

        renderer.Write("<p><ac:image><ri:attachment ri:filename=\"")
            .WriteAttributeEscaped(filename)
            .Write("\"/></ac:image></p>")
            .Write('\n');

        return true;
    }

    /// <summary>
    /// Fails loud on the diagram-as-code dialects DocuMe does <em>not</em> render.
    /// </summary>
    /// <remarks>
    /// Deliberate asymmetry against an unknown <em>language</em>, which keeps degrading to an
    /// unlabelled code macro. An unknown language costs syntax highlighting and preserves
    /// every character the author wrote — cosmetic, and failing a whole page over it would
    /// wreck the §4.4 acceptance run for no reader-visible gain. These tokens are different
    /// in kind: GitHub and GitLab render them as pictures, so the author wrote them expecting
    /// a diagram, and degrading them to source text loses the meaning rather than the
    /// styling. Mermaid is the one dialect DocuMe has a render path for (§4), so the rest
    /// have to say so out loud.
    /// </remarks>
    private static void RejectUnrenderedDiagramDialect(string language)
    {
        if (!DiagramDialects.Contains(language))
        {
            return;
        }

        throw new NotSupportedException(
            $"Fence language '{language}' is a diagram dialect DocuMe cannot render — it would "
            + "publish as source text where a picture belongs. Only ```mermaid diagrams are "
            + "rendered (PLAN.md §4). Convert the diagram to mermaid, or attach it as an image.");
    }

    /// <summary>
    /// Rejoins Markdig's split info line. Its default parser splits at the first whitespace
    /// (<c>Info</c> holds the first token, <c>Arguments</c> the trimmed remainder), so
    /// rejoining lets the attribute syntax stay independent of where Markdig chose to split.
    /// </summary>
    private static string ReadInfoLine(FencedCodeBlock obj) =>
        string.IsNullOrEmpty(obj.Arguments)
            ? obj.Info ?? string.Empty
            : obj.Info + " " + obj.Arguments;

    /// <summary>
    /// Splits the info line into its first whitespace-delimited token and the trimmed
    /// remainder. Both are empty for a bare <c>```</c> fence.
    /// </summary>
    private static string FirstToken(string infoLine, out string rest)
    {
        var span = infoLine.AsSpan().TrimStart();
        var end = span.IndexOfAny(' ', '\t');
        if (end < 0)
        {
            rest = string.Empty;
            return span.ToString();
        }

        rest = span[end..].Trim().ToString();
        return span[..end].ToString();
    }

    /// <summary>
    /// Parses the fence info line into the code macro's parameters, throwing
    /// <see cref="NotSupportedException"/> on an attribute this converter does not
    /// understand rather than dropping it.
    /// </summary>
    private static CodeFenceAttributes ParseInfoLine(string line)
    {
        string? language = null;
        string? unknownLanguage = null;
        string? title = null;
        int? firstLine = null;
        var collapse = false;
        var lineNumbers = false;
        var expectLanguage = true;
        var position = 0;

        while (position < line.Length)
        {
            while (position < line.Length && char.IsWhiteSpace(line[position]))
            {
                position++;
            }

            if (position == line.Length)
            {
                break;
            }

            var start = position;
            while (position < line.Length && !char.IsWhiteSpace(line[position]))
            {
                position++;
            }

            var token = line[start..position];

            // Only the first token can be a language, and only if it is not itself
            // an attribute — `​```collapse` means "collapse, no language", per mark.
            if (expectLanguage)
            {
                expectLanguage = false;
                if (!IsAttribute(token))
                {
                    language = MapLanguage(token);

                    // `-` is mark's explicit "no language" spelling, so an omitted brush is
                    // exactly what its author asked for and nothing is lost. Every other
                    // unmapped token is a language the renderer did not recognize.
                    if (language is null && !string.Equals(token, "-", StringComparison.Ordinal))
                    {
                        unknownLanguage = token;
                    }

                    continue;
                }
            }

            if (string.Equals(token, "title", StringComparison.OrdinalIgnoreCase))
            {
                // The title is the rest of the line, unquoted, spaces and all.
                title = line[position..].Trim();
                if (title.Length == 0)
                {
                    throw new NotSupportedException(
                        "Code fence attribute 'title' has no value. Write `title My Title`, or drop the keyword.");
                }

                break;
            }

            if (string.Equals(token, "collapse", StringComparison.OrdinalIgnoreCase))
            {
                collapse = true;
                continue;
            }

            if (string.Equals(token, "nocollapse", StringComparison.OrdinalIgnoreCase))
            {
                collapse = false;
                continue;
            }

            if (string.Equals(token, "linenumbers", StringComparison.OrdinalIgnoreCase))
            {
                lineNumbers = true;
                continue;
            }

            // A bare number is Confluence's firstline, which implies line numbering.
            if (TryParseFirstLine(token, out var parsed))
            {
                firstLine = parsed;
                lineNumbers = true;
                continue;
            }

            throw new NotSupportedException(
                $"Unsupported code fence attribute '{token}'. Supported: collapse, nocollapse, "
                + "linenumbers, a positive line number, and `title <text>` (the title is the rest of the line, "
                + "not title=<text>). A language, if given, must come first; write `-` for none.");
        }

        return new CodeFenceAttributes(language, unknownLanguage, collapse, lineNumbers, firstLine, title);
    }

    private static bool IsAttribute(string token) =>
        string.Equals(token, "collapse", StringComparison.OrdinalIgnoreCase)
        || string.Equals(token, "nocollapse", StringComparison.OrdinalIgnoreCase)
        || string.Equals(token, "linenumbers", StringComparison.OrdinalIgnoreCase)
        || string.Equals(token, "title", StringComparison.OrdinalIgnoreCase)
        || int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    /// <summary>
    /// Parses a firstline token. <see cref="NumberStyles.None"/> rejects a sign, so a
    /// negative number is an unknown attribute rather than a silently clamped one;
    /// <c>0</c> is rejected here too because Confluence numbers from 1.
    /// </summary>
    private static bool TryParseFirstLine(string token, out int firstLine)
    {
        if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
        {
            firstLine = parsed;
            return true;
        }

        firstLine = 0;
        return false;
    }

    private static string? MapLanguage(string token) =>
        LanguageMap.TryGetValue(token, out var language) ? language : null;

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

    /// <summary>
    /// Code macro parameters read off a fence info line. A <c>null</c> reference or
    /// <c>false</c> means "not requested", and the parameter is omitted entirely.
    /// </summary>
    /// <param name="UnknownLanguage">
    /// The language token when it mapped to no Confluence brush, for the caller to report as a
    /// degradation; <c>null</c> when the fence carried no language, carried mark's explicit
    /// <c>-</c>, or carried a mapped one. Kept here rather than reported from the parser so
    /// info-line parsing stays a pure function.
    /// </param>
    private readonly record struct CodeFenceAttributes(
        string? Language,
        string? UnknownLanguage,
        bool Collapse,
        bool LineNumbers,
        int? FirstLine,
        string? Title);
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

/// <summary>
/// <c>**bold**</c>/<c>__bold__</c> → <c>&lt;strong&gt;</c>;
/// <c>*italic*</c>/<c>_italic_</c> → <c>&lt;em&gt;</c>;
/// <c>~~struck~~</c> → <c>&lt;span style="text-decoration: line-through;"&gt;</c>.
/// </summary>
/// <remarks>
/// The strikethrough spelling is Atlassian's documented one and the shape the
/// Confluence editor itself produces (confmark emits <em>and</em> parses only this
/// form). It is a deliberate divergence from mark, which emits <c>&lt;del&gt;</c> —
/// but by omission rather than by decision: mark registers no strikethrough
/// renderer and so inherits goldmark's default HTML tag. A tag Confluence rewrites
/// on save would make every republish see drift against the stored body and churn
/// the approval content hash (§8), so the editor's own shape is the safer contract.
/// <para>
/// Any other delimiter character fails loud. Unreachable with the pipeline's
/// Strikethrough-only options, but a future pipeline that enabled Markdig's other
/// emphasis extras would otherwise render <c>^sup^</c> as <c>&lt;em&gt;</c> and
/// <c>==mark==</c> as <c>&lt;strong&gt;</c> — silent, wrong, and plausible.
/// </para>
/// </remarks>
internal sealed class EmphasisRenderer : MarkdownObjectRenderer<ConfluenceStorageRenderer, EmphasisInline>
{
    protected override void Write(ConfluenceStorageRenderer renderer, EmphasisInline obj)
    {
        if (obj.DelimiterChar == '~' && obj.DelimiterCount == 2)
        {
            renderer.Write("<span style=\"text-decoration: line-through;\">");
            renderer.WriteChildren(obj);
            renderer.Write("</span>");
            return;
        }

        if (obj.DelimiterChar is not ('*' or '_'))
        {
            throw new NotSupportedException(
                $"No storage-format mapping for emphasis delimiter '{obj.DelimiterChar}' " +
                $"repeated {obj.DelimiterCount} time(s). Only **bold**, *italic* and " +
                "~~strikethrough~~ are supported by the M1 converter.");
        }

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
/// Images (<c>![alt](src)</c>) are also a <see cref="LinkInline"/> — Markdig flags them
/// with <see cref="LinkInline.IsImage"/> — so they are dispatched from here to
/// <see cref="WriteImage"/>. Relative non-<c>.md</c> link targets fail loud.
/// </summary>
internal sealed class LinkInlineRenderer : MarkdownObjectRenderer<ConfluenceStorageRenderer, LinkInline>
{
    protected override void Write(ConfluenceStorageRenderer renderer, LinkInline obj)
    {
        if (obj.IsImage)
        {
            WriteImage(renderer, obj);
            return;
        }

        var url = obj.Url ?? string.Empty;

        // Same-page anchor (spike S2 default): drop the anchor, keep the link text —
        // there is no destination page and heading anchors may not survive.
        if (url.StartsWith('#'))
        {
            var message =
                $"Same-page anchor link '{url}' publishes as its link text with no link at all: "
                + "spike S2 does not assume Confluence's editor preserves heading anchors, so the "
                + "destination is dropped. This is the one degradation that removes a "
                + "destination rather than styling.";
            renderer.Report(ConversionDiagnosticCodes.SamePageAnchorLink, url, message);
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

    /// <summary>
    /// Writes <c>![alt](src)</c> as <c>&lt;ac:image&gt;</c> wrapping either an
    /// <c>&lt;ri:attachment&gt;</c> (local file) or an <c>&lt;ri:url&gt;</c> (external URL) —
    /// the two forms Atlassian's storage-format reference documents, and the two
    /// kovetskiy/mark emits from the same template (<c>stdlib/stdlib.go</c>, <c>ac:image</c>).
    /// <para>
    /// Only <c>ac:title</c> and <c>ac:alt</c> are emitted, in mark's order. mark's other
    /// attributes (<c>ac:width</c>, <c>ac:original-width</c>, <c>ac:align</c>,
    /// <c>ac:layout</c>) are all derived from either CLI config or the pixel dimensions of
    /// the image file on disk; this converter is a pure text transform and never opens a
    /// file, so inventing them here is not possible. Both are omitted when absent rather
    /// than written empty, so <c>![](p.png)</c> yields a bare <c>&lt;ac:image&gt;</c>.
    /// </para>
    /// </summary>
    private static void WriteImage(ConfluenceStorageRenderer renderer, LinkInline obj)
    {
        RejectTrailingAttributeBlock(obj);

        var url = obj.Url ?? string.Empty;
        if (url.Length == 0)
        {
            throw new NotSupportedException(
                "Image syntax with an empty source ('![alt]()') has no attachment or URL to " +
                "reference, so it cannot become an <ac:image>.");
        }

        var external = IsExternal(url);
        if (external && url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            // <ri:url> takes a fetchable URL; Confluence will not inline a base64
            // payload, so this would publish a visibly broken image. Rejected here
            // with the other source checks, before anything is written.
            throw new NotSupportedException(
                "Image source is a 'data:' URI, which <ri:url ri:value=\"…\"> cannot " +
                "reference. Save the image to a file in the wiki and link it relatively.");
        }

        renderer.Write("<ac:image");

        if (!string.IsNullOrEmpty(obj.Title))
        {
            renderer.Write(" ac:title=\"").WriteAttributeEscaped(obj.Title).Write('"');
        }

        var alt = FlattenAltText(obj);
        if (alt.Length > 0)
        {
            renderer.Write(" ac:alt=\"").WriteAttributeEscaped(alt).Write('"');
        }

        renderer.Write('>');

        if (external)
        {
            renderer.Write("<ri:url ri:value=\"").WriteAttributeEscaped(url).Write("\"/>");
        }
        else
        {
            var resolver = renderer.AttachmentResolver
                ?? throw new InvalidOperationException(
                    $"Local image '{url}' requires an attachment resolver, but none was " +
                    "supplied to ConfluenceStorageConverter.Convert.");
            var filename = resolver(url)
                ?? throw new InvalidOperationException(
                    $"Local image '{url}' does not resolve to any known file (broken image " +
                    "reference). Fix the path or add the file.");

            renderer.Write("<ri:attachment ri:filename=\"").WriteAttributeEscaped(filename).Write("\"/>");
        }

        renderer.Write("</ac:image>");
    }

    /// <summary>
    /// Flattens the image's children — its alt text — to the plain string that
    /// <c>ac:alt</c> can hold. Emphasis contributes its inner text only: markers have no
    /// meaning inside an XML attribute that Confluence renders as an accessibility string,
    /// and both reference implementations flatten the same way (mark's
    /// <c>nodeToHTMLText</c>; confmark models alt as a plain <c>String</c>). Anything whose
    /// text projection would <em>lose a destination</em> — a nested link or image — fails
    /// loud instead.
    /// </summary>
    private static string FlattenAltText(LinkInline obj)
    {
        var alt = new StringBuilder();
        Append(obj, alt);
        return alt.ToString();

        static void Append(ContainerInline container, StringBuilder alt)
        {
            foreach (var child in container)
            {
                switch (child)
                {
                    case LiteralInline literal:
                        alt.Append(literal.Content.AsSpan());
                        break;
                    case CodeInline code:
                        alt.Append(code.Content);
                        break;

                    // The resolved character, matching HtmlEntityInlineRenderer: ac:alt is
                    // an XML attribute, so an HTML-only entity name would be as undefined
                    // there as in the body, and WriteAttributeEscaped re-escapes whatever
                    // this resolves to (a `&quot;` becomes `"` here and `&quot;` again on
                    // the way out, so it cannot break out of the attribute).
                    case HtmlEntityInline entity:
                        alt.Append(entity.Transcoded.AsSpan());
                        break;
                    case LineBreakInline:
                        alt.Append(' ');
                        break;
                    case EmphasisInline emphasis:
                        Append(emphasis, alt);
                        break;
                    default:
                        throw new NotSupportedException(
                            $"Image alt text contains a '{child.GetType().Name}', which has no " +
                            "plain-text projection for the ac:alt attribute. Simplify the alt text.");
                }
            }
        }
    }

    /// <summary>
    /// PLAN.md §7 offers <c>{width=300}</c> after an image as a way to set
    /// <c>ac:width</c>. With no attributes extension in the pipeline Markdig parses it as an
    /// ordinary <see cref="LiteralInline"/> in the enclosing paragraph (verified against the
    /// real parse tree), so honoring it needs its own slice — either Markdig's
    /// GenericAttributes extension or hand-consuming this sibling. Until then it must fail
    /// loud: rendering the image and letting <c>{width=300}</c> through as body text would
    /// publish visible junk beside the image.
    /// </summary>
    private static void RejectTrailingAttributeBlock(LinkInline obj)
    {
        if (obj.NextSibling is not LiteralInline next || !next.Content.AsSpan().StartsWith("{"))
        {
            return;
        }

        throw new NotSupportedException(
            $"Image is followed immediately by '{next.Content}'. An attribute block such as " +
            "'{width=300}' (PLAN.md §7) is not yet honored, and publishing it as text beside " +
            "the image would be visible junk. Remove it or put a space before it.");
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
/// <para>
/// Emits no <see cref="ConversionDiagnostic"/>: a definition is metadata with no rendering on
/// GitHub either, whether or not any <c>[text][ref]</c> uses it, so dropping it loses nothing
/// (an <em>unresolved</em> reference stays literal text, exactly as GitHub shows it). Reporting
/// it would put a warning on a lossless construct and make §4.4's "zero unknown-construct
/// warnings" unreachable for a non-loss.
/// </para>
/// </summary>
internal sealed class LinkReferenceDefinitionGroupRenderer
    : MarkdownObjectRenderer<ConfluenceStorageRenderer, LinkReferenceDefinitionGroup>
{
    protected override void Write(ConfluenceStorageRenderer renderer, LinkReferenceDefinitionGroup obj)
    {
        // Intentionally emits nothing.
    }
}

/// <summary>
/// HTML <em>blocks</em>. A block that holds nothing but HTML comments is dropped from the
/// output (§7: "markers are repo-side concerns") — DocuMe's own refresh workflow writes
/// <c>&lt;!-- HAND-EDITED START --&gt;</c> / <c>&lt;!-- HAND-EDITED END --&gt;</c> markers into
/// consumer wikis (§6, §9), so a page carrying one has to convert. Every other HTML block
/// fails loud.
/// </summary>
/// <remarks>
/// <para>
/// Dropping on <see cref="HtmlBlock.Type"/> alone would be silently destructive, which the
/// parse tree makes plain. CommonMark's comment block (type 2) starts at a line beginning
/// <c>&lt;!--</c> and ends at the line <em>containing</em> <c>--&gt;</c>, and the whole of
/// that closing line belongs to the block — so <c>&lt;!-- c --&gt; tail</c> is one
/// comment-typed block carrying the author's <c>tail</c>. Worse, an unterminated
/// <c>&lt;!-- oops</c> runs to the end of its container, swallowing every following
/// paragraph into one comment-typed block. Both were verified against the real tree.
/// A blanket drop would therefore delete author content with no error at all.
/// </para>
/// <para>
/// Hence the contract is <em>comment-only</em>: the block is dropped when
/// <see cref="IsCommentOnly"/> can account for all of its text as well-formed comments plus
/// whitespace, and throws otherwise. Failing loud on <c>&lt;!-- c --&gt; tail</c> rejects a
/// line GitHub does render, which is the deliberate trade: the tail was never inline-parsed,
/// so emitting it would publish any markdown in it (<c>**bold**</c>, a link) as literal text.
/// A one-line error the author fixes by moving the comment beats either silent loss or
/// silent mangling — the same call the fence-attribute and <c>{width=300}</c> paths make.
/// </para>
/// </remarks>
internal sealed class HtmlBlockRenderer : MarkdownObjectRenderer<ConfluenceStorageRenderer, HtmlBlock>
{
    protected override void Write(ConfluenceStorageRenderer renderer, HtmlBlock obj)
    {
        var text = ReadLines(obj);

        if (obj.Type != HtmlBlockType.Comment)
        {
            // Storage format is not HTML: Confluence rejects or rewrites raw markup on
            // save, so a passed-through tag is drift against the stored body and churn in
            // the approval hash (§8) even when it appears to work.
            throw new NotSupportedException(
                $"No storage-format renderer for a raw HTML block ({Describe(text)}). Only HTML "
                + "comments are supported, and they are dropped from the output (PLAN.md §7); "
                + "storage format is not HTML, so a raw tag has no reliable mapping. Express the "
                + "content in markdown, or use a Confluence macro.");
        }

        if (IsCommentOnly(text, out var remainder))
        {
            // Intentionally emits nothing — not even a newline, so a comment between two
            // paragraphs leaves no blank line and no empty <p></p> behind it.
            return;
        }

        if (remainder.StartsWith("<!--", StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"Unterminated HTML comment ({Describe(remainder)}). Markdown runs an unclosed "
                + "comment to the end of the document, so everything after it would be dropped "
                + "as comment text rather than published. Close it with '-->'.");
        }

        throw new NotSupportedException(
            $"An HTML comment shares its line with content that is not a comment "
            + $"({Describe(remainder)}). Markdown treats the whole line as one HTML block, so "
            + "that content never became markdown and publishing it would emit it literally "
            + "(a '**bold**' would stay asterisks). Put the comment on a line of its own.");
    }

    /// <summary>
    /// True when <paramref name="text"/> is a run of well-formed <c>&lt;!-- … --&gt;</c>
    /// comments separated only by whitespace, so dropping the block loses nothing the author
    /// wrote. Otherwise <paramref name="remainder"/> is the first text that could not be
    /// accounted for, which the caller turns into a diagnostic.
    /// </summary>
    private static bool IsCommentOnly(string text, out string remainder)
    {
        var position = 0;
        while (true)
        {
            while (position < text.Length && char.IsWhiteSpace(text[position]))
            {
                position++;
            }

            if (position == text.Length)
            {
                remainder = string.Empty;
                return true;
            }

            if (!text.AsSpan(position).StartsWith("<!--", StringComparison.Ordinal))
            {
                remainder = text[position..].Trim();
                return false;
            }

            // Search from past the opener so its own '--' cannot double as the closer:
            // HTML5's abrupt-closing forms ('<!-->', '<!--->') are left to fail loud rather
            // than guessed at, while the canonical empty comment '<!---->' still matches.
            var close = text.IndexOf("-->", position + 4, StringComparison.Ordinal);
            if (close < 0)
            {
                remainder = text[position..].Trim();
                return false;
            }

            position = close + 3;
        }
    }

    /// <summary>Rejoins the block's raw lines with <c>\n</c>, exactly as the parser sliced them.</summary>
    private static string ReadLines(HtmlBlock obj)
    {
        var lines = obj.Lines;
        var slices = lines.Lines;
        var text = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                text.Append('\n');
            }

            text.Append(slices[i].Slice.AsSpan());
        }

        return text.ToString();
    }

    /// <summary>
    /// Quotes a one-line excerpt for an error message, so a block that swallowed half a page
    /// still produces a readable diagnostic.
    /// </summary>
    private static string Describe(string text)
    {
        var firstLine = text.AsSpan();
        var breakAt = firstLine.IndexOf('\n');
        if (breakAt >= 0)
        {
            firstLine = firstLine[..breakAt];
        }

        var excerpt = firstLine.Length > 60 ? string.Concat(firstLine[..60], "…") : firstLine.ToString();
        return firstLine.Length < text.Length ? $"'{excerpt}' …" : $"'{excerpt}'";
    }
}

/// <summary>
/// Inline raw HTML. An inline HTML <em>comment</em> is dropped (§7, the same rule
/// <see cref="HtmlBlockRenderer"/> applies to block comments); any other inline tag fails
/// loud.
/// </summary>
/// <remarks>
/// The comment node is dropped and nothing else is touched, so
/// <c>Text with an &lt;!-- c --&gt; comment.</c> keeps both surrounding spaces and publishes
/// as <c>Text with an  comment.</c>. That double space is deliberate. Collapsing it would
/// make the renderer an editor of the author's whitespace, and the same rule would then owe
/// an answer for a leading space, a trailing space and a heading — a widening surface for no
/// gain, because XHTML collapses consecutive whitespace when rendered, so a Confluence reader
/// sees exactly one space either way. Keeping the transform "drop the node, touch nothing
/// else" is also what makes the §8 content hash explainable.
/// </remarks>
internal sealed class HtmlInlineRenderer : MarkdownObjectRenderer<ConfluenceStorageRenderer, HtmlInline>
{
    protected override void Write(ConfluenceStorageRenderer renderer, HtmlInline obj)
    {
        var tag = obj.Tag ?? string.Empty;

        // As in the block renderer, the closer must start past the opener, so the opener's
        // own '--' cannot serve as it.
        if (tag.StartsWith("<!--", StringComparison.Ordinal)
            && tag.AsSpan(4).EndsWith("-->", StringComparison.Ordinal))
        {
            return;
        }

        throw new NotSupportedException(
            $"No storage-format renderer for inline raw HTML '{tag}'. Only HTML comments are "
            + "supported inline, and they are dropped from the output (PLAN.md §7); storage "
            + "format is not HTML, so Confluence would reject or rewrite the tag on save. Use "
            + "markdown, or a Confluence macro.");
    }
}

/// <summary>
/// A task marker reached here belongs to a list that is <em>not</em> all-task (see
/// <see cref="ListRenderer.TryGetTaskMarkers"/>): the list rendered as a plain
/// <c>&lt;ul&gt;</c>/<c>&lt;ol&gt;</c>, so the marker is written back as text and the
/// item's completion state stays visible instead of being silently dropped. This is
/// mark's fallback too (<c>renderer/tasklist.go</c>, <c>renderTaskCheckBox</c>),
/// confirmed against its <c>testdata/tasklists-mixed.html</c> fixture, which renders
/// <c>&lt;li&gt;[x] task item&lt;/li&gt;</c>.
/// <para>
/// PLAN.md §7 words this fallback as "emoji markers", but mark — the reference §7
/// names for task lists — emits the source spelling, and echoing what the author
/// typed invents no content. The one normalization is case: <c>[X]</c> is written
/// back as <c>[x]</c>, since <see cref="TaskList"/> keeps only the boolean.
/// </para>
/// </summary>
internal sealed class TaskListRenderer : MarkdownObjectRenderer<ConfluenceStorageRenderer, TaskList>
{
    protected override void Write(ConfluenceStorageRenderer renderer, TaskList obj)
    {
        // No trailing space: unlike the task-list path this leaves the marker's
        // separator on the following literal, so the item reads `[x] text` once.
        renderer.Write(obj.Checked ? "[x]" : "[ ]");
    }
}

internal sealed class LiteralInlineRenderer : MarkdownObjectRenderer<ConfluenceStorageRenderer, LiteralInline>
{
    protected override void Write(ConfluenceStorageRenderer renderer, LiteralInline obj)
        => renderer.WriteEscaped(obj.Content.AsSpan());
}

/// <summary>
/// A character reference — named (<c>&amp;copy;</c>) or numeric (<c>&amp;#169;</c>,
/// <c>&amp;#xA9;</c>) — is published as the <em>character it resolves to</em>, taken from
/// Markdig's <see cref="HtmlEntityInline.Transcoded"/> and put back through
/// <see cref="ConfluenceStorageRenderer.WriteEscaped"/>.
/// <para>
/// The choice is resolved character over source spelling because storage format is XML, and
/// XML predefines only five entity names (<c>&amp;amp;</c>, <c>&amp;lt;</c>,
/// <c>&amp;gt;</c>, <c>&amp;quot;</c>, <c>&amp;apos;</c>). An HTML-only name such as
/// <c>&amp;copy;</c> or <c>&amp;nbsp;</c> is undefined in an XML document, so echoing it
/// would invite Confluence to reject the body or silently rewrite it on save — and a
/// server-side rewrite is §8 hash drift, which invalidates approvals nothing changed
/// (§9.2). Same reasoning as the strikethrough decision, which emits the editor's own
/// shape rather than the shape that reads best in source. Resolved characters are plain
/// UTF-8 and unambiguous.
/// </para>
/// <para>
/// Escaping on the way out is what keeps the round-trip honest: <c>&amp;amp;</c>
/// transcodes to <c>&amp;</c> and is re-escaped to <c>&amp;amp;</c>, never
/// <c>&amp;amp;amp;</c>. <c>&amp;amp;</c>, <c>&amp;lt;</c> and <c>&amp;gt;</c> therefore
/// come out byte-identical; <c>&amp;quot;</c> and <c>&amp;apos;</c> resolve to a bare
/// <c>"</c> and <c>'</c>, which need no escaping in element content (they are escaped
/// again by <see cref="ConfluenceStorageRenderer.WriteAttributeEscaped"/> when the text
/// lands in an attribute instead). A reference Markdig does
/// not recognize (<c>&amp;nosuchthing;</c>, or <c>&amp;copy</c> with no semicolon) never
/// becomes one of these nodes at all — it stays a <see cref="LiteralInline"/> and is
/// escaped as the literal text the author typed.
/// </para>
/// </summary>
internal sealed class HtmlEntityInlineRenderer
    : MarkdownObjectRenderer<ConfluenceStorageRenderer, HtmlEntityInline>
{
    protected override void Write(ConfluenceStorageRenderer renderer, HtmlEntityInline obj)
        => renderer.WriteEscaped(obj.Transcoded.AsSpan());
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
