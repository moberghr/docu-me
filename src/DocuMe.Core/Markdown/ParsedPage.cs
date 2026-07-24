namespace DocuMe.Core.Markdown;

/// <summary>
/// Result of splitting a wiki markdown file into its frontmatter, resolved
/// title, and frontmatter-free body (PLAN.md §5.2, §6.2 step 1). Produced by
/// <see cref="FrontmatterParser.Parse"/>; the body is what the converter renders.
/// </summary>
/// <param name="Frontmatter">Parsed frontmatter; defaults (empty sources, null overrides) when the block is absent.</param>
/// <param name="Title">
/// Resolved title: the frontmatter override, else the first H1's text, else
/// <c>null</c> when neither exists (the caller validates before publishing).
/// </param>
/// <param name="Body">The markdown with the frontmatter block removed. Still contains the H1.</param>
public sealed record ParsedPage(PageFrontmatter Frontmatter, string? Title, string Body);
