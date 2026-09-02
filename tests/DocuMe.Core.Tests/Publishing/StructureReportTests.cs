using DocuMe.Core.Publishing;
using Shouldly;

namespace DocuMe.Core.Tests.Publishing;

/// <summary>
/// The shape of the tree, as a pure function of the publishable paths
/// (<c>docs/specs/2026-09-02-wiki-structure.md</c> §3.1). Two findings and deliberately no more: a
/// directory holding pages with no index page, and a parent wider than <c>wiki.maxChildren</c>.
/// </summary>
/// <remarks>
/// The driver is the AurServices cutover: 146 pages published, every check green, and 54 of them in one
/// flat pile on the space root. Nothing was wrong with any page; the tree had a shape and nothing modelled
/// it. These tests pin the sentence that makes a reader act — "these ten pages are on the space root" —
/// rather than any particular wording of it.
/// </remarks>
public sealed class StructureReportTests
{
    /// <summary>The default this suite asserts against; <c>WikiConfig.DefaultMaxChildren</c> owns the value.</summary>
    private const int Twelve = 12;

    /// <summary>
    /// Two directories with pages, one of them indexed. <c>20-integrations</c> is the orphan: its pages
    /// walk past their own directory and land on the root index.
    /// </summary>
    private static readonly string[] MixedTree =
    [
        "README.md",
        "10-domains/README.md",
        "10-domains/loans.md",
        "20-integrations/payments.md",
        "20-integrations/billing.md",
    ];

    [Fact]
    public void Names_a_directory_that_holds_pages_and_has_no_index_page()
    {
        var report = StructureReport.Of(MixedTree, homePage: null, maxChildren: Twelve);

        var orphan = report.OrphanedDirectories.ShouldHaveSingleItem();

        orphan.Directory.ShouldBe("20-integrations");
        orphan.PageCount.ShouldBe(2);
        orphan.IndexPath.ShouldBe("20-integrations/README.md");
    }

    /// <summary>
    /// The ancestor the pages actually hang under, which is the half that makes the finding actionable:
    /// "filed under the root index" and "filed under the space root" are different problems.
    /// </summary>
    [Fact]
    public void Names_the_ancestor_the_orphaned_directorys_pages_resolve_to()
    {
        var report = StructureReport.Of(MixedTree, homePage: null, maxChildren: Twelve);

        report.OrphanedDirectories.ShouldHaveSingleItem().ResolvedParent.ShouldBe("README.md");
    }

    /// <summary>
    /// The AurServices shape: no root index either, so the orphaned directories' pages walk all the way
    /// out to the space root. <c>ResolvedParent</c> is null there, and null is how the report spells it.
    /// </summary>
    [Fact]
    public void A_page_with_no_index_page_above_it_at_all_resolves_to_the_space_root()
    {
        string[] tree = ["20-integrations/payments.md", "30-infrastructure/dns.md"];

        var report = StructureReport.Of(tree, homePage: null, maxChildren: Twelve);

        // The count first: ShouldAllBe over an empty list passes, so without this the assertion below
        // would hold just as well if the check had found nothing at all.
        report.OrphanedDirectories.Select(directory => directory.Directory)
            .ShouldBe(["20-integrations", "30-infrastructure"]);
        report.OrphanedDirectories.ShouldAllBe(directory => directory.ResolvedParent == null);
    }

    /// <summary>
    /// The wiki root is a directory like any other. It is also the one whose missing index page put 54
    /// AurServices pages on the space root, so exempting it would hide the finding that mattered most.
    /// </summary>
    [Fact]
    public void The_wiki_root_holding_pages_with_no_index_page_is_itself_a_finding()
    {
        string[] tree = ["overview.md", "operations.md"];

        var report = StructureReport.Of(tree, homePage: null, maxChildren: Twelve);

        var orphan = report.OrphanedDirectories.ShouldHaveSingleItem();

        orphan.Directory.ShouldBe(string.Empty);
        orphan.IndexPath.ShouldBe("README.md");
        orphan.PageCount.ShouldBe(2);
    }

    /// <summary>An indexed directory is not a finding, which is the whole negative half of the check.</summary>
    [Fact]
    public void A_directory_with_an_index_page_is_not_a_finding()
    {
        var report = StructureReport.Of(MixedTree, homePage: null, maxChildren: Twelve);

        report.OrphanedDirectories
            .Select(directory => directory.Directory)
            .ShouldNotContain("10-domains");
    }

