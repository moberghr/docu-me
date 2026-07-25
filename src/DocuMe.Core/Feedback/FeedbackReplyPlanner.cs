namespace DocuMe.Core.Feedback;

/// <summary>
/// Decides which triaged inbox items get a reply, and which of their comments can be closed
/// (PLAN.md §9 step 5) — the decision half, with no network and no filesystem in it.
/// </summary>
/// <remarks>
/// <para>
/// Pure for the reasons <see cref="FeedbackInboxPlanner"/> is, with one extra that matters more here:
/// this is the half of the feedback loop that <em>writes to Confluence</em>, so what a <c>--dry-run</c>
/// prints and what a real run posts must be the same object, not two code paths that agree today.
/// </para>
/// <para>
/// <strong>An item is answered exactly once, and <c>repliedAt</c> is what makes that true.</strong> The
/// triage that set <c>status</c> happened in an earlier PR and the reply runs on a later cron, so
/// "already answered" cannot be inferred from anything else the item carries — see
/// <see cref="FeedbackItem.RepliedAt"/>.
/// </para>
/// <para>
/// <strong>Reply first, close second, and never the other way round.</strong> Closing a comment is the
/// gesture that takes it off a human's radar; doing that before the answer is posted risks a reviewer's
/// question being silently closed unanswered if the reply then fails. The order is expressed here (a
/// resolve is a member of the reply, not a peer of it) so an executor cannot get it wrong.
/// </para>
/// </remarks>
public static class FeedbackReplyPlanner
{
    /// <summary>Turns stored items plus the channel's current state into the run's plan.</summary>
    public static FeedbackReplyPlan Plan(FeedbackReplyObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var replies = new List<PlannedReply>();
        var skipped = new List<SkippedReply>();

        foreach (var stored in observation.Items.OrderBy(item => item.FilePath, StringComparer.Ordinal))
        {
            var planned = PlanItem(stored, observation, skipped);
            if (planned is not null)
            {
                replies.Add(planned);
            }
        }

        return new FeedbackReplyPlan(replies, skipped);
    }

    /// <summary>
    /// The pages whose live comments have to be read before <see cref="Plan"/> can decide anything —
    /// the pages named by items that are triaged, unanswered and addressable.
    /// </summary>
    /// <remarks>
    /// Here rather than in the reader so that one rule says which items are candidates. The reader uses
    /// it to size its work (a wiki with two pending items should cost two page reads, not eighty), and
    /// <see cref="Plan"/> re-derives the same answer per item with a reportable reason attached.
    /// </remarks>
    public static IReadOnlySet<string> PagesToRead(IReadOnlyList<StoredFeedbackItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var pages = new HashSet<string>(StringComparer.Ordinal);

        foreach (var stored in items)
        {
            if (stored.Item is { } item && IsCandidate(item) && item.Page is { Length: > 0 } page)
            {
                pages.Add(page);
            }
        }

        return pages;
    }

    /// <summary>
    /// Whether this item is one a reply could still be owed for: triaged, not yet answered, and carrying
    /// a comment id this build knows how to post to.
    /// </summary>
    private static bool IsCandidate(FeedbackItem item)
        => item.RepliedAt is not { Length: > 0 }
            && FeedbackReplyText.IsTriaged(item.Status)
            && CommentId(item) is { Length: > 0 };

