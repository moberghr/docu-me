using DocuMe.Core.Markdown;
using Shouldly;

namespace DocuMe.Core.Tests.Markdown;

/// <summary>
/// Direct converter assertions that a golden file cannot express — chiefly the
/// fail-loud contract for constructs the M1 seed does not yet render (PLAN.md §7).
/// Rendering behavior itself is pinned by the golden-file suite.
/// </summary>
public sealed class ConfluenceStorageConverterTests
{
    [Theory]

    // An indented CodeBlock (only fenced code is supported), then an HtmlBlock.
    [InlineData("    indented code")]
    [InlineData("<div>raw html</div>")]
    public void Convert_throws_on_unsupported_block_construct(string markdown)
    {
        // Every construct above lacks a dedicated renderer, so the catch-all must
        // throw rather than silently drop or mis-transform it.
        var ex = Should.Throw<NotSupportedException>(() => ConfluenceStorageConverter.Convert(markdown));
        ex.Message.ShouldContain("No storage-format renderer");
    }

    [Fact]
    public void Convert_renders_external_link_without_a_resolver()
    {
        // External links carry their own href — no page-title resolver needed.
        var storage = ConfluenceStorageConverter.Convert("See [the docs](https://example.com).");

        storage.ShouldContain("<a href=\"https://example.com\">the docs</a>");
    }

    [Fact]
    public void Convert_throws_on_image_rather_than_dropping_it()
    {
        // Images (also a LinkInline) map to an attachment + <ac:image> in a later
        // slice; until then they must fail loud, not silently vanish.
        var ex = Should.Throw<NotSupportedException>(
            () => ConfluenceStorageConverter.Convert("![a diagram](diagram.png)"));
        ex.Message.ShouldContain("Image");
    }

    [Fact]
    public void Convert_throws_on_relative_non_md_link()
    {
        // §7 maps relative .md links and external URLs only; a link to a source
        // file or directory is unsupported and must surface, not be guessed at.
        var ex = Should.Throw<NotSupportedException>(
            () => ConfluenceStorageConverter.Convert("See [the service](../src/LoanService.cs)."));
        ex.Message.ShouldContain("neither a '.md' page link nor an external URL");
    }

    [Fact]
    public void Convert_throws_on_relative_md_link_without_a_resolver()
    {
        // A relative page link can only render if a resolver supplies the target
        // title; without one the converter must fail loud, not emit a broken link.
        var ex = Should.Throw<InvalidOperationException>(
            () => ConfluenceStorageConverter.Convert("See [Loans](domains/loans/README.md)."));
        ex.Message.ShouldContain("requires a page-link resolver");
    }

    [Fact]
    public void Convert_throws_when_a_relative_link_target_does_not_resolve()
    {
        // A resolver that returns null means the cross-reference is broken; that
        // must surface rather than silently producing an empty content-title.
        var ex = Should.Throw<InvalidOperationException>(
            () => ConfluenceStorageConverter.Convert("See [Gone](missing/page.md).", _ => null));
        ex.Message.ShouldContain("broken cross-reference");
    }

    [Fact]
    public void Convert_throws_on_a_page_link_title_rather_than_dropping_it()
    {
        // <ac:link> has no representable tooltip, so a title on an internal .md link
        // must fail loud rather than silently vanish (external <a> keeps its title).
        var ex = Should.Throw<NotSupportedException>(
            () => ConfluenceStorageConverter.Convert("See [Loans](loans.md \"tip\").", _ => "Loans"));
        ex.Message.ShouldContain("title tooltip");
    }

    [Theory]

    // §7's mapping (mark's): NOTE→info, TIP→tip, IMPORTANT→info, WARNING→note,
    // CAUTION→warning. NOTE and IMPORTANT sharing `info` is a spec'd accepted loss.
    [InlineData("> [!NOTE]\n> Heads up.", "info")]
    [InlineData("> [!TIP]\n> Heads up.", "tip")]
    [InlineData("> [!IMPORTANT]\n> Heads up.", "info")]
    [InlineData("> [!WARNING]\n> Heads up.", "note")]
    [InlineData("> [!CAUTION]\n> Heads up.", "warning")]