    /// <summary>
    /// <c>wiki.homePage</c> decides what an index page is called, and the check has to read the same key
    /// <see cref="PageHierarchy"/> reads or it would report directories that are indexed.
    /// </summary>
    [Fact]
    public void Honours_a_custom_home_page_name()
    {
        string[] tree = ["index.md", "10-domains/index.md", "10-domains/loans.md"];

        var report = StructureReport.Of(tree, homePage: "index.md", maxChildren: Twelve);

        report.OrphanedDirectories.ShouldBeEmpty();
    }

    /// <summary>
    /// Direct pages only: a directory whose pages all live one level further down is not holding pages.
    /// </summary>
    /// <remarks>
    /// The narrower of two readings of SC1's "every directory with publishable pages", and the one the
    /// spec's own arithmetic supports: fifteen AurServices directories holding seventy pages is a count of
    /// directories that hold pages directly. It leaves one shape unreported — <c>a</c> in a tree of
    /// <c>README.md</c> plus <c>a/b/README.md</c> has no index and no direct pages, so nothing is said
    /// while <c>a/b</c>'s index hangs two levels up. Recorded rather than hidden; see the plan.
    /// </remarks>
    [Fact]
    public void Counts_the_pages_directly_in_the_directory_and_not_its_descendants()
    {
        string[] tree = ["README.md", "a/b/page.md"];

        var report = StructureReport.Of(tree, homePage: null, maxChildren: Twelve);

        var orphan = report.OrphanedDirectories.ShouldHaveSingleItem();

        orphan.Directory.ShouldBe("a/b");
        orphan.PageCount.ShouldBe(1);
    }

    /// <summary>SC2: the width finding, and the root counts as a parent.</summary>
    [Fact]
    public void Names_a_parent_with_more_children_than_the_limit()
    {
        var tree = Enumerable.Range(1, 5).Select(n => $"page-{n}.md").Append("README.md").ToArray();

        var report = StructureReport.Of(tree, homePage: null, maxChildren: 4);

        var wide = report.WideParents.ShouldHaveSingleItem();

        wide.Parent.ShouldBe("README.md");
        wide.ChildCount.ShouldBe(5);
    }

    /// <summary>
    /// The space root is a parent with no page behind it, spelled as a null <c>Parent</c> — the same
    /// "absent key on the wire" spelling an unowned page uses, so root has exactly one representation.
    /// </summary>
    [Fact]
    public void The_space_root_counts_as_a_parent()
    {
        var tree = Enumerable.Range(1, 3).Select(n => $"page-{n}.md").ToArray();

        var report = StructureReport.Of(tree, homePage: null, maxChildren: 2);

        report.WideParents.ShouldHaveSingleItem().Parent.ShouldBeNull();
    }

    /// <summary>
    /// "More than" and not "at least": a repo that sets the number to what it has should not be told its
    /// tree is too wide.
    /// </summary>
    [Fact]
    public void Exactly_the_limit_is_not_a_finding()
    {
        var tree = Enumerable.Range(1, 4).Select(n => $"page-{n}.md").Append("README.md").ToArray();

        var report = StructureReport.Of(tree, homePage: null, maxChildren: 4);

        report.WideParents.ShouldBeEmpty();
    }

    /// <summary>
    /// A healthy tree produces nothing, which is what lets the check say <c>Ok</c> rather than "no
    /// findings, probably".
    /// </summary>
    [Fact]
    public void A_fully_indexed_narrow_tree_reports_nothing()
    {
        string[] tree = ["README.md", "10-domains/README.md", "10-domains/loans.md"];

        var report = StructureReport.Of(tree, homePage: null, maxChildren: Twelve);

        report.OrphanedDirectories.ShouldBeEmpty();
        report.WideParents.ShouldBeEmpty();
        report.HasFindings.ShouldBeFalse();
    }

    /// <summary>
    /// Ordinal order, both lists, because the check's output is read by a human scanning for their
    /// directory and by a skill diffing two runs. Neither survives a set's enumeration order.
    /// </summary>
    [Fact]
    public void Findings_come_out_in_ordinal_path_order()
    {
        string[] tree = ["z-last/page.md", "a-first/page.md", "m-middle/page.md"];

        var report = StructureReport.Of(tree, homePage: null, maxChildren: Twelve);

        report.OrphanedDirectories
            .Select(directory => directory.Directory)
            .ShouldBe(["a-first", "m-middle", "z-last"]);

        // The same claim for the other list, which the summary said and only the first list proved. It
        // needs an indexed tree: in the tree above every page resolves to the space root, so there is one
        // parent group and one group cannot demonstrate an order. Ordinally the root sorts first, then
        // "README.md" ahead of the section indexes, because 'R' precedes 'a'.
        string[] indexed =
        [
            "README.md",
            "z-last/README.md", "z-last/page.md",
            "a-first/README.md", "a-first/page.md",
            "m-middle/README.md", "m-middle/page.md",
        ];

        StructureReport.Of(indexed, homePage: null, maxChildren: 0)
            .WideParents
            .Select(parent => parent.Parent)
            .ShouldBe([null, "README.md", "a-first/README.md", "m-middle/README.md", "z-last/README.md"]);
    }