    /// <summary>
    /// One item's decision: the reply to post, or <c>null</c> with a reason appended to
    /// <paramref name="skipped"/>.
    /// </summary>
    private static PlannedReply? PlanItem(
        StoredFeedbackItem stored,
        FeedbackReplyObservation observation,
        List<SkippedReply> skipped)
    {
        if (stored.Item is not { } item)
        {
            skipped.Add(new SkippedReply(stored.FilePath, null, FeedbackReplySkipReason.Unreadable));

            return null;
        }

        // Checked before anything else about the item, because on a cron this is the answer for every
        // item the loop has ever handled and it costs nothing to establish.
        if (item.RepliedAt is { Length: > 0 })
        {
            skipped.Add(new SkippedReply(
                stored.FilePath,
                CommentId(item),
                FeedbackReplySkipReason.AlreadyReplied));

            return null;
        }

        if (!FeedbackReplyText.IsTriaged(item.Status))
        {
            skipped.Add(new SkippedReply(
                stored.FilePath,
                CommentId(item),
                FeedbackReplySkipReason.NotTriaged));

            return null;
        }

        if (CommentId(item) is not { Length: > 0 } commentId || item.Page is not { Length: > 0 } page)
        {
            skipped.Add(new SkippedReply(
                stored.FilePath,
                CommentId(item),
                FeedbackReplySkipReason.Unaddressable));

            return null;
        }

        if (!observation.ReadPages.Contains(page))
        {
            skipped.Add(new SkippedReply(
                stored.FilePath,
                commentId,
                FeedbackReplySkipReason.PageNotPublished));

            return null;
        }

        if (!observation.LiveComments.TryGetValue(commentId, out var live))
        {
            skipped.Add(new SkippedReply(stored.FilePath, commentId, FeedbackReplySkipReason.CommentGone));

            return null;
        }

        var (resolve, version) = PlanResolve(live);

        // The kind comes from the live comment, not from the item: the item's `kind` was written when the
        // comment was ingested and picks the endpoint a reply is posted to, which is not something to get
        // from a file a human can edit when the channel is right here saying what it is.
        return new PlannedReply(
            stored,
            page,
            commentId,
            live.Kind,
            item.Status!,
            FeedbackReplyText.Compose(item.Status, item.Resolution),
            resolve,
            version);
    }

    /// <summary>
    /// Whether the comment is closed after the reply, and at which version — §9 step 5's "resolves
    /// inline comments where the API allows", with each way of not allowing it named rather than lumped
    /// together as "skipped".
    /// </summary>
    /// <remarks>
    /// <see cref="ReplyResolvePlan.AlreadyResolved"/> is not a failure and is worth distinguishing: a
    /// human closed the comment between the triage and this run, which is the loop working, whereas
    /// <see cref="ReplyResolvePlan.NotClosable"/> leaves an open comment nobody will close without
    /// clicking it.
    /// </remarks>
    private static (ReplyResolvePlan Resolve, int? Version) PlanResolve(ObservedLiveComment live)
    {
        if (!string.Equals(live.Kind, FeedbackKind.Inline, StringComparison.Ordinal))
        {
            return (ReplyResolvePlan.NotApplicable, null);
        }

        if (live.IsResolved)
        {
            return (ReplyResolvePlan.AlreadyResolved, null);
        }

        if (!live.IsClosable)
        {
            return (ReplyResolvePlan.NotClosable, null);
        }

        if (live.Version is not { } version)
        {
            return (ReplyResolvePlan.NoVersion, null);
        }

        return (ReplyResolvePlan.Planned, version);
    }

    /// <summary>
    /// The channel's own comment id out of the item's channel-prefixed <c>id</c>
    /// (<see cref="FeedbackItemId"/>), or <c>null</c> when the id names no channel this build can post to.
    /// </summary>
    /// <remarks>
    /// An id without the <c>conf-comment-</c> prefix is an item from a channel §9 anticipates but v1 does
    /// not implement. Refusing it is the point: posting its bare id to Confluence would answer whichever
    /// unrelated comment happens to carry that number.
    /// </remarks>
    private static string? CommentId(FeedbackItem item)
    {
        if (item.Id is not { Length: > 0 } id)
        {
            return null;
        }

        if (!id.StartsWith(FeedbackItemId.ConfluenceCommentPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var commentId = id[FeedbackItemId.ConfluenceCommentPrefix.Length..];

        return commentId.Length > 0 ? commentId : null;
    }
}
