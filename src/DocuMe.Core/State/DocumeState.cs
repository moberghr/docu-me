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

    /// <summary>
    /// Diagram attachment filename → the pixel width last measured for it, which is the <c>ac:width</c>
    /// its image carries in the published body (PLAN.md §7,
    /// <see cref="Publishing.DiagramImageWidth"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Remembered rather than re-derived because the width comes from the rendered SVG, and a publish
    /// renders only the diagrams whose bytes it uploads. Without this, editing a page's <em>text</em>
    /// would republish its unchanged diagrams without their width — an attribute that appeared and
    /// vanished depending on which unrelated edit came last, which is worse than never setting it.
    /// Absent for a page last published by a DocuMe that did not write widths; such a page picks one up
    /// the next time its diagram changes, or on a <c>--force</c>.
    /// </para>
    /// <para>
    /// It is always the published width, never a newer measurement the page has not shown: a diagram is
    /// re-rendered only by a run that also rewrites the body (a create, or the <c>--force</c> that
    /// re-uploads everything), because a diagram whose source is unchanged is never in the upload set.
    /// The flip side is the staleness <see cref="Publishing.DiagramImageWidth"/> describes — a renderer
    /// that lays the same source out differently is not re-measured until a <c>--force</c> asks it to be.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, string> DiagramWidths { get; init; }
        = new Dictionary<string, string>();

    /// <summary>
    /// The fingerprint of the source files this page's <c>sources</c> globs matched when the live body
    /// was published, and when it was sealed (docs/specs/2026-08-19-sealed-source-verdicts.md §3.2).
    /// </summary>
    /// <remarks>
    /// Owned by publish, like <see cref="ContentHash"/> beside it, and written at the same moment: the
    /// seal is a claim about the bytes a body was generated from, so it is only honest if the run that
    /// wrote it also wrote the body. Absent on every page published before this existed, and on a page no
    /// publish has yet been able to read the sources of — and that absence has to stay distinguishable
    /// from an empty corpus, which is why a page whose globs match no file seals
    /// <see cref="Drift.SourcesFingerprint.Of"/> of nothing rather than null. Read by
    /// <see cref="Drift.SealedVerdicts.Apply"/>, which holds a flagged page out of the drift report when
    /// this value still equals a fresh fingerprint of the same globs.
    /// </remarks>
    public SealedVerdict? Verdict { get; init; }

    public ApprovalState? Approval { get; init; }

    public bool Stale { get; init; }

    /// <summary>
    /// Whether the page carries the <c>docume</c> managed-marker property (§5.3,
    /// <see cref="Publishing.ManagedMarker"/>), stamped at create and self-healed on the next body
    /// update of a page published before the marker existed.
    /// </summary>
    /// <remarks>
    /// A cache of the stamp, never the authority: <c>--prune</c> reads the live property before every
    /// delete, so a hand-edited <c>true</c> here cannot delete an unstamped page. What the flag buys is
    /// the read it saves; a publish never has to ask Confluence whether a page is already stamped.
    /// Default false, which is also what every pre-marker state file says implicitly, and such a page
    /// is stamped by its next body update.
    /// </remarks>
    public bool Marked { get; init; }

    /// <summary>Newest comment already ingested (feedback loop cursor).</summary>
    public string? FeedbackCursor { get; init; }
}

/// <summary>
/// What a publish sealed for one page: the fingerprint of its <c>sources</c> at the moment it generated
/// the live body, when that was, and from which commit (spec §3.2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>It is not an approval, and it claims nothing about the prose.</strong> Approval is §8's
/// reviewer label and is untouched by this record. The seal says one thing only — these were the source
/// bytes when the body was published — and its whole purpose is to let a later <c>drift</c> run answer
/// "did the files this page derives from actually move?" instead of "were they inside the commit range?".
/// </para>
/// <para>
/// <see cref="RepoSha"/> is what makes a seal auditable rather than merely trusted. A publish from a
/// dirty working tree seals uncommitted bytes, which no fingerprint can detect; recording the commit the
/// run believed it was publishing is what lets somebody find that out afterwards. It is the run's sha
/// (<see cref="Publishing.PublishExecutionOptions.RepoSha"/>) and therefore absent for a publish outside
/// a git checkout, exactly as the provenance stamp on the page version is.
/// </para>
/// </remarks>
public sealed record SealedVerdict
{
    /// <summary>
    /// <see cref="Drift.SourcesFingerprint"/> of the matched files: <c>sha256:</c> + 64 lowercase hex,
    /// §5.3's one spelling. The empty-set value is a legitimate seal (globs that match no file), never a
    /// stand-in for "unknown".
    /// </summary>
    public string? SourcesHash { get; init; }

    /// <summary>When the seal was taken, ISO-8601 UTC to the second (<see cref="Sync.LabelReader.TimestampFormat"/>).</summary>
    public string? SealedAt { get; init; }

    /// <summary>The repo commit the sealing run published, or absent when it had none.</summary>
    public string? RepoSha { get; init; }
}

/// <summary>
/// The two values <see cref="ApprovalState.Status"/> takes (PLAN.md §5.3). Constants rather than
/// an enum because the state file spells them as-is and stays hand-readable; one place to compare
/// against keeps a typo from reading as "not approved" and silently skipping invalidation.
/// </summary>
public static class ApprovalStatus
{
    /// <summary>A reviewer's <c>approved</c> label was observed on the published version.</summary>
    public const string Approved = "approved";

    /// <summary>Approval was invalidated by a content change, or never granted (§8).</summary>
    public const string NeedsReview = "needs-review";
}

public sealed record ApprovalState
{
    /// <summary><c>approved</c> | <c>needs-review</c> (see PLAN.md §8, <see cref="ApprovalStatus"/>).</summary>
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
