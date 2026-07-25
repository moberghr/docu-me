using DocuMe.Core.Markdown;
using DocuMe.Core.State;

namespace DocuMe.Core.Publishing;

/// <summary>Where an attachment's bytes come from (PLAN.md §6.2 steps 3 and 5).</summary>
public enum AttachmentKind
{
    /// <summary>A file committed in the wiki tree — an image an author placed and referenced.</summary>
    Asset,

    /// <summary>The SVG a <c>```mermaid</c> fence renders to (§6.2 step 3).</summary>
    Diagram,
}

/// <summary>
/// One attachment a page references now, carrying what an upload needs: the flat Confluence
/// filename, where the bytes come from, and their hash.
/// </summary>
/// <param name="Name">
/// The flat Confluence attachment filename — <see cref="WikiTree.FlattenToAttachmentName"/> for an
/// asset, <see cref="MermaidAttachmentName.ForSource"/> for a diagram. This is the key
/// <c>state.json</c> stores and the name the published body references.
/// </param>
/// <param name="Kind">Which of the two sources below is populated.</param>
/// <param name="AssetPath">
/// Wiki-root-relative path of the file to upload, for <see cref="AttachmentKind.Asset"/>;
/// <c>null</c> for a diagram.
/// </param>
/// <param name="DiagramSource">
/// The mermaid fence body to render, for <see cref="AttachmentKind.Diagram"/>; <c>null</c> for an
/// asset.
/// </param>
/// <param name="ContentHash">
/// Hash of the bytes that would be uploaded, or <c>null</c> for a diagram this offline plan has not
/// rendered — see <see cref="PublishPipeline"/> on why an unrendered diagram is always an upload
/// anyway.
/// </param>
public sealed record PlannedAttachment(
    string Name,
    AttachmentKind Kind,
    string? AssetPath,
    string? DiagramSource,
    string? ContentHash);

/// <summary>
/// One page's whole plan: the decision, the body a real run would upload, and the attachments it
/// references. What <c>--dry-run</c> prints and what the write path (§6.2 steps 5-8) executes.
/// </summary>
/// <param name="Path">Wiki-root-relative markdown path — the <see cref="DocumeState.Pages"/> key.</param>
/// <param name="Title">The resolved Confluence title (§6.2 step 1).</param>
/// <param name="ParentPath">
/// The markdown path of the page this one hangs under, or <c>null</c> for the tree root — which the
/// write path files under <c>confluence.rootPageId</c>. From <see cref="PageHierarchy"/>; a path
/// rather than a page id because a plan knows no ids, and the parent's id may not exist until the
/// same run creates it.
/// </param>
/// <param name="Plan">The decision, from <see cref="PublishPlanner.PlanPage"/>.</param>
/// <param name="UploadBody">
/// The storage format a real run would upload: the converter's output with the §8 banner injected
/// above it. <c>null</c> when the run writes no body (<see cref="PagePublishAction.Skip"/> and
/// <see cref="PagePublishAction.UpdateAttachments"/>). Deliberately <em>not</em> the hash preimage —
/// <see cref="PagePublishPlan.ContentHash"/> was taken before injection (§8, rule §9.2).
/// </param>
/// <param name="Attachments">
/// Every attachment the page references now, ordered by name. The upload subset is
/// <see cref="PagePublishPlan.ChangedAttachments"/>; the full set is what state records (§5.3).
/// </param>
/// <param name="Diagnostics">
/// Deliberate degradations the conversion applied (§4.4). Reported, never fatal.
/// </param>
public sealed record PlannedPage(
    string Path,
    string Title,
    string? ParentPath,
    PagePublishPlan Plan,
    string? UploadBody,
    IReadOnlyList<PlannedAttachment> Attachments,
    IReadOnlyList<ConversionDiagnostic> Diagnostics)
{
    /// <summary>Shorthand for <see cref="PagePublishPlan.Action"/>.</summary>
    public PagePublishAction Action => Plan.Action;

    /// <summary>
    /// True when a <see cref="PublishScope"/> forced this page to <see cref="PagePublishAction.Skip"/> —
    /// a page the run would otherwise have written.
    /// </summary>
    /// <remarks>
    /// Kept apart from an ordinary skip because the two mean opposite things to a reader: "nothing moved"
    /// versus "something moved and this run left it alone". A scoped run that printed the second as the
    /// first would read exactly like a full run, which is the failure mode of the whole feature.
    /// Deliberately false for a page that was going to skip anyway: the honest number is what the scope
    /// cost, not how many pages it named.
    /// </remarks>
    public bool ExcludedByScope { get; init; }
}