    // GitHub matches the keyword case-insensitively.
    [InlineData("> [!note]\n> Heads up.", "info")]
    public void Convert_renders_a_root_github_alert_as_a_panel_macro(string markdown, string macro)
    {
        // Whole-output equality also pins the marker-line consumption: the body is
        // the author's text only, with no leftover "[!NOTE]" and no leading space
        // from the line break that ended the marker line.
        ConfluenceStorageConverter.Convert(markdown).ShouldBe(
            $"<ac:structured-macro ac:name=\"{macro}\"><ac:parameter ac:name=\"icon\">true</ac:parameter>"
            + "<ac:rich-text-body>\n<p>Heads up.</p>\n</ac:rich-text-body></ac:structured-macro>\n");
    }

    [Fact]
    public void Convert_drops_the_marker_paragraph_when_the_marker_stands_alone()
    {
        // A blank quote line makes the marker its own paragraph rather than the first
        // line of the body's; it must be removed outright, not left as an empty <p>.
        ConfluenceStorageConverter.Convert("> [!NOTE]\n>\n> Heads up.").ShouldBe(
            "<ac:structured-macro ac:name=\"info\"><ac:parameter ac:name=\"icon\">true</ac:parameter>"
            + "<ac:rich-text-body>\n<p>Heads up.</p>\n</ac:rich-text-body></ac:structured-macro>\n");
    }

    [Fact]
    public void Convert_renders_an_alert_with_no_body_as_an_empty_panel()
    {
        // Degenerate but faithful — the author wrote an empty alert and GitHub renders
        // an empty callout. Pinned so the shape is a deliberate choice rather than a
        // surprise the first time M2 posts one to Confluence.
        ConfluenceStorageConverter.Convert("> [!NOTE]").ShouldBe(
            "<ac:structured-macro ac:name=\"info\"><ac:parameter ac:name=\"icon\">true</ac:parameter>"
            + "<ac:rich-text-body>\n</ac:rich-text-body></ac:structured-macro>\n");
    }

    [Theory]

    // Not one of GitHub's five keywords.
    [InlineData("> [!FOO]\n> Heads up.")]

    // GitHub requires the marker to occupy its whole first line, so this is a quote
    // whose text happens to start with brackets — converting it would invent a panel.
    [InlineData("> [!NOTE] Heads up.")]

    // The marker is emphasized, not bare, so the first line is not a marker at all.
    [InlineData("> **[!NOTE]**\n> Heads up.")]
    public void Convert_renders_a_quote_that_is_not_a_bare_alert_marker_as_a_blockquote(string markdown)
    {
        var storage = ConfluenceStorageConverter.Convert(markdown);

        storage.ShouldStartWith("<blockquote>");
        storage.ShouldNotContain("ac:structured-macro");
    }

    [Fact]
    public void Convert_renders_a_nested_alert_as_a_plain_blockquote()
    {
        // §7: only a root-level alert becomes a panel. GitHub does not recognize an
        // alert nested in another blockquote either, so its marker text stays visible
        // instead of being consumed as a panel type.
        var storage = ConfluenceStorageConverter.Convert("> [!NOTE]\n> Outer.\n>\n> > [!WARNING]\n> > Inner.");

        storage.ShouldContain("<blockquote>\n<p>[!WARNING] Inner.</p>\n</blockquote>");
        storage.ShouldContain("ac:name=\"info\"");
        storage.ShouldNotContain("ac:name=\"note\"");
    }

    [Fact]
    public void Convert_renders_an_alert_in_a_tight_list_item_without_leaking_the_implicit_paragraph()
    {
        // A tight list sets ImplicitParagraph for its items; a panel body always wraps
        // block content, so the renderer must reset and restore the flag or the body
        // loses its <p>. Same nesting bug class that bit the blockquote in M1 slice 2.
        var storage = ConfluenceStorageConverter.Convert("- item with an alert\n  > [!CAUTION]\n  > Careful.\n- plain item\n");

        storage.ShouldBe(
            "<ul>\n<li>item with an alert<ac:structured-macro ac:name=\"warning\">"
            + "<ac:parameter ac:name=\"icon\">true</ac:parameter><ac:rich-text-body>\n"
            + "<p>Careful.</p>\n</ac:rich-text-body></ac:structured-macro>\n</li>\n"
            + "<li>plain item</li>\n</ul>\n");
    }

    [Fact]
    public void Convert_keeps_the_code_macro_well_formed_when_the_body_contains_the_cdata_terminator()
    {
        // A literal CDATA terminator inside the code would prematurely close the
        // section, so it must be split to keep the storage fragment parseable XML.
        var storage = ConfluenceStorageConverter.Convert("```\na]]>b\n```");

        storage.ShouldContain("<![CDATA[a]]]]><![CDATA[>b]]>");
        storage.ShouldNotContain("[CDATA[a]]>b");
    }

