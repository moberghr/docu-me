namespace DocuMe.Core.Feedback;

/// <summary>
/// One comment as ingestion sees it, with every channel-specific detail already resolved.
/// </summary>
/// <remarks>
/// <strong>Channel-neutral on purpose.</strong> This is where Confluence stops: the reader
/// (<see cref="FeedbackReader"/>) turns a <see cref="Confluence.ConfluenceComment"/> into one of these,
/// and the planner below never sees an API type. §9 makes the inbox item the seam for future intake
/// channels, so the decision half of the loop is written against a shape any channel can produce.
/// </remarks>
/// <param name="Id">The channel's own id for the comment.</param>
/// <param name="Kind"><see cref="FeedbackKind.Inline"/> or <see cref="FeedbackKind.Footer"/>.</param>
/// <param name="AuthorAccountId">
/// The account that wrote it, or <c>null</c> when the channel did not say. Compared against
/// <see cref="FeedbackObservation.BotAccountId"/> to skip DocuMe's own replies (§6.3).
/// </param>
/// <param name="AuthorDisplayName">
/// The name to record, where the channel could be asked for one. <c>null</c> falls back to
/// <paramref name="AuthorAccountId"/>, then to <see cref="FeedbackAuthor.Unknown"/>.
/// </param>
/// <param name="CreatedAt">
/// When it was written, in whatever form the channel answered. Unreadable or missing values are handled
/// rather than dropped — see <see cref="FeedbackInboxPlanner"/>.
/// </param>
/// <param name="Body">The feedback text, verbatim and untrusted (rule §1.3).</param>
/// <param name="QuotedText">The anchored page text for an inline comment, verbatim; <c>null</c> otherwise.</param>
/// <param name="IsResolved">
/// Whether the channel considers the comment closed. Resolved comments are observed but not ingested:
/// a human already dealt with it, and filing it for triage would ask for the work twice.
/// </param>
public sealed record ObservedComment(
    string Id,
    string Kind,
    string? AuthorAccountId,
    string? AuthorDisplayName,
    string? CreatedAt,
    string? Body,
    string? QuotedText,
    bool IsResolved);

/// <summary>
/// Every comment observed on one page, with the cursor that says how far the last run got.
/// </summary>
/// <param name="Path">The wiki-relative markdown path, as <c>state.json</c> keys it (§5.3).</param>
/// <param name="Cursor">
/// The page's <c>feedbackCursor</c>: the newest comment already ingested (§5.3). <c>null</c> on a page
/// nothing has been ingested from, which ingests everything — the first run on a page with existing
/// discussion is supposed to pick that discussion up.
/// </param>
/// <param name="Comments">The comments the read returned, in whatever order they arrived.</param>
public sealed record ObservedPageComments(
    string Path,
    string? Cursor,
    IReadOnlyList<ObservedComment> Comments);

/// <summary>
/// One comment read across the wiki, ready for <see cref="FeedbackInboxPlanner.Plan"/>.
/// </summary>
/// <param name="Pages">The pages read, with their comments.</param>
/// <param name="BotAccountId">
/// The account DocuMe authenticates as, whose comments are its own replies and are never ingested
/// (§6.3). <c>null</c> means the account could not be established — in which case nothing is skipped as
/// the bot's, because guessing wrong in that direction would silently drop a person's feedback.
/// </param>
/// <param name="ExistingItemFiles">
/// The inbox file names already on disk. An item is never rewritten: by the time
/// <c>/docs-feedback</c> has triaged one, the file carries a <c>status</c> and a <c>resolution</c> that
/// a re-ingest would overwrite with <c>new</c>. The cursor normally makes this moot; this is what keeps a
/// hand-edited or unreadable cursor from costing anybody their triage.
/// </param>
public sealed record FeedbackObservation(
    IReadOnlyList<ObservedPageComments> Pages,
    string? BotAccountId,
    IReadOnlySet<string> ExistingItemFiles);
