namespace DocuMe.Core.State;

/// <summary>What a publish run does with one page, decided at PLAN.md §6.2 step 5.</summary>
public enum PagePublishAction
{
    /// <summary>No <c>pageId</c> in state: create the page and upload all its attachments.</summary>
    Create,

    /// <summary>The body hash moved (or <c>--force</c>): update the page, upload changed attachments.</summary>
    Update,

    /// <summary>
    /// The body is byte-identical but attachment content moved. Upload those attachments and leave
    /// the page version alone — see <see cref="PublishPlanner"/> for why this is its own action.
    /// </summary>
    UpdateAttachments,

    /// <summary>
    /// The page itself is unchanged but the tree now hangs it under a different parent: reposition it
    /// with a bodyless move. Spends no page version and never touches approval — see
    /// <see cref="PublishPlanner"/> for why a reparent cannot ride along on an update here.
    /// </summary>
    Move,

    /// <summary>Nothing changed: skip the page and log it (§6.2 step 5).</summary>
    Skip,
}

/// <summary>
/// The decision for one page: what to write, which attachments to upload, and whether the write
/// invalidates approval (§6.2 steps 5 and 7). Pure data — computed by <see cref="PublishPlanner"/>
/// before any network call, so <c>--dry-run</c> reports exactly what a real run would do.
/// </summary>
/// <param name="Path">Wiki-root-relative markdown path, the key used by <see cref="DocumeState.Pages"/>.</param>
/// <param name="Action">What the publish does with the page.</param>
/// <param name="ContentHash">
/// The freshly computed body hash (<see cref="State.ContentHash.OfBody"/>). Recorded to state after a
/// successful write, and carried here even for <see cref="PagePublishAction.Skip"/> so a caller can
/// assert it matches what state already holds.
/// </param>
/// <param name="ChangedAttachments">
/// Attachment filenames whose bytes differ from state, sorted ordinally. The upload set for §6.2 step 5.
/// </param>
/// <param name="OrphanAttachments">
/// Attachment filenames state still lists that this page no longer produces, sorted ordinally.
/// Reported, never deleted: §6.2 gives attachment removal no verb, and a stale attachment is
/// invisible on the page once nothing references it.
/// </param>
/// <param name="InvalidatesApproval">
/// True when this write must strip the <c>approved</c> label and move state to
/// <see cref="ApprovalStatus.NeedsReview"/> (§6.2 step 7). Keyed strictly off a body-hash change on an
/// approved page, so banner-only, machine and position-only edits never invalidate (§8, rule §9.2).
/// </param>
public sealed record PagePublishPlan(
    string Path,
    PagePublishAction Action,
    string ContentHash,
    IReadOnlyList<string> ChangedAttachments,
    IReadOnlyList<string> OrphanAttachments,
    bool InvalidatesApproval)
{
    /// <summary>True when the run writes a page body — the actions that consume a page version.</summary>
    public bool WritesBody => Action is PagePublishAction.Create or PagePublishAction.Update;
}

