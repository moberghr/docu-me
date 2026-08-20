namespace DocuMe.Core.Markdown;

/// <summary>
/// YAML frontmatter of a wiki page (PLAN.md §5.2). The block is stripped from
/// the markdown before conversion; these fields drive drift detection
/// (<see cref="Sources"/>), the page title, and adoption of existing pages.
/// </summary>
public sealed record PageFrontmatter
{
    /// <summary>
    /// Code paths this page derives from. Each entry is a glob matched against
    /// changed files during drift detection (PLAN.md §6.4). Empty when absent.
    /// </summary>
    public IReadOnlyList<string> Sources { get; init; } = [];

    /// <summary>Optional title override. When absent, the title falls back to the first H1.</summary>
    public string? Title { get; init; }

    /// <summary>
    /// Confluence page id. Set by <c>publish</c>; may be pre-seeded when adopting
    /// an existing page. Kept as a string — Confluence ids overflow <c>int</c>.
    /// </summary>
    public string? PageId { get; init; }

    /// <summary>
    /// Who owns this page. Carried into the drift report so a drift finding has an addressee
    /// (<c>docs/specs/2026-08-20-page-owners.md</c> §3.1). Absent or blank means <c>null</c>, the same
    /// collapse <see cref="Title"/> and <see cref="PageId"/> get in
    /// <see cref="FrontmatterParser"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Carried verbatim, never normalized.</strong> Nothing here prepends <c>@</c>, folds case,
    /// trims beyond what YAML already did, validates the shape, or resolves the value against anything.
    /// </para>
    /// <para>
    /// That refusal is deliberate and it is the §9.5 line: who owns a page, and what a mention looks
    /// like on the forge that repo uses, is the consumer repo's knowledge, not the tool's. A tool that
    /// "helpfully" turned <c>alice</c> into <c>@alice</c> would notify whichever GitHub account happens
    /// to hold that name — a stranger, in the ordinary case where the repo's convention is an email or
    /// a display name. Silence is the safe failure: an owner written without the forge's mention syntax
    /// reaches the PR comment as plain text, pings nobody, and is visibly wrong to the person reading
    /// it. Do not "fix" this by normalizing.
    /// </para>
    /// </remarks>
    public string? Owner { get; init; }

    /// <summary>
    /// Whether this page may reach Confluence. <c>publish: false</c> marks a
    /// draft: a page committed to the tree that must not be published yet. The
    /// plan holds a draft back and reports it, and drift detection ignores it.
    /// Absence of the key means <c>true</c>, because publishing is the default
    /// contract of a file under the wiki root.
    /// </summary>
    public bool Publish { get; init; } = true;
}
