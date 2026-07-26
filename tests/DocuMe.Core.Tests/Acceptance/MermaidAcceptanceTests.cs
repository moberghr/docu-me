using System.Text.RegularExpressions;
using DocuMe.Core.Acceptance;
using DocuMe.Core.Markdown;
using DocuMe.Core.Tests.Markdown;
using Shouldly;

namespace DocuMe.Core.Tests.Acceptance;

/// <summary>
/// The mermaid render pass (PLAN.md §4.4, spec finding (9)).
/// </summary>
/// <remarks>
/// <para>
/// Most of these drive a <em>stub</em> render script, the same way
/// <see cref="Markdown.MermaidRendererTests"/> does: the pass's own job is collecting, deduping,
/// grouping and counting, and a stub pins all of it with no npm dependency and no flakiness.
/// </para>
/// <para>
/// One test runs the real <c>beautiful-mermaid</c> over the golden corpus, and it is the reason
/// this pass exists — see
/// <see cref="The_real_renderer_over_the_golden_corpus_rejects_the_two_diagrams_github_renders"/>.
/// It skips with a reason when <c>node_modules</c> is absent.
/// </para>
/// </remarks>
public sealed partial class MermaidAcceptanceTests : IDisposable
{
    private const string RenderedSvg = """<svg width="1" height="1"/>""";

    private readonly string _stubDirectory = Directory
        .CreateTempSubdirectory("docume-mermaid-acceptance-tests")
        .FullName;

    public void Dispose() => Directory.Delete(_stubDirectory, recursive: true);

    [Fact]
    public async Task The_real_renderer_over_the_golden_corpus_rejects_the_two_diagrams_github_renders()
    {
        var script = BundledRenderScript.TryFind();
        Assert.SkipUnless(script is not null, BundledRenderScript.DependencyMissingReason);

        // The whole justification for a render pass, measured rather than argued. tests/golden's
        // mermaid.md holds four diagrams; conversion accepts all four, because the attachment name
        // is a pure function of the source and the resolver therefore cannot fail (§8). Two of them
        // beautiful-mermaid 1.1.3 refuses: `graph TD;` (a trailing semicolon on the header, which
        // mermaid.js and GitHub both accept) and `pie` (not implemented). PLAN.md §4 line 128 calls
        // the mechanism "Proven on AurServices (59 diagrams)"; on a 27-page corpus it is already
        // 2-for-4, which is what makes the AurServices run worth doing at all. That run is M7 Aur
        // adoption now: gate-m1-aurservices-files was closed without action, and the dialect census
        // moved with it (.claude/references/decisions.md).
        var conversion = ConversionAcceptance.RunDirectory(GoldenCorpus.Directory, GoldenCorpus.Resolvers);
        conversion.FailedPageCount.ShouldBe(0);

        var report = await MermaidAcceptance.RenderDiagramsAsync(
            conversion,
            new MermaidRenderer(script!),
            TestContext.Current.CancellationToken);

        report.Renders.ShouldNotBeNull();
        var renders = report.Renders!;

        renders.Count.ShouldBe(4);
        renders.DistinctCount.ShouldBe(4);
        renders.FailedCount.ShouldBe(2);
        renders.FailedPageCount.ShouldBe(1);
        renders.AllRendered.ShouldBeFalse();

        // One reason bucket for both: every refusal is the same message with a different header
        // quoted, so normalizing the quotes is what turns N diagrams into one finding plus a
        // dialect list.
        renders.Failures.Count.ShouldBe(1);
        renders.Failures[0].Reason.ShouldContain("Invalid mermaid header");
        renders.Failures[0].Reason.ShouldNotContain("graph TD;");
        renders.Failures[0].ByDialect.ShouldBe(
            [new ConstructCount("graph TD;", 1), new ConstructCount("pie title Pets", 1)]);

        // Measured against the real renderer, not the stub: the headers it names as acceptable are
        // constant across both refusals and therefore survive into Detail, while the two offending
        // headers differ and elide. This is what `docume convert` prints.
        var detail = renders.Failures[0].Detail;
        detail.ShouldContain("""Expected "graph TD", "flowchart LR", "stateDiagram-v2", etc.""");
        detail.ShouldContain("""Invalid mermaid header: "…".""");
        detail.ShouldNotContain("graph TD;");
        detail.ShouldNotContain("pie title Pets");

        // The census covers what rendered too: the question is which dialects a corpus uses, not
        // only which ones broke.
        renders.ByDialect.ShouldBe(
            [
                new ConstructCount("flowchart LR", 1),
                new ConstructCount("graph TD;", 1),
                new ConstructCount("pie title Pets", 1),
                new ConstructCount("sequenceDiagram", 1),
            ]);
    }

