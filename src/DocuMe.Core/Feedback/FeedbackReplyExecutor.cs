using DocuMe.Core.Confluence;

namespace DocuMe.Core.Feedback;

/// <summary>One reply that could not be posted, or one comment that could not be closed.</summary>
/// <param name="CommentId">The comment it was about.</param>
/// <param name="Replied">Whether the reply itself landed — <c>false</c> means the failure was the reply.</param>
/// <param name="Detail">What Confluence said.</param>
public sealed record FeedbackReplyFailure(string CommentId, bool Replied, string Detail);

/// <summary>What one <c>sync --reply</c> run actually did.</summary>
/// <param name="Posted">Replies Confluence accepted.</param>
/// <param name="Resolved">Inline comments closed.</param>
/// <param name="Failures">Everything that did not work, in the order it was attempted.</param>
/// <param name="StoppedBecause">
/// Why the run stopped before the end of the plan, or <c>null</c> when it worked through all of it.
/// </param>
public sealed record FeedbackReplyResult(
    int Posted,
    int Resolved,
    IReadOnlyList<FeedbackReplyFailure> Failures,
    string? StoppedBecause);

/// <summary>
/// Executes a <see cref="FeedbackReplyPlan"/> against Confluence (PLAN.md §9 step 5): posts each reply,
/// stamps the item that asked for it, and closes the inline comments the plan says can be closed.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Post, stamp, then close — per item, in that order.</strong> The stamp goes to disk between the
/// reply and the resolve, not at the end of the run, because it is the only thing standing between a
/// crashed run and a second "Fixed in the latest version" under the same comment on the next cron. The
/// close comes last because closing is what takes a comment off a human's radar, and doing that before
/// the answer exists risks a question being silently retired unanswered.
/// </para>
/// <para>
/// <strong>A failed close does not un-stamp the item.</strong> The reply is posted and
/// <c>repliedAt</c> says so truthfully; re-running to retry the close would post the reply again, which
/// is the worse of the two outcomes. The failure is reported instead, and closing a comment by hand is
/// one click for whoever reads the report.
/// </para>
/// <para>
/// <strong>An auth failure stops the whole run, everything else is per item</strong> — the posture
/// <c>PublishExecutor</c> takes, for the reason rule §1.2 gives: replaying an expired token across forty
/// replies is how an account gets locked out. One comment that 404s is one comment; a 401 is the run.
/// </para>
/// <para>
/// <strong>A cancellation is returned, never thrown.</strong> Every stop above comes back as a
/// <see cref="FeedbackReplyResult.StoppedBecause"/> so the caller can report it, and a Ctrl-C is no
/// different — <em>because</em> the stamp makes it different from a publish. An interrupted publish loses
/// page ids the next run re-earns; here the replies are already on disk as answered, so what a throw
/// discards is the only notice that a close failed. <see cref="FeedbackReplyPlanner"/> will never plan a
/// stamped item again, so a discarded report is a comment left open that nothing will mention twice.
/// </para>
/// </remarks>
public static class FeedbackReplyExecutor
{
    /// <summary>Posts the plan.</summary>
    /// <param name="client">A Confluence client for the target site.</param>
    /// <param name="plan">What to post, from <see cref="FeedbackReplyPlanner.Plan"/>.</param>
    /// <param name="repliedAt">The timestamp stamped onto every item this run answers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<FeedbackReplyResult> ExecuteAsync(
        ConfluenceClient client,
        FeedbackReplyPlan plan,
        DateTimeOffset repliedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(plan);

        var failures = new List<FeedbackReplyFailure>();
        var posted = 0;
        var resolved = 0;

