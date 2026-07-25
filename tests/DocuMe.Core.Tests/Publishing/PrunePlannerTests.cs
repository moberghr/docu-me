using DocuMe.Core.Publishing;
using DocuMe.Core.State;
using Shouldly;

namespace DocuMe.Core.Tests.Publishing;

/// <summary>
/// Which orphans <c>--prune</c> may delete, and in what order (PLAN.md §6.2 "Orphans", rule §9.6).
/// Pure and offline, which is the whole point: the refusals are the part that protects live pages, and
/// they are verifiable without a Confluence account.
/// </summary>
public sealed class PrunePlannerTests
{
    /// <summary>
    /// Confluence re-parents the children of a deleted page rather than deleting them, so a page has to
    /// be trashed before whatever it hangs under. Path depth cannot express that — an index page and its
    /// siblings sit at the same depth — so the order comes from the parent ids state records.
    /// </summary>
    [Fact]
    public void Deletes_children_before_the_pages_they_hang_under()
    {
        var state = State(
            ("a/README.md", "10", Parent: "1"),
            ("a/b/README.md", "20", Parent: "10"),
            ("a/b/page.md", "30", Parent: "20"));

        var plan = PrunePlanner.Plan(state, ["a/README.md", "a/b/README.md", "a/b/page.md"]);

        plan.Refused.ShouldBeEmpty();
        plan.Pages.Select(page => page.Path).ShouldBe(["a/b/page.md", "a/b/README.md", "a/README.md"]);
        plan.Pages.Select(page => page.PageId).ShouldBe(["30", "20", "10"]);
    }

    /// <summary>
    /// The case the whole guard exists for: a deleted index page whose siblings are still published.
    /// Trashing it would move live pages somewhere the tree does not say, and moving them is a page
    /// write a prune has no business making.
    /// </summary>
    [Fact]
    public void Refuses_an_orphan_that_still_has_a_live_child()
    {
        var state = State(
            ("a/README.md", "10", Parent: "1"),
            ("a/kept.md", "20", Parent: "10"));

        var plan = PrunePlanner.Plan(state, ["a/README.md"]);

        plan.Pages.ShouldBeEmpty();
        plan.IsEmpty.ShouldBeTrue();

        var refused = plan.Refused.ShouldHaveSingleItem();
        refused.Path.ShouldBe("a/README.md");
        refused.Reason.ShouldContain("a/kept.md");
        refused.Reason.ShouldContain("--force");
    }

    /// <summary>
    /// Refusal has to travel upwards: a refused orphan is a page that is still there, so its own parent
    /// would move it too. Stopping at the direct child would delete the grandparent and undo the refusal
    /// one level down.
    /// </summary>
    [Fact]
    public void Refusal_carries_up_to_the_pages_above_a_refused_orphan()
    {
        var state = State(
            ("a/README.md", "10", Parent: "1"),
            ("a/b/README.md", "20", Parent: "10"),
            ("a/b/kept.md", "30", Parent: "20"));

        var plan = PrunePlanner.Plan(state, ["a/README.md", "a/b/README.md"]);

        plan.Pages.ShouldBeEmpty();
        plan.Refused.Select(entry => entry.Path).ShouldBe(["a/README.md", "a/b/README.md"]);

        // The grandparent's refusal names the orphan that blocked it, not the live page two levels down:
        // the page to look at is the one immediately in the way.
        plan.Refused
            .First(entry => string.Equals(entry.Path, "a/README.md", StringComparison.Ordinal))
            .Reason.ShouldContain("a/b/README.md");
    }

    /// <summary>
    /// A sibling's refusal must not spread: the pages under a refused orphan are what block it, and an
    /// unrelated orphan elsewhere in the tree is still deletable.
    /// </summary>
    [Fact]
    public void One_refusal_does_not_block_an_unrelated_orphan()
    {
        var state = State(
            ("a/README.md", "10", Parent: "1"),
            ("a/kept.md", "20", Parent: "10"),
            ("c/gone.md", "30", Parent: "1"));

        var plan = PrunePlanner.Plan(state, ["a/README.md", "c/gone.md"]);

        plan.Pages.Select(page => page.Path).ShouldBe(["c/gone.md"]);
        plan.Refused.Select(entry => entry.Path).ShouldBe(["a/README.md"]);
    }

