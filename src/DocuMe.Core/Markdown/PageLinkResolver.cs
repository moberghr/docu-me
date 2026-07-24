namespace DocuMe.Core.Markdown;

/// <summary>
/// Resolves a relative markdown link's path component (e.g. <c>../loans/README.md</c>,
/// exactly as written in the source, with any <c>#fragment</c> already stripped) to
/// the Confluence page title it maps to, or <c>null</c> when it resolves to no known
/// page (a broken cross-reference).
/// </summary>
/// <remarks>
/// The whole-tree path→title map that backs this is built by the publish pipeline
/// (M2, PLAN.md §6.2); the converter only consumes the lookup, so it stays a pure
/// text transform. Path normalization (resolving <c>../</c> against the page being
/// converted, matching against the wiki root) is the resolver's responsibility.
/// </remarks>
public delegate string? PageLinkResolver(string relativeMarkdownPath);
