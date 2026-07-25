using DocuMe.Core.Publishing;
using DocuMe.Core.State;

namespace DocuMe.Core.Sync;

/// <summary>
/// Reconciles observed Confluence labels into <c>_meta/state.json</c> (PLAN.md §6.3's Labels bullet,
/// §8's read side): a pure decision, then a pure application of it.
/// </summary>
/// <remarks>
/// <para>
/// Pure for the same reason <see cref="PublishPlanner"/> is: a <c>--dry-run</c> and a real run share
/// one decision instead of two code paths that agree until they do not, and the tests need neither a
/// network nor a clock. Everything that comes from outside — which pages carry a label, what version
/// they are at, what time it is — arrives in a <see cref="LabelObservation"/>.
/// </para>
/// <para>
/// <strong>Nothing here writes to Confluence.</strong> <c>sync --labels</c> is a read plus a state-file
/// write; the only label writes in the whole design are publish's invalidation (§6.2 step 7) and
/// <c>drift --mark</c> (§6.4). Nor does it read page bodies — rule §9.1 makes the repo the source of
/// truth, so a label and a version number are the whole of what a sync learns from Confluence.
/// Committing the result is the caller's job (§6.3).
/// </para>
/// </remarks>
public static class LabelSyncPlanner
{
    /// <summary>
    /// What <c>approvedBy</c> records, because Confluence will not say who added a label.
    /// </summary>
    /// <remarks>
    /// PLAN.md §13's S3 spike asked whether the label author is obtainable on Cloud. It is not: CQL
    /// search results carry no label author, and v1 <c>content/{id}/label</c> answers
    /// <c>{prefix, name, id, label}</c> with no creator either, so §6.3's documented fallback is the
    /// answer rather than a shortcut. The one thing it must never be filled from is the account DocuMe
    /// authenticates as — the reviewer and the bot are different people, and a fabricated approver in a
    /// financial org's audit trail (§8) is worse than an honest "unknown".
    /// </remarks>
    public const string UnknownApprover = "unknown";

    /// <summary>
    /// Decides what <paramref name="observation"/> means for <paramref name="state"/>, changing nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Keyed on page id.</strong> <see cref="PageHierarchy.PathsByPageId"/> inverts state's
    /// path → id map; a labelled id it does not answer for is an
    /// <see cref="UnmanagedLabelledPage"/> — reported and skipped, never matched by title.
    /// </para>
    /// <para>
    /// <strong>Deliberately quiet where nothing changed.</strong> A page already recorded as approved at
    /// the version now observed plans nothing, and an already-correct <c>stale</c> flag plans nothing,
    /// so a cron sync over an unchanged space produces an unchanged file and no PR (§6.3). The one
    /// exception is the case §8 exists for: a label that stayed while the page moved to a new version
    /// re-records at that version, keeping the displaced approval in history
    /// (<see cref="StateUpdates.RecordApproval"/>). An observation with no version restamps nothing,
    /// because "we could not tell" is not evidence that anything moved.
    /// </para>
    /// <para>
    /// A page state has never published (no <c>pageId</c>) is skipped: it has no Confluence page, so no
    /// label on it can exist to observe.
    /// </para>
    /// </remarks>
    public static LabelSyncPlan Plan(DocumeState state, LabelObservation observation)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(observation);

        if (string.IsNullOrEmpty(observation.ObservedAt))
        {
            throw new ArgumentException(
                "An observation needs the time it was made — it is what lands in approvedAt (§8).",
                nameof(observation));
        }

        var approvedIds = new HashSet<string>(observation.ApprovedPageIds, StringComparer.Ordinal);
        var staleIds = new HashSet<string>(observation.StalePageIds, StringComparer.Ordinal);
        var pathsByPageId = PageHierarchy.PathsByPageId(state);

        var approvals = new List<PlannedApproval>();
        var revocations = new List<PlannedRevocation>();
        var staleChanges = new List<PlannedStaleChange>();

