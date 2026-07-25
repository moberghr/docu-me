using DocuMe.Core.State;

namespace DocuMe.Core.Feedback;

/// <summary>
/// Turns observed comments into inbox items and cursor moves (PLAN.md §6.3's Comments bullet, §5.4): a
/// pure decision, then a pure application of it.
/// </summary>
/// <remarks>
/// <para>
/// Pure for the same reasons <see cref="Sync.LabelSyncPlanner"/> is — one decision behind both
/// <c>--dry-run</c> and a real run, and tests that need neither a network, a clock nor a filesystem.
/// Everything that comes from outside arrives in a <see cref="FeedbackObservation"/>, including the two
/// facts that are not about comments at all: which account is DocuMe's, and which item files already
/// exist.
/// </para>
/// <para>
/// <strong>It takes no <see cref="DocumeState"/> to plan</strong>, which is the one place it deviates
/// from the label planner's shape. A page's cursor rides along in
/// <see cref="ObservedPageComments.Cursor"/> instead, so the whole decision is expressible by any intake
/// channel §9 adds later, not just by one that happens to read DocuMe's state file. State comes back in
/// at <see cref="Apply"/>, which is the only step that writes to it.
/// </para>
/// <para>
/// <strong>It writes nothing anywhere.</strong> Items land on disk through
/// <see cref="FeedbackInbox.Write"/> and committing them is the caller's job (§6.3's closing line) — the
/// sync workflow commits to a <c>docs/sync</c> branch and opens a PR, because a machine pushing to a
/// protected branch does not work in this org.
/// </para>
/// </remarks>
public static class FeedbackInboxPlanner
{
    /// <summary>
    /// Decides which observed comments become inbox items, and how far each page's cursor moves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The cursor is a createdAt watermark, and it moves past skipped comments too.</strong> A
    /// comment is ingested when it is strictly newer than the cursor; the new cursor is the newest
    /// <c>createdAt</c> the run <em>accounted for</em>, whether it filed it or skipped it as DocuMe's own
    /// reply, as resolved, or as already on disk. Otherwise a bot reply at the top of a thread would be
    /// re-examined on every run forever, and the cursor would never pass it.
    /// </para>
    /// <para>
    /// <strong>What that costs, stated rather than hidden:</strong> an edited or reopened old comment is
    /// not re-ingested, because its <c>createdAt</c> does not move. §5.3 defines the cursor as "newest
    /// comment already ingested", and the alternative — keying on a modification time or a version number
    /// — would file a second item every time somebody fixed a typo in their own comment. A reopened
    /// comment is a case for a human, and <c>docume status</c> plus the open-comment guard are where it
    /// surfaces.
    /// </para>
    /// <para>
    /// <strong>A comment with no readable timestamp is ingested and moves nothing.</strong> It cannot be
    /// placed against the cursor, and the two ways to be wrong are not symmetric: filing it risks a
    /// duplicate item (which <see cref="FeedbackSkipReason.AlreadyOnDisk"/> then catches), while dropping
    /// it loses a reviewer's comment for good.
    /// </para>
    /// <para>
    /// The cursor never moves backwards: a watermark that could regress would re-ingest everything
    /// between the two values.
    /// </para>
    /// </remarks>
    public static FeedbackIngestPlan Plan(FeedbackObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var items = new List<PlannedFeedbackItem>();
        var cursors = new List<PlannedCursor>();
        var skipped = new List<SkippedComment>();

        // Ordered by path, then by comment id, so the plan, the report and the resulting file list read
        // the same way twice regardless of what order the API answered in.
        foreach (var page in observation.Pages.OrderBy(page => page.Path, StringComparer.Ordinal))
        {
            var planned = PlanPage(page, observation, items, skipped);
            if (planned is not null)
            {
                cursors.Add(planned);
            }
        }

        return new FeedbackIngestPlan(items, cursors, skipped);
    }

    /// <summary>
    /// Writes <paramref name="plan"/>'s cursor moves into <paramref name="state"/>, returning the new
    /// state. The inbox items themselves are files, not state — <see cref="FeedbackInbox.Write"/>.
    /// </summary>
    /// <remarks>
    /// Split from the item writes on purpose: state.json and the inbox are two artifacts of one sync, and
    /// a run that advanced a cursor without writing the item it advanced past would lose that comment
    /// permanently. The caller writes the items first and the state last, so the failure mode is a
    /// re-ingest (caught by <see cref="FeedbackSkipReason.AlreadyOnDisk"/>) rather than a silent loss.
    /// </remarks>
    public static DocumeState Apply(DocumeState state, FeedbackIngestPlan plan)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(plan);

        var updated = state;

        foreach (var cursor in plan.Cursors)
        {
            updated = StateUpdates.SetFeedbackCursor(updated, cursor.Path, cursor.Cursor);
        }