    [Theory]

    // Enabling the pipe-table extension makes '|' significant during inline
    // parsing, so a paragraph that merely contains pipes must still round-trip as
    // text: Markdig turns the unused delimiters back into literals, and if one
    // ever leaked through it would hit the fail-loud catch-all and fail the page.
    [InlineData("Run `ls | wc -l` to count.", "Run <code>ls | wc -l</code> to count.")]
    [InlineData("Either a | b applies.", "Either a | b applies.")]

    // No header separator (`|---|`), so this is not a table — RequireHeaderSeparator
    // defaults to true, keeping GFM semantics.
    [InlineData("| a | b |", "| a | b |")]
    public void Convert_keeps_pipes_in_a_non_table_paragraph_as_text(string markdown, string expectedInner)
    {
        ConfluenceStorageConverter.Convert(markdown).ShouldBe($"<p>{expectedInner}</p>\n");
    }

    [Fact]
    public void Convert_renders_a_table_nested_in_a_tight_list_item_without_leaking_the_implicit_paragraph()
    {
        // A tight list sets ImplicitParagraph for its items; the table's cells must
        // restore it per cell so the shape is identical to a top-level table. This
        // is the nesting class of bug that bit the blockquote renderer in M1 slice 2.
        var storage = ConfluenceStorageConverter.Convert("- Codes:\n\n  | A | B |\n  |---|---|\n  | 1 | 2 |\n");

        storage.ShouldContain("<tr>\n<th>A</th>\n<th>B</th>\n</tr>");
        storage.ShouldContain("<tr>\n<td>1</td>\n<td>2</td>\n</tr>");
        storage.ShouldNotContain("<th><p>");
        storage.ShouldNotContain("<td><p>");
    }

    [Fact]
    public void Convert_leaves_a_pipe_inside_a_fenced_code_block_alone()
    {
        // Shell pipelines in code samples are everywhere in this wiki; block-level
        // parsing claims the fence before the table parser sees the '|', but pin it
        // so enabling a future inline extension cannot quietly turn code into a table.
        var storage = ConfluenceStorageConverter.Convert("```sh\nls | wc -l\n```\n");

        storage.ShouldContain("<![CDATA[ls | wc -l]]>");
        storage.ShouldNotContain("<table>");
    }

    [Fact]
    public void Convert_renders_a_header_only_table()
    {
        // A header row with no body rows is still a valid table; it must not emit an
        // empty <tbody> pair or trip the fail-loud catch-all.
        var storage = ConfluenceStorageConverter.Convert("| A | B |\n|---|---|\n");

        storage.ShouldBe("<table>\n<tbody>\n<tr>\n<th>A</th>\n<th>B</th>\n</tr>\n</tbody>\n</table>\n");
    }

    [Fact]
    public void Convert_lets_a_table_interrupt_a_paragraph()
    {
        // KNOWN DIVERGENCE from GitHub: GFM requires a table's header to start its
        // own block, so GitHub renders this as one paragraph of literal pipes, while
        // Markdig splits the paragraph and builds the table. Markdig's reading is
        // what the author meant, so it is kept rather than worked around — but it is
        // pinned here so the difference is asserted instead of discovered on a page.
        var storage = ConfluenceStorageConverter.Convert("Some text\n| a | b |\n|---|---|\n| 1 | 2 |\n");

        storage.ShouldBe(
            "<p>Some text </p>\n<table>\n<tbody>\n<tr>\n<th>a</th>\n<th>b</th>\n</tr>\n"
            + "<tr>\n<td>1</td>\n<td>2</td>\n</tr>\n</tbody>\n</table>\n");
    }

    [Fact]
    public void Convert_drops_column_alignment_rather_than_emitting_a_style_attribute()
    {
        // §7 accepted loss: storage format has no per-column alignment. Markdig's
        // own HTML renderer would emit style="text-align: …"; ours must not, since
        // Confluence would not honor it anyway.
        var storage = ConfluenceStorageConverter.Convert("| A | B |\n|:-:|--:|\n| 1 | 2 |\n");

        storage.ShouldNotContain("text-align");
        storage.ShouldNotContain("style=");
        storage.ShouldNotContain("<col");
    }

    [Theory]