        // Ordered by path so the plan, the report and the resulting file diff read the same way twice.
        foreach (var (path, page) in state.Pages.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (page.PageId is not { Length: > 0 } pageId)
            {
                continue;
            }

            var observedVersion = observation.VersionsByPageId.TryGetValue(pageId, out var version)
                ? version
                : (int?)null;

            var labelled = approvedIds.Contains(pageId);

            var approval = PlanApproval(
                path,
                pageId,
                page,
                labelled,
                observedVersion,
                observation.ObservedAt);

            if (approval is not null)
            {
                approvals.Add(approval);
            }

            var revocation = PlanRevocation(path, pageId, page, labelled);
            if (revocation is not null)
            {
                revocations.Add(revocation);
            }

            var stale = staleIds.Contains(pageId);
            if (stale != page.Stale)
            {
                staleChanges.Add(new PlannedStaleChange(path, pageId, stale));
            }
        }

        return new LabelSyncPlan(
            approvals,
            revocations,
            staleChanges,
            Unmanaged(approvedIds, staleIds, pathsByPageId));
    }

    /// <summary>
    /// Applies <paramref name="plan"/> through <see cref="StateUpdates"/>, returning the new state.
    /// </summary>
    /// <remarks>
    /// Every write goes through the same transitions the publish pipeline uses, revocation included:
    /// <see cref="StateUpdates.InvalidateApproval"/> is the one transition that retires an approval, so
    /// a human taking a label off and a republish changing a hash leave the same shape of audit trail
    /// behind (§8). Persisting the result is the caller's job — §6.3 hands committing to the workflow
    /// that opens the <c>docs/sync</c> PR.
    /// </remarks>
    public static DocumeState Apply(DocumeState state, LabelSyncPlan plan)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(plan);

        var updated = state;

        foreach (var approval in plan.Approvals)
        {
            updated = StateUpdates.RecordApproval(
                updated,
                approval.Path,
                approval.ApprovedBy,
                approval.ApprovedAt,
                approval.Version);
        }

        foreach (var revocation in plan.Revocations)
        {
            updated = StateUpdates.InvalidateApproval(updated, revocation.Path);
        }

        foreach (var change in plan.StaleChanges)
        {
            updated = StateUpdates.SetStale(updated, change.Path, change.Stale);
        }

        return updated;
    }

    /// <summary>
    /// The approval this page needs recorded, or <c>null</c> when its state already says what the label
    /// says.
    /// </summary>
    private static PlannedApproval? PlanApproval(
        string path,
        string pageId,
        PageState page,
        bool labelled,
        int? observedVersion,
        string observedAt)
    {
        if (!labelled)
        {
            return null;
        }

        var approval = page.Approval;
        if (!string.Equals(approval?.Status, ApprovalStatus.Approved, StringComparison.Ordinal))
        {
            return new PlannedApproval(path, pageId, UnknownApprover, observedAt, observedVersion, null);
        }

        // Already approved. Re-record only when the page has demonstrably moved under a label that
        // stayed (§8 records approval at the version current at observation time); restamping on every
        // run would rewrite approvedAt hourly and open an empty PR each time.
        if (observedVersion is not { } current || current == approval!.ApprovedVersion)
        {
            return null;
        }

        return new PlannedApproval(
            path,
            pageId,
            UnknownApprover,
            observedAt,
            current,
            approval!.ApprovedVersion);
    }

    /// <summary>
    /// The approval this page needs cleared, or <c>null</c> — §6.3's "label absent but state says
    /// approved → clear (someone revoked)".
    /// </summary>
    private static PlannedRevocation? PlanRevocation(string path, string pageId, PageState page, bool labelled)
    {
        if (labelled)
        {
            return null;
        }

        var approval = page.Approval;
        if (!string.Equals(approval?.Status, ApprovalStatus.Approved, StringComparison.Ordinal))
        {
            return null;
        }

        return new PlannedRevocation(path, pageId, approval!.ApprovedVersion);
    }

    /// <summary>
    /// Labelled pages no state entry claims, ordered by id so the report is stable.
    /// </summary>
    private static List<UnmanagedLabelledPage> Unmanaged(
        HashSet<string> approvedIds,
        HashSet<string> staleIds,
        IReadOnlyDictionary<string, string> pathsByPageId)
    {
        var unmanaged = new List<UnmanagedLabelledPage>();
        var labelled = approvedIds
            .Union(staleIds, StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal);

        foreach (var pageId in labelled)
        {
            if (pathsByPageId.ContainsKey(pageId))
            {
                continue;
            }

            unmanaged.Add(new UnmanagedLabelledPage(
                pageId,
                approvedIds.Contains(pageId),
                staleIds.Contains(pageId)));
        }

        return unmanaged;
    }
}
