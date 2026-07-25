namespace DocuMe.Core.State;

/// <summary>
/// What a completed upsert knows about a page, ready to be written back to state (PLAN.md §6.2 step 8).
/// </summary>
/// <param name="PageId">The Confluence page id — assigned by the create, echoed by the update.</param>
/// <param name="Title">The title as published (§6.2 step 1: frontmatter override or first H1).</param>
/// <param name="ParentPageId">The parent it was filed under, or <c>null</c> at the space root.</param>
/// <param name="ContentHash">The body hash this publish wrote, banner excluded (§5.3).</param>
/// <param name="PublishedVersion">The Confluence page version the write produced.</param>
/// <param name="Attachments">
/// Attachment filename → content hash for every attachment the page now references. The full set,
/// not the uploaded subset: state records what the page has, and an unchanged attachment that was
/// skipped is still attached.
/// </param>
/// <param name="DiagramWidths">
/// Diagram attachment filename → the <c>ac:width</c> the published body carries for it
/// (<see cref="PageState.DiagramWidths"/>). Positional rather than an optional property so a caller
/// cannot forget it and silently erase what the last publish remembered.
/// </param>
public sealed record PublishedPage(
    string PageId,
    string Title,
    string? ParentPageId,
    string ContentHash,
    int PublishedVersion,
    IReadOnlyDictionary<string, string> Attachments,
    IReadOnlyDictionary<string, string> DiagramWidths);

/// <summary>
/// Pure transitions on <see cref="DocumeState"/> for the publish pipeline's bookkeeping
/// (PLAN.md §6.2 steps 7 and 8). Separate from <see cref="StateStore"/>, which owns file IO and the
/// schema version: these functions take a state and return a new one, so a run can apply them
/// page by page and persist once at the end.
/// </summary>
public static class StateUpdates
{
    /// <summary>
    /// Records a successful publish for <paramref name="path"/>, creating the entry if state has
    /// never seen the page.
    /// </summary>
    /// <remarks>
    /// Publish owns <c>pageId</c>, <c>title</c>, <c>parentPageId</c>, <c>contentHash</c>,
    /// <c>publishedVersion</c>, <c>attachments</c> and <c>diagramWidths</c> and overwrites all seven.
    /// It does not own
    /// <c>approval</c>, <c>stale</c> or <c>feedbackCursor</c> — those belong to <c>sync</c> (§6.3)
    /// and <c>drift</c> (§6.4) — so they are carried through untouched. Approval invalidation is a
    /// separate, explicit step: see <see cref="InvalidateApproval"/>.
    /// </remarks>
    public static DocumeState RecordPublish(DocumeState state, string path, PublishedPage published)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(published);

        state.Pages.TryGetValue(path, out var existing);

        var updated = (existing ?? new PageState()) with
        {
            PageId = published.PageId,
            Title = published.Title,
            ParentPageId = published.ParentPageId,
            ContentHash = published.ContentHash,
            PublishedVersion = published.PublishedVersion,
            Attachments = new Dictionary<string, string>(published.Attachments, StringComparer.Ordinal),
            DiagramWidths = new Dictionary<string, string>(published.DiagramWidths, StringComparer.Ordinal),
        };

        return WithPage(state, path, updated);
    }

    /// <summary>
    /// Moves an approved page to <see cref="ApprovalStatus.NeedsReview"/> after a content change,
    /// preserving the approval that was invalidated as a history entry (§6.2 step 7, §8).
    /// </summary>
    /// <remarks>
    /// Idempotent and total: a page that is unknown, unapproved or already needs review comes back
    /// unchanged, so a caller may apply it without first re-deriving the decision that
    /// <see cref="PagePublishPlan.InvalidatesApproval"/> already made. History is append-only —
    /// §8 keeps it for audit, which a financial org reads as "never rewritten".
    /// </remarks>
    public static DocumeState InvalidateApproval(DocumeState state, string path)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!state.Pages.TryGetValue(path, out var page))
        {
            return state;
        }

        var approval = page.Approval;
        if (!string.Equals(approval?.Status, ApprovalStatus.Approved, StringComparison.Ordinal))
        {
            return state;
        }

        var history = new List<ApprovalHistoryEntry>(approval!.History)
        {
            new() { By = approval.ApprovedBy, At = approval.ApprovedAt, Version = approval.ApprovedVersion },
        };

        var invalidated = new ApprovalState
        {
            Status = ApprovalStatus.NeedsReview,
            ApprovedBy = null,
            ApprovedAt = null,
            ApprovedVersion = null,
            History = history,
        };

        return WithPage(state, path, page with { Approval = invalidated });
    }

    /// <summary>Stamps the repo commit this publish run ran against (§5.3 <c>lastPublishedSha</c>).</summary>
    public static DocumeState RecordLastPublishedSha(DocumeState state, string sha)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrEmpty(sha);

        return state with { LastPublishedSha = sha };
    }

    /// <summary>Drops a page's entry — the state half of a confirmed <c>--prune</c> (§6.2, rule §9.6).</summary>
    public static DocumeState RemovePage(DocumeState state, string path)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!state.Pages.ContainsKey(path))
        {
            return state;
        }

        var pages = new Dictionary<string, PageState>(state.Pages, StringComparer.Ordinal);
        pages.Remove(path);

        return state with { Pages = pages };
    }

    private static DocumeState WithPage(DocumeState state, string path, PageState page)
    {
        var pages = new Dictionary<string, PageState>(state.Pages, StringComparer.Ordinal)
        {
            [path] = page,
        };

        return state with { Pages = pages };
    }
}
