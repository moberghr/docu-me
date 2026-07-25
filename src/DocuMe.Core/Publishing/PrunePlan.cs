using DocuMe.Core.State;

namespace DocuMe.Core.Publishing;

/// <summary>One orphan a confirmed <c>--prune</c> deletes (PLAN.md §6.2 "Orphans", rule §9.6).</summary>
/// <param name="Path">
/// The wiki-root-relative markdown path the state entry is keyed by. There is no file at it any more —
/// that is what makes the page an orphan — so it names the page for a human rather than locating it.
/// </param>
/// <param name="PageId">
/// The Confluence page to trash, or <c>null</c> when state carries an entry that was never published.
/// A null id means there is nothing to delete and only a stale state entry to drop, which is a
/// bookkeeping fix rather than a destructive act.
/// </param>
public sealed record PlannedPrune(string Path, string? PageId);

/// <summary>
/// An orphan <c>--prune</c> will not delete, and why. Reported as a warning: refusing is the safe
/// outcome, so it must not fail the run, and it must not be silent either (rule §9.6).
/// </summary>
/// <param name="Path">The orphan's markdown path.</param>
/// <param name="Reason">What deleting it would have done to pages this run is keeping.</param>
public sealed record RefusedPrune(string Path, string Reason);

/// <summary>
/// What a <c>--prune</c> would do, computed offline from state: the deletes in the order they must run,
/// the orphans refused, and the orphans a scope left out.
/// </summary>
/// <param name="Pages">
/// The deletes, deepest-first — every page filed under a page appears before it. Executing them in this
/// order is not a preference: see <see cref="PrunePlanner"/>.
/// </param>
/// <param name="Refused">Orphans deleting would have moved live pages. Reported, never deleted.</param>
/// <param name="OutOfScope">
/// Orphans outside <c>--changed-since</c>'s change set, ordered. Still reported by the plan (an orphan
/// is whole-tree, see <see cref="PublishScope"/>), just not deleted by this run.
/// </param>
public sealed record PrunePlan(
    IReadOnlyList<PlannedPrune> Pages,
    IReadOnlyList<RefusedPrune> Refused,
    IReadOnlyList<string> OutOfScope)
{
    /// <summary>Nothing to delete: no orphans, or every one of them refused or out of scope.</summary>
    public bool IsEmpty => Pages.Count == 0;
}

/// <summary>
/// Decides which orphans a <c>--prune</c> may delete and in what order (PLAN.md §6.2 "Orphans",
/// rule §9.6).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Deepest-first, because Confluence keeps the children.</strong> Cloud has historically
/// re-parented the children of a deleted page up one level rather than trashing them with it. Deleting a
/// parent before its children would therefore move pages state still files under it, and state would go
/// on claiming a <c>parentPageId</c> that no longer exists. Ordering by the parent relation state
/// records — not by path depth, which cannot tell <c>a/README.md</c> from its sibling <c>a/x.md</c> —
/// means a page is always trashed before whatever it hangs under.
/// </para>
/// <para>
/// <strong>An orphan with a live child is refused, not deleted.</strong> Delete a directory's index page
/// while its siblings remain and Confluence moves those live pages somewhere the tree does not say. That
/// is the reparenting case §6.2 already warns about on the publish side
/// (<see cref="PublishExecutor"/>): the fix is to republish the children under their new parent, which
/// is a page write, and a prune has no business making one. So it names the page and leaves it alone.
/// Refusal is transitive — an orphan whose only child is a refused orphan is refused too, because that
/// child is still there to be moved.
/// </para>
/// <para>
/// <strong>The parent relation comes from state, not from the tree.</strong> The question is what
/// Confluence holds right now, and <c>state.json</c>'s <c>parentPageId</c> is the record of that;
/// <see cref="PageHierarchy"/> answers a different question (where a page belongs), and an orphan is
/// absent from the tree it walks. Plan against the state a publish run just wrote, so a page the run
/// moved counts as moved.
/// </para>
/// <para>
/// Pure and offline, like every other planner here, which is what lets the whole of <c>--prune</c> be
/// verified without a Confluence account — including the refusals, which are the part that matters.
/// </para>
/// </remarks>
public static class PrunePlanner
{
    /// <summary>
    /// Plans the deletes for <paramref name="orphanPaths"/>.
    /// </summary>
    /// <param name="state">
    /// State as the run leaves it (<see cref="PublishOutcome.State"/> after a publish, or the loaded
    /// state under <c>--dry-run</c>). Never mutated.
    /// </param>
    /// <param name="orphanPaths">
    /// The orphans to consider, from <see cref="PublishReport.OrphanPages"/>. Every one must be a key in
    /// <paramref name="state"/>: an orphan is a state entry by definition, so a path state does not know
    /// is a caller mistake rather than a page to delete.
    /// </param>
    /// <param name="scope">
    /// The run's scope, or <c>null</c> for a whole-tree run. A deletion does appear in
    /// <c>git diff --name-only</c>, so <c>--changed-since</c> narrows a prune to the orphans whose path
    /// is in the change set; everything else is reported as
    /// <see cref="PrunePlan.OutOfScope"/>. <c>--page</c> never gets this far
    /// (<see cref="PruneGuard"/> refuses the combination).
    /// </param>
    /// <exception cref="ArgumentException">An orphan path is not in <paramref name="state"/>.</exception>
    public static PrunePlan Plan(
        DocumeState state,
        IEnumerable<string> orphanPaths,
        PublishScope? scope = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(orphanPaths);

        var outOfScope = new List<string>();
        var candidates = new Dictionary<string, PageState>(StringComparer.Ordinal);

        foreach (var path in orphanPaths.Order(StringComparer.Ordinal))
        {
            if (!state.Pages.TryGetValue(path, out var page))
            {
                throw new ArgumentException(
                    $"'{path}' is not in state, so it cannot be an orphan: an orphan is a state entry "
                    + "whose file is gone.",
                    nameof(orphanPaths));
            }

            // An orphan has no attachments to put it in scope — its file, and so its whole plan, is gone.
            if (scope is not null && !scope.Includes(path, []))
            {
                outOfScope.Add(path);
                continue;
            }

            candidates[path] = page;
        }

        var childrenByParentId = ChildrenByParentId(state);
        var blockers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var order = new List<string>(candidates.Count);
        var visiting = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in candidates.Keys.Order(StringComparer.Ordinal))
        {
            Visit(path, candidates, childrenByParentId, blockers, visiting, order);
        }

