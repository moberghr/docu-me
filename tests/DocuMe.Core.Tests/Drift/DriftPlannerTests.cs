using DocuMe.Core.Drift;
using DocuMe.Core.Markdown;
using Shouldly;

namespace DocuMe.Core.Tests.Drift;

/// <summary>
/// The drift matcher's contract (PLAN.md §6.4). What is pinned here is mostly about the shape of a
/// <em>negative</em> answer: this command's normal output is "nothing drifted", a green advisory check
/// is nobody's cue to investigate, so every way a glob could silently never fire is a way the tool lies
/// and gets believed.
/// </summary>
public sealed class DriftPlannerTests
{
    private const string Baseline = "abc1234";
    private const string Head = "HEAD";

    [Fact]
    public void Plan_MatchesAPageAgainstItsSourceGlob()
    {
        var report = Plan(
            ["src/Loans/LoanService.cs", "src/Rates/Table.cs"],
            Page("domains/loans.md", "Loans", "src/Loans/**"));

        var page = report.Pages.ShouldHaveSingleItem();
        page.Path.ShouldBe("domains/loans.md");
        page.Title.ShouldBe("Loans");
        page.Matches.ShouldHaveSingleItem().Pattern.ShouldBe("src/Loans/**");
        page.Matches[0].Files.ShouldBe(["src/Loans/LoanService.cs"]);
        report.HasDrift.ShouldBeTrue();
    }

    [Fact]
    public void Plan_ReportsWhichPatternMatchedWhichFiles()
    {
        // §6.4 asks for the matched patterns, not just the page: a reviewer who cannot see why a page
        // was flagged has to re-run the diff by hand to decide whether to care.
        var report = Plan(
            ["src/Loans/A.cs", "src/Loans/B.cs", "docs/adr/007-rates.md", "src/Unrelated/C.cs"],
            Page("domains/loans.md", "Loans", "src/Loans/**", "docs/adr/*.md", "src/Never/**"));

        var matches = report.Pages.ShouldHaveSingleItem().Matches;
        matches.Count.ShouldBe(2);
        matches[0].Pattern.ShouldBe("src/Loans/**");
        matches[0].Files.ShouldBe(["src/Loans/A.cs", "src/Loans/B.cs"]);
        matches[1].Pattern.ShouldBe("docs/adr/*.md");
        matches[1].Files.ShouldBe(["docs/adr/007-rates.md"]);
    }

    [Fact]
    public void Plan_CountsAFileMatchedByTwoPatternsOnce()
    {
        // Overlapping globs on one page are ordinary (a directory plus one file inside it). A count
        // that added them up would report a one-file change as two.
        var report = Plan(
            ["src/Loans/LoanService.cs"],
            Page("domains/loans.md", "Loans", "src/Loans/**", "src/Loans/LoanService.cs"));

        var page = report.Pages.ShouldHaveSingleItem();
        page.Matches.Count.ShouldBe(2);
        page.MatchedFileCount.ShouldBe(1);
    }

    [Fact]
    public void Plan_LeavesAPageWhoseSourcesWereNotTouchedAlone()
    {
        var report = Plan(["README.md"], Page("domains/loans.md", "Loans", "src/Loans/**"));

        report.Pages.ShouldBeEmpty();
        report.HasDrift.ShouldBeFalse();
        report.PagesWithSourcesCount.ShouldBe(1);
    }

    /// <summary>
    /// The denominators are the difference between "your docs are fine" and "this feature is switched
    /// off", and both answers otherwise print as zero.
    /// </summary>
    [Fact]
    public void Plan_SaysWhenNoPageDeclaresSourcesAtAll()
    {
        var report = Plan(
            ["src/Loans/LoanService.cs"],
            Page("domains/loans.md", "Loans"),
            Page("index.md", "Home"));

        report.SourcesUndeclared.ShouldBeTrue();
        report.PageCount.ShouldBe(2);
        report.PagesWithSourcesCount.ShouldBe(0);
        report.HasDrift.ShouldBeFalse();
    }

    [Fact]
    public void Plan_CarriesTheRevisionsAndTheChangedFileCount()
    {
        var report = Plan(
            ["a.cs", "b.cs", "b.cs"],
            Page("domains/loans.md", "Loans", "src/**"));

        report.Baseline.ShouldBe(Baseline);
        report.Head.ShouldBe(Head);

        // De-duplicated: `git diff --no-renames` lists a rename as two paths, but a caller that passed
        // the same path twice must not inflate the count a report headline shows.
        report.ChangedFileCount.ShouldBe(2);
    }