    // A language alone stays exactly as it was before fence attributes existed.
    [InlineData("cs", "<ac:parameter ac:name=\"language\">csharp</ac:parameter>")]

    // Each attribute emits its parameter, in mark's ac:code order (language,
    // collapse, linenumbers, firstline, title).
    [InlineData("cs collapse", "<ac:parameter ac:name=\"language\">csharp</ac:parameter><ac:parameter ac:name=\"collapse\">true</ac:parameter>")]
    [InlineData("cs linenumbers", "<ac:parameter ac:name=\"language\">csharp</ac:parameter><ac:parameter ac:name=\"linenumbers\">true</ac:parameter>")]

    // A bare number is firstline, and implies line numbering (mark's README).
    [InlineData("cs 5", "<ac:parameter ac:name=\"language\">csharp</ac:parameter><ac:parameter ac:name=\"linenumbers\">true</ac:parameter><ac:parameter ac:name=\"firstline\">5</ac:parameter>")]

    // An off switch, and a repeat, both resolve to "not requested" => omitted.
    [InlineData("cs nocollapse", "<ac:parameter ac:name=\"language\">csharp</ac:parameter>")]
    [InlineData("cs collapse nocollapse", "<ac:parameter ac:name=\"language\">csharp</ac:parameter>")]

    // `-` is mark's "no language, but I want the attributes" placeholder, and an
    // attribute in first position means the same thing.
    [InlineData("- collapse", "<ac:parameter ac:name=\"collapse\">true</ac:parameter>")]
    [InlineData("collapse", "<ac:parameter ac:name=\"collapse\">true</ac:parameter>")]
    [InlineData("linenumbers", "<ac:parameter ac:name=\"linenumbers\">true</ac:parameter>")]
    [InlineData("7", "<ac:parameter ac:name=\"linenumbers\">true</ac:parameter><ac:parameter ac:name=\"firstline\">7</ac:parameter>")]

    // The title is the rest of the line, unquoted — mark's syntax, and it keeps its
    // internal spacing. A title in first position needs no language either.
    [InlineData("cs title Program.cs", "<ac:parameter ac:name=\"language\">csharp</ac:parameter><ac:parameter ac:name=\"title\">Program.cs</ac:parameter>")]
    [InlineData("cs title A b c", "<ac:parameter ac:name=\"language\">csharp</ac:parameter><ac:parameter ac:name=\"title\">A b c</ac:parameter>")]
    [InlineData("title A b c", "<ac:parameter ac:name=\"title\">A b c</ac:parameter>")]

    // Keywords are matched case-insensitively, unlike mark — which only ever widens
    // what is understood, and never silently misreads a token.
    [InlineData("cs COLLAPSE TITLE Foo", "<ac:parameter ac:name=\"language\">csharp</ac:parameter><ac:parameter ac:name=\"collapse\">true</ac:parameter><ac:parameter ac:name=\"title\">Foo</ac:parameter>")]

    // An unknown language still omits the parameter without throwing (fail-safe),
    // even when attributes follow it.
    [InlineData("brainfuck collapse", "<ac:parameter ac:name=\"collapse\">true</ac:parameter>")]
    public void Convert_maps_code_fence_attributes_to_macro_parameters(string infoLine, string expectedParameters)
    {
        var storage = ConfluenceStorageConverter.Convert($"```{infoLine}\nbody\n```\n");

        storage.ShouldBe(
            $"<ac:structured-macro ac:name=\"code\">{expectedParameters}"
            + "<ac:plain-text-body><![CDATA[body]]></ac:plain-text-body></ac:structured-macro>\n");
    }

    [Fact]
    public void Convert_escapes_a_code_fence_title_as_element_content()
    {
        // The title is author text on the fence line, so it can carry XML specials.
        var storage = ConfluenceStorageConverter.Convert("```sh title Pipe <in> & out\nbody\n```\n");

        storage.ShouldContain("<ac:parameter ac:name=\"title\">Pipe &lt;in&gt; &amp; out</ac:parameter>");
    }

    [Theory]

    // The whole point of the slice: an attribute this converter does not understand
    // must fail the page rather than vanish from it.
    [InlineData("cs mysteryflag")]
    [InlineData("cs colapse")]

    // PLAN.md §7's illustrative `title=Foo` spelling is NOT mark's syntax, and mark
    // itself would silently turn it into a bogus `theme` parameter. Fail instead.
    [InlineData("cs title=Foo")]
    [InlineData("cs collapse=true")]

