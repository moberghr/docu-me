using DocuMe.Core.Confluence;
using DocuMe.Core.State;

namespace DocuMe.Core.Feedback;

/// <summary>
/// What the reply read cost, alongside the observation it produced.
/// </summary>
/// <param name="Observation">The reader's output, ready for <see cref="FeedbackReplyPlanner.Plan"/>.</param>
/// <param name="ItemsRead">Item files found across the inbox and the archive.</param>
/// <param name="PagesRead">Published pages whose live comments were fetched.</param>
/// <param name="PagesUnpublished">
/// Pages a pending item names that <c>state.json</c> has no <c>pageId</c> for, so there was nothing to
/// read. Their items are reported by the planner rather than silently dropped.
/// </param>
public sealed record FeedbackReplyReadResult(
    FeedbackReplyObservation Observation,
    int ItemsRead,
    int PagesRead,
    int PagesUnpublished);

/// <summary>
/// Reads what the reply pass needs (PLAN.md §9 step 5): the stored inbox and archive items, and the
/// current state of the comments they name.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Only the pages with something pending are read.</strong>
/// <see cref="FeedbackReplyPlanner.PagesToRead"/> narrows an ~80-page wiki to the handful of pages a
/// merged triage PR just touched, which is what keeps a nightly reply pass from costing the same as a
/// full ingestion.
/// </para>
/// <para>
/// <strong>Both comment collections are read for every such page</strong>, rather than only the one the
/// item's <c>kind</c> names. The item is a committed, hand-editable file; the channel is the authority on
/// which collection a comment lives in, and trusting the file instead would post a reply to the wrong
/// endpoint — a new thread that answers nobody — whenever the two disagreed.
/// </para>
/// <para>
/// <strong>Comment bodies are read and deliberately discarded here.</strong> The endpoints answer them
/// and nothing in this path keeps them: the reply pass needs to know a comment exists, whether it is
/// closed and at which version, and never what it says (rule §1.3).
/// </para>
/// </remarks>
public static class FeedbackReplyReader
{
    /// <summary>Runs the read.</summary>
    /// <param name="client">A Confluence client for the target site.</param>
    /// <param name="state">Current state — its page map turns a page path into a page id.</param>
    /// <param name="directories">Where item files live: the inbox, then the archive.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ConfluenceAuthenticationException">401/403: token expired or revoked (rule §1.2).</exception>
    /// <exception cref="ConfluenceApiException">Any other non-success status.</exception>
    /// <exception cref="ConfluenceProtocolException">A response was not the documented shape.</exception>
    public static async Task<FeedbackReplyReadResult> ReadAsync(
        ConfluenceClient client,
        DocumeState state,
        IReadOnlyList<string> directories,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(directories);

        var items = FeedbackInbox.Read(directories);
        var wanted = FeedbackReplyPlanner.PagesToRead(items);

        var read = new HashSet<string>(StringComparer.Ordinal);
        var live = new Dictionary<string, ObservedLiveComment>(StringComparer.Ordinal);
        var unpublished = 0;

        foreach (var path in wanted.OrderBy(page => page, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!state.Pages.TryGetValue(path, out var page)
                || page.PageId is not { Length: > 0 } pageId)
            {
                unpublished++;

                continue;
            }

            var footer = await client.GetFooterCommentsAsync(pageId, cancellationToken)
                .ConfigureAwait(false);
            var inline = await client.GetInlineCommentsWithBodiesAsync(pageId, cancellationToken)
                .ConfigureAwait(false);

            foreach (var comment in footer.Concat(inline))
            {
                live[comment.Id] = Observe(comment);
            }

            read.Add(path);
        }

        var observation = new FeedbackReplyObservation(items, read, live);

        return new FeedbackReplyReadResult(observation, items.Count, read.Count, unpublished);
    }

    /// <summary>
    /// Maps one live comment to the channel-neutral shape the planner reads. The body and the anchored
    /// text are not carried at all.
    /// </summary>
    private static ObservedLiveComment Observe(ConfluenceComment comment)
        => new(
            comment.Id,
            comment.Kind == ConfluenceCommentKind.Inline ? FeedbackKind.Inline : FeedbackKind.Footer,
            comment.IsResolved,
            !comment.IsDangling,
            comment.Version);
}