    [Fact]
    public void Plan_OrdersAffectedPagesByPath()
    {
        var report = Plan(
            ["src/A.cs"],
            Page("zebra.md", "Zebra", "src/**"),
            Page("alpha.md", "Alpha", "src/**"));

        report.Pages.Select(page => page.Path).ShouldBe(["alpha.md", "zebra.md"]);
    }

    /// <summary>
    /// <c>src/Loans/</c> is how a human spells "that directory". <see cref="Microsoft.Extensions.FileSystemGlobbing.Matcher"/>
    /// would match no file for it, forever, and the page would never be flagged.
    /// </summary>
    [Fact]
    public void Plan_TreatsATrailingSlashAsTheDirectoryItObviouslyMeans()
    {
        var report = Plan(
            ["src/Loans/deep/LoanService.cs"],
            Page("domains/loans.md", "Loans", "src/Loans/"));

        var match = report.Pages.ShouldHaveSingleItem().Matches.ShouldHaveSingleItem();

        // Reported as the frontmatter spells it, not as normalized: the report explains a file the
        // reader can open.
        match.Pattern.ShouldBe("src/Loans/");
        match.Files.ShouldBe(["src/Loans/deep/LoanService.cs"]);
    }

    [Fact]
    public void Plan_AnchorsALeadingSlashAtTheRepoRoot()
    {
        // The gitignore habit. Left alone it matches nothing.
        var report = Plan(["src/Loans/A.cs"], Page("domains/loans.md", "Loans", "/src/Loans/**"));

        report.Pages.ShouldHaveSingleItem().Matches.ShouldHaveSingleItem().Pattern.ShouldBe("/src/Loans/**");
    }

    [Fact]
    public void Plan_MatchesCaseSensitively()
    {
        // git reports paths as it stores them, and the tree's own wiki.exclude globs are Ordinal too
        // (WikiTree.InScope). A case-folding matcher here would disagree with the one already shipping.
        var report = Plan(["SRC/Loans/A.cs"], Page("domains/loans.md", "Loans", "src/Loans/**"));

        report.Pages.ShouldBeEmpty();
    }

    [Fact]
    public void Plan_IgnoresBlanksOnBothSides()
    {
        var report = Plan(
            [string.Empty, "   ", "src/A.cs"],
            Page("domains/loans.md", "Loans", "  ", "src/**"));

        report.ChangedFileCount.ShouldBe(1);
        report.Pages.ShouldHaveSingleItem().Matches.ShouldHaveSingleItem().Pattern.ShouldBe("src/**");
    }

    [Fact]
    public void Plan_OnAnEmptyDiffReportsNothing()
    {
        var report = Plan([], Page("domains/loans.md", "Loans", "src/**"));

        report.ChangedFileCount.ShouldBe(0);
        report.Pages.ShouldBeEmpty();
        report.SourcesUndeclared.ShouldBeFalse();
    }

    [Fact]
    public void Plan_IsDeterministic()
    {
        // Two runs over one diff must agree, because a CI job edits one PR comment in place and a
        // reordered list would read as a new finding.
        string[] changed = ["src/Loans/A.cs", "src/Rates/B.cs"];
        var pages = new[]
        {
            Page("domains/loans.md", "Loans", "src/Loans/**"),
            Page("domains/rates.md", "Rates", "src/Rates/**"),
        };

        // Compared as JSON, not as records: a record's list members compare by reference, so equality
        // here would pass on two reports that disagreed about everything.
        DriftPlanner.Plan(Baseline, Head, changed, pages).ToJson()
            .ShouldBe(DriftPlanner.Plan(Baseline, Head, changed, pages).ToJson());
    }

    [Fact]
    public void Plan_NeedsBothRevisions()
    {
        Should.Throw<ArgumentException>(() => DriftPlanner.Plan(
            " ", Head, [], [Page("a.md", "A", "src/**")]));
        Should.Throw<ArgumentException>(() => DriftPlanner.Plan(
            Baseline, " ", [], [Page("a.md", "A", "src/**")]));
    }

    private static DriftReport Plan(string[] changedFiles, params WikiPage[] pages) =>
        DriftPlanner.Plan(Baseline, Head, changedFiles, pages);

    private static WikiPage Page(string path, string title, params string[] sources) => new(
        path,
        title,
        new ParsedPage(new PageFrontmatter { Sources = sources }, title, string.Empty));
}