    /// <summary>
    /// Ordinal grouping, so two directories differing only in case stay two directories. Confluence
    /// titles are case-insensitive and a filesystem may be too, but the path set is the contract here and
    /// folding them would silently merge two findings into one wrong count.
    /// </summary>
    [Fact]
    public void Directories_differing_only_by_case_are_two_directories()
    {
        string[] tree = ["README.md", "Guides/setup.md", "guides/install.md"];

        var report = StructureReport.Of(tree, homePage: null, maxChildren: Twelve);

        report.OrphanedDirectories
            .Select(directory => directory.Directory)
            .ShouldBe(["Guides", "guides"]);
    }

    /// <summary>
    /// The index name comes from <c>wiki.homePage</c> and nowhere else: a directory holding a
    /// conventionally-named <c>README.md</c> under a config that calls the index <c>index.md</c> has no
    /// index page, and saying otherwise would be the resolver and the check disagreeing.
    /// </summary>
    [Fact]
    public void A_readme_is_not_an_index_page_when_the_config_names_a_different_one()
    {
        string[] tree = ["index.md", "10-domains/README.md", "10-domains/loans.md"];

        var report = StructureReport.Of(tree, homePage: "index.md", maxChildren: Twelve);

        var orphan = report.OrphanedDirectories.ShouldHaveSingleItem();

        orphan.Directory.ShouldBe("10-domains");
        orphan.IndexPath.ShouldBe("10-domains/index.md");

        // Both pages in it are ordinary pages under this config, README.md included.
        orphan.PageCount.ShouldBe(2);
        orphan.ResolvedParent.ShouldBe("index.md");
    }

    /// <summary>The widest parent, for the check's one-line summary. Null when there are no pages at all.</summary>
    [Fact]
    public void Reports_the_widest_parent_for_the_summary_line()
    {
        var report = StructureReport.Of(MixedTree, homePage: null, maxChildren: Twelve);

        // README.md parents 10-domains/README.md, 20-integrations/payments.md and .../billing.md.
        report.WidestParentChildCount.ShouldBe(3);
    }

    /// <summary>
    /// The dogfood wiki caught this one: <c>_meta/</c> is excluded and <c>_meta/GAPS.md</c> is in the tree
    /// only because <c>wiki.extraPages</c> re-includes it, so demanding <c>_meta/README.md</c> is advice
    /// nobody can take — the file would be excluded and the directory would still have no index.
    /// </summary>
    [Fact]
    public void A_directory_holding_only_re_included_pages_is_not_an_orphaned_directory()
    {
        string[] tree = ["README.md", "_meta/GAPS.md"];

        var report = StructureReport.Of(
            tree,
            homePage: null,
            maxChildren: Twelve,
            reIncludedPaths: new HashSet<string>(StringComparer.Ordinal) { "_meta/GAPS.md" });

        report.OrphanedDirectories.ShouldBeEmpty();
    }

    /// <summary>
    /// The exemption is about the directory, not the page: a re-included page still hangs somewhere, and a
    /// directory that also holds ordinary pages is still missing an index page for them.
    /// </summary>
    [Fact]
    public void A_re_included_page_still_counts_as_a_child_and_does_not_exempt_its_neighbours()
    {
        string[] tree = ["_meta/GAPS.md", "_meta/notes.md"];

        var report = StructureReport.Of(
            tree,
            homePage: null,
            maxChildren: 1,
            reIncludedPaths: new HashSet<string>(StringComparer.Ordinal) { "_meta/GAPS.md" });

        report.OrphanedDirectories.ShouldHaveSingleItem().Directory.ShouldBe("_meta");
        report.WideParents.ShouldHaveSingleItem().ChildCount.ShouldBe(2);
    }

    [Fact]
    public void An_empty_tree_is_not_a_finding()
    {
        var report = StructureReport.Of([], homePage: null, maxChildren: Twelve);

        report.HasFindings.ShouldBeFalse();
        report.WidestParentChildCount.ShouldBe(0);
    }
}