        return updated;
    }

    /// <summary>
    /// One page's decisions, appending to the run's item and skip lists; returns the page's cursor move,
    /// or <c>null</c> when it stays where it is.
    /// </summary>
    private static PlannedCursor? PlanPage(
        ObservedPageComments page,
        FeedbackObservation observation,
        List<PlannedFeedbackItem> items,
        List<SkippedComment> skipped)
    {
        var hasCursor = FeedbackTimestamp.TryParse(page.Cursor, out var cursor);
        var watermark = hasCursor ? cursor : (DateTimeOffset?)null;

        foreach (var comment in page.Comments.OrderBy(comment => comment.Id, StringComparer.Ordinal))
        {
            var hasCreatedAt = FeedbackTimestamp.TryParse(comment.CreatedAt, out var createdAt);

            // Older than the watermark: a previous run filed it. Checked first because on a cron this is
            // the answer for nearly every comment, and the checks below are about comments worth deciding.
            if (hasCursor && hasCreatedAt && createdAt <= cursor)
            {
                skipped.Add(new SkippedComment(page.Path, comment.Id, FeedbackSkipReason.AlreadyIngested));
                watermark = Later(watermark, createdAt);

                continue;
            }

            var reason = SkipReason(comment, page.Path, observation);
            if (reason is not null)
            {
                skipped.Add(new SkippedComment(page.Path, comment.Id, reason.Value));

                // A comment this run deliberately declined still counts as accounted for: leaving the
                // watermark behind it would re-decide it on every run for as long as the page exists.
                // The one exception is an id no file can be named after, which nothing here can settle.
                if (reason != FeedbackSkipReason.UnusableId && hasCreatedAt)
                {
                    watermark = Later(watermark, createdAt);
                }

                continue;
            }

            items.Add(new PlannedFeedbackItem(
                page.Path,
                FeedbackItemFile.NameFor(page.Path, comment.Id),
                ItemFor(page.Path, comment)));

            if (hasCreatedAt)
            {
                watermark = Later(watermark, createdAt);
            }
        }

        if (watermark is not { } moved || (hasCursor && moved <= cursor))
        {
            return null;
        }

        return new PlannedCursor(page.Path, FeedbackTimestamp.Write(moved), page.Cursor);
    }

    /// <summary>
    /// Why this comment is not ingested, or <c>null</c> when it is. Ordered by how much the reason says
    /// about the comment: whose it is, then whether it is still open, then whether it can be filed.
    /// </summary>
    private static FeedbackSkipReason? SkipReason(
        ObservedComment comment,
        string path,
        FeedbackObservation observation)
    {
        // §6.3: skip the bot's own replies. Without this, the reply /docs-feedback posts ("Fixed in the
        // latest version — thanks", §9 step 5) comes back as new feedback on the next sync and gets
        // triaged as a reviewer's claim. A comment with no author is never assumed to be DocuMe's.
        if (observation.BotAccountId is { Length: > 0 } bot
            && string.Equals(comment.AuthorAccountId, bot, StringComparison.Ordinal))
        {
            return FeedbackSkipReason.Bot;
        }

        if (comment.IsResolved)
        {
            return FeedbackSkipReason.Resolved;
        }

        if (comment.Body is not { Length: > 0 } || comment.Body.AsSpan().IsWhiteSpace())
        {
            return FeedbackSkipReason.NoBody;
        }

        if (!FeedbackItemFile.IsUsableId(comment.Id))
        {
            return FeedbackSkipReason.UnusableId;
        }

        if (observation.ExistingItemFiles.Contains(FeedbackItemFile.NameFor(path, comment.Id)))
        {
            return FeedbackSkipReason.AlreadyOnDisk;
        }

        return null;
    }

    /// <summary>
    /// The §5.4 item for one comment. Everything the channel said is copied verbatim; the only fields
    /// this side decides are <c>id</c>'s channel prefix, the normalized timestamp, and
    /// <see cref="FeedbackStatus.New"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>quotedText</c> is carried for inline comments only, per §5.4 — a footer comment is anchored to
    /// nothing, and a channel that answered anchored text for one would be describing something this
    /// shape has no meaning for. <c>resolution</c> is left unset: it is <c>/docs-feedback</c>'s to fill.
    /// </para>
    /// <para>
    /// The <c>conf-comment-</c> prefix is the one channel-specific decision left in this class, and it is
    /// here rather than in <see cref="ObservedComment"/> because v1 has exactly one intake (§9). When a
    /// second arrives, the channel — not the planner — is what should name it, and the prefix moves into
    /// the observation.
    /// </para>
    /// </remarks>
    private static FeedbackItem ItemFor(string path, ObservedComment comment)
    {
        var createdAt = FeedbackTimestamp.TryParse(comment.CreatedAt, out var parsed)
            ? FeedbackTimestamp.Write(parsed)
            : null;

        var inline = string.Equals(comment.Kind, FeedbackKind.Inline, StringComparison.Ordinal);

        return new FeedbackItem
        {
            Id = FeedbackItemId.ForConfluenceComment(comment.Id),
            Page = path,
            Kind = comment.Kind,
            Author = Author(comment),
            CreatedAt = createdAt,
            QuotedText = inline ? comment.QuotedText : null,
            Body = comment.Body,
            Status = FeedbackStatus.New,
            Resolution = null,
        };
    }

    /// <summary>
    /// A display name where the channel had one, else the account id — which at least identifies the
    /// person to anyone with Confluence access — else <see cref="FeedbackAuthor.Unknown"/>.
    /// </summary>
    private static string Author(ObservedComment comment)
    {
        if (comment.AuthorDisplayName is { Length: > 0 } name && !name.AsSpan().IsWhiteSpace())
        {
            return name;
        }

        if (comment.AuthorAccountId is { Length: > 0 } accountId)
        {
            return accountId;
        }

        return FeedbackAuthor.Unknown;
    }

    private static DateTimeOffset Later(DateTimeOffset? watermark, DateTimeOffset candidate)
        => watermark is { } current && current >= candidate ? current : candidate;
}