        foreach (var reply in plan.Replies)
        {
            // Returned, not thrown: a close that failed after its reply was posted is reported nowhere
            // else, and the item's `repliedAt` stamp means no later run will ever plan it again — so
            // throwing the list away leaves an answered comment sitting open with nothing to say so.
            if (cancellationToken.IsCancellationRequested)
            {
                return new FeedbackReplyResult(posted, resolved, failures, Cancelled(reply.CommentId, posted));
            }

            try
            {
                await PostAsync(client, reply, cancellationToken).ConfigureAwait(false);
            }
            catch (ConfluenceAuthenticationException ex)
            {
                failures.Add(new FeedbackReplyFailure(reply.CommentId, Replied: false, ex.Message));

                var stopped = $"Confluence rejected the credentials, so the run stopped at comment "
                    + $"{reply.CommentId} with {posted} reply/replies posted.";

                return new FeedbackReplyResult(posted, resolved, failures, stopped);
            }
            catch (ConfluenceException ex)
            {
                failures.Add(new FeedbackReplyFailure(reply.CommentId, Replied: false, ex.Message));

                continue;
            }
            catch (HttpRequestException ex)
            {
                // The transport has already retried with backoff, so a connection still being refused
                // will refuse the rest too. Saying that once beats saying it forty times.
                failures.Add(new FeedbackReplyFailure(reply.CommentId, Replied: false, ex.Message));

                var stopped = $"Confluence became unreachable at comment {reply.CommentId}, with "
                    + $"{posted} reply/replies posted.";

                return new FeedbackReplyResult(posted, resolved, failures, stopped);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Not a failed reply: the reply was not refused, the operator stopped the run. Whether the
                // token trips the request in flight or is seen at the next item's turn, the answer is the
                // same — come back with what the run already did rather than discarding it.
                return new FeedbackReplyResult(posted, resolved, failures, Cancelled(reply.CommentId, posted));
            }

            posted++;

            // Before the resolve, and before the next item: this is the record that stops a second reply.
            FeedbackInbox.MarkReplied(reply.Source, repliedAt);

            if (reply.Resolve != ReplyResolvePlan.Planned || reply.ResolveAtVersion is not { } version)
            {
                continue;
            }

            try
            {
                await client.ResolveInlineCommentAsync(reply.CommentId, version, cancellationToken)
                    .ConfigureAwait(false);

                resolved++;
            }
            catch (ConfluenceAuthenticationException ex)
            {
                failures.Add(new FeedbackReplyFailure(reply.CommentId, Replied: true, ex.Message));

                var stopped = $"Confluence rejected the credentials while closing comment "
                    + $"{reply.CommentId}. {posted} reply/replies were posted and stamped.";

                return new FeedbackReplyResult(posted, resolved, failures, stopped);
            }
            catch (ConfluenceException ex)
            {
                // Typically a 409: somebody touched the comment between the read and now. The reply is
                // already posted and recorded, so this is reported and never retried here.
                failures.Add(new FeedbackReplyFailure(reply.CommentId, Replied: true, ex.Message));
            }
            catch (HttpRequestException ex)
            {
                failures.Add(new FeedbackReplyFailure(reply.CommentId, Replied: true, ex.Message));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The reply is posted and stamped, so this comment is answered and will never be planned
                // again. Only this return says its close did not happen.
                return new FeedbackReplyResult(
                    posted,
                    resolved,
                    failures,
                    CancelledAfterReply(reply.CommentId, posted));
            }
        }

        return new FeedbackReplyResult(posted, resolved, failures, StoppedBecause: null);
    }

    /// <summary>
    /// What a Ctrl-C says. It names the count that was posted because that is the question an operator who
    /// just interrupted a reply pass has: every reply that landed is stamped, so re-running answers only
    /// what is still unanswered rather than posting a second time.
    /// </summary>
    private static string Cancelled(string commentId, int posted)
        => $"the run was cancelled at comment {commentId}, with {posted} reply/replies posted and stamped. "
            + "Nothing after it was attempted; re-run to carry on from there, and see any failure above — "
            + "an item that was answered is never planned again, so this report is the only record of it.";

    /// <summary>
    /// The same, for a cancellation that arrives between a reply and its close: the answer is out and
    /// stamped, so the comment is done being planned while still showing as open to a human.
    /// </summary>
    private static string CancelledAfterReply(string commentId, int posted)
        => $"the run was cancelled while closing comment {commentId}. {posted} reply/replies were posted "
            + "and stamped, so nothing will be said twice — but that comment may still show as open and "
            + "no later run will revisit it, so close it by hand.";

    private static async Task PostAsync(
        ConfluenceClient client,
        PlannedReply reply,
        CancellationToken cancellationToken)
    {
        var kind = string.Equals(reply.Kind, FeedbackKind.Inline, StringComparison.Ordinal)
            ? ConfluenceCommentKind.Inline
            : ConfluenceCommentKind.Footer;

        await client
            .ReplyToCommentAsync(
                new ConfluenceCommentReply(reply.CommentId, kind, reply.Body),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
