using DocuMe.Core.Confluence;
using DocuMe.Core.State;

namespace DocuMe.Core.Feedback;

/// <summary>
/// What one comment read cost, alongside the observation it produced — for a run that reports how much
/// Confluence it talked to.
/// </summary>
/// <param name="Observation">The reader's output, ready for <see cref="FeedbackInboxPlanner.Plan"/>.</param>
/// <param name="PagesRead">Published pages whose comments were read.</param>
/// <param name="PagesSkipped">
/// Pages state knows but has never published (no <c>pageId</c>), so there is no Confluence page for a
/// comment to be on.
/// </param>
/// <param name="CommentsRead">Comments the two endpoints returned in total, before any filtering.</param>
/// <param name="AuthorsResolved">Distinct comment authors a display name was looked up for.</param>
public sealed record FeedbackReadResult(
    FeedbackObservation Observation,
    int PagesRead,
    int PagesSkipped,
    int CommentsRead,
    int AuthorsResolved);

/// <summary>
/// Reads every managed page's comments out of Confluence (PLAN.md §6.3's Comments bullet): the footer
/// thread and the inline comments, with their text, plus the two identities the decision needs — who
/// DocuMe is, and what each author is called.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Keyed on the page ids in <c>state.json</c>, never on titles or a space search.</strong> A
/// comment is only feedback about a wiki page if DocuMe published that page, and §6.3's cursor is
/// per-page state, so the read follows state's page map exactly like the publish pipeline does. A page
/// state has never published is skipped and counted rather than searched for.
/// </para>
/// <para>
/// <strong>It writes nothing</strong> — not to Confluence, not to disk. Deciding what the comments mean
/// is <see cref="FeedbackInboxPlanner"/>'s job, writing them is <see cref="FeedbackInbox"/>'s, and
/// committing is the caller's (§6.3). Rule §9.1 still holds: this reads comments, never page bodies, so
/// nothing here can turn Confluence back into a content source.
/// </para>
/// <para>
/// <strong>Comment bodies are untrusted from here on</strong> (CLAUDE.md §0.2, rule §1.3). This layer's
/// entire contribution to that rule is to move the text without looking at it.
/// </para>
/// <para>
/// <strong>What a run costs:</strong> two requests per published page — the two collections are separate
/// endpoints and neither answers the other's comments — plus one for the authenticating account and one
/// per distinct comment author. So a ~80-page wiki on a six-hourly cron is ~160 requests a run, which the
/// client's retry pipeline is sized for; the alternative, a CQL comment search over the space, cannot be
/// keyed to state's page ids and would report comments on pages DocuMe does not manage.
/// </para>
/// </remarks>
public static class FeedbackReader
{
    /// <summary>Runs the read.</summary>
    /// <param name="client">A Confluence client for the target site.</param>
    /// <param name="state">Current state — its page map is the read's work list, and its cursors ride along.</param>
    /// <param name="existingItemFiles">
    /// Inbox file names already on disk (<see cref="FeedbackInbox.ExistingItemFiles"/>), carried into the
    /// observation so the planner stays pure.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ConfluenceAuthenticationException">401/403: token expired or revoked (rule §1.2).</exception>
    /// <exception cref="ConfluenceApiException">Any other non-success status.</exception>
    /// <exception cref="ConfluenceProtocolException">A response was not the documented shape.</exception>
    public static async Task<FeedbackReadResult> ReadAsync(
        ConfluenceClient client,
        DocumeState state,
        IReadOnlySet<string> existingItemFiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(existingItemFiles);

        // One request per run, before any comment is read: the bot's own replies have to be recognizable
        // (§6.3), and its display name seeds the author cache so its comments never cost a second lookup.
        var bot = await client.GetCurrentUserAsync(cancellationToken).ConfigureAwait(false);

        var names = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [bot.AccountId] = bot.DisplayName,
        };

        var pages = new List<ObservedPageComments>();
        var skipped = 0;
        var read = 0;

        foreach (var (path, page) in state.Pages.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (page.PageId is not { Length: > 0 } pageId)
            {
                skipped++;

                continue;
            }

            var footer = await client.GetFooterCommentsAsync(pageId, cancellationToken).ConfigureAwait(false);
            var inline = await client.GetInlineCommentsWithBodiesAsync(pageId, cancellationToken)
                .ConfigureAwait(false);

            read += footer.Count + inline.Count;

            var observed = new List<ObservedComment>(footer.Count + inline.Count);
            foreach (var comment in footer.Concat(inline))
            {
                observed.Add(await ObserveAsync(client, comment, names, cancellationToken)
                    .ConfigureAwait(false));
            }

            pages.Add(new ObservedPageComments(path, page.FeedbackCursor, observed));
        }

        var observation = new FeedbackObservation(pages, bot.AccountId, existingItemFiles);

        // The cache is seeded with the bot, which was not looked up — so it is not counted as one.
        return new FeedbackReadResult(observation, pages.Count, skipped, read, names.Count - 1);
    }

    /// <summary>
    /// Maps one Confluence comment to the channel-neutral shape the planner reads, resolving the author's
    /// display name through <paramref name="names"/>.
    /// </summary>
    private static async Task<ObservedComment> ObserveAsync(
        ConfluenceClient client,
        ConfluenceComment comment,
        Dictionary<string, string?> names,
        CancellationToken cancellationToken)
    {
        var inline = comment.Kind == ConfluenceCommentKind.Inline;
        var author = await DisplayNameAsync(client, comment.AuthorAccountId, names, cancellationToken)
            .ConfigureAwait(false);

        return new ObservedComment(
            comment.Id,
            inline ? FeedbackKind.Inline : FeedbackKind.Footer,
            comment.AuthorAccountId,
            author,
            comment.CreatedAt,
            comment.Body,
            inline ? comment.QuotedText : null,
            comment.IsResolved);
    }

    /// <summary>
    /// The display name for <paramref name="accountId"/>, looked up once per run per account.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cached because a comment read over ~80 pages is one work item and the authors are a handful of
    /// people: the request count is the number of distinct authors, not the number of comments. A negative
    /// answer is cached too — an account Confluence will not name is not worth asking about twice.
    /// </para>
    /// <para>
    /// A 404 leaves the name null and the planner falls back to the account id
    /// (<see cref="FeedbackInboxPlanner"/>). A 401/403 is not caught anywhere here: rule §1.2 makes an auth
    /// failure a hard stop, never something a sync works around.
    /// </para>
    /// </remarks>
    private static async Task<string?> DisplayNameAsync(
        ConfluenceClient client,
        string? accountId,
        Dictionary<string, string?> names,
        CancellationToken cancellationToken)
    {
        if (accountId is not { Length: > 0 })
        {
            return null;
        }

        if (names.TryGetValue(accountId, out var cached))
        {
            return cached;
        }

        var user = await client.FindUserAsync(accountId, cancellationToken).ConfigureAwait(false);
        names[accountId] = user?.DisplayName;

        return user?.DisplayName;
    }
}