/// <summary>
/// Decides <see cref="PagePublishPlan"/>s by comparing freshly computed hashes against
/// <c>_meta/state.json</c> (PLAN.md §6.2 step 5). Pure computation: no IO, no network, no clock,
/// which is what lets <c>--dry-run</c> and the real run share one decision.
/// </summary>
public static class PublishPlanner
{
    /// <summary>Plans one page.</summary>
    /// <param name="path">Wiki-root-relative markdown path.</param>
    /// <param name="current">
    /// The page's entry in state, or <c>null</c> when state has never seen it. An entry with no
    /// <c>pageId</c> also means "create": an adopted skeleton (§6.1 <c>--adopt</c>) carries titles
    /// and paths before any page exists.
    /// </param>
    /// <param name="contentHash">The page's freshly computed body hash.</param>
    /// <param name="attachmentHashes">
    /// Attachment filename → content hash for every attachment this page references now. The
    /// filenames are the flattened upload names, the same keys state stores.
    /// </param>
    /// <param name="force">
    /// <c>--force</c>: republish even when nothing moved. It distrusts state rather than the source,
    /// so every attachment is re-uploaded too — a force exists precisely for the case where the
    /// remote no longer matches what state claims. It never invalidates approval on its own: an
    /// unchanged hash means unchanged content, and §8 invalidates on content change only.
    /// </param>
    /// <param name="parentMoved">
    /// True when Confluence files the page somewhere the source tree no longer says
    /// (<see cref="Publishing.PageHierarchy.ParentMoved"/>). Decided by the caller and in <em>paths</em>,
    /// for the same reason <paramref name="contentHash"/> is computed by the caller: the planner stays
    /// pure, so a reparent is visible to <c>--dry-run</c> instead of being discovered in the write path.
    /// </param>
    public static PagePublishPlan PlanPage(
        string path,
        PageState? current,
        string contentHash,
        IReadOnlyDictionary<string, string> attachmentHashes,
        bool force = false,
        bool parentMoved = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(contentHash);
        ArgumentNullException.ThrowIfNull(attachmentHashes);

        var allNames = Sorted(attachmentHashes.Keys);

        if (current?.PageId is null)
        {
            return new PagePublishPlan(
                path, PagePublishAction.Create, contentHash, allNames, [], InvalidatesApproval: false);
        }

        var orphans = Sorted(current.Attachments.Keys.Where(name => !attachmentHashes.ContainsKey(name)));
        var bodyChanged = !string.Equals(current.ContentHash, contentHash, StringComparison.Ordinal);

        if (bodyChanged || force)
        {
            // A body write carries the new parent with it (ConfluencePageRevision.ParentId), so a page
            // that moved AND changed needs no separate move.
            var changed = force ? allNames : Changed(current, attachmentHashes);
            var invalidates = bodyChanged && IsApproved(current);

            return new PagePublishPlan(
                path, PagePublishAction.Update, contentHash, changed, orphans, invalidates);
        }

        var changedOnly = Changed(current, attachmentHashes);

        if (parentMoved)
        {
            // Decided before the attachment check because the two are independent: a reparented page
            // whose image also changed uploads that image AND moves, and neither is a body write, so
            // neither spends a version. Approval stands: contentHash is body-only, this page's body
            // did not move, and where a page hangs is not what a reviewer approved (§8, rule §9.2).
            return new PagePublishPlan(
                path, PagePublishAction.Move, contentHash, changedOnly, orphans, InvalidatesApproval: false);
        }

        if (changedOnly.Length > 0)
        {
            // §6.2 step 5 reads "unchanged -> skip", but the hash it speaks of covers the body
            // alone. A hand-placed image keeps its filename when its bytes change, so a literal
            // skip would leave the published page showing last month's picture. Mermaid diagrams
            // cannot reach here — MermaidAttachmentName derives the filename from the source, so a
            // changed diagram changes the body too. Approval is untouched on purpose: §8 keys
            // invalidation off contentHash, and this path did not move it.
            return new PagePublishPlan(
                path, PagePublishAction.UpdateAttachments, contentHash, changedOnly, orphans, InvalidatesApproval: false);
        }

        return new PagePublishPlan(
            path, PagePublishAction.Skip, contentHash, [], orphans, InvalidatesApproval: false);
    }

    /// <summary>
    /// State entries whose markdown file is gone (§6.2 "Orphans"), sorted ordinally. Reported by
    /// every run; deleted from Confluence only by <c>--prune</c> after interactive confirmation
    /// (rule §9.6), which is not this type's business.
    /// </summary>
    /// <param name="state">The loaded state file.</param>
    /// <param name="presentPaths">Wiki-root-relative paths the current tree walk found.</param>
    public static IReadOnlyList<string> OrphanPages(DocumeState state, IEnumerable<string> presentPaths)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(presentPaths);

        var present = presentPaths.ToHashSet(StringComparer.Ordinal);

        return Sorted(state.Pages.Keys.Where(path => !present.Contains(path)));
    }

    private static bool IsApproved(PageState page) =>
        string.Equals(page.Approval?.Status, ApprovalStatus.Approved, StringComparison.Ordinal);

    private static string[] Changed(
        PageState current,
        IReadOnlyDictionary<string, string> attachmentHashes)
    {
        var changed = attachmentHashes
            .Where(pair => !current.Attachments.TryGetValue(pair.Key, out var known)
                || !string.Equals(known, pair.Value, StringComparison.Ordinal))
            .Select(pair => pair.Key);

        return Sorted(changed);
    }

    private static string[] Sorted(IEnumerable<string> names) =>
        names.Order(StringComparer.Ordinal).ToArray();
}
