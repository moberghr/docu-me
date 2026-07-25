namespace DocuMe.Core.Publishing;

/// <summary>
/// Which files a scoped publish run may write: <c>--changed-since &lt;sha&gt;</c> and
/// <c>--page &lt;path&gt;</c> (PLAN.md §6.2, last paragraph).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A scope narrows what is written, never what is known.</strong> The tree is still loaded
/// whole, every page still converts and every decision is still made; the scope only forces the pages
/// outside it to <see cref="State.PagePublishAction.Skip"/>. Filtering the tree walk instead would break
/// two things at once. <em>Orphans:</em> an orphan is a state entry whose file is gone, so a walk
/// narrowed to one page makes every other page in the wiki look deleted — and a later <c>--prune</c>
/// would offer to delete them. <em>The link map:</em> a relative <c>.md</c> link resolves to a page
/// title, so converting one page needs every page's title (§6.2 step 2, which is why the flag's spec
/// says "plus link-map rebuild").
/// </para>
/// <para>
/// <strong>A changed asset pulls in the pages that reference it.</strong> The scope holds file paths,
/// not pages, because an image whose bytes moved does not touch a byte of markdown: keying the scope off
/// markdown alone would make <c>--changed-since</c> the one publish that cannot ship a changed image.
/// Diagrams need no such rule — a mermaid attachment's filename is a hash of its source, so a changed
/// fence changes the page body too, and the page's own path already matches.
/// </para>
/// <para>
/// <strong>It does not narrow §7 either.</strong> A page the converter refuses still fails the run when
/// the scope excludes it: the repo is broken whichever pages this run was going to write, and a scoped
/// publish that tolerates an unconvertible page is how one stays broken. What the scope narrows is the
/// write set, and nothing else.
/// </para>
/// <para>
/// Paths are compared ordinally, like every other path in the tool (<c>state.json</c> keys, the link
/// map). A path whose case does not match the file system is therefore not a match — which is what makes
/// a mistyped <c>--page</c> a loud failure (<see cref="MissingFrom"/>) rather than a run that publishes
/// nothing and says it succeeded.
/// </para>
/// </remarks>
public sealed class PublishScope
{
    private readonly HashSet<string> _paths;

    private PublishScope(string description, IEnumerable<string> paths)
    {
        _paths = paths
            .Select(Normalize)
            .Where(path => path.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        Description = description;
        Paths = [.. _paths.Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// How the scope was asked for, spelled as the flag that produced it, so a report can name what
    /// narrowed the run instead of quietly printing smaller numbers.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// The wiki-root-relative paths in scope: normalized, de-duplicated and ordered. Both markdown pages
    /// and assets, since either can put a page in scope.
    /// </summary>
    public IReadOnlyList<string> Paths { get; }

    /// <summary><c>--page &lt;path&gt;</c>, repeatable: an explicit list of markdown paths.</summary>
    /// <param name="pagePaths">Wiki-root-relative markdown paths.</param>
    public static PublishScope ForPages(IEnumerable<string> pagePaths)
    {
        ArgumentNullException.ThrowIfNull(pagePaths);

        return new PublishScope("--page", pagePaths);
    }

    /// <summary>
    /// <c>--changed-since &lt;sha&gt;</c>: whatever
    /// <see cref="Git.GitRepository.ChangedFilesSinceAsync"/> reported, markdown and assets alike.
    /// </summary>
    /// <param name="sha">The commit the caller compared against, for <see cref="Description"/>.</param>
    /// <param name="changedPaths">Wiki-root-relative paths of the files that changed.</param>
    public static PublishScope ForFilesChangedSince(string sha, IEnumerable<string> changedPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);
        ArgumentNullException.ThrowIfNull(changedPaths);

        return new PublishScope($"--changed-since {sha}", changedPaths);
    }

    /// <summary>
    /// Whether a run may write this page: its own markdown path is in scope, or one of the assets it
    /// references is.
    /// </summary>
    /// <param name="pagePath">The page's wiki-root-relative markdown path.</param>
    /// <param name="attachments">
    /// Everything the page references now, from the plan. Diagrams are ignored: they carry no asset path,
    /// and a changed diagram already changes the page body.
    /// </param>
    public bool Includes(string pagePath, IEnumerable<PlannedAttachment> attachments)
    {
        ArgumentException.ThrowIfNullOrEmpty(pagePath);
        ArgumentNullException.ThrowIfNull(attachments);

        if (_paths.Contains(Normalize(pagePath)))
        {
            return true;
        }

        return attachments.Any(attachment =>
            attachment.AssetPath is { Length: > 0 } asset && _paths.Contains(Normalize(asset)));
    }

    /// <summary>
    /// The scope's paths that name nothing in <paramref name="knownPaths"/> — how a caller turns a
    /// typo'd <c>--page</c> into an error instead of a run that silently writes nothing.
    /// </summary>
    /// <param name="knownPaths">
    /// What the scope's paths are allowed to name. The tree's markdown paths for <c>--page</c>;
    /// <c>--changed-since</c> does not ask, because a changed file may legitimately be a deleted page, an
    /// asset, or something under <c>_meta/</c>.
    /// </param>
    public IReadOnlyList<string> MissingFrom(IEnumerable<string> knownPaths)
    {
        ArgumentNullException.ThrowIfNull(knownPaths);

        var known = knownPaths.Select(Normalize).ToHashSet(StringComparer.Ordinal);

        return [.. Paths.Where(path => !known.Contains(path))];
    }

    /// <summary>
    /// Spells a path the way the tree does: <c>/</c> separators, no leading <c>./</c> and no leading
    /// slash. A hand-typed <c>--page</c> and a path from git should not disagree over punctuation.
    /// </summary>
    private static string Normalize(string path)
    {
        var normalized = path.Trim().Replace('\\', '/');

        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.TrimStart('/');
    }
}
