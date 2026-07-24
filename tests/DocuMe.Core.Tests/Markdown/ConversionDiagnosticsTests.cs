using DocuMe.Core.Markdown;
using Shouldly;

namespace DocuMe.Core.Tests.Markdown;

/// <summary>
/// The diagnostics channel of PLAN.md §4.4: every construct that converts but
/// <em>degrades</em> must say so, every construct that loses nothing must stay silent, and
/// supplying a sink must not move a single byte of the emitted storage format (§4.3 — the 27
/// hand-reviewed goldens call <c>Convert</c> without one).
/// </summary>
public sealed class ConversionDiagnosticsTests
{
    [Fact]
    public void Unknown_fence_language_reports_a_diagnostic()
    {
        var diagnostics = new List<ConversionDiagnostic>();

        var storage = ConfluenceStorageConverter.Convert(
            "```brainfuck\n++.\n```",
            diagnostics: diagnostics);

        var diagnostic = diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(ConversionDiagnosticCodes.UnknownFenceLanguage);

        // Construct carries the source token verbatim — the §4.4 report groups by dialect.
        diagnostic.Construct.ShouldBe("brainfuck");
        diagnostic.Message.ShouldContain("unhighlighted");

        // The point of a diagnostic: observability only. The macro is exactly what it was
        // before this channel existed — no language parameter, body untouched.
        storage.ShouldBe(
            "<ac:structured-macro ac:name=\"code\"><ac:plain-text-body><![CDATA[++.]]>"
            + "</ac:plain-text-body></ac:structured-macro>\n");
    }

    [Fact]
    public void Unknown_fence_language_is_reported_once_per_fence()
    {
        var diagnostics = new List<ConversionDiagnostic>();

        ConfluenceStorageConverter.Convert(
            "```brainfuck\n++.\n```\n\ntext\n\n```brainfuck\n--.\n```\n\n```nim\nlet x = 1\n```",
            diagnostics: diagnostics);

        // Repeats are kept rather than deduplicated: a §4.4 run wants occurrence counts, and
        // the caller can group. Order is document order.
        diagnostics.Select(d => d.Construct).ShouldBe(["brainfuck", "brainfuck", "nim"]);
    }

    [Fact]
    public void Mixed_task_list_reports_a_diagnostic()
    {
        var diagnostics = new List<ConversionDiagnostic>();

        var storage = ConfluenceStorageConverter.Convert(
            "- [x] task item\n- regular item\n- [ ] another task item",
            diagnostics: diagnostics);

        var diagnostic = diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(ConversionDiagnosticCodes.MixedTaskList);

        // Construct is the element it degraded to; the counts live in the message.
        diagnostic.Construct.ShouldBe("ul");
        diagnostic.Message.ShouldContain("2 of 3 items");

        // Unchanged from the task-lists-mixed golden: markers kept as literal text.
        storage.ShouldBe(
            "<ul>\n<li>[x] task item</li>\n<li>regular item</li>\n<li>[ ] another task item</li>\n</ul>\n");
    }

    [Fact]
    public void Mixed_ordered_task_list_reports_the_ordered_element()
    {
        var diagnostics = new List<ConversionDiagnostic>();

        ConfluenceStorageConverter.Convert("1. [x] shipped\n2. still open", diagnostics: diagnostics);

        diagnostics.ShouldHaveSingleItem().Construct.ShouldBe("ol");
    }

    [Fact]
    public void Nested_mixed_task_list_reports_the_inner_list_too()
    {
        var diagnostics = new List<ConversionDiagnostic>();

        ConfluenceStorageConverter.Convert(
            "- [x] outer task\n- plain outer\n  - [ ] inner task\n  - plain inner",
            diagnostics: diagnostics);

        // Each degraded list is its own loss, so each reports. Both are <ul> here.
        diagnostics.Count.ShouldBe(2);
        diagnostics.ShouldAllBe(d => d.Code == ConversionDiagnosticCodes.MixedTaskList);
    }

    [Fact]
    public void Same_page_anchor_link_reports_a_diagnostic()
    {
        var diagnostics = new List<ConversionDiagnostic>();

        var storage = ConfluenceStorageConverter.Convert(
            "See [the overview](#overview) below.",
            diagnostics: diagnostics);

        var diagnostic = diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(ConversionDiagnosticCodes.SamePageAnchorLink);

        // Construct is the anchor itself: spike S2 needs to know *which* anchors AurServices
        // uses, not just how many.
        diagnostic.Construct.ShouldBe("#overview");

        // Still text-only, exactly as before the channel existed.
        storage.ShouldBe("<p>See the overview below.</p>\n");
    }

    [Fact]
    public void Anchor_on_a_page_link_is_not_the_same_page_degradation()
    {
        var diagnostics = new List<ConversionDiagnostic>();

        // A fragment on a *page* link is stripped, but the link survives, so this is not the
        // anchor degradation. Left unreported on purpose — a separate decision if it ever is.
        var storage = ConfluenceStorageConverter.Convert(
            "See [design](design.md#layout).",
            _ => "Design",
            diagnostics: diagnostics);

        storage.ShouldContain("<ri:page ri:content-title=\"Design\"/>");
        diagnostics.ShouldBeEmpty();
    }

