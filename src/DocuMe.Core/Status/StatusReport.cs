using System.Text.Json;
using System.Text.Json.Serialization;
using DocuMe.Core.Json;
using DocuMe.Core.Publishing;

namespace DocuMe.Core.Status;

/// <summary>
/// How one page's published state compares to the repo right now (PLAN.md §6.6), mapped from the
/// decision <see cref="PublishPipeline.Plan"/> already made for it.
/// </summary>
/// <remarks>
/// A separate enum from <see cref="State.PagePublishAction"/> rather than the same one renamed: the
/// publish action is a verb aimed at a run ("update this page") while a status row is an observation
/// about the wiki ("this page drifted"), and the JSON names below are a consumed contract (§10) that
/// must not move the day an action is added for a reason status has no opinion about.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<StatusSync>))]
public enum StatusSync
{
    /// <summary>No <c>pageId</c> in state: the page exists in the repo and not in Confluence.</summary>
    [JsonStringEnumMemberName("unpublished")]
    Unpublished,

    /// <summary>Published, and nothing about it has moved since.</summary>
    [JsonStringEnumMemberName("in-sync")]
    InSync,

    /// <summary>The body differs from the one state recorded: a republish would rewrite the page.</summary>
    [JsonStringEnumMemberName("drifted")]
    Drifted,

    /// <summary>The body is unchanged but an attachment's bytes moved (§6.2 step 5).</summary>
    [JsonStringEnumMemberName("attachments")]
    AttachmentsChanged,

    /// <summary>The body is unchanged and the source tree now hangs the page under a different parent.</summary>
    [JsonStringEnumMemberName("moved")]
    Moved,
}

/// <summary>The verdict of one <c>doctor</c>-lite check (PLAN.md §6.6).</summary>
/// <remarks>
/// Declared in ascending severity so <see cref="StatusReport.WorstCheck"/> is a plain
/// <c>Max</c>. <see cref="NotChecked"/> ranks below <see cref="Ok"/> deliberately: a check that was
/// skipped is not a finding, and letting it outrank a healthy one would make every offline run look
/// worse than it is.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<StatusCheckOutcome>))]
public enum StatusCheckOutcome
{
    /// <summary>
    /// The check did not run — no credentials, or <c>--offline</c>. The zero value on purpose: a check
    /// nobody filled in reads as "unknown", never as "fine".
    /// </summary>
    [JsonStringEnumMemberName("not-checked")]
    NotChecked,

    /// <summary>Healthy.</summary>
    [JsonStringEnumMemberName("ok")]
    Ok,

    /// <summary>Worth reading, not fatal: orphans in state, a protected space, Node absent with no diagrams to render.</summary>
    [JsonStringEnumMemberName("warning")]
    Warning,

    /// <summary>Something is broken and a publish would hit it: an expired token, a page the converter refuses.</summary>
    [JsonStringEnumMemberName("problem")]
    Problem,
}

/// <summary>
/// One <c>doctor</c>-lite line (PLAN.md §6.6): a named question, its verdict, and the sentence a
/// human needs to act on it.
/// </summary>
/// <param name="Name">Short label, e.g. <c>node</c> or <c>confluence</c>.</param>
/// <param name="Outcome">The verdict.</param>
/// <param name="Detail">
/// What was found, in words. NEVER a credential: not the token, not the account email, not a
/// truncation of either (CLAUDE.md §0.3, rule §1.1).
/// </param>
public sealed record StatusCheck(string Name, StatusCheckOutcome Outcome, string Detail);

