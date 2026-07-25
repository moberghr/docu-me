namespace DocuMe.Core.Publishing;

/// <summary>
/// Where every page hangs in the published page tree: markdown path → parent markdown path
/// (PLAN.md §6.2, "parents before children, depth-first").
/// </summary>
/// <remarks>
/// <para>
/// A directory's index page is its children's parent, and the index filename is the one
/// <c>wiki.homePage</c> names (§5.1, <c>README.md</c> by default) applied per directory rather than
/// only at the root: <c>a/b/page.md</c> hangs under <c>a/b/README.md</c>, which hangs under
/// <c>a/README.md</c>, up to the root index — whose parent is <c>confluence.rootPageId</c> and is
/// therefore reported as <c>null</c>.
/// </para>
/// <para>
/// A directory with no index page is skipped, not synthesized: with no <c>a/b/README.md</c>,
/// <c>a/b/page.md</c> hangs under <c>a/README.md</c>. Inventing an intermediate page would publish a
/// page no author wrote, and the repo is the source of truth (rule §9.1).
/// </para>
/// <para>
/// Pure and offline, which is the point of computing it here rather than in the write path:
/// <c>--dry-run</c> can print the tree a real run would build, and the executor is left with the one
/// thing it alone knows — mapping these paths onto Confluence page ids, including ids of parents it
/// created moments earlier in the same run.
/// </para>
/// </remarks>
public static class PageHierarchy
{
    /// <summary>The index filename assumed when <c>wiki.homePage</c> is absent (§5.1's own default).</summary>
    private const string DefaultIndexName = "README.md";

    /// <summary>
    /// Resolves the parent of every page in one pass.
    /// </summary>
    /// <param name="pagePaths">
    /// Every publishable page's wiki-root-relative path, <c>/</c>-separated
    /// (<see cref="Markdown.WikiTree.Pages"/>).
    /// </param>
    /// <param name="homePage">
    /// <c>wiki.homePage</c> (§5.1). Only its filename is used, because the same name marks the index
    /// of every directory; a value with directories in it therefore behaves as its last segment.
    /// </param>
    /// <returns>
    /// Path → parent path, where <c>null</c> means "the tree root": the executor files those under
    /// <c>confluence.rootPageId</c>.
    /// </returns>
    public static IReadOnlyDictionary<string, string?> Resolve(
        IEnumerable<string> pagePaths,
        string? homePage = null)
    {
        ArgumentNullException.ThrowIfNull(pagePaths);

        var paths = pagePaths.ToHashSet(StringComparer.Ordinal);
        var indexName = IndexName(homePage);

        return paths.ToDictionary(
            path => path,
            path => NearestIndexAbove(path, paths, indexName),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Resolves one page's parent: the nearest index page strictly above it, or <c>null</c> at the
    /// tree root.
    /// </summary>
    /// <param name="pagePath">The page's wiki-root-relative path.</param>
    /// <param name="pagePaths">Every publishable page's path — what makes a candidate parent real.</param>
    /// <param name="homePage">See <see cref="Resolve"/>.</param>
    public static string? ParentOf(string pagePath, IReadOnlySet<string> pagePaths, string? homePage = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(pagePath);
        ArgumentNullException.ThrowIfNull(pagePaths);

        return NearestIndexAbove(pagePath, pagePaths, IndexName(homePage));
    }

    private static string? NearestIndexAbove(string pagePath, IReadOnlySet<string> pagePaths, string indexName)
    {
        var directory = DirectoryOf(pagePath);
        var isIndex = string.Equals(FileNameOf(pagePath), indexName, StringComparison.Ordinal);

        // An index page's parent is the index ABOVE it: a directory page cannot parent itself, and
        // starting the walk in its own directory would find exactly itself.
        var search = isIndex ? Above(directory) : directory;

        while (search is not null)
        {
            var candidate = search.Length == 0 ? indexName : $"{search}/{indexName}";
            if (!string.Equals(candidate, pagePath, StringComparison.Ordinal) && pagePaths.Contains(candidate))
            {
                return candidate;
            }

            search = Above(search);
        }

        return null;
    }

    private static string IndexName(string? homePage)
    {
        var normalized = homePage?.Replace('\\', '/').Trim('/');

        return string.IsNullOrWhiteSpace(normalized) ? DefaultIndexName : FileNameOf(normalized);
    }

    /// <summary>The directory one level up, or <c>null</c> once the walk has passed the wiki root.</summary>
    private static string? Above(string? directory) =>
        string.IsNullOrEmpty(directory) ? null : DirectoryOf(directory);

    /// <summary>The path's directory, <c>""</c> at the wiki root.</summary>
    private static string DirectoryOf(string path)
    {
        var slash = path.LastIndexOf('/');

        return slash < 0 ? string.Empty : path[..slash];
    }

    private static string FileNameOf(string path)
    {
        var slash = path.LastIndexOf('/');

        return slash < 0 ? path : path[(slash + 1)..];
    }
}
