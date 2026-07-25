using DocuMe.Core.Confluence;
using DocuMe.Core.Publishing;
using Shouldly;

namespace DocuMe.Core.Tests.Publishing;

/// <summary>
/// The child-order diff (PLAN.md §6.2's post-pass). Pure, so every case here is a list and an
/// expectation rather than an HTTP server.
/// </summary>
public sealed class ChildOrderPlannerTests
{
    /// <summary>
    /// The page ids the source tree wants in this order — spelled as ids because that is what a move
    /// names, with the paths they stand for in the tests that need them.
    /// </summary>
    private static readonly string[] Desired = ["a", "b", "c"];

    [Fact]
    public void An_order_that_already_matches_moves_nothing()
    {
        var moves = ChildOrderPlanner.Plan(Desired, ["a", "b", "c"]);

        moves.ShouldBeEmpty();
    }

    /// <summary>
    /// The case the post-pass exists for: a page added to the tree lands at the end of its parent's
    /// children because a create appends, and the tree wants it in the middle. One move, not three.
    /// </summary>
    [Fact]
    public void A_page_appended_at_the_end_moves_once_to_where_the_tree_wants_it()
    {
        var moves = ChildOrderPlanner.Plan(Desired, ["a", "c", "b"]);

        var move = moves.ShouldHaveSingleItem();
        move.PageId.ShouldBe("b");
        move.Position.ShouldBe(ConfluencePageMovePosition.After);
        move.TargetId.ShouldBe("a");
    }

    /// <summary>
    /// A page the tree wants first has no predecessor to sit after, so it anchors on the first page the
    /// plan is leaving alone — the one case that uses <c>before</c>.
    /// </summary>
    [Fact]
    public void The_first_page_anchors_before_the_first_page_left_alone()
    {
        var moves = ChildOrderPlanner.Plan(Desired, ["b", "c", "a"]);

        var move = moves.ShouldHaveSingleItem();
        move.PageId.ShouldBe("a");
        move.Position.ShouldBe(ConfluencePageMovePosition.Before);
        move.TargetId.ShouldBe("b");
    }

    /// <summary>
    /// <c>append</c> reparents (<see cref="ConfluencePageMovePosition.Append"/>), and the post-pass never
    /// changes a page's parent — that is the plan-time reparent's job
    /// (<see cref="PageHierarchy.ParentMoved"/>). A move issued here that appended would silently refile
    /// the page under its own sibling.
    /// </summary>
    [Fact]
    public void Never_appends_because_appending_would_reparent_the_page()
    {
        var moves = ChildOrderPlanner.Plan(Desired, ["c", "b", "a"]);

        moves.ShouldNotBeEmpty();
        moves.ShouldAllBe(move => move.Position != ConfluencePageMovePosition.Append);
    }

    /// <summary>
    /// Minimality is the requirement §6.2 words as "minimal move operations", and it is not cosmetic:
    /// every move is a write against a page somebody may be watching, so a wholly reversed list of three
    /// costs two moves (the one page already in place stays) rather than three.
    /// </summary>
    [Fact]
    public void Leaves_the_longest_run_that_is_already_in_order_alone()
    {
        var moves = ChildOrderPlanner.Plan(Desired, ["c", "b", "a"]);

        moves.Count.ShouldBe(2);
        moves.ShouldNotContain(move => string.Equals(move.PageId, "c", StringComparison.Ordinal));
    }

    /// <summary>
    /// A page somebody added by hand under the same parent is not the tool's to place: rule §9.1 makes the
    /// repo the source of truth for the pages it owns, not for a page it has never heard of. It is never
    /// moved, and never used as an anchor either — anchoring on it would tie DocuMe's order to a page a
    /// human can move at any time.
    /// </summary>
    [Fact]
    public void Leaves_a_page_the_tree_does_not_own_where_it_is()
    {
        var moves = ChildOrderPlanner.Plan(Desired, ["foreign", "c", "a", "b"]);

        moves.ShouldNotContain(move => string.Equals(move.PageId, "foreign", StringComparison.Ordinal));
        moves.ShouldNotContain(move => string.Equals(move.TargetId, "foreign", StringComparison.Ordinal));
    }

