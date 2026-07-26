using DocuMe.Core.Confluence;

namespace DocuMe.Core.Publishing;

/// <summary>
/// One repositioning the child-order post-pass performs: put a page next to a sibling
/// (PLAN.md §6.2, "using minimal move operations").
/// </summary>
/// <param name="PageId">The page to move.</param>
/// <param name="Position">
/// <see cref="ConfluencePageMovePosition.Before"/> or <see cref="ConfluencePageMovePosition.After"/>.
/// Never <see cref="ConfluencePageMovePosition.Append"/>: appending is the reparent, and the post-pass
/// never changes a page's parent.
/// </param>
/// <param name="TargetId">The sibling to sit beside.</param>
public sealed record ChildOrderMove(
    string PageId,
    ConfluencePageMovePosition Position,
    string TargetId);

/// <summary>
/// What the post-pass did to one parent's children (PLAN.md §6.2).
/// </summary>
/// <param name="ParentPath">
/// The parent page's markdown path, or <c>null</c> for the top of the wiki
/// (<c>confluence.rootPageId</c>).
/// </param>
/// <param name="ParentPageId">The parent's Confluence page id.</param>
/// <param name="MovedPaths">
/// The children that were repositioned, in the order the moves were issued. The pages that were
/// already in the right place are deliberately not listed: the interesting number is what the run
/// changed.
/// </param>
public sealed record ChildReorder(
    string? ParentPath,
    string ParentPageId,
    IReadOnlyList<string> MovedPaths);

/// <summary>
/// Turns "Confluence lists these children in this order, the source tree wants that order" into the
/// fewest sibling moves that get there (PLAN.md §6.2's child-ordering post-pass).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Pure, and separate from the requests,</strong> for the same reason
/// <see cref="State.PublishPlanner"/> is: the interesting part of reordering is the diff, and a diff that
/// needs an HTTP server to exercise is a diff nobody covers properly.
/// </para>
/// <para>
/// <strong>Children the tree does not own are never moved and never anchored on.</strong> A page a
/// human added by hand under the same parent is not in the desired order, so it is filtered out of the
/// problem entirely: DocuMe reorders its own pages relative to each other and leaves the foreign page
/// wherever it lands. Trying to interleave it would mean deciding where somebody else's page belongs.
/// </para>
/// <para>
/// <strong>Minimal means "keep the longest run that is already in order".</strong> The pages that form
/// the longest subsequence already in the desired relative order stay put; every other page is moved
/// next to its desired predecessor. That is the standard lower bound for this problem, and it matters
/// at DocuMe's scale for a reason beyond elegance: every move is a write against a page a reviewer may
/// be watching, so a run that reorders 30 siblings to fix one insertion is 29 notifications nobody
/// asked for.
/// </para>
/// </remarks>
public static class ChildOrderPlanner
{
    /// <summary>
    /// Plans the moves that turn <paramref name="observed"/> into <paramref name="desired"/>.
    /// </summary>
    /// <param name="desired">
    /// The page ids the source tree owns under this parent, in the order it wants them
    /// (<see cref="PublishReport.Pages"/> order, which is tree order).
    /// </param>
    /// <param name="observed">
    /// The child page ids as Confluence lists them
    /// (<see cref="ConfluenceClient.GetChildPagesAsync"/>). May contain pages absent from
    /// <paramref name="desired"/>, and may be missing pages that are in it.
    /// </param>
    /// <returns>
    /// The moves to issue, in order. Empty when the observed order already satisfies the desired one —
    /// which is the answer on every run of a settled wiki.
    /// </returns>
    /// <remarks>
    /// The moves must be issued <em>in the returned order</em>: each one anchors on a sibling the
    /// earlier moves have already placed, so applying them out of order can leave the tree in neither
    /// the old order nor the new one.
    /// </remarks>
    public static IReadOnlyList<ChildOrderMove> Plan(
        IReadOnlyList<string> desired,
        IReadOnlyList<string> observed)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(observed);

        var present = observed.ToHashSet(StringComparer.Ordinal);

        // A page the tree owns that Confluence does not list under this parent cannot be positioned
        // relative to its siblings: it is somewhere else entirely, which is the reparent's business
        // (PageHierarchy.ParentMoved) and not this pass's.
        var wanted = desired.Where(present.Contains).ToList();

        // Nothing to order: one page is in order by definition, and zero means this parent's children
        // are all somebody else's.
        if (wanted.Count < 2)
        {
            return [];
        }

        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < wanted.Count; index++)
        {
            rank[wanted[index]] = index;
        }

        var ranks = observed
            .Where(rank.ContainsKey)
            .Select(id => rank[id])
            .ToList();

        var settled = LongestOrderedRun(ranks);

        // Always at least one page: a longest ordered run over two or more pages is never empty. It is
        // the only fixed point in the observed order the moves can be aimed at.
        var anchor = settled.Min();
        var moves = new List<ChildOrderMove>();

        for (var index = 0; index < wanted.Count; index++)
        {
            if (settled.Contains(index))
            {
                continue;
            }

            // Anchoring on the DESIRED predecessor rather than on anything observed is what makes the
            // sequence self-correcting: by the time this move is issued, everything before it is
            // already in place, whether it was left alone or moved a step earlier.
            //
            // The first page has no predecessor, and anchoring it on the SECOND page is the trap — the
            // second page may not be in place yet either, so the pair would end up correctly ordered
            // relative to each other and still sitting after the pages being left alone. It anchors on
            // the first of those instead, which is the one page whose position is already known good;
            // every other page in the leading run then chains behind it.
            var move = index > 0
                ? new ChildOrderMove(wanted[index], ConfluencePageMovePosition.After, wanted[index - 1])
                : new ChildOrderMove(wanted[index], ConfluencePageMovePosition.Before, wanted[anchor]);

            moves.Add(move);
        }

        return moves;
    }

    /// <summary>
    /// The desired positions that are already in ascending order in the observed list — the pages worth
    /// leaving alone, i.e. a longest increasing subsequence.
    /// </summary>
    /// <remarks>
    /// Quadratic on purpose. The input is one parent's child list, which is tens of pages in the
    /// wikis this tool exists for, and the O(n log n) form of this trades that irrelevant win for
    /// binary-search bookkeeping that is much harder to read as correct.
    /// </remarks>
    private static HashSet<int> LongestOrderedRun(List<int> ranks)
    {
        var length = new int[ranks.Count];
        var previous = new int[ranks.Count];
        var longest = -1;

        for (var index = 0; index < ranks.Count; index++)
        {
            length[index] = 1;
            previous[index] = -1;

            for (var earlier = 0; earlier < index; earlier++)
            {
                if (ranks[earlier] < ranks[index] && length[earlier] + 1 > length[index])
                {
                    length[index] = length[earlier] + 1;
                    previous[index] = earlier;
                }
            }

            if (longest < 0 || length[index] > length[longest])
            {
                longest = index;
            }
        }

        var settled = new HashSet<int>();
        for (var index = longest; index >= 0; index = previous[index])
        {
            settled.Add(ranks[index]);
        }

        return settled;
    }
}
