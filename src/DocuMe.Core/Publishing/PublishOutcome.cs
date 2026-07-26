using DocuMe.Core.State;

namespace DocuMe.Core.Publishing;

/// <summary>
/// What actually happened to one page (PLAN.md §6.2 steps 5-8). Only pages the run touched get one:
/// a skipped page is a fact about the plan, not about the run, and <see cref="PublishReport.SkipCount"/>
/// already reports it.
/// </summary>
/// <param name="Path">Wiki-root-relative markdown path.</param>
/// <param name="Title">The title as published.</param>
/// <param name="Action">What the plan asked for. <see cref="Recreated"/> says where the run diverged.</param>
/// <param name="PageId">The Confluence page id, from the create or echoed by the update.</param>
/// <param name="Version">
/// The page version state now records: the version the write produced, or the unchanged one for an
/// attachment-only publish, which deliberately spends no version.
/// </param>
/// <param name="UploadedAttachments">Attachment filenames this page actually uploaded, in upload order.</param>
/// <param name="ApprovalRevoked">Whether the run removed the <c>approved</c> label (§6.2 step 7, §8).</param>
/// <param name="Recreated">
/// True when the plan said "update" but the page was gone from Confluence, so the run created it
/// again with a new id. Its labels are gone with the old page — <c>docume sync</c> reconciles those.
/// </param>
public sealed record PagePublishResult(
    string Path,
    string Title,
    PagePublishAction Action,
    string PageId,
    int Version,
    IReadOnlyList<string> UploadedAttachments,
    bool ApprovalRevoked,
    bool Recreated)
{
    /// <summary>
    /// Whether §6.2 step 7's re-review comment was posted on this page — true only when
    /// <see cref="PublishExecutionOptions.NotifyReviewers"/> asked for it, this run revoked the approval,
    /// and Confluence accepted the comment.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ApprovalRevoked"/> on purpose: a comment Confluence refused warns and
    /// leaves the revocation standing, so "revoked" and "reviewers were told" are two facts and a run can
    /// produce the first without the second.
    /// </remarks>
    public bool ReviewersNotified { get; init; }
}

/// <summary>
/// A page the run could not publish. One page failing does not stop the others: a bulk publish
/// reports every failure so one command shows an author everything that has to change.
/// </summary>
/// <param name="Path">Wiki-root-relative markdown path.</param>
/// <param name="Message">What went wrong, in the words of whatever refused it.</param>
public sealed record PagePublishFailure(string Path, string Message);

/// <summary>
/// The result of executing a <see cref="PublishReport"/> (PLAN.md §6.2 steps 5-8).
/// </summary>
/// <param name="State">
/// State as the run left it, with every successful page recorded. The caller persists it — including
/// after a failure, because a page id earned by a create must not be lost: a re-run that forgot it
/// would try to create the page again and hit Confluence's duplicate-title rejection.
/// </param>
/// <param name="StateChanged">
/// Whether <paramref name="State"/> differs from what was passed in. Lets a caller skip writing
/// <c>state.json</c> on an all-skip run rather than touching a committed file for nothing.
/// </param>
/// <param name="Pages">Every page the run touched, in publish order.</param>
/// <param name="Failures">Pages that failed. Empty on a clean run.</param>
/// <param name="Warnings">
/// Things worth saying that failed nothing — a page filed under a different parent than the plan, a
/// recreated page whose labels are gone.
/// </param>
/// <param name="StoppedBecause">
/// Why the run stopped before finishing, or <c>null</c>. Set for the refusals that make continuing
/// pointless or unsafe: a locked space, an unconverted page, an unreachable space, an expired token
/// (rule §1.2) or a broken diagram renderer. Pages after the stop were never attempted.
/// </param>
public sealed record PublishOutcome(
    DocumeState State,
    bool StateChanged,
    IReadOnlyList<PagePublishResult> Pages,
    IReadOnlyList<PagePublishFailure> Failures,
    IReadOnlyList<string> Warnings,
    string? StoppedBecause)
{
    /// <summary>
    /// The parents whose child order the post-pass reconciled (§6.2). Empty when every parent's children
    /// were already in tree order, when the run wrote nothing, when <c>--no-reorder</c> turned the pass
    /// off, or when the run stopped before reaching it.
    /// </summary>
    public IReadOnlyList<ChildReorder> Reorders { get; init; } = [];

    /// <summary>True when every planned page was published and nothing cut the run short.</summary>
    /// <remarks>
    /// A child order the post-pass could not reconcile does not fail a run: the pages and their content
    /// published, and it is reported in <see cref="Warnings"/>
    /// (<see cref="PublishExecutionOptions.Reorder"/>).
    /// </remarks>
    public bool Succeeded => Failures.Count == 0 && StoppedBecause is null;

    /// <summary>Pages created (§6.2 step 5), including the recreates.</summary>
    public int CreatedCount =>
        Pages.Count(page => page.Action == PagePublishAction.Create || page.Recreated);

    /// <summary>Pages updated in place — a page version spent each.</summary>
    public int UpdatedCount =>
        Pages.Count(page => page.Action == PagePublishAction.Update && !page.Recreated);

    /// <summary>Pages whose attachments moved but whose body did not: no page version spent.</summary>
    public int AttachmentOnlyCount => Pages.Count(page => page.Action == PagePublishAction.UpdateAttachments);

    /// <summary>Pages repositioned in the page tree without a body write (§6.2).</summary>
    public int MovedCount => Pages.Count(page => page.Action == PagePublishAction.Move);

    /// <summary>
    /// Child moves the ordering post-pass issued across the run. Counted apart from
    /// <see cref="MovedCount"/> because the two answer different questions: that one is "pages the tree
    /// reparented", this one is "siblings put back in order".
    /// </summary>
    public int ReorderedCount => Reorders.Sum(reorder => reorder.MovedPaths.Count);

    /// <summary>Attachment uploads across the run.</summary>
    public int UploadedAttachmentCount => Pages.Sum(page => page.UploadedAttachments.Count);

    /// <summary>Approvals this run revoked (§8).</summary>
    public int ApprovalsRevokedCount => Pages.Count(page => page.ApprovalRevoked);

    /// <summary>
    /// Pages that got §6.2 step 7's re-review comment. Zero without <c>--notify-reviewers</c>, and lower
    /// than <see cref="ApprovalsRevokedCount"/> when Confluence refused a comment (see
    /// <see cref="Warnings"/>).
    /// </summary>
    public int ReviewersNotifiedCount => Pages.Count(page => page.ReviewersNotified);
}