    /// <summary>
    /// A page the tree owns that Confluence does not list under this parent is filed somewhere else
    /// entirely, which is the reparent's problem. Positioning it relative to siblings it is not among
    /// would be a request against an unrelated part of the tree.
    /// </summary>
    [Fact]
    public void Ignores_a_page_confluence_does_not_list_under_this_parent()
    {
        var moves = ChildOrderPlanner.Plan(Desired, ["c", "a"]);

        // 'b' is somewhere else in the tree, so the two pages that ARE here get ordered around it.
        var move = moves.ShouldHaveSingleItem();
        move.PageId.ShouldBe("a");
        move.Position.ShouldBe(ConfluencePageMovePosition.Before);
        move.TargetId.ShouldBe("c");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void A_parent_with_fewer_than_two_owned_children_has_no_order_to_fix(int owned)
    {
        var observed = owned == 0 ? new[] { "foreign" } : ["a", "foreign"];

        ChildOrderPlanner.Plan(Desired, observed).ShouldBeEmpty();
    }

    /// <summary>
    /// The property that makes the whole pass trustworthy, checked over every one of the 120 orders
    /// Confluence could be holding five pages in: applying the planned moves in the planned order,
    /// with Confluence's own before/after semantics, always ends at the order the tree asked for.
    /// </summary>
    /// <remarks>
    /// Exhaustive rather than a handful of examples because the moves are sequential and each one anchors
    /// on the result of the last: the failure mode is a permutation where that chain breaks, and picking
    /// examples by hand is exactly how such a permutation goes unnoticed.
    /// </remarks>
    [Fact]
    public void Every_order_five_pages_could_be_in_ends_up_in_tree_order()
    {
        string[] desired = ["p1", "p2", "p3", "p4", "p5"];

        foreach (var observed in Permutations(desired))
        {
            var moves = ChildOrderPlanner.Plan(desired, observed);

            var settled = string.Join(",", Apply(observed, moves));

            settled.ShouldBe(string.Join(",", desired), $"observed: {string.Join(",", observed)}");
        }
    }

    /// <summary>
    /// The same property with a page nobody owns sitting among the siblings: DocuMe's own pages still end
    /// in tree order relative to each other, and the foreign page is still there afterwards.
    /// </summary>
    [Fact]
    public void Reaches_tree_order_around_a_page_it_does_not_own()
    {
        string[] desired = ["p1", "p2", "p3", "p4"];

        foreach (var owned in Permutations(desired))
        {
            for (var slot = 0; slot <= owned.Count; slot++)
            {
                var observed = owned.ToList();
                observed.Insert(slot, "foreign");

                var settled = Apply(observed, ChildOrderPlanner.Plan(desired, observed));

                settled.ShouldContain("foreign");
                string.Join(",", settled.Where(desired.Contains))
                    .ShouldBe(string.Join(",", desired), $"observed: {string.Join(",", observed)}");
            }
        }
    }

    /// <summary>
    /// Confluence's own move semantics, as the endpoint documents them: the page is lifted out and put
    /// back immediately before or after the target.
    /// </summary>
    private static List<string> Apply(IReadOnlyList<string> observed, IReadOnlyList<ChildOrderMove> moves)
    {
        var order = observed.ToList();

        foreach (var move in moves)
        {
            order.Remove(move.PageId).ShouldBeTrue($"{move.PageId} is not among the children");

            var target = order.IndexOf(move.TargetId);
            target.ShouldBeGreaterThanOrEqualTo(0, $"{move.TargetId} is not among the children");

            order.Insert(
                move.Position == ConfluencePageMovePosition.After ? target + 1 : target,
                move.PageId);
        }

        return order;
    }

    private static List<List<string>> Permutations(IReadOnlyList<string> items)
    {
        if (items.Count <= 1)
        {
            return [[.. items]];
        }

        var permutations = new List<List<string>>();
        for (var index = 0; index < items.Count; index++)
        {
            var rest = items.Where((_, position) => position != index).ToList();
            foreach (var permutation in Permutations(rest))
            {
                permutation.Insert(0, items[index]);
                permutations.Add(permutation);
            }
        }

        return permutations;
    }
}
