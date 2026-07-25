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
    /// Records an observed <c>approved</c> label against <paramref name="path"/> — the state half of
    /// <c>sync --labels</c> (PLAN.md §6.3, §8).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Total, and idempotent where it matters.</strong> A path state has never seen comes back
    /// unchanged rather than created: <c>sync</c> reconciles labels onto pages state already knows,
    /// keyed by page id, and a label on a page DocuMe does not manage is something to report, not to
    /// invent an entry for. Re-recording the same version is the caller's decision to skip
    /// (<see cref="Sync.LabelSyncPlanner"/>) — this function does what it is told, so a caller may
    /// deliberately restamp.
    /// </para>
    /// <para>
    /// <strong>An approval it displaces goes to history first.</strong> §8 keeps approval history for
    /// audit, and the case that would otherwise lose one is real: a page approved at v5 whose author
    /// edited it in a browser is observed at v7 with the label still on, so the record moves to v7 and
    /// the v5 approval survives only here. Only a genuine approval is archived — a
    /// <see cref="ApprovalStatus.NeedsReview"/> record has no approval fields left to keep, and its own
    /// history already holds whatever <see cref="InvalidateApproval"/> retired.
    /// </para>
    /// </remarks>
    /// <param name="state">The state to transform.</param>
    /// <param name="path">Wiki-relative markdown path of the page.</param>
    /// <param name="by">
    /// Who approved, which is <c>"unknown"</c> whenever Confluence will not say — see
    /// <see cref="Sync.LabelSyncPlanner.UnknownApprover"/> (§13 S3). Never the authenticating account:
    /// the reviewer and the bot are different people.
    /// </param>
    /// <param name="at">When the label was observed, ISO-8601, supplied by the caller so the transform
    /// stays pure and testable without a clock.</param>
    /// <param name="version">
    /// The page version current at observation time (§8), or <c>null</c> when the observation could not
    /// establish one. Recorded as-is rather than defaulted to
    /// <see cref="PageState.PublishedVersion"/>, which is a different fact.
    /// </param>
    public static DocumeState RecordApproval(
        DocumeState state,
        string path,
        string by,
        string at,
        int? version)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(by);
        ArgumentException.ThrowIfNullOrEmpty(at);

        if (!state.Pages.TryGetValue(path, out var page))
        {
            return state;
        }

        var previous = page.Approval;
        var history = new List<ApprovalHistoryEntry>(previous?.History ?? []);

        if (string.Equals(previous?.Status, ApprovalStatus.Approved, StringComparison.Ordinal))
        {
            history.Add(new ApprovalHistoryEntry
            {
                By = previous!.ApprovedBy,
                At = previous.ApprovedAt,
                Version = previous.ApprovedVersion,
            });
        }

        var approval = new ApprovalState
        {
            Status = ApprovalStatus.Approved,
            ApprovedBy = by,
            ApprovedAt = at,
            ApprovedVersion = version,
            History = history,
        };

        return WithPage(state, path, page with { Approval = approval });
    }

    /// <summary>
    /// Sets or clears a page's <c>stale</c> flag — the state half of the <c>stale</c> label, observed by
    /// <c>sync --labels</c> (§6.3) and written by <c>drift --mark</c> (§6.4).
    /// </summary>
    /// <remarks>
    /// Total, and a no-op when the flag already reads that way: <c>sync</c> runs on a cron and commits
    /// through a PR (§6.3), so a run that rewrote the state file with identical content would open an
    /// empty PR every time it ran.
    /// </remarks>
    public static DocumeState SetStale(DocumeState state, string path, bool stale)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!state.Pages.TryGetValue(path, out var page) || page.Stale == stale)
        {
            return state;
        }

        return WithPage(state, path, page with { Stale = stale });
    }

    /// <summary>
    /// Moves an approved page to <see cref="ApprovalStatus.NeedsReview"/>, preserving the approval that
    /// was invalidated as a history entry (§6.2 step 7, §8).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two callers, one transition. A republish that changed <c>contentHash</c> invalidates
    /// (§6.2 step 7), and <c>sync --labels</c> applies the same thing when a reviewer has taken the
    /// label off — §6.3's "label absent but state says approved → clear (someone revoked)". The
    /// bookkeeping is identical either way: the retired approval is archived, never dropped, because a
    /// revocation destroying the record of what it revoked would defeat the audit trail §8 keeps it for.
    /// </para>
    /// <para>
    /// Idempotent and total: a page that is unknown, unapproved or already needs review comes back
    /// unchanged, so a caller may apply it without first re-deriving the decision that
    /// <see cref="PagePublishPlan.InvalidatesApproval"/> already made. History is append-only —
    /// §8 keeps it for audit, which a financial org reads as "never rewritten".
    /// </para>
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
