using DocuMe.Core.State;

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

    /// <summary>
    /// The pages in publish order: every page after the page it hangs under, siblings in path order
    /// (PLAN.md §6.2, "parents before children, depth-first").
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Path order is not publish order,</strong> which is the whole reason this exists. A page
    /// is filed under an index page somewhere above it, and a name can perfectly well sort before that
    /// index: <c>10-domains/README.md</c> comes before <c>README.md</c> ordinally, because <c>'1'</c>
    /// comes before <c>'R'</c>. Publishing in path order would then try to file a child under a parent
    /// this run has not created yet, and the child fails for want of an id — on exactly the
    /// numeric-prefix convention §6.2 names as the way a repo expresses the order it wants.
    /// </para>
    /// <para>
    /// Siblings keep their path order, so the numeric prefixes still say what they mean; it is only the
    /// parent that is hoisted above them.
    /// </para>
    /// </remarks>
    /// <param name="parents">Path → parent path, from <see cref="Resolve"/>.</param>
    public static IReadOnlyList<string> PublishOrder(IReadOnlyDictionary<string, string?> parents)
    {
        ArgumentNullException.ThrowIfNull(parents);

        var childrenByParent = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var roots = new List<string>();

        foreach (var (path, parent) in parents.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (parent is null)
            {
                roots.Add(path);
                continue;
            }

            if (!childrenByParent.TryGetValue(parent, out var siblings))
            {
                siblings = [];
                childrenByParent[parent] = siblings;
            }

            siblings.Add(path);
        }

        var ordered = new List<string>(parents.Count);
        var pending = new Stack<string>(Enumerable.Reverse(roots));

        while (pending.Count > 0)
        {
            var path = pending.Pop();
            ordered.Add(path);

            if (!childrenByParent.TryGetValue(path, out var children))
            {
                continue;
            }

            // Pushed backwards so the first child is popped first: depth-first, siblings in path order.
            for (var index = children.Count - 1; index >= 0; index--)
            {
                pending.Push(children[index]);
            }
        }

        if (ordered.Count == parents.Count)
        {
            return ordered;
        }

        // Unreachable by construction — a parent is always an index page strictly above its child, so the
        // graph cannot cycle and every page is reachable from a root. Appended rather than trusted anyway:
        // a page silently dropped here is a page the publish never mentions again, which is far worse than
        // a page published in an order somebody has to look at.
        var seen = ordered.ToHashSet(StringComparer.Ordinal);
        ordered.AddRange(parents.Keys
            .Where(path => !seen.Contains(path))
            .OrderBy(path => path, StringComparer.Ordinal));

        return ordered;
    }

    /// <summary>
    /// Page id → wiki path for every page state has published: the reverse of what
    /// <see cref="DocumeState.Pages"/> stores, and what lets a recorded <c>parentPageId</c> be read
    /// back as a path.
    /// </summary>
    /// <param name="state">The loaded <c>_meta/state.json</c> (§5.3).</param>
    public static IReadOnlyDictionary<string, string> PathsByPageId(DocumeState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var paths = new Dictionary<string, string>(StringComparer.Ordinal);

        // Ordered so that a state file where two paths claim one id — corruption, not a tree — still
        // plans the same way twice. Reported nowhere and thrown for nowhere: a publish that cannot
        // even say what it would do is worse than one that picks the first path deterministically.
        foreach (var (path, page) in state.Pages.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (page.PageId is { Length: > 0 } id)
            {
                paths.TryAdd(id, path);
            }
        }

        return paths;
    }

    /// <summary>
    /// True when Confluence files a page somewhere the source tree no longer says — the reparent §6.2
    /// performs with a bodyless move.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Decided in paths, not page ids,</strong> which is what makes it a plan-time decision at
    /// all: the id of a parent page this same run creates does not exist until the run creates it
    /// (<c>a/README.md</c> added above pages that already exist), so an id comparison could only happen
    /// in the write path and <c>--dry-run</c> would have to guess. The executor still resolves the
    /// <em>target</em> id, exactly as it does for a create's parent.
    /// </para>
    /// <para>
    /// A recorded parent id no page in state owns counts as moved: the page is filed outside the tree
    /// DocuMe knows about — a hand reorganization, a re-pointed <c>confluence.rootPageId</c>, a parent
    /// recreated under a new id — and the repo is the source of truth (rule §9.1).
    /// </para>
    /// </remarks>
    /// <param name="current">
    /// The page's entry in state, or <c>null</c>. A page state has no id for is a create, which files
    /// itself under the right parent to begin with.
    /// </param>
    /// <param name="plannedParentPath">Where the tree puts it now (<see cref="Resolve"/>); <c>null</c> at the root.</param>
    /// <param name="pathsByPageId">From <see cref="PathsByPageId"/>.</param>
    /// <param name="rootPageId">
    /// <c>confluence.rootPageId</c> (§5.1) — the page the tree root hangs under, and therefore the id a
    /// page with no parent path is recorded against.
    /// </param>
    public static bool ParentMoved(
        PageState? current,
        string? plannedParentPath,
        IReadOnlyDictionary<string, string> pathsByPageId,
        string? rootPageId)
    {
        ArgumentNullException.ThrowIfNull(pathsByPageId);

        if (current?.PageId is not { Length: > 0 })
        {
            return false;
        }

        if (current.ParentPageId is not { Length: > 0 } recorded
            || string.Equals(recorded, rootPageId, StringComparison.Ordinal))
        {
            return plannedParentPath is not null;
        }

        if (!pathsByPageId.TryGetValue(recorded, out var recordedPath))
        {
            return true;
        }

        return !string.Equals(recordedPath, plannedParentPath, StringComparison.Ordinal);
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
