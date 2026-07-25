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
            cancellationToken.ThrowIfCancellationRequested();

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
        }

        return new FeedbackReplyResult(posted, resolved, failures, StoppedBecause: null);
    }

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