    [Fact]
    public async Task The_conversion_page_names_exactly_the_headers_the_real_renderer_rejects()
    {
        var script = BundledRenderScript.TryFind();
        Assert.SkipUnless(script is not null, BundledRenderScript.DependencyMissingReason);

        var conversion = ConversionAcceptance.RunDirectory(GoldenCorpus.Directory, GoldenCorpus.Resolvers);

        var report = await MermaidAcceptance.RenderDiagramsAsync(
            conversion,
            new MermaidRenderer(script!),
            TestContext.Current.CancellationToken);

        var rejected = report.Renders!.Failures
            .SelectMany(failure => failure.ByDialect.Select(dialect => dialect.Construct))
            .ToList();

        var claimed = ClaimedRejectedHeaders();

        // Vacuous-pass guards. An empty rejected set (a renderer version that accepts everything the
        // corpus holds) or an empty table would make both directions below pass on nothing.
        rejected.ShouldNotBeEmpty();
        claimed.ShouldNotBeEmpty();

        // Forward: the direction that rots silently. A renderer version that starts refusing a new
        // dialect leaves the page describing a subset it no longer accepts, and nothing else looks.
        var unnamed = rejected
            .Where(header => !claimed.Exists(claim => header.StartsWith(claim, StringComparison.Ordinal)))
            .ToList();

        unnamed.ShouldBeEmpty(UnnamedMessage);

        // Reverse: the over-claim. A renderer version that starts rendering `pie` leaves the page
        // warning a reader away from a diagram that now works.
        var stale = claimed
            .Where(header => !rejected.Exists(construct => construct.StartsWith(header, StringComparison.Ordinal)))
            .ToList();

        stale.ShouldBeEmpty(StaleMessage);
    }

    [Fact]
    public async Task An_unrenderable_diagram_takes_an_otherwise_clean_conversion_below_the_bar()
    {
        var page = new AcceptancePage(
            "diagrams.md",
            "```mermaid\npie title Pets\n```\n",
            new PageResolvers(_ => null, _ => null, _ => "mermaid-pie.svg"));

        var conversion = ConversionAcceptance.Run([page]);

        // Conversion is perfectly happy — no failure, no degradation, bar met. That is not a bug
        // in the converter: it never renders, so it cannot know. It is why §4.4 needs a second pass.
        conversion.MeetsAcceptanceBar.ShouldBeTrue();
        conversion.Renders.ShouldBeNull();

        var report = await MermaidAcceptance.RenderDiagramsAsync(
            conversion,
            new MermaidRenderer(WriteRejectsPieStub()),
            TestContext.Current.CancellationToken);

        report.MeetsAcceptanceBar.ShouldBeFalse();
        report.Renders!.FailedCount.ShouldBe(1);
    }

    [Fact]
    public async Task Groups_rejected_diagrams_by_reason_and_keeps_the_dialect_that_triggered_each()
    {
        var report = await RunAsync(
            new DiagramOccurrence("a.md", "pie title Pets\n  \"Dogs\" : 386"),
            new DiagramOccurrence("b.md", "graph TD\n  A --> B"),
            new DiagramOccurrence("c.md", "pie title Cars\n  \"Ford\" : 12"));

        report.Count.ShouldBe(3);
        report.FailedCount.ShouldBe(2);
        report.FailedPageCount.ShouldBe(2);

        // Two different headers, one reason: the reason is the construct, the header is the dialect.
        report.Failures.Count.ShouldBe(1);
        report.Failures[0].PageCount.ShouldBe(2);
        report.Failures[0].ByDialect.ShouldBe(
            [new ConstructCount("pie title Cars", 1), new ConstructCount("pie title Pets", 1)]);
        report.Failures[0].Occurrences.Select(occurrence => occurrence.Path).ShouldBe(["a.md", "c.md"]);
    }