    /// <summary>
    /// A page with no children is the ordinary case, and a page whose only child is another orphan in the
    /// same run is deletable because that child goes first.
    /// </summary>
    [Fact]
    public void An_orphan_whose_only_children_are_orphans_is_deletable()
    {
        var state = State(
            ("a/README.md", "10", Parent: "1"),
            ("a/gone.md", "20", Parent: "10"));

        var plan = PrunePlanner.Plan(state, ["a/README.md", "a/gone.md"]);

        plan.Refused.ShouldBeEmpty();
        plan.Pages.Select(page => page.Path).ShouldBe(["a/gone.md", "a/README.md"]);
    }

    /// <summary>
    /// <c>--changed-since</c> composes with <c>--prune</c> because a deletion does appear in
    /// <c>git diff --name-only</c>. An orphan outside the change set is reported, not deleted.
    /// </summary>
    [Fact]
    public void A_changed_since_scope_narrows_the_deletes_to_the_change_set()
    {
        var state = State(
            ("a/gone.md", "10", Parent: "1"),
            ("b/gone.md", "20", Parent: "1"));

        var scope = PublishScope.ForFilesChangedSince("c0ffee", ["a/gone.md"]);
        var plan = PrunePlanner.Plan(state, ["a/gone.md", "b/gone.md"], scope);

        plan.Pages.Select(page => page.Path).ShouldBe(["a/gone.md"]);
        plan.OutOfScope.ShouldBe(["b/gone.md"]);
        plan.Refused.ShouldBeEmpty();
    }

    /// <summary>
    /// An out-of-scope orphan survives the run, so it blocks its parent exactly the way a live page does.
    /// A scope narrows what is deleted; it cannot make a page that is still there safe to orphan.
    /// </summary>
    [Fact]
    public void An_out_of_scope_orphan_blocks_the_page_above_it()
    {
        var state = State(
            ("a/README.md", "10", Parent: "1"),
            ("a/gone.md", "20", Parent: "10"));

        var scope = PublishScope.ForFilesChangedSince("c0ffee", ["a/README.md"]);
        var plan = PrunePlanner.Plan(state, ["a/README.md", "a/gone.md"], scope);

        plan.Pages.ShouldBeEmpty();
        plan.OutOfScope.ShouldBe(["a/gone.md"]);
        plan.Refused.ShouldHaveSingleItem().Reason.ShouldContain("a/gone.md");
    }

    /// <summary>
    /// A state entry that names no page has nothing to delete in Confluence — only a stale entry to drop,
    /// which is bookkeeping rather than a destructive act.
    /// </summary>
    [Fact]
    public void An_entry_with_no_page_id_plans_as_a_state_only_drop()
    {
        var state = State(("a/gone.md", null, Parent: null));

        var plan = PrunePlanner.Plan(state, ["a/gone.md"]);

        plan.Pages.ShouldHaveSingleItem().PageId.ShouldBeNull();
    }

    /// <summary>
    /// An orphan is a state entry whose file is gone, so a path state has never heard of is a caller
    /// mistake. Failing loudly beats planning a delete for a page nothing knows anything about.
    /// </summary>
    [Fact]
    public void A_path_state_does_not_know_is_rejected()
    {
        var exception = Should.Throw<ArgumentException>(
            () => PrunePlanner.Plan(new DocumeState(), ["never/seen.md"]));

        exception.Message.ShouldContain("never/seen.md");
    }

    [Fact]
    public void No_orphans_plans_nothing()
    {
        var plan = PrunePlanner.Plan(State(("a/kept.md", "10", Parent: "1")), []);

        plan.IsEmpty.ShouldBeTrue();
        plan.Pages.ShouldBeEmpty();
        plan.Refused.ShouldBeEmpty();
        plan.OutOfScope.ShouldBeEmpty();
    }

    /// <summary>
    /// A hand-edited <c>state.json</c> can spell a parent cycle, which a naive walk would recurse into
    /// until the stack ran out. It terminates and answers, rather than taking the process down.
    /// </summary>
    [Fact]
    public void A_parent_cycle_in_state_terminates()
    {
        var state = State(
            ("a.md", "10", Parent: "20"),
            ("b.md", "20", Parent: "10"));

        var plan = PrunePlanner.Plan(state, ["a.md", "b.md"]);

        plan.Pages.Select(page => page.Path).Order(StringComparer.Ordinal).ShouldBe(["a.md", "b.md"]);
    }

    private static DocumeState State(params (string Path, string? PageId, string? Parent)[] pages) =>
        new()
        {
            Pages = pages.ToDictionary(
                page => page.Path,
                page => new PageState { PageId = page.PageId, ParentPageId = page.Parent },
                StringComparer.Ordinal),
        };
}
