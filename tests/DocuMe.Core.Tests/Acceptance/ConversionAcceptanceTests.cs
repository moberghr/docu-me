using DocuMe.Core.Acceptance;
using DocuMe.Core.Markdown;
using DocuMe.Core.Tests.Markdown;
using Shouldly;

namespace DocuMe.Core.Tests.Acceptance;

/// <summary>
/// The PLAN.md §4.4 acceptance runner. Its fixture corpus is the golden suite
/// (<c>tests/golden</c>): 27 pages whose conversion is already reviewed by hand, so the runner's
/// expected output over them is known exactly rather than asserted against itself.
/// </summary>
public sealed class ConversionAcceptanceTests : IDisposable
{
    /// <summary>
    /// Every degradation the golden corpus reports, as <c>page | code | construct</c> in report
    /// order (page path, then render order within a page).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hand-verified, not generated: this is the runner's contract in the same sense the
    /// <c>.storage.xml</c> files are the converter's (§4.3). A new golden case that changes this
    /// list is telling you something — either it degrades, or the converter started degrading
    /// something it used to convert whole. Update the list only with that diff understood.
    /// </para>
    /// <para>
    /// Two rows arrived with the table-alignment and alert-collapse reporting sites, each verified
    /// against the corpus by hand. <c>tables.md</c> line 6 is <c>|--------|:----:|------------|</c>,
    /// one centered column of three; the corpus's other tables (<c>tables-ragged.md</c>,
    /// <c>alert-with-blocks.md</c>) use a plain <c>|---|</c> and lose nothing. <c>alerts.md</c>
    /// holds exactly one <c>[!IMPORTANT]</c> (line 13), while its three NOTE markers and
    /// <c>alerts-nested.md</c>'s root NOTE keep the <c>info</c> panel they own and stay silent —
    /// the collapse is IMPORTANT's loss, not NOTE's. No row for the ordered-list or task-numbering
    /// sites: every ordered list in the corpus starts at 1, and its one ordered task list is mixed,
    /// so it degrades to <c>&lt;ol&gt;</c> and reports that instead.
    /// </para>
    /// </remarks>
    private static readonly string[] GoldenDegradations =
    [
        "alerts.md | alert-type-collapsed | [!IMPORTANT]",
        "code-blocks.md | unknown-fence-language | brainfuck",
        "links-external.md | same-page-anchor-link | #introduction",
        "mermaid.md | unknown-fence-language | nim",
        "tables.md | table-alignment-dropped | center",
        "task-lists-mixed.md | mixed-task-list | ul",
        "task-lists-mixed.md | mixed-task-list | ol",
    ];

