namespace DocuMe.Core.Feedback;

/// <summary>Why a stored item gets no reply this run.</summary>
/// <remarks>
/// The first two are routine — on a cron nearly every item in the archive is
/// <see cref="AlreadyReplied"/> and nearly every item in the inbox is <see cref="NotTriaged"/> — so the
/// CLI counts those and names the rest. The others each describe an item nobody will ever answer unless
/// a human looks, which is exactly the thing that must not be silent.
/// </remarks>
public enum FeedbackReplySkipReason
{
    /// <summary><c>status</c> is still <c>new</c>: <c>/docs-feedback</c> has not triaged it (§9 step 3).</summary>
    NotTriaged,

    /// <summary><c>repliedAt</c> is already set: an earlier run answered this comment.</summary>
    AlreadyReplied,

    /// <summary>The file could not be read or parsed (<see cref="StoredFeedbackItem.Item"/> is null).</summary>
    Unreadable,

    /// <summary>
    /// The item names no page, no comment id, or an id with no channel prefix — nothing a reply could be
    /// addressed to.
    /// </summary>
    Unaddressable,

    /// <summary>
    /// The item's page has no published counterpart, so its comments were never read. Usually an item
    /// about a page that was removed from the wiki, or one <c>state.json</c> has no <c>pageId</c> for.
    /// </summary>
    PageNotPublished,

    /// <summary>The page was read and the comment is not on it any more: somebody deleted it.</summary>
    CommentGone,
}

/// <summary>What the reply pass will do about closing a comment after it answers it.</summary>
public enum ReplyResolvePlan
{
    /// <summary>A footer comment: the channel has no resolution state to set (§9 step 5's "where the API allows").</summary>
    NotApplicable,

    /// <summary>An open inline comment with a version: resolve it after the reply lands.</summary>
    Planned,

    /// <summary>Already resolved in the channel — the reply still goes out, the close does not.</summary>
    AlreadyResolved,

    /// <summary>The anchor is dangling, which Confluence refuses to update at all.</summary>
    NotClosable,

    /// <summary>The channel answered no version, and a resolve is an optimistic-lock write.</summary>
    NoVersion,
}

/// <summary>One reply to post, and what to do about the comment afterwards.</summary>
/// <remarks>
/// <see cref="Source"/> is the whole item as it was read, not just its path, so the executor stamps
/// <c>repliedAt</c> onto the item it planned from rather than re-reading the file after the network call.
/// The two would agree in a CLI run either way; carrying it removes the question.
/// </remarks>
/// <param name="Source">The item file this reply answers, and where the stamp goes.</param>
/// <param name="Page">The page the feedback is about, for the report.</param>
/// <param name="CommentId">The channel's comment id, with the inbox item's channel prefix stripped.</param>
/// <param name="Kind"><see cref="FeedbackKind.Inline"/> or <see cref="FeedbackKind.Footer"/>.</param>
/// <param name="Status">The triage outcome being communicated, for the report.</param>
/// <param name="Body">The reply in storage format, already escaped (<see cref="FeedbackReplyText"/>).</param>
/// <param name="Resolve">Whether the comment is closed afterwards, or why not.</param>
/// <param name="ResolveAtVersion">
/// The comment's current version, non-<c>null</c> exactly when <paramref name="Resolve"/> is
/// <see cref="ReplyResolvePlan.Planned"/>.
/// </param>
public sealed record PlannedReply(
    StoredFeedbackItem Source,
    string Page,
    string CommentId,
    string Kind,
    string Status,
    string Body,
    ReplyResolvePlan Resolve,
    int? ResolveAtVersion)
{
    /// <summary>The item file, for a report that names what it is about to change.</summary>
    public string FilePath => Source.FilePath;
}

/// <summary>A stored item the reply pass will not answer.</summary>
/// <param name="FilePath">The item file.</param>
/// <param name="CommentId">The channel comment id, where the item named one.</param>
/// <param name="Reason">Why.</param>
public sealed record SkippedReply(string FilePath, string? CommentId, FeedbackReplySkipReason Reason);

/// <summary>
/// What one <c>sync --reply</c> run would post (PLAN.md §9 step 5): a reply per triaged item, the
/// inline comments it will close, and everything it declined.
/// </summary>
/// <remarks>
/// Pure data, like <see cref="FeedbackIngestPlan"/>: the plan is what <c>--dry-run</c> renders and what a
/// real run executes, so the two cannot disagree about what a run does to Confluence — which matters more
/// here than on the ingestion side, because this is the half that writes.
/// </remarks>
/// <param name="Replies">The replies to post, ordered by item file path.</param>
/// <param name="Skipped">Every stored item that gets no reply, with its reason.</param>
public sealed record FeedbackReplyPlan(
    IReadOnlyList<PlannedReply> Replies,
    IReadOnlyList<SkippedReply> Skipped)
{
    /// <summary>Whether this run would write anything to Confluence at all.</summary>
    public bool HasChanges => Replies.Count > 0;

    /// <summary>How many inline comments it would close.</summary>
    public int ResolveCount => Replies.Count(reply => reply.Resolve == ReplyResolvePlan.Planned);

    /// <summary>How many items were skipped for <paramref name="reason"/>.</summary>
    public int SkippedCount(FeedbackReplySkipReason reason) => Skipped.Count(skip => skip.Reason == reason);
}