    [Fact]
    public async Task The_group_detail_elides_the_headers_that_differ_and_keeps_the_ones_it_offers()
    {
        var report = await RunAsync(
            new DiagramOccurrence("a.md", "pie title Pets\n  \"Dogs\" : 386"),
            new DiagramOccurrence("c.md", "pie title Cars\n  \"Ford\" : 12"));

        var group = report.Failures[0];

        // Reason stays the key: every quoted run elided, so two headers are one bucket.
        group.Reason.ShouldBe(
            Wrapped("""Invalid mermaid header: "…". Expected "…", "…", "…", etc."""));

        // Detail is the same message read back to a person. The two headers disagree, so they
        // elide; the expected list is identical in both messages, so it survives — and it is the
        // only part of the message that says what to write instead.
        group.Detail.ShouldBe(
            Wrapped("""Invalid mermaid header: "…". Expected "graph TD", "flowchart LR", "stateDiagram-v2", etc."""));
    }

    [Fact]
    public async Task A_group_of_one_elides_nothing_and_reads_exactly_as_publish_prints_it()
    {
        // The asymmetry this closes: `publish` prints the renderer's message verbatim, so a single
        // failure had a clear report there and an elided one here for no reason a reader can see.
        var report = await RunAsync(new DiagramOccurrence("a.md", "pie title Pets\n  \"Dogs\" : 386"));

        report.Failures[0].Detail.ShouldBe(
            Wrapped(
                """Invalid mermaid header: "pie title Pets". """
                + """Expected "graph TD", "flowchart LR", "stateDiagram-v2", etc."""));
    }

    /// <summary>
    /// The renderer's stderr as <see cref="MermaidRenderException"/> hands it on. The wrapper quotes
    /// stderr with <c>'</c> and the report normalizes on <c>"</c>, so the wrapper is prose to it —
    /// which is why the elision lands inside the quoted message and not around it.
    /// </summary>
    private static string Wrapped(string stderr) =>
        "The mermaid render script failed (exit 2) on this diagram: "
        + $"'render-mermaid: the diagram did not render: {stderr}'";

    [Fact]
    public async Task Renders_each_distinct_source_once_however_many_pages_hold_it()
    {
        var log = Path.Combine(_stubDirectory, "renders.log");
        const string shared = "graph TD\n  A --> B";

        var report = await RunAsync(
            WriteLoggingStub(log),
            new DiagramOccurrence("a.md", shared),
            new DiagramOccurrence("b.md", shared),
            new DiagramOccurrence("c.md", "flowchart LR\n  a --> b"));

        // Two renders for three fences: the attachment name is a pure function of the source (§8),
        // so the same diagram on two pages is one attachment and a second Node round-trip could
        // report nothing new. On AurServices that is 59 fences, not 59 renders.
        var rendered = await File.ReadAllLinesAsync(log, TestContext.Current.CancellationToken);
        rendered.Length.ShouldBe(2);
        report.Count.ShouldBe(3);
        report.DistinctCount.ShouldBe(2);
        report.ByDialect.ShouldBe(
            [new ConstructCount("graph TD", 2), new ConstructCount("flowchart LR", 1)]);
    }

    [Fact]
    public async Task A_setup_fault_stops_the_pass_instead_of_becoming_a_finding()
    {
        // A missing script, a missing Node or an uninstalled beautiful-mermaid rejects every
        // diagram identically, and a report saying "59 diagrams failed" would be read as a broken
        // corpus rather than a broken machine. Only MermaidRenderFault.Diagram becomes a row.
        var renderer = new MermaidRenderer(Path.Combine(_stubDirectory, "not-scaffolded.mjs"));

        var error = await Should.ThrowAsync<MermaidRenderException>(
            () => MermaidAcceptance.RunAsync(
                [new DiagramOccurrence("a.md", "graph TD\n  A --> B")],
                renderer,
                TestContext.Current.CancellationToken));

        error.Fault.ShouldBe(MermaidRenderFault.Setup);
    }

