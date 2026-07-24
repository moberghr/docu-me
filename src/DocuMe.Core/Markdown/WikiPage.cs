namespace DocuMe.Core.Markdown;

/// <summary>
/// One markdown page in the wiki tree, as discovered by <see cref="WikiTree.Load"/>
/// (PLAN.md §6.2 step 1).
/// </summary>
/// <param name="Path">
/// Wiki-root-relative path with forward slashes, e.g. <c>domains/loans/README.md</c> —
/// the key every relative link resolves to and the key <c>state.json</c> uses (§5.3).
/// </param>
/// <param name="Title">
/// The Confluence page title: the <c>wiki.extraPages</c> override, else the frontmatter
/// <c>title:</c>, else the first H1 (§5.2). Never null — a page with no resolvable title
/// fails the tree load loud.
/// </param>
/// <param name="Parsed">
/// The parsed file: frontmatter (drives §6.4 drift), the resolved title, and the
/// frontmatter-free body the converter renders.
/// </param>
public sealed record WikiPage(string Path, string Title, ParsedPage Parsed);
