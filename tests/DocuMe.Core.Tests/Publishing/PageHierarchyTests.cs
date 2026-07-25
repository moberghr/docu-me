using DocuMe.Core.Publishing;
using DocuMe.Core.State;
using Shouldly;

namespace DocuMe.Core.Tests.Publishing;

/// <summary>
/// Parent resolution (PLAN.md §6.2, "parents before children, depth-first"): the directory index page
/// parents its directory, the walk skips directories that have no index, and the root index has no
/// parent at all.
/// </summary>
public sealed class PageHierarchyTests
{
    /// <summary><c>confluence.rootPageId</c> (§5.1): the page a top-of-tree page is recorded against.</summary>
    private const string RootId = "1";

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

    [Fact]
    public void Reads_a_recorded_parent_id_back_as_the_path_that_owns_it()
    {
        var state = State(
            ("README.md", "10", null), ("guides/setup.md", "20", "10"), ("guides/adopted.md", null, null));

        var paths = PageHierarchy.PathsByPageId(state);

        paths["10"].ShouldBe("README.md");
        paths["20"].ShouldBe("guides/setup.md");

        // An adopted skeleton has no page yet (§6.1), so it owns no id to read back.
        paths.Count.ShouldBe(2);
    }

    [Fact]
    public void A_page_still_under_the_parent_the_tree_names_has_not_moved()
    {
        var state = State(("README.md", "10", null), ("guides/setup.md", "20", "10"));

        PageHierarchy
            .ParentMoved(state.Pages["guides/setup.md"], "README.md", PageHierarchy.PathsByPageId(state), RootId)
            .ShouldBeFalse();
    }

    /// <summary>
    /// The reparent §6.2 has to catch: an index page added above a page whose markdown did not change,
    /// so the only evidence is that its recorded parent is a different <em>path</em> than the tree says.
    /// </summary>
    [Fact]
    public void A_page_the_tree_hangs_somewhere_else_has_moved()
    {
        var state = State(
            ("README.md", "10", null), ("guides/README.md", "30", "10"), ("guides/setup.md", "20", "10"));

        PageHierarchy
            .ParentMoved(
                state.Pages["guides/setup.md"], "guides/README.md", PageHierarchy.PathsByPageId(state), RootId)
            .ShouldBeTrue();
    }

    /// <summary>
    /// The case that has no id to compare against at all: the new parent is created by the same run, so
    /// nothing in state names it yet. Deciding in paths is what makes this visible to <c>--dry-run</c>.
    /// </summary>
    [Fact]
    public void A_parent_this_run_has_not_created_yet_still_counts_as_moved()
    {
        var state = State(("README.md", "10", null), ("guides/setup.md", "20", "10"));

        PageHierarchy
            .ParentMoved(
                state.Pages["guides/setup.md"], "guides/README.md", PageHierarchy.PathsByPageId(state), RootId)
            .ShouldBeTrue();
    }

    [Theory]

    // Recorded against confluence.rootPageId and the tree still puts it at the root.
    [InlineData(RootId, null, false)]

    // No parent id at all — a space with no rootPageId configured files a top page that way.
    [InlineData(null, null, false)]

    // At the root in Confluence, under a page in the tree, and the other way round.
    [InlineData(RootId, "README.md", true)]
    [InlineData("10", null, true)]

    // An id no page in state owns: filed outside the tree DocuMe knows, so reconcile it (rule §9.1).
    [InlineData("99999", "README.md", true)]
    public void Decides_the_root_and_the_unknown_parent_cases(string? recorded, string? planned, bool moved)
    {
        var state = State(("README.md", "10", null), ("guides/setup.md", "20", recorded));

        PageHierarchy
            .ParentMoved(state.Pages["guides/setup.md"], planned, PageHierarchy.PathsByPageId(state), RootId)
            .ShouldBe(moved);
    }

    [Fact]
    public void A_page_that_has_never_been_published_has_not_moved()
    {
        // `init --adopt` leaves titles and paths with no page id (§6.1): the create files it correctly.
        var adopted = new PageState { Title = "Setup" };

        PageHierarchy.ParentMoved(adopted, "README.md", new Dictionary<string, string>(), RootId).ShouldBeFalse();
        PageHierarchy.ParentMoved(null, "README.md", new Dictionary<string, string>(), RootId).ShouldBeFalse();
    }

    /// <summary>
    /// The defect this exists to stop: a numeric-prefixed page sorts before the index page it hangs under
    /// (<c>'1'</c> before <c>'R'</c>), so publishing in path order would try to file a child under a parent
    /// the run has not created yet — on the very convention §6.2 names for expressing order.
    /// </summary>
    [Fact]
    public void Publish_order_puts_a_parent_before_a_child_that_sorts_ahead_of_it()
    {
        var parents = PageHierarchy.Resolve([
            "README.md",
            "10-domains/README.md",
            "10-domains/orders.md",
            "20-guides/README.md",
        ]);

        PageHierarchy.PublishOrder(parents).ShouldBe([
            "README.md",
            "10-domains/README.md",
            "10-domains/orders.md",
            "20-guides/README.md",
        ]);
    }

    /// <summary>
    /// Depth-first, and siblings still in path order — which is what makes the numeric prefixes mean
    /// something to the child-order post-pass (<see cref="ChildOrderPlanner"/>).
    /// </summary>
    [Fact]
    public void Publish_order_walks_each_branch_to_the_bottom_with_siblings_in_path_order()
    {
        var parents = PageHierarchy.Resolve([
            "README.md",
            "b/README.md",
            "b/c/README.md",
            "b/c/deep.md",
            "b/later.md",
            "a-first.md",
        ]);

        PageHierarchy.PublishOrder(parents).ShouldBe([
            "README.md",
            "a-first.md",
            "b/README.md",
            "b/c/README.md",
            "b/c/deep.md",
            "b/later.md",
        ]);
    }

    /// <summary>
    /// A wiki with no root index page: every page is its own root, and none is lost.
    /// </summary>
    [Fact]
    public void Publish_order_keeps_every_page_when_there_is_no_index_above_them()
    {
        var parents = PageHierarchy.Resolve(["beta.md", "alpha.md", "guides/setup.md"]);

        PageHierarchy.PublishOrder(parents).ShouldBe(["alpha.md", "beta.md", "guides/setup.md"]);
    }

    private static DocumeState State(params (string Path, string? PageId, string? ParentPageId)[] pages)
    {
        var entries = pages.ToDictionary(
            page => page.Path,
            page => new PageState
            {
                Title = page.Path,
                PageId = page.PageId,
                ParentPageId = page.ParentPageId,
            },
            StringComparer.Ordinal);

        return new DocumeState { Pages = entries };
    }
}
