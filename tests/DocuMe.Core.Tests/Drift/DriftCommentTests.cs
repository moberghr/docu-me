using DocuMe.Core.Drift;
using Shouldly;

namespace DocuMe.Core.Tests.Drift;

/// <summary>
/// The PR-comment block (PLAN.md §6.4's <c>--format github-comment</c>). It is posted by a bot that
/// edits one comment in place, so what matters is that it is stable across runs, that it says something
/// honest when nothing drifted, and that a consumer's page title cannot turn into markdown formatting.
/// </summary>
public sealed class DriftCommentTests
{
    [Fact]
    public void Render_OpensWithTheMarkerACiJobFindsItsOwnCommentBy()
    {
        DriftComment.Render(Report(Page())).ShouldStartWith(DriftComment.Marker);
    }

    [Fact]
    public void Render_NamesTheAffectedPagesAndWhatTheyMatched()
    {
        var comment = DriftComment.Render(Report(Page()));

        comment.ShouldContain("This PR touches sources for **1 wiki page** of 3 with declared sources:");
        comment.ShouldContain("- **Loans** — `domains/loans.md`");
        comment.ShouldContain("- `src/Loans/**` → `src/Loans/A.cs`, `src/Loans/B.cs`");
        comment.ShouldContain("advisory");
    }

    [Fact]
    public void Render_CarriesTheRevisionsAndTheDiffSize()
    {
        var comment = DriftComment.Render(Report(Page()));

        comment.ShouldContain("baseline `abc1234` → head `def5678`, 9 changed files.");
    }

    /// <summary>
    /// The comment is a slot, not a log: leaving yesterday's warning up after the PR fixed the docs
    /// would teach the reviewer to ignore it.
    /// </summary>
    [Fact]
    public void Render_SaysSoWhenNothingDrifted()
    {
        var comment = DriftComment.Render(Report());

        comment.ShouldStartWith(DriftComment.Marker);
        comment.ShouldContain("No documented sources were touched.");
        comment.ShouldNotContain("touches sources for");
    }

    [Fact]
    public void Render_DistinguishesNothingDriftedFromNothingDeclared()
    {
        var comment = DriftComment.Render(Report() with { PagesWithSourcesCount = 0 });

        comment.ShouldContain("No page in this wiki declares a `sources:` glob");
        comment.ShouldNotContain("No documented sources were touched.");
    }

    [Fact]
    public void Render_StatesTheOverflowRatherThanTrimmingQuietly()
    {
        var files = Enumerable.Range(1, 9).Select(index => $"src/Loans/F{index}.cs").ToList();
        var page = new DriftedPage("domains/loans.md", "Loans", [new SourceMatch("src/Loans/**", files)]);

        var comment = DriftComment.Render(Report(page));

        comment.ShouldContain("`src/Loans/F5.cs` and 4 more");
        comment.ShouldNotContain("F6.cs");
    }

    [Fact]
    public void Render_EscapesAPageTitleThatLooksLikeMarkdown()
    {
        var page = new DriftedPage(
            "domains/rates.md",
            "Rates_and_Fees <draft>",
            [new SourceMatch("src/Rates/**", ["src/Rates/A.cs"])]);

        var comment = DriftComment.Render(Report(page));

        comment.ShouldContain(@"**Rates\_and\_Fees \<draft\>**");
    }

    [Fact]
    public void Render_IsDeterministic()
    {
        var report = Report(Page());

        DriftComment.Render(report).ShouldBe(DriftComment.Render(report));
    }

    [Fact]
    public void Render_ReadsSingularForOneFileAndOnePage()
    {
        var page = new DriftedPage("a.md", "A", [new SourceMatch("src/**", ["src/A.cs"])]);
        var comment = DriftComment.Render(Report(page) with { ChangedFileCount = 1 });

        comment.ShouldContain("**1 wiki page**");
        comment.ShouldContain("1 changed file.");
    }

    private static DriftReport Report(params DriftedPage[] pages) => new()
    {
        Baseline = "abc1234",
        Head = "def5678",
        ChangedFileCount = 9,
        PageCount = 4,
        PagesWithSourcesCount = 3,
        Pages = pages,
    };

    private static DriftedPage Page() => new(
        "domains/loans.md",
        "Loans",
        [new SourceMatch("src/Loans/**", ["src/Loans/A.cs", "src/Loans/B.cs"])]);
}
