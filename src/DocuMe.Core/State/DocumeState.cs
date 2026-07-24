namespace DocuMe.Core.State;

/// <summary>
/// Model for a consumer repo's <c>_meta/state.json</c> (PLAN.md §5.3). Committed,
/// machine-owned; one <see cref="PageState"/> per published page. Read and written
/// by <see cref="StateStore"/>, which owns the <c>version</c>/migration seam.
/// </summary>
public sealed record DocumeState
{
    /// <summary>Schema version; owned by <see cref="StateStore"/>.</summary>
    public int Version { get; init; } = StateStore.CurrentVersion;

    /// <summary>Repo commit the wiki was last generated against.</summary>
    public string? BaselineSha { get; init; }

    /// <summary>Repo commit of the last publish run.</summary>
    public string? LastPublishedSha { get; init; }

    /// <summary>Wiki-relative markdown path → page state.</summary>
    public IReadOnlyDictionary<string, PageState> Pages { get; init; }
        = new Dictionary<string, PageState>();
}

public sealed record PageState
{
    public string? PageId { get; init; }

    public string? Title { get; init; }

    public string? ParentPageId { get; init; }

    /// <summary>Hash of the converted body EXCLUDING the banner — drives change detection and approval invalidation.</summary>
    public string? ContentHash { get; init; }

    /// <summary>Confluence page version we last wrote.</summary>
    public int PublishedVersion { get; init; }

    /// <summary>Attachment filename → content hash.</summary>
    public IReadOnlyDictionary<string, string> Attachments { get; init; }
        = new Dictionary<string, string>();

    public ApprovalState? Approval { get; init; }

    public bool Stale { get; init; }

    /// <summary>Newest comment already ingested (feedback loop cursor).</summary>
    public string? FeedbackCursor { get; init; }
}

public sealed record ApprovalState
{
    /// <summary><c>approved</c> | <c>needs-review</c> (see PLAN.md §8).</summary>
    public string? Status { get; init; }

    public string? ApprovedBy { get; init; }

    public string? ApprovedAt { get; init; }

    public int? ApprovedVersion { get; init; }

    public IReadOnlyList<ApprovalHistoryEntry> History { get; init; } = [];
}

public sealed record ApprovalHistoryEntry
{
    public string? By { get; init; }

    public string? At { get; init; }

    public int? Version { get; init; }
}
