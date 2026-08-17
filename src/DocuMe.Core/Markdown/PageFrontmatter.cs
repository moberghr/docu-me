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
    /// Whether this page may reach Confluence. <c>publish: false</c> marks a
    /// draft: a page committed to the tree that must not be published yet. The
    /// plan holds a draft back and reports it, and drift detection ignores it.
    /// Absence of the key means <c>true</c>, because publishing is the default
    /// contract of a file under the wiki root.
    /// </summary>
    public bool Publish { get; init; } = true;
}
