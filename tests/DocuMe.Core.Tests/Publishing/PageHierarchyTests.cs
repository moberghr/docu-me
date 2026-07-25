using DocuMe.Core.Publishing;
using Shouldly;

namespace DocuMe.Core.Tests.Publishing;

/// <summary>
/// Parent resolution (PLAN.md §6.2, "parents before children, depth-first"): the directory index page
/// parents its directory, the walk skips directories that have no index, and the root index has no
/// parent at all.
/// </summary>
public sealed class PageHierarchyTests
{
    private static readonly string[] FullTree =
    [
        "README.md",
        "10-domains/README.md",
        "10-domains/loans/README.md",
        "10-domains/loans/pricing.md",
        "10-domains/settlement.md",
        "operations.md",
    ];

    [Theory]
    [InlineData("README.md", null)]
    [InlineData("operations.md", "README.md")]
    [InlineData("10-domains/README.md", "README.md")]
    [InlineData("10-domains/settlement.md", "10-domains/README.md")]
    [InlineData("10-domains/loans/README.md", "10-domains/README.md")]
    [InlineData("10-domains/loans/pricing.md", "10-domains/loans/README.md")]
    public void Hangs_every_page_under_the_nearest_index_page_above_it(string path, string? expected)
    {
        var parents = PageHierarchy.Resolve(FullTree);

        parents[path].ShouldBe(expected);
    }

    /// <summary>
    /// A directory with no index page contributes no level: inventing one would publish a page no
    /// author wrote (rule §9.1).
    /// </summary>
    [Fact]
    public void Skips_a_directory_that_has_no_index_page()
    {
        string[] paths = ["README.md", "10-domains/loans/pricing.md"];

        PageHierarchy.Resolve(paths)["10-domains/loans/pricing.md"].ShouldBe("README.md");
    }

    /// <summary>
    /// With no root index either, the walk runs out and the page goes to the tree root — which the
    /// write path files under <c>confluence.rootPageId</c>.
    /// </summary>
    [Fact]
    public void Puts_a_page_at_the_tree_root_when_no_index_page_exists_above_it()
    {
        string[] paths = ["guides/setup.md", "guides/deploy.md"];

        PageHierarchy.Resolve(paths).Values.ShouldAllBe(parent => parent == null);
    }

    /// <summary>
    /// The index filename follows <c>wiki.homePage</c> (§5.1) rather than being hardcoded, and applies
    /// per directory: a repo whose home page is <c>Home.md</c> has no <c>README.md</c> convention.
    /// </summary>
    [Fact]
    public void Takes_the_index_filename_from_the_configured_home_page()
    {
        string[] paths = ["Home.md", "README.md", "guides/Home.md", "guides/setup.md"];

        var parents = PageHierarchy.Resolve(paths, "Home.md");

        parents["Home.md"].ShouldBeNull();
        parents["guides/Home.md"].ShouldBe("Home.md");
        parents["guides/setup.md"].ShouldBe("guides/Home.md");

        // A README is an ordinary page in that repo, not an index.
        parents["README.md"].ShouldBe("Home.md");
    }

    [Fact]
    public void Resolves_one_page_without_the_whole_tree()
    {
        var paths = FullTree.ToHashSet(StringComparer.Ordinal);

        PageHierarchy.ParentOf("10-domains/loans/pricing.md", paths).ShouldBe("10-domains/loans/README.md");
    }
}