    /// <summary>
    /// The silent half of the contract, and the more load-bearing one: a diagnostic that fired
    /// on a lossless construct would make §4.4's "zero unknown-construct warnings" unreachable
    /// for reasons that are not losses. Every case below either has no counterpart to lose or
    /// degrades <em>exactly</em> the way GitHub itself renders it.
    /// </summary>
    [Theory]

    // --- fences with no lost language ---
    // A mapped language, an alias, and a mapped language plus attributes.
    [InlineData("```csharp\nvar x = 1;\n```")]
    [InlineData("```cs\nvar x = 1;\n```")]
    [InlineData("```sh linenumbers\necho hi\n```")]

    // A bare fence never had a language to lose.
    [InlineData("```\nplain\n```")]

    // `-` is mark's explicit "no language" spelling, so an omitted brush is what the author
    // asked for. Reporting it would warn on every deliberately unlabelled fence.
    [InlineData("```-\nplain\n```")]
    [InlineData("```- collapse\nplain\n```")]

    // --- lists with no lost task semantics ---
    // An all-task list becomes a native <ac:task-list>; a plain list has nothing to lose.
    [InlineData("- [x] done\n- [ ] open")]
    [InlineData("- one\n- two")]

    // A marker that does not open its item never becomes a task marker on GitHub either
    // (GfmTaskListExtension's positional rule), so this list is plain and lossless.
    [InlineData("- mark the box [x] before signing")]

    // --- links that keep their destination ---
    [InlineData("[out](https://example.com) and <https://example.com> and <me@example.com>")]

    // --- constructs that degrade the way GitHub does ---
    // A nested GitHub alert keeps its visible marker line; GitHub does not recognize it
    // either, so the reader sees the same thing. Asserted rather than assumed, because it
    // looks like a degradation site.
    [InlineData("> > [!NOTE]\n> > nested note")]

    // Reference definitions are metadata that render to nothing anywhere, used or not.
    [InlineData("[text][ref]\n\n[ref]: https://example.com")]
    [InlineData("plain text\n\n[unused]: https://example.com")]

    // An unrecognized character reference stays the literal text the author typed, which is
    // what GitHub shows too.
    [InlineData("Costs &nosuchthing; nothing.")]

    // `[TOC]` below root level stays literal text — GitHub has no [TOC] construct at all.
    [InlineData("- [TOC]")]
    public void Construct_that_loses_nothing_reports_no_diagnostic(string markdown)
    {
        var diagnostics = new List<ConversionDiagnostic>();

        ConfluenceStorageConverter.Convert(markdown, diagnostics: diagnostics);

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void Convert_without_a_sink_emits_identical_storage_format()
    {
        // This is what protects the 27 hand-reviewed goldens (§4.3): they call Convert with
        // the body plus three positional resolvers and no sink, so the sink must be inert.
        const string Markdown = """
            A [same-page anchor](#here) plus a fence and a mixed list.

            ```brainfuck
            ++.
            ```

            - [x] task item
            - regular item
            """;

        var diagnostics = new List<ConversionDiagnostic>();

        var withoutSink = ConfluenceStorageConverter.Convert(Markdown);
        var withSink = ConfluenceStorageConverter.Convert(Markdown, diagnostics: diagnostics);

        withSink.ShouldBe(withoutSink);
        diagnostics.Count.ShouldBe(3);
    }

    [Fact]
    public void Diagnostics_arrive_in_document_order()
    {
        var diagnostics = new List<ConversionDiagnostic>();

        ConfluenceStorageConverter.Convert(
            """
            - [x] task item
            - regular item

            Then [an anchor](#later).

            ```brainfuck
            ++.
            ```
            """,
            diagnostics: diagnostics);

        diagnostics.Select(d => d.Code).ShouldBe(
        [
            ConversionDiagnosticCodes.MixedTaskList,
            ConversionDiagnosticCodes.SamePageAnchorLink,
            ConversionDiagnosticCodes.UnknownFenceLanguage,
        ]);
    }

    [Fact]
    public void Convert_appends_to_the_sink_and_never_clears_it()
    {
        // A 79-page run may hand one collection to every page; wiping it would silently lose
        // every earlier page's findings.
        var seeded = new ConversionDiagnostic("seeded", "from-an-earlier-page", "kept");
        var diagnostics = new List<ConversionDiagnostic> { seeded };

        ConfluenceStorageConverter.Convert("[anchor](#a)", diagnostics: diagnostics);

        diagnostics[0].ShouldBe(seeded);
        diagnostics[1].Code.ShouldBe(ConversionDiagnosticCodes.SamePageAnchorLink);
    }

    [Fact]
    public void A_page_that_fails_loud_keeps_the_diagnostics_it_reported_first()
    {
        // The sink is the caller's collection, so whatever was reached before the throw is
        // still readable. A §4.4 run therefore learns "this page failed on X *and* degrades Y"
        // from a single pass instead of needing the fix before it can see the rest.
        var diagnostics = new List<ConversionDiagnostic>();

        Should.Throw<NotSupportedException>(() => ConfluenceStorageConverter.Convert(
            "[anchor](#a)\n\n<div>raw html</div>",
            diagnostics: diagnostics));

        diagnostics.ShouldHaveSingleItem().Code.ShouldBe(ConversionDiagnosticCodes.SamePageAnchorLink);
    }
}
