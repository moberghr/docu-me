namespace DocuMe.Core.Feedback;

/// <summary>One inbox item to write, and where to write it.</summary>
/// <param name="Path">The page the feedback is about (state.json's key).</param>
/// <param name="FileName">
/// The item's file name inside the inbox directory (<see cref="FeedbackItemFile.NameFor"/>) — a name, not
/// a path, so nothing downstream can be talked into writing outside the inbox.
/// </param>
/// <param name="Item">The item itself, exactly as it will be serialized (§5.4).</param>
public sealed record PlannedFeedbackItem(string Path, string FileName, FeedbackItem Item);

/// <summary>One page's <c>feedbackCursor</c> to advance (PLAN.md §5.3, §6.3).</summary>
/// <param name="Path">The page.</param>
/// <param name="Cursor">The new cursor: the newest comment createdAt this run accounted for.</param>
/// <param name="PreviousCursor">What it was, for a report that can show the move.</param>
public sealed record PlannedCursor(string Path, string Cursor, string? PreviousCursor);

/// <summary>Why a comment the read returned produced no inbox item.</summary>
/// <remarks>
/// Every skip is reported rather than silently applied: a sync that quietly discarded a reviewer's
/// comment would be indistinguishable from one that never saw it. Only <see cref="AlreadyIngested"/> is
/// routine — on a cron, it is almost every comment on almost every run — so the CLI counts that one and
/// names the rest.
/// </remarks>
public enum FeedbackSkipReason
{
    /// <summary>Older than the page's cursor: a previous run already filed it.</summary>
    AlreadyIngested,

    /// <summary>Written by the account DocuMe authenticates as — one of its own replies (§6.3).</summary>
    Bot,

    /// <summary>Resolved in Confluence: a human has already closed it.</summary>
    Resolved,

    /// <summary>The channel answered no body, so there is no feedback to file.</summary>
    NoBody,

    /// <summary>An inbox item for this comment is already on disk, triaged or not.</summary>
    AlreadyOnDisk,

    /// <summary>The comment id cannot be named in a file (<see cref="FeedbackItemFile.IsUsableId"/>).</summary>
    UnusableId,
}

/// <summary>A comment that was read but not ingested.</summary>
/// <param name="Path">The page it is on.</param>
/// <param name="CommentId">The channel's comment id.</param>
/// <param name="Reason">Why it was skipped.</param>
public sealed record SkippedComment(string Path, string CommentId, FeedbackSkipReason Reason);

/// <summary>
/// What one <c>sync --comments</c> run would write: inbox items, cursor moves, and everything it
/// declined to file (PLAN.md §6.3's Comments bullet).
/// </summary>
/// <remarks>
/// Pure data, like <see cref="Sync.LabelSyncPlan"/> — the plan is what a <c>--dry-run</c> renders and
/// what a real run applies, so the two can never disagree about what a sync does.
/// </remarks>
/// <param name="Items">The inbox items to write, ordered by page then comment id.</param>
/// <param name="Cursors">The cursor moves to apply.</param>
/// <param name="Skipped">Every comment read but not ingested, with its reason.</param>
public sealed record FeedbackIngestPlan(
    IReadOnlyList<PlannedFeedbackItem> Items,
    IReadOnlyList<PlannedCursor> Cursors,
    IReadOnlyList<SkippedComment> Skipped)
{
    /// <summary>Whether applying this plan would change anything on disk.</summary>
    public bool HasChanges => Items.Count > 0 || Cursors.Count > 0;

    /// <summary>How many changes it carries — items plus cursor moves.</summary>
    public int ChangeCount => Items.Count + Cursors.Count;

    /// <summary>How many comments were skipped for <paramref name="reason"/>.</summary>
    public int SkippedCount(FeedbackSkipReason reason) => Skipped.Count(skip => skip.Reason == reason);
}
