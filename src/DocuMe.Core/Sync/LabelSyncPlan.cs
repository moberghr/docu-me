namespace DocuMe.Core.Sync;

/// <summary>
/// What one <c>sync --labels</c> run observed in Confluence, as the reconciler wants it (PLAN.md §6.3).
/// </summary>
/// <remarks>
/// Page ids, never titles: state maps a path to a page id, a title is a thing humans rename, and a
/// title-keyed reconcile would silently approve the wrong page the first time two pages shared a name.
/// The observation timestamp is supplied rather than read from the clock so
/// <see cref="LabelSyncPlanner.Plan"/> is a pure function of its inputs — a <c>--dry-run</c> and the
/// real run that follows it then reach the same plan.
/// </remarks>
/// <param name="ApprovedPageIds">Page ids carrying the <c>approved</c> label.</param>
/// <param name="StalePageIds">Page ids carrying the <c>stale</c> label.</param>
/// <param name="VersionsByPageId">
/// Page id → the page version current at observation time (§8). Partial by design: the CQL search's
/// <c>expand=version</c> is best-effort, so an id may be missing and the reconciler treats an unknown
/// version as "do not restamp" rather than as version zero.
/// </param>
/// <param name="ObservedAt">When the labels were read, ISO-8601. Becomes <c>approvedAt</c>.</param>
public sealed record LabelObservation(
    IReadOnlyCollection<string> ApprovedPageIds,
    IReadOnlyCollection<string> StalePageIds,
    IReadOnlyDictionary<string, int> VersionsByPageId,
    string ObservedAt);

/// <summary>
/// The decisions one <c>sync --labels</c> run would write into <c>_meta/state.json</c>, separated from
/// the writing so <c>--dry-run</c> and a real run share one decision (PLAN.md §6.3).
/// </summary>
/// <param name="Approvals">Pages whose label says approved and whose state does not say so yet.</param>
/// <param name="Revocations">Pages state records as approved whose label a human has taken off.</param>
/// <param name="StaleChanges">Pages whose <c>stale</c> flag disagrees with the <c>stale</c> label.</param>
/// <param name="Unmanaged">
/// Labelled pages state has no entry for — reported, never guessed at. A human labelling a page DocuMe
/// does not publish is an ordinary thing to do in a shared space, so it is information rather than an
/// error, and matching it to a path by title is exactly the guess this design refuses to make.
/// </param>
public sealed record LabelSyncPlan(
    IReadOnlyList<PlannedApproval> Approvals,
    IReadOnlyList<PlannedRevocation> Revocations,
    IReadOnlyList<PlannedStaleChange> StaleChanges,
    IReadOnlyList<UnmanagedLabelledPage> Unmanaged)
{
    /// <summary>Whether applying this plan would change the state file at all.</summary>
    /// <remarks>
    /// Unmanaged pages are deliberately not counted: they change nothing on disk, and a sync that
    /// reported "changes" because someone else labelled their own page would commit an empty diff
    /// through a PR on every cron run (§6.3).
    /// </remarks>
    public bool HasChanges => Approvals.Count > 0 || Revocations.Count > 0 || StaleChanges.Count > 0;

    /// <summary>How many state writes this plan carries, for a one-line summary.</summary>
    public int ChangeCount => Approvals.Count + Revocations.Count + StaleChanges.Count;
}

/// <summary>An approval to record (PLAN.md §8).</summary>
/// <param name="Path">Wiki-relative markdown path.</param>
/// <param name="PageId">The Confluence page carrying the label.</param>
/// <param name="ApprovedBy">
/// <see cref="LabelSyncPlanner.UnknownApprover"/> in practice — Confluence exposes no label author
/// (§13 S3).
/// </param>
/// <param name="ApprovedAt">The observation timestamp, from <see cref="LabelObservation.ObservedAt"/>.</param>
/// <param name="Version">The page version current at observation time, when the observation had one.</param>
/// <param name="PreviousVersion">
/// The version an existing approval was recorded at, when this is a re-record rather than a first
/// approval. Non-null means the page moved on under a label that stayed — a human edited it in a
/// browser — which is worth saying in the report rather than silently overwriting.
/// </param>
public sealed record PlannedApproval(
    string Path,
    string PageId,
    string ApprovedBy,
    string ApprovedAt,
    int? Version,
    int? PreviousVersion);

/// <summary>An approval to clear because the label is gone (§6.3: "someone revoked").</summary>
/// <param name="Path">Wiki-relative markdown path.</param>
/// <param name="PageId">The Confluence page whose label is absent.</param>
/// <param name="ApprovedVersion">The version the approval being cleared was recorded at.</param>
public sealed record PlannedRevocation(string Path, string PageId, int? ApprovedVersion);

/// <summary>A <c>stale</c> flag to set or clear so state agrees with the label (§6.3, §6.4).</summary>
/// <param name="Path">Wiki-relative markdown path.</param>
/// <param name="PageId">The Confluence page.</param>
/// <param name="Stale">The value the flag is moving to.</param>
public sealed record PlannedStaleChange(string Path, string PageId, bool Stale);

/// <summary>A labelled page no state entry claims.</summary>
/// <remarks>
/// Carries no title on purpose: the reconciler is given page ids and knows no titles, and the caller
/// that read the labels still has the search results to name one from. Inventing a title field here
/// would mean threading a second map through a pure function to make a report line prettier.
/// </remarks>
/// <param name="PageId">The page id as the search answered it.</param>
/// <param name="Approved">It carries the <c>approved</c> label.</param>
/// <param name="Stale">It carries the <c>stale</c> label.</param>
public sealed record UnmanagedLabelledPage(string PageId, bool Approved, bool Stale);