    // A theme name is a Confluence Server concept the Cloud macro no longer
    // documents; accepting arbitrary tokens as themes is exactly what makes mark
    // swallow the typos above.
    [InlineData("cs midnight")]

    // `title` with nothing after it asks for a title we cannot produce.
    [InlineData("cs title")]
    [InlineData("cs collapse title")]

    // Confluence numbers lines from 1, so 0 and negatives are not firstline values.
    [InlineData("cs 0")]
    [InlineData("cs -1")]

    // Other toolchains spell fence attributes differently (Docusaurus/VitePress
    // quoted titles, Prism line-range highlighting). DocuMe speaks mark's syntax
    // only, and says so loudly instead of half-honoring a foreign dialect — note
    // that the quoted forms never even reach the title branch, since `title="x"`
    // tokenizes as one unknown word rather than the bare `title` keyword.
    [InlineData("js {1,3-4}")]
    [InlineData("bash title=\"Deploy\"")]
    [InlineData("bash title=\"Deploy to prod\"")]
    public void Convert_fails_loud_on_an_unsupported_code_fence_attribute(string infoLine)
    {
        Should.Throw<NotSupportedException>(() => ConfluenceStorageConverter.Convert($"```{infoLine}\nbody\n```\n"));
    }

    [Fact]
    public void Convert_does_not_throw_for_an_unknown_language_the_way_it_does_for_an_unknown_attribute()
    {
        // The asymmetry is deliberate and worth pinning: an unmapped language costs
        // only syntax highlighting, while a dropped attribute would publish
        // something the author did not ask for.
        ConfluenceStorageConverter.Convert("```brainfuck\nbody\n```\n")
            .ShouldBe("<ac:structured-macro ac:name=\"code\"><ac:plain-text-body><![CDATA[body]]></ac:plain-text-body></ac:structured-macro>\n");

        Should.Throw<NotSupportedException>(() => ConfluenceStorageConverter.Convert("```brainfuck mysteryflag\nbody\n```\n"));
    }

    [Fact]
    public void Convert_does_not_read_the_code_body_as_fence_attributes()
    {
        // Only the info line carries attributes; a body line that happens to read
        // like one must stay code.
        var storage = ConfluenceStorageConverter.Convert("```sh\ntitle Not A Title\ncollapse\n```\n");

        storage.ShouldBe(
            "<ac:structured-macro ac:name=\"code\"><ac:parameter ac:name=\"language\">bash</ac:parameter>"
            + "<ac:plain-text-body><![CDATA[title Not A Title\ncollapse]]></ac:plain-text-body></ac:structured-macro>\n");
    }

    [Theory]

    // Confluence's own status strings, and GitHub's case-insensitive `[X]`.
    [InlineData("- [x] done", "complete")]
    [InlineData("- [X] done", "complete")]
    [InlineData("- [ ] done", "incomplete")]
    public void Convert_renders_an_all_task_list_as_a_confluence_task_list(string markdown, string status)
    {
        // Whole-output equality also pins marker consumption: the body is the author's
        // text with no leftover "[x]" and no leading space — Markdig leaves the
        // marker's separating space on the following literal, unlike goldmark/mark.
        ConfluenceStorageConverter.Convert(markdown).ShouldBe(
            "<ac:task-list>\n<ac:task>\n<ac:task-id>1</ac:task-id>\n"
            + $"<ac:task-status>{status}</ac:task-status>\n"
            + "<ac:task-body>done</ac:task-body>\n</ac:task>\n</ac:task-list>\n");
    }

    [Theory]
    [InlineData("- see [x](https://example.com) here", "see <a href=\"https://example.com\">x</a> here")]
    [InlineData("- see [ ](https://example.com) here", "see <a href=\"https://example.com\"> </a> here")]
    public void Convert_keeps_a_bracketed_link_in_a_list_item_a_link(string markdown, string expectedInner)
    {
        // The regression this slice's custom parser exists to prevent. Markdig's stock
        // TaskListInlineParser matches [x]/[ ] anywhere inside a list item and is
        // installed ahead of the link parser, so it eats the label of [x](url) and
        // leaves "(url)" as text — a silently broken link. GfmTaskListInlineParser
        // requires the marker to open the item, which is also GitHub's rule.
        ConfluenceStorageConverter.Convert(markdown).ShouldBe($"<ul>\n<li>{expectedInner}</li>\n</ul>\n");
    }