/// <summary>
/// A page the converter refused (its fail-loud contract, §7). It cannot publish, so the run reports
/// every such page and exits non-zero rather than publishing a partial wiki.
/// </summary>
/// <param name="Path">Wiki-root-relative markdown path.</param>
/// <param name="Message">The converter's message, naming the construct it will not convert.</param>
public sealed record PageConversionFailure(string Path, string Message);

/// <summary>
/// The plan for one publish run, computed with no network call and no write
/// (<see cref="PublishPipeline.Plan"/>). <c>--dry-run</c> prints it; a real run executes it.
/// </summary>
/// <param name="SpaceKey">The space the run targets (§5.1), echoed so the report names its target.</param>
/// <param name="GeneratedOn">The date the §8 banner records, one value for the whole run.</param>
/// <param name="Pages">Every page that converted, in tree order.</param>
/// <param name="Failures">Pages the converter refused.</param>
/// <param name="OrphanPages">
/// State entries whose markdown file is gone (§6.2 "Orphans"): reported by every run, deleted only
/// by a confirmed <c>--prune</c> (rule §9.6).
/// </param>
/// <param name="WriteRefusal">
/// Why a real run must not write, from <see cref="PublishGuard.WriteRefusal"/>, or <c>null</c>.
/// </param>
/// <param name="Scope">
/// The scope that narrowed the write set (<c>--changed-since</c>, <c>--page</c>), or <c>null</c> for a
/// whole-tree run. Carried so the report can name what narrowed it.
/// </param>
public sealed record PublishReport(
    string? SpaceKey,
    DateOnly? GeneratedOn,
    IReadOnlyList<PlannedPage> Pages,
    IReadOnlyList<PageConversionFailure> Failures,
    IReadOnlyList<string> OrphanPages,
    string? WriteRefusal,
    PublishScope? Scope = null)
{
    /// <summary>False when the target space is locked (<see cref="PublishGuard"/>).</summary>
    public bool CanWrite => WriteRefusal is null;

    /// <summary>
    /// True when a real run may proceed: every page converted and the target space is writable.
    /// The write path must check this; <c>--dry-run</c> reports it as the exit code.
    /// </summary>
    public bool CanPublish => Failures.Count == 0 && CanWrite;

    /// <summary>Pages with no <c>pageId</c> in state — the run creates them (§6.2 step 5).</summary>
    public int CreateCount => Count(PagePublishAction.Create);

    /// <summary>Pages whose body hash moved (or <c>--force</c>).</summary>
    public int UpdateCount => Count(PagePublishAction.Update);

    /// <summary>Pages whose body is unchanged but an attachment's bytes moved: no page version spent.</summary>
    public int AttachmentOnlyCount => Count(PagePublishAction.UpdateAttachments);

    /// <summary>
    /// Pages this run writes nothing to: nothing moved, or <see cref="Scope"/> held the page back.
    /// <see cref="ExcludedByScope"/> separates the two.
    /// </summary>
    public int SkipCount => Count(PagePublishAction.Skip);

    /// <summary>
    /// Pages <see cref="Scope"/> kept from being written that a whole-tree run would have written. Named
    /// rather than counted, so a scoped run can say exactly what it left alone.
    /// </summary>
    public IReadOnlyList<PlannedPage> ExcludedByScope => [.. Pages.Where(page => page.ExcludedByScope)];

    /// <summary>Attachment uploads across the run, counted per page (attachments are per page).</summary>
    public int UploadCount => Pages.Sum(page => page.Plan.ChangedAttachments.Count);

    /// <summary>
    /// Uploads whose bytes do not exist yet because the diagram has not been rendered. Informational:
    /// they are already counted in <see cref="UploadCount"/>.
    /// </summary>
    public int UnrenderedDiagramCount => Pages
        .Sum(page => page.Attachments.Count(attachment =>
            attachment.Kind == AttachmentKind.Diagram && attachment.ContentHash is null));

    /// <summary>
    /// Attachments state lists that the page no longer produces, counted per page. Reported, never
    /// deleted — see <see cref="PagePublishPlan.OrphanAttachments"/>.
    /// </summary>
    public int OrphanAttachmentCount => Pages.Sum(page => page.Plan.OrphanAttachments.Count);

    /// <summary>Deliberate degradations across the run (§4.4).</summary>
    public int DiagnosticCount => Pages.Sum(page => page.Diagnostics.Count);

    /// <summary>
    /// Pages whose write revokes an existing approval (§6.2 step 7, §8). The list a reviewer wants
    /// before a bulk republish, which is why <c>--dry-run</c> prints it by name rather than as a count.
    /// </summary>
    public IReadOnlyList<PlannedPage> InvalidatedApprovals =>
        [.. Pages.Where(page => page.Plan.InvalidatesApproval)];

    private int Count(PagePublishAction action) => Pages.Count(page => page.Action == action);
}