    private readonly string _dir = Directory.CreateTempSubdirectory("docume-acceptance-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Converts_every_page_of_the_golden_corpus_without_one_failing()
    {
        var report = RunGolden();

        report.PageCount.ShouldBe(
            Directory.EnumerateFiles(GoldenCorpus.Directory, "*.md", SearchOption.AllDirectories).Count());
        report.FailedPageCount.ShouldBe(0, FailureSummary(report));
    }

    [Fact]
    public void Reports_exactly_the_known_degradations_of_the_golden_corpus()
    {
        var report = RunGolden();

        Flatten(report).ShouldBe(GoldenDegradations);
    }

    [Fact]
    public void Groups_degradations_by_code_and_orders_warnings_by_frequency()
    {
        var report = RunGolden();

        // Two codes fire twice each, so that tie breaks ordinally on the code; the three single
        // occurrences sort after them, ordinally among themselves. Deterministic order is what
        // makes a run's output diffable.
        report.Diagnostics.Select(group => group.Code).ShouldBe(
            [
                ConversionDiagnosticCodes.MixedTaskList,
                ConversionDiagnosticCodes.UnknownFenceLanguage,
                ConversionDiagnosticCodes.AlertTypeCollapsed,
                ConversionDiagnosticCodes.SamePageAnchorLink,
                ConversionDiagnosticCodes.TableAlignmentDropped,
            ]);
    }

    [Fact]
    public void Groups_each_code_by_the_dialect_that_triggered_it()
    {
        var report = RunGolden();

        var fences = Group(report, ConversionDiagnosticCodes.UnknownFenceLanguage);
        fences.Count.ShouldBe(2);
        fences.PageCount.ShouldBe(2);
        fences.ByConstruct.ShouldBe([new ConstructCount("brainfuck", 1), new ConstructCount("nim", 1)]);

        // Two occurrences, one page: the count and the page count are different questions.
        var tasks = Group(report, ConversionDiagnosticCodes.MixedTaskList);
        tasks.Count.ShouldBe(2);
        tasks.PageCount.ShouldBe(1);
        tasks.ByConstruct.ShouldBe([new ConstructCount("ol", 1), new ConstructCount("ul", 1)]);
    }

    [Fact]
    public void Strict_policy_leaves_the_golden_corpus_short_of_the_acceptance_bar()
    {
        var report = RunGolden();

        report.Policy.AcceptedCodes.ShouldBeEmpty();
        report.WarningCount.ShouldBe(GoldenDegradations.Length);
        report.Diagnostics.ShouldAllBe(group => group.Severity == DiagnosticSeverity.Warning);
        report.MeetsAcceptanceBar.ShouldBeFalse();
    }

    [Fact]
    public void Accepting_every_reported_code_clears_the_bar_without_hiding_a_single_count()
    {
        var policy = new AcceptancePolicy(
        [
            ConversionDiagnosticCodes.UnknownFenceLanguage,
            ConversionDiagnosticCodes.MixedTaskList,
            ConversionDiagnosticCodes.SamePageAnchorLink,
            ConversionDiagnosticCodes.AlertTypeCollapsed,
            ConversionDiagnosticCodes.TableAlignmentDropped,
        ]);

        var report = ConversionAcceptance.RunDirectory(GoldenCorpus.Directory, GoldenCorpus.Resolvers, policy);

        report.MeetsAcceptanceBar.ShouldBeTrue();
        report.WarningCount.ShouldBe(0);

        // The whole point of a severity tier over simply not reporting the loss: the count and
        // the dialect breakdown survive being accepted.
        report.DiagnosticCount.ShouldBe(GoldenDegradations.Length);
        report.Diagnostics.ShouldAllBe(group => group.Severity == DiagnosticSeverity.Note);
        Flatten(report).ShouldBe(GoldenDegradations);
    }

    [Fact]
    public void An_empty_corpus_does_not_clear_the_acceptance_bar()
    {
        var report = ConversionAcceptance.Run([]);

        report.PageCount.ShouldBe(0);
        report.MeetsAcceptanceBar.ShouldBeFalse();
    }

    [Fact]
    public void Groups_failed_pages_by_construct_and_keeps_the_dialect_that_triggered_each()
    {
        var report = ConversionAcceptance.Run(
        [
            Page("docs/arch.md", "```plantuml\n@startuml\n@enduml\n```\n"),
            Page("docs/net.md", "```dot\ndigraph {}\n```\n"),
            Page("docs/legacy.md", "Inline <span>html</span> here.\n"),
        ]);

        report.FailedPageCount.ShouldBe(3);
        report.Failures.Count.ShouldBe(2, FailureSummary(report));

        // Two dialects, one construct: the quoted token is normalized out of the grouping key,
        // which is what lets "how many pages hit this" and "which dialects" be read separately.
        var dialects = report.Failures[0];
        dialects.Count.ShouldBe(2);
        dialects.Kind.ShouldContain("is a diagram dialect DocuMe cannot render");
        dialects.Kind.ShouldNotContain("plantuml");
        dialects.ByToken.ShouldBe([new ConstructCount("dot", 1), new ConstructCount("plantuml", 1)]);
        dialects.Occurrences.Select(occurrence => occurrence.Path).ShouldBe(["docs/arch.md", "docs/net.md"]);
        dialects.Occurrences[0].Message.ShouldContain("plantuml");

        report.Failures[1].ByToken.ShouldBe([new ConstructCount("<span>", 1)]);
    }

    [Fact]
    public void Keeps_the_degradations_a_page_reported_before_it_failed()
    {
        var report = ConversionAcceptance.Run(
            [Page("mixed.md", "```brainfuck\n+++\n```\n\n```dot\ndigraph {}\n```\n")]);

        // One pass tells a reader "this page fails on X *and* degrades Y", which is why the
        // converter does not discard diagnostics when it throws.
        report.FailedPageCount.ShouldBe(1);
        report.DiagnosticCount.ShouldBe(1);
        Flatten(report).ShouldBe(["mixed.md | unknown-fence-language | brainfuck"]);
    }

    [Fact]
    public void A_failure_message_quoting_nothing_groups_by_its_whole_text()
    {
        const string message = "GFM pipe tables cannot express merged cells.";
        List<PageConversionResult> pages =
        [
            new("a.md", new ConversionFailure(message, null, message), []),
            new("b.md", new ConversionFailure(message, null, message), []),
        ];

        var report = AcceptanceReport.From(pages, AcceptancePolicy.Strict);

        report.Failures.Count.ShouldBe(1);
        report.Failures[0].Kind.ShouldBe(message);
        report.Failures[0].Count.ShouldBe(2);
        report.Failures[0].ByToken.ShouldBeEmpty();
    }

    [Fact]
    public void RunTree_binds_each_page_its_own_resolvers_so_a_nested_relative_link_resolves()
    {
        Write("README.md", "# Home\n\nSee [loans](domains/loans/README.md).\n");

        // '../../README.md' only resolves when the resolvers are bound to the *linking page's*
        // directory; against the wiki root it climbs above the tree and the converter fails loud.
        Write("domains/loans/README.md", "# Loans\n\nBack to [home](../../README.md).\n");

        var report = ConversionAcceptance.RunTree(WikiTree.Load(_dir));

        report.PageCount.ShouldBe(2);
        report.FailedPageCount.ShouldBe(0, FailureSummary(report));
        report.DiagnosticCount.ShouldBe(0);
        report.MeetsAcceptanceBar.ShouldBeTrue();
    }

    private static AcceptanceReport RunGolden() =>
        ConversionAcceptance.RunDirectory(GoldenCorpus.Directory, GoldenCorpus.Resolvers);

    private static AcceptancePage Page(string path, string body) =>
        new(path, body, new PageResolvers(_ => null, _ => null, _ => null));

    private static DiagnosticGroup Group(AcceptanceReport report, string code) =>
        report.Diagnostics.Single(group => string.Equals(group.Code, code, StringComparison.Ordinal));

    /// <summary>Every degradation as <c>page | code | construct</c>, in report order.</summary>
    private static IReadOnlyList<string> Flatten(AcceptanceReport report) =>
    [
        .. report.Pages.SelectMany(page => page.Diagnostics.Select(
            diagnostic => $"{page.Path} | {diagnostic.Code} | {diagnostic.Construct}")),
    ];

    private static string FailureSummary(AcceptanceReport report) =>
        report.Failures.Count == 0
            ? "no failures"
            : string.Join(
                " // ",
                report.Failures.Select(group => $"{group.Count}x {group.Occurrences[0].Message}"));

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_dir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}