    [Theory]

    // Mid-item text, and a marker protected by a code span (a wiki documenting the
    // task-list syntax itself). Both render exactly as GitHub renders them.
    [InlineData("- mark the box [x] before signing", "mark the box [x] before signing")]
    [InlineData("- `[x]` means done", "<code>[x]</code> means done")]
    public void Convert_keeps_a_marker_that_does_not_open_the_item_as_text(string markdown, string expectedInner)
    {
        ConfluenceStorageConverter.Convert(markdown).ShouldBe($"<ul>\n<li>{expectedInner}</li>\n</ul>\n");
    }

    [Fact]
    public void Convert_does_not_treat_a_marker_opening_a_later_paragraph_as_a_task()
    {
        // GFM recognizes a marker only where it opens the item, meaning the first
        // paragraph; Markdig's stock parser accepts any paragraph in the item.
        ConfluenceStorageConverter.Convert("- first\n\n  [x] not a marker\n").ShouldBe(
            "<ul>\n<li><p>first</p>\n<p>[x] not a marker</p>\n</li>\n</ul>\n");
    }

    [Fact]
    public void Convert_leaves_a_bracketed_x_outside_a_list_alone()
    {
        // Enabling the extension must not make "[x]" significant in ordinary prose.
        ConfluenceStorageConverter.Convert("Tick [x] in the form.").ShouldBe("<p>Tick [x] in the form.</p>\n");
    }

    [Fact]
    public void Convert_keeps_the_paragraph_in_a_loose_task_list_body()
    {
        // Looseness is honored exactly as for a plain list, so a task body is bare
        // inline content when tight and keeps its <p> when loose.
        ConfluenceStorageConverter.Convert("- [x] done\n\n- [ ] todo\n").ShouldBe(
            "<ac:task-list>\n<ac:task>\n<ac:task-id>1</ac:task-id>\n<ac:task-status>complete</ac:task-status>\n"
            + "<ac:task-body><p>done</p>\n</ac:task-body>\n</ac:task>\n"
            + "<ac:task>\n<ac:task-id>2</ac:task-id>\n<ac:task-status>incomplete</ac:task-status>\n"
            + "<ac:task-body><p>todo</p>\n</ac:task-body>\n</ac:task>\n</ac:task-list>\n");
    }

    [Fact]
    public void Convert_numbers_task_ids_uniquely_per_page_and_identically_on_every_render()
    {
        // Confluence tracks task completion by id, so two lists on one page must not
        // repeat ids (mark's per-list counter does). Re-rendering the same source must
        // also produce the same ids, or the content hash (§8) would churn — hence a
        // counter on the per-call renderer rather than a static one.
        const string markdown = "- [x] a\n\ntext\n\n- [ ] b\n";

        var first = ConfluenceStorageConverter.Convert(markdown);

        first.ShouldContain("<ac:task-id>1</ac:task-id>");
        first.ShouldContain("<ac:task-id>2</ac:task-id>");
        ConfluenceStorageConverter.Convert(markdown).ShouldBe(first);
    }

    [Fact]
    public void Convert_renders_a_task_list_nested_in_a_tight_list_item()
    {
        // The nesting-bug class from M1 slice 2: a tight outer item sets
        // ImplicitParagraph, and the task body must honor its own list's looseness
        // instead of inheriting the outer item's.
        ConfluenceStorageConverter.Convert("- steps\n  - [x] done\n").ShouldBe(
            "<ul>\n<li>steps<ac:task-list>\n<ac:task>\n<ac:task-id>1</ac:task-id>\n"
            + "<ac:task-status>complete</ac:task-status>\n<ac:task-body>done</ac:task-body>\n"
            + "</ac:task>\n</ac:task-list>\n</li>\n</ul>\n");
    }

    [Fact]
    public void Convert_renders_an_ordered_task_list_as_a_task_list()
    {
        // Storage format has no numbered task list, so ordering is dropped rather
        // than emitting <ol> with the markers demoted to text.
        var storage = ConfluenceStorageConverter.Convert("1. [x] done\n2. [ ] todo\n");

        storage.ShouldStartWith("<ac:task-list>");
        storage.ShouldNotContain("<ol>");
    }

    [Fact]
    public void Convert_empty_input_produces_empty_output()
    {
        ConfluenceStorageConverter.Convert(string.Empty).ShouldBe(string.Empty);
    }
}
