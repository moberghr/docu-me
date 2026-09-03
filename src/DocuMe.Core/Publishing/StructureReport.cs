namespace DocuMe.Core.Publishing;

/// <summary>
/// A directory with publishable pages under it and no index page, so the tree skips a level there
/// (<c>docs/specs/2026-09-02-wiki-structure.md</c> §3.1).
/// </summary>
/// <remarks>
/// <strong>Under it, not only in it.</strong> A directory holding nothing directly is still a skipped
/// level when a page lives below it: with no <c>a/README.md</c>, <c>a/b/README.md</c> hangs off whatever
/// index is above <c>a</c>, and the section it should have sat in does not exist as a page. Counting only
/// direct pages would let the check fall silent on a tree that still skips levels — the same silence the
/// whole feature exists to end — so it reports every level, and
/// <see cref="DirectPageCount"/> is what separates "ten pages are loose here" from "this level is
/// missing".
/// </remarks>
/// <param name="Directory">
/// Wiki-root-relative directory path, <c>/</c>-separated and with no trailing slash. The empty string is
/// the wiki root itself, which is a directory like any other and is the one whose missing index page
/// files pages directly on the space root.
/// </param>
/// <param name="PageCount">Publishable pages anywhere beneath the directory, its own included.</param>
/// <param name="DirectPageCount">
/// Of those, the ones in the directory itself. Zero means the level is missing rather than crowded.
/// </param>
/// <param name="ResolvedParent">
/// Where this directory's index page would hang once somebody writes it, or <c>null</c> for the space
/// root. The half that makes the finding actionable: a level missing under the root index and one missing
/// under the space root are different problems, and only the second puts ten integrations pages in one
/// alphabetical pile.
/// </param>
/// <remarks>
/// It is a fact about the DIRECTORY, not about every page counted in <see cref="PageCount"/>. The
/// directory's own loose pages do resolve here — they have no nearer index — but a page further down
/// hangs under whatever index sits between, so reading this as "all of these pages are filed there" is
/// wrong whenever <see cref="DirectPageCount"/> is short of <see cref="PageCount"/>.
/// </remarks>
/// <param name="IndexPath">
/// The file to create, wiki-root-relative. Named rather than described because the AurServices fix was
/// seventeen <c>README.md</c> files and the reason nobody wrote them is that nothing ever asked for them
/// by name.
/// </param>
public sealed record OrphanedDirectory(
    string Directory,
    int PageCount,
    int DirectPageCount,
    string? ResolvedParent,
    string IndexPath);

/// <summary>
/// A parent with more children than <c>wiki.maxChildren</c> (§3.1).
/// </summary>
/// <param name="Parent">
/// The parent page's wiki-root-relative path, or <c>null</c> for the space root. Null is the whole
/// spelling: <c>DocumeJson</c> drops nulls, so a root finding carries no <c>parent</c> key at all and
/// "the space root" has exactly one representation on the wire, the way an unowned page has exactly one.
/// </param>
/// <param name="ChildCount">Pages filed directly under it.</param>
public sealed record WideParent(string? Parent, int ChildCount);

/// <summary>
/// The shape of the wiki tree: which directories hold pages nobody indexed, and which parents are too
/// wide to read (<c>docs/specs/2026-09-02-wiki-structure.md</c> §3.1).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Pure, and deliberately so.</strong> It takes the same two inputs
/// <see cref="PageHierarchy.Resolve"/> takes plus one number, so it needs no credentials, no network and
/// no state, and it runs under <c>--offline</c>. That is what lets <c>docume status</c> answer the
/// question on a laptop with no token, which is where somebody restructuring a wiki actually is.
/// </para>
/// <para>
/// <strong>It reports; it never proposes.</strong> <see cref="PageHierarchy"/> refuses to synthesize an
/// index page that does not exist, because inventing one would publish a page with no source, and that
/// refusal stands. This type names the file to write and stops there.
/// </para>
/// <para>
/// <strong>Two findings and no more.</strong> Nothing here counts depth, checks section names, or has an
/// opinion about what the top level should contain: those are a repo's editorial judgement and belong in
/// <c>_meta/STYLE.md</c> (rule §9.5), not in a lint.
/// </para>
/// </remarks>
public sealed record StructureReport
{
    private StructureReport(
        IReadOnlyList<OrphanedDirectory> orphanedDirectories,
        IReadOnlyList<WideParent> wideParents,
        int widestParentChildCount)
    {
        OrphanedDirectories = orphanedDirectories;
        WideParents = wideParents;
        WidestParentChildCount = widestParentChildCount;
    }

    /// <summary>Directories holding pages with no index page, in ordinal directory order.</summary>
    public IReadOnlyList<OrphanedDirectory> OrphanedDirectories { get; }

    /// <summary>Parents wider than the limit, in ordinal parent order with the space root first.</summary>
    public IReadOnlyList<WideParent> WideParents { get; }

    /// <summary>
    /// Children under the widest parent, whether or not it exceeds the limit — the number the check's
    /// one-line summary quotes. Zero for an empty tree.
    /// </summary>
    public int WidestParentChildCount { get; }

    /// <summary>Whether the tree's shape is worth a word.</summary>
    public bool HasFindings => OrphanedDirectories.Count > 0 || WideParents.Count > 0;