        var pages = new List<PlannedPrune>(order.Count);
        var refused = new List<RefusedPrune>();

        foreach (var path in order)
        {
            var blocking = blockers[path];
            if (blocking.Count == 0)
            {
                pages.Add(new PlannedPrune(path, candidates[path].PageId));
                continue;
            }

            refused.Add(new RefusedPrune(path, Refusal(blocking)));
        }

        return new PrunePlan(pages, [.. refused.OrderBy(entry => entry.Path, StringComparer.Ordinal)], outOfScope);
    }

    /// <summary>
    /// Walks the pages filed under <paramref name="path"/> before <paramref name="path"/> itself, which
    /// makes <paramref name="order"/> the delete order and settles every child's verdict before its
    /// parent needs it.
    /// </summary>
    private static void Visit(
        string path,
        Dictionary<string, PageState> candidates,
        Dictionary<string, IReadOnlyList<string>> childrenByParentId,
        Dictionary<string, IReadOnlyList<string>> blockers,
        HashSet<string> visiting,
        List<string> order)
    {
        // The first test is the memo; the second guards a parent cycle a hand-edited state file could
        // spell, which would otherwise recurse until the stack ran out.
        if (blockers.ContainsKey(path) || !visiting.Add(path))
        {
            return;
        }

        var blocking = new List<string>();

        foreach (var child in Children(candidates[path], childrenByParentId))
        {
            if (!candidates.ContainsKey(child))
            {
                // A page whose file is still there, or one a scope kept out of this run: either way it
                // survives, so whatever it hangs under has to survive with it.
                blocking.Add(child);
                continue;
            }

            Visit(child, candidates, childrenByParentId, blockers, visiting, order);

            if (blockers.TryGetValue(child, out var childBlockers) && childBlockers.Count > 0)
            {
                blocking.Add(child);
            }
        }

        blockers[path] = blocking;
        order.Add(path);
    }

    private static IReadOnlyList<string> Children(
        PageState page,
        Dictionary<string, IReadOnlyList<string>> childrenByParentId)
    {
        if (page.PageId is not { Length: > 0 } id)
        {
            return [];
        }

        return childrenByParentId.TryGetValue(id, out var children) ? children : [];
    }

    /// <summary>Confluence page id → the state paths filed under it, ordered.</summary>
    private static Dictionary<string, IReadOnlyList<string>> ChildrenByParentId(DocumeState state)
    {
        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var (path, page) in state.Pages)
        {
            if (page.ParentPageId is not { Length: > 0 } parentId)
            {
                continue;
            }

            if (!children.TryGetValue(parentId, out var siblings))
            {
                siblings = [];
                children[parentId] = siblings;
            }

            siblings.Add(path);
        }

        return children.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)[.. pair.Value.Order(StringComparer.Ordinal)],
            StringComparer.Ordinal);
    }

    private static string Refusal(IReadOnlyList<string> blocking) =>
        $"state still files {blocking.Count} page(s) under it that this run does not delete "
        + $"({string.Join(", ", blocking)}). Confluence re-parents the children of a deleted page rather "
        + "than deleting them, so trashing this one would move live pages somewhere the tree does not "
        + "say. Publish the children under their new parent first (`--force` writes the move), then "
        + "prune again.";
}