/// <summary>
/// One row of the status table: what the repo says about a page, what state records about it, and
/// the difference (PLAN.md §6.6, the per-page half of §6.5's table).
/// </summary>
/// <param name="Path">Wiki-root-relative markdown path — the <c>state.json</c> key.</param>
/// <param name="Title">The resolved Confluence title.</param>
/// <param name="Owner">
/// The page's <c>owner:</c> frontmatter (§5.2) exactly as the author wrote it, or <c>null</c> when it
/// declares none — the dashboard's Owner column, which answers "who do I ask about this page?" without
/// opening the repo (<c>docs/specs/2026-08-20-page-owners.md</c> §2). Read off the tree rather than out
/// of state, because ownership is a fact about the repo as it stands now: an owner changed in a commit
/// shows up before the next publish records anything. Carried, never interpreted — see
/// <see cref="Markdown.PageFrontmatter.Owner"/> for why prepending <c>@</c> would name a stranger.
/// </param>
/// <param name="Sync">How the published page compares to the repo.</param>
/// <param name="PageId">The Confluence page id, or <c>null</c> when the page has never been published.</param>
/// <param name="Url">
/// The page in Confluence, or <c>null</c> when it is unpublished or <c>confluence.baseUrl</c> is not a
/// usable absolute URL. §6.5's "link" column.
/// </param>
/// <param name="PublishedVersion">
/// The Confluence page version DocuMe last wrote, or <c>null</c> for an unpublished page. Never
/// <c>0</c>: state spells "no version" as zero, and a zero in a report reads like a real version.
/// </param>
/// <param name="AttachmentCount">Attachments the page references now — images plus rendered diagrams.</param>
/// <param name="Approval">
/// <c>approved</c> | <c>needs-review</c>, or <c>null</c> when state carries no approval record at all.
/// The three are distinct: the third means nothing has ever looked, which is not the same as a
/// reviewer having withheld approval.
/// </param>
/// <param name="ApprovedBy">Who approved the current version, or <c>null</c>.</param>
/// <param name="ApprovedAt">When, or <c>null</c>.</param>
/// <param name="ApprovedVersion">
/// The page version that was approved, or <c>null</c>. Worth reading next to
/// <paramref name="PublishedVersion"/>: an approval recorded against an older version is one a
/// republish already invalidated.
/// </param>
/// <param name="Stale">
/// Whether a source change has marked the page stale (§6.4). Read from state, so it stays
/// <c>false</c> until <c>docume drift --mark</c> writes it.
/// </param>
/// <param name="DiagnosticCount">Deliberate conversion degradations on this page (§4.4).</param>
public sealed record StatusPage(
    string Path,
    string Title,
    string? Owner,
    StatusSync Sync,
    string? PageId,
    string? Url,
    int? PublishedVersion,
    int AttachmentCount,
    string? Approval,
    string? ApprovedBy,
    string? ApprovedAt,
    int? ApprovedVersion,
    bool Stale,
    int DiagnosticCount);

/// <summary>
/// The whole of <c>docume status</c> (PLAN.md §6.6): the dashboard's data computed locally, plus the
/// <c>doctor</c>-lite checks. One model behind both the Spectre table and <c>--json</c>, so the two
/// cannot disagree.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It reports; it never writes.</strong> Not the state file, not Confluence, not even a
/// rendered diagram. §6.5's <c>dashboard</c> is the command that writes a page (M3); this one is a
/// terminal report and its only side effect is stdout.
/// </para>
/// <para>
/// <strong>What it cannot know, it leaves out.</strong> <see cref="NotYetAvailable"/> names the
/// columns §6.5 promises that this build cannot answer. A cell left empty for an unbuilt milestone is
/// honest; "0 open comments" from a build with no comment reader is a lie, and the whole point of a
/// status command is being believed.
/// </para>
/// </remarks>
public sealed record StatusReport
{
    /// <summary>Absolute path of the <c>docume.json</c> the report was built from.</summary>
    public required string ConfigPath { get; init; }

    /// <summary>Absolute path of the wiki root (<c>wiki.root</c> resolved against the config's directory).</summary>
    public required string WikiRoot { get; init; }

    /// <summary>Absolute path of the state file, whether or not it exists.</summary>
    public required string StatePath { get; init; }

    /// <summary>
    /// Whether <see cref="StatePath"/> was there. <c>false</c> means nothing has been published from
    /// this checkout yet, which is why every page then reads as <see cref="StatusSync.Unpublished"/>.
    /// </summary>
    public bool StateFileExists { get; init; }

    /// <summary>Target space key (<c>confluence.spaceKey</c>).</summary>
    public string? SpaceKey { get; init; }

    /// <summary>Target wiki base URL (<c>confluence.baseUrl</c>).</summary>
    public string? BaseUrl { get; init; }

    /// <summary>Parent page the wiki tree hangs under, or <c>null</c> for the space root.</summary>
    public string? RootPageId { get; init; }

    /// <summary>Commit the wiki content was last generated against (§5.3).</summary>
    public string? BaselineSha { get; init; }

    /// <summary>Commit of the last publish run (§5.3).</summary>
    public string? LastPublishedSha { get; init; }

    /// <summary>Every page in the tree, in publish order.</summary>
    public IReadOnlyList<StatusPage> Pages { get; init; } = [];

    /// <summary>Pages the converter refuses (§7). None of them can publish until they are fixed.</summary>
    public IReadOnlyList<PageConversionFailure> Failures { get; init; } = [];

    /// <summary>State entries whose markdown file is gone (§6.2 "Orphans").</summary>
    public IReadOnlyList<string> Orphans { get; init; } = [];

    /// <summary>The <c>doctor</c>-lite checks, in the order they should be read.</summary>
    public IReadOnlyList<StatusCheck> Checks { get; init; } = [];