    /// <summary>
    /// Computes the report.
    /// </summary>
    /// <param name="pagePaths">
    /// Every publishable page's wiki-root-relative path, <c>/</c>-separated
    /// (<see cref="Markdown.WikiTree.Pages"/>) — the same set <see cref="PageHierarchy.Resolve"/> takes.
    /// </param>
    /// <param name="homePage">
    /// <c>wiki.homePage</c> (§5.1). Read through <see cref="PageHierarchy.IndexName"/> rather than
    /// re-derived here: a check that disagreed with the resolver about what an index page is called would
    /// report directories that are indexed.
    /// </param>
    /// <param name="maxChildren">
    /// <c>wiki.maxChildren</c> (<see cref="Config.WikiConfig.MaxChildren"/>). Strictly more than this is a
    /// finding, so a repo that sets the number to what it has is not told its own tree is too wide.
    /// </param>
    /// <param name="reIncludedPaths">
    /// Pages that are in the tree only because <c>wiki.extraPages</c> named them (§5.1). A directory whose
    /// every page is one of these is never an orphaned directory: to have needed re-including at all they
    /// must have been excluded, so the index page this check would ask for could not publish even if
    /// somebody wrote it, and advice that cannot be taken is worse than silence. They still count as
    /// children under whatever parents them, because in Confluence they really are.
    /// </param>
    /// <remarks>
    /// <strong>The wiki root is never exempt by that rule</strong>, however few pages a tree has and
    /// however all of them got in. <c>wiki.exclude</c> can hide a subtree; it cannot exclude the root the
    /// wiki is rooted at, so a missing root index is always a file somebody can usefully write. Exempting
    /// it would silence the finding on a wiki whose only page is a re-included one, and that wiki has no
    /// home page at all.
    /// </remarks>
    public static StructureReport Of(
        IEnumerable<string> pagePaths,
        string? homePage,
        int maxChildren,
        IReadOnlySet<string>? reIncludedPaths = null)
    {
        ArgumentNullException.ThrowIfNull(pagePaths);

        var paths = pagePaths.ToHashSet(StringComparer.Ordinal);
        var reIncluded = reIncludedPaths ?? new HashSet<string>(StringComparer.Ordinal);
        var indexName = PageHierarchy.IndexName(homePage);
        var parents = PageHierarchy.Resolve(paths, homePage);

        // The ancestor is computed for the DIRECTORY — where its index page would hang if somebody wrote
        // it — rather than read off one of its pages. Same answer for a directory whose pages are all
        // loose in it, and the only answer that means anything for one whose pages are further down.
        var orphaned = PagesByDirectory(paths)
            .Where(directory => !paths.Contains(IndexIn(directory.Key, indexName)))
            .Where(directory => directory.Key.Length == 0 || !directory.Value.All(reIncluded.Contains))
            .Select(directory => new OrphanedDirectory(
                directory.Key,
                directory.Value.Count,
                directory.Value.Count(page => DirectoryOf(page).Equals(directory.Key, StringComparison.Ordinal)),
                PageHierarchy.ParentOf(IndexIn(directory.Key, indexName), paths, homePage),
                IndexIn(directory.Key, indexName)))
            .OrderBy(directory => directory.Directory, StringComparer.Ordinal)
            .ToList();

        // Ordinal by parent with the root first, because null sorts before every string here and the root
        // is the parent a reader looking at a flat wiki is looking for.
        var widths = parents.Values
            .GroupBy(parent => parent, StringComparer.Ordinal)
            .Select(parent => new WideParent(parent.Key, parent.Count()))
            .OrderBy(parent => parent.Parent, StringComparer.Ordinal)
            .ToList();

        return new StructureReport(
            orphaned,
            widths.Where(parent => parent.ChildCount > maxChildren).ToList(),
            widths.Count == 0 ? 0 : widths.Max(parent => parent.ChildCount));
    }

    /// <summary>
    /// Every directory any page lives in or under, the wiki root included, mapped to the pages beneath it.
    /// </summary>
    /// <remarks>
    /// A page is recorded against its own directory and against every directory above it, so a level with
    /// nothing of its own still appears — which is the whole point: that level is exactly the one a tree
    /// skips silently.
    /// </remarks>
    private static Dictionary<string, List<string>> PagesByDirectory(IEnumerable<string> paths)
    {
        var beneath = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var path in paths)
        {
            // Walks up to and INCLUDING the wiki root: the root's own directory is the empty string, so the
            // loop records it and then stops, which is why the condition is on the previous step's value.
            var directory = DirectoryOf(path);
            var atRoot = false;

            while (!atRoot)
            {
                if (!beneath.TryGetValue(directory, out var pages))
                {
                    pages = [];
                    beneath[directory] = pages;
                }

                pages.Add(path);
                atRoot = directory.Length == 0;
                directory = DirectoryOf(directory);
            }
        }

        return beneath;
    }

    /// <summary>The index page of one directory, wiki-root-relative. <c>""</c> is the wiki root.</summary>
    private static string IndexIn(string directory, string indexName) =>
        directory.Length == 0 ? indexName : $"{directory}/{indexName}";

    /// <summary>The path's directory, <c>""</c> at the wiki root.</summary>
    private static string DirectoryOf(string path)
    {
        var slash = path.LastIndexOf('/');

        return slash < 0 ? string.Empty : path[..slash];
    }
}