    [Fact]
    public async Task A_corpus_with_no_diagrams_has_nothing_to_reject_and_starts_no_process()
    {
        // The renderer points at a script that does not exist: if the pass started a process for an
        // empty corpus, this would throw a setup fault instead of returning.
        var renderer = new MermaidRenderer(Path.Combine(_stubDirectory, "never-read.mjs"));

        var report = await MermaidAcceptance.RunAsync([], renderer, TestContext.Current.CancellationToken);

        report.Count.ShouldBe(0);
        report.AllRendered.ShouldBeTrue();
        report.ByDialect.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_diagram_whose_header_follows_a_blank_line_reports_the_header_as_its_dialect()
    {
        // Markdig hands the fence body verbatim, so a leading blank line is the author's, not a
        // parse artifact. beautiful-mermaid trims it; the dialect axis has to as well, or one
        // corpus would report `` and `graph TD` as two dialects for the same diagram type.
        var report = await RunAsync(new DiagramOccurrence("a.md", "\n\ngraph TD\n  A --> B"));

        report.ByDialect.ShouldBe([new ConstructCount("graph TD", 1)]);
        report.AllRendered.ShouldBeTrue();
    }

    /// <summary>
    /// The header spellings <c>20-reference/conversion.md</c> tells a reader are rejected: the first
    /// column of the table under "Mermaid". A table rather than the sentence it replaced, because a
    /// claim about what does not work has to be readable by the thing that knows.
    /// </summary>
    private static List<string> ClaimedRejectedHeaders()
    {
        var page = File.ReadAllText(
            Path.Combine(RepoRoot, "docs", "wiki", "20-reference", "conversion.md"));

        var section = MermaidSection().Match(page);
        section.Success.ShouldBeTrue($"No \"## Mermaid\" section in {ConversionPagePath}.");

        return RejectedRow()
            .Matches(section.Value)
            .Select(row => row.Groups["header"].Value)
            .ToList();
    }

    private Task<DiagramRenderReport> RunAsync(params DiagramOccurrence[] diagrams) =>
        RunAsync(WriteRejectsPieStub(), diagrams);

    private Task<DiagramRenderReport> RunAsync(string script, params DiagramOccurrence[] diagrams) =>
        MermaidAcceptance.RunAsync(diagrams, new MermaidRenderer(script), TestContext.Current.CancellationToken);

    /// <summary>
    /// A stub that refuses a <c>pie</c> header the way beautiful-mermaid 1.1.3 does — same exit
    /// code, same message shape, the offending header quoted — and renders anything else.
    /// </summary>
    private string WriteRejectsPieStub()
    {
        const string body =
            $$"""
            {{BundledRenderScript.ReadSourceFromStdin}}
            const header = source.split('\n').map((l) => l.trim()).find((l) => l !== '');
            if (header.startsWith('pie')) {
              process.stderr.write(
                `render-mermaid: the diagram did not render: Invalid mermaid header: "${header}". `
                  + 'Expected "graph TD", "flowchart LR", "stateDiagram-v2", etc.',
              );
              process.exitCode = 2;
            } else {
              process.stdout.write('<svg width="1" height="1"/>');
            }
            """;

        return WriteStub("rejects-pie.mjs", body);
    }

    /// <summary>A stub that records one line per invocation, so renders can be counted.</summary>
    private string WriteLoggingStub(string logPath)
    {
        var body =
            $$"""
            import { appendFileSync } from 'node:fs';
            {{BundledRenderScript.ReadSourceFromStdin}}
            appendFileSync('{{logPath}}', source.split('\n')[0] + '\n');
            process.stdout.write('{{RenderedSvg}}');
            """;

        return WriteStub("logs-renders.mjs", body);
    }

    private string WriteStub(string name, string body)
    {
        var path = Path.Combine(_stubDirectory, name);
        File.WriteAllText(path, body);
        return path;
    }

    private const string ConversionPagePath = "docs/wiki/20-reference/conversion.md";

    private const string UnnamedMessage =
        "The real renderer rejects a header the Mermaid table in " + ConversionPagePath + " does not "
        + "name, so a reader whose diagram GitHub renders gets no warning from the page. Add a row. "
        + "Unnamed:";

    private const string StaleMessage =
        "The Mermaid table in " + ConversionPagePath + " names a header the real renderer no longer "
        + "rejects, so the page warns a reader off a diagram that works. Drop the row. Stale:";

    /// <summary>The "## Mermaid" section, bounded at the next heading so the table below it is the one read.</summary>
    [GeneratedRegex(
        @"^## Mermaid$.*?(?=^\#{2,3} |\z)",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex MermaidSection();

    /// <summary>
    /// A table row whose first cell is a code span: the header spellings. The table's own header row
    /// and its separator carry no backticks, which is what keeps them out.
    /// </summary>
    [GeneratedRegex(
        @"^\|\s*`(?<header>[^`]+)`\s*\|",
        RegexOptions.Multiline | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex RejectedRow();

    private static string RepoRoot { get; } = Locate();

    /// <summary>Walks up to the directory holding <c>DocuMe.slnx</c>: the page ships in the tree.</summary>
    private static string Locate()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DocuMe.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            $"No DocuMe.slnx above {AppContext.BaseDirectory}, so {ConversionPagePath} cannot be found.");
    }
}