    /// <summary>
    /// The shape of the tree (<c>docs/specs/2026-09-02-wiki-structure.md</c> §3.1): directories holding
    /// pages nobody indexed, and parents wider than <c>wiki.maxChildren</c>.
    /// </summary>
    /// <remarks>
    /// Carried structurally rather than only as the structure check's detail string, so a repo that wants
    /// to gate on tree shape can read it out of <c>--json</c> and <c>/docs-restructure</c> can plan from
    /// it. The check itself is a <see cref="StatusCheckOutcome.Warning"/> and never a
    /// <see cref="StatusCheckOutcome.Problem"/>: a flat tree publishes perfectly well, and what is wrong
    /// with it is a judgement about readers that DocuMe does not get to fail a build over.
    /// </remarks>
    public StructureReport? Structure { get; init; }

    /// <summary>
    /// Data §6.5's dashboard shows that this build cannot compute, and why. Derived from what the
    /// report actually holds, so an entry disappears once the milestone behind it lands.
    /// </summary>
    public IReadOnlyList<string> NotYetAvailable { get; init; } = [];

    /// <summary>Pages in the tree.</summary>
    public int PageCount => Pages.Count;

    /// <summary>Pages Confluence has (a <c>pageId</c> in state).</summary>
    public int PublishedCount => Pages.Count(page => page.PageId is not null);

    /// <summary>Pages in the repo and not in Confluence.</summary>
    public int UnpublishedCount => Count(StatusSync.Unpublished);

    /// <summary>Published pages nothing has moved.</summary>
    public int InSyncCount => Count(StatusSync.InSync);

    /// <summary>Published pages whose body no longer matches the repo.</summary>
    public int DriftedCount => Count(StatusSync.Drifted);

    /// <summary>Published pages whose attachments moved but whose body did not.</summary>
    public int AttachmentsChangedCount => Count(StatusSync.AttachmentsChanged);

    /// <summary>Published pages the source tree now files somewhere else.</summary>
    public int MovedCount => Count(StatusSync.Moved);

    /// <summary>Pages state records as approved.</summary>
    public int ApprovedCount => Pages.Count(page =>
        string.Equals(page.Approval, State.ApprovalStatus.Approved, StringComparison.Ordinal));

    /// <summary>Pages state records as awaiting review.</summary>
    public int NeedsReviewCount => Pages.Count(page =>
        string.Equals(page.Approval, State.ApprovalStatus.NeedsReview, StringComparison.Ordinal));

    /// <summary>
    /// Pages with no approval record at all. Counted separately from
    /// <see cref="NeedsReviewCount"/> because "nobody has looked" and "review was withdrawn" are
    /// different facts, and until §6.3's label sync runs every page is the first.
    /// </summary>
    public int UnrecordedApprovalCount => Pages.Count(page => page.Approval is null);

    /// <summary>Pages marked stale by §6.4.</summary>
    public int StaleCount => Pages.Count(page => page.Stale);

    /// <summary>
    /// Approved share of the tree, rounded down, or <c>null</c> for an empty wiki. Rounded down so
    /// "99%" can never mean "all but one page approved, and the last one is the API-keys page".
    /// </summary>
    public int? ApprovedPercent => PageCount == 0 ? null : ApprovedCount * 100 / PageCount;

    /// <summary>
    /// Whether the published wiki differs from the repo: any page unpublished, drifted, moved or
    /// carrying changed attachments, or any orphan in state. What <c>--fail-on-drift</c> keys off.
    /// </summary>
    /// <remarks>
    /// Converter failures are deliberately not drift. A page the converter refuses has never reached
    /// Confluence and cannot be out of step with it; it is a <see cref="StatusCheckOutcome.Problem"/>
    /// in <see cref="Checks"/>, which is where a reader looks for "this wiki cannot publish".
    /// </remarks>
    public bool HasDrift => Orphans.Count > 0 || Pages.Any(page => page.Sync != StatusSync.InSync);

    /// <summary>
    /// The most severe verdict any check reached, for a one-line headline. See
    /// <see cref="StatusCheckOutcome"/> on why a skipped check ranks below a healthy one.
    /// </summary>
    public StatusCheckOutcome WorstCheck => Checks.Count == 0
        ? StatusCheckOutcome.NotChecked
        : Checks.Max(check => check.Outcome);

    /// <summary>
    /// The report as JSON — the contract §10's skills paste into a PR body, so it is a published
    /// shape rather than a debug dump.
    /// </summary>
    /// <remarks>
    /// <see cref="DocumeJson.Options"/> on purpose: camelCase, indented, nulls dropped, exactly like
    /// every other file DocuMe writes. A reader that already parses <c>state.json</c> needs no second
    /// set of conventions.
    /// </remarks>
    public string ToJson() => JsonSerializer.Serialize(this, DocumeJson.Options);

    private int Count(StatusSync sync) => Pages.Count(page => page.Sync == sync);
}
