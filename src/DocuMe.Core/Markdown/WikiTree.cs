using DocuMe.Core.Config;
using Microsoft.Extensions.FileSystemGlobbing;

namespace DocuMe.Core.Markdown;

/// <summary>
/// The whole-tree view of a markdown wiki: every publishable page with its resolved title,
/// every asset with the flat Confluence attachment filename it will be uploaded under, and
/// the per-page resolvers the converter needs (PLAN.md §6.2 steps 1-2, the "link map").
/// </summary>
/// <remarks>
/// <para>
/// This is the layer that owns the filesystem and the tree, so
/// <see cref="ConfluenceStorageConverter"/> does not have to: the converter receives three
/// delegates and stays a pure text transform (§7). The split is why the golden-file suite can
/// pin converter behavior with hand-written lookups and why conversion is deterministic for
/// the §8 content hash.
/// </para>
/// <para>
/// Loading is eager and validating. A tree that cannot be published — a page with no title,
/// two pages claiming one Confluence title, two assets claiming one attachment filename —
/// throws <see cref="WikiTreeException"/> listing every problem at once, rather than
/// producing a map with holes that would surface later as a broken link.
/// </para>
/// </remarks>
public sealed class WikiTree
{
    private readonly Dictionary<string, WikiPage> _pagesByPath;
    private readonly Dictionary<string, string> _attachmentNames;

    private WikiTree(
        string root,
        IReadOnlyList<WikiPage> pages,
        IReadOnlyList<string> assets,
        Dictionary<string, WikiPage> pagesByPath,
        Dictionary<string, string> attachmentNames)
    {
        Root = root;
        Pages = pages;
        Assets = assets;
        _pagesByPath = pagesByPath;
        _attachmentNames = attachmentNames;
    }

    /// <summary>The wiki root directory this tree was loaded from.</summary>
    public string Root { get; }

    /// <summary>
    /// Every publishable page, ordered by wiki-root-relative path (ordinal). The order is
    /// explicit rather than filesystem-dependent so a publish run behaves identically on
    /// every machine; §6.2's parents-before-children walk derives from these paths.
    /// </summary>
    public IReadOnlyList<WikiPage> Pages { get; }

    /// <summary>
    /// Every non-markdown file in scope (images and anything else a page can reference),
    /// as wiki-root-relative paths ordered the same way. These are attachment candidates:
    /// the publish pipeline uploads the ones pages actually reference (§6.2 step 5).
    /// </summary>
    public IReadOnlyList<string> Assets { get; }

    /// <summary>
    /// The mermaid resolver, which needs no tree knowledge at all: the attachment filename
    /// is a pure function of the diagram source (<see cref="MermaidAttachmentName"/>).
    /// </summary>
    /// <remarks>
    /// Because naming never fails, conversion of a page with a mermaid fence always succeeds
    /// and a <em>render</em> failure surfaces later, at publish, where
    /// <see cref="MermaidRenderer"/> maps it loud. That is deliberate: the converter cannot
    /// know whether Node can render a diagram without shelling out, which §7 forbids it from
    /// doing. The converter's "resolver returned null" branch still guards callers that
    /// supply their own resolver.
    /// </remarks>
    public static MermaidDiagramResolver DiagramResolver => MermaidAttachmentName.ForSource;

    /// <summary>
    /// Walks <paramref name="wikiRoot"/> and builds the map (§6.2 steps 1-2).
    /// </summary>
    /// <param name="wikiRoot">
    /// The wiki root directory — <c>wiki.root</c> resolved against the consumer repo (§5.1).
    /// </param>
    /// <param name="wiki">
    /// The <c>wiki</c> config section: <c>exclude</c> globs, <c>extraPages</c> re-inclusions.
    /// Defaults to <see cref="WikiConfig"/>'s defaults when omitted.
    /// </param>
    /// <exception cref="DirectoryNotFoundException">The wiki root does not exist.</exception>
    /// <exception cref="WikiTreeException">The tree cannot be published as it stands.</exception>
    public static WikiTree Load(string wikiRoot, WikiConfig? wiki = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wikiRoot);
        wiki ??= new WikiConfig();

        if (!Directory.Exists(wikiRoot))
        {
            throw new DirectoryNotFoundException($"Wiki root directory not found: {wikiRoot}");
        }

        var errors = new List<string>();
        var titleOverrides = TitleOverrides(wiki, errors);
        var included = InScope(wikiRoot, wiki, errors);

        var pages = new List<WikiPage>();
        var assets = new List<string>();
        foreach (var path in included)
        {
            if (!IsMarkdown(path))
            {
                assets.Add(path);
                continue;
            }

            var page = ReadPage(wikiRoot, path, titleOverrides, errors);
            if (page is not null)
            {
                pages.Add(page);
            }
        }

        var pagesByPath = pages.ToDictionary(p => p.Path, StringComparer.Ordinal);
        var attachmentNames = AttachmentNames(assets, errors);
        ValidateUniqueTitles(pages, errors);

        if (errors.Count > 0)
        {
            throw new WikiTreeException(wikiRoot, errors);
        }

        return new WikiTree(wikiRoot, pages, assets, pagesByPath, attachmentNames);
    }

    /// <summary>
    /// The Confluence attachment filename an asset is uploaded under: its wiki-root-relative
    /// path with directory separators flattened to <c>_</c>, because Confluence attachments
    /// are flat per page and <c>images/sub/deep.png</c> cannot keep its directories.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A pure function of the asset's path alone, and that is the whole point (§8, rule §9.2).
    /// The filename lands in the published body and therefore in the page's
    /// <c>contentHash</c>, so the tempting alternative — keep the bare filename, disambiguate
    /// only when two assets collide — would let <em>adding an unrelated file elsewhere</em>
    /// rename an existing attachment, churn the hash of a page nobody edited, and revoke its
    /// approval. Here a name changes only when the asset itself moves, which changes the
    /// referencing markdown anyway.
    /// </para>
    /// <para>
    /// The cost is a longer name in Confluence's attachment list (<c>images_architecture.png</c>
    /// rather than <c>architecture.png</c>), and one residual ambiguity: a literal <c>_</c> in a
    /// directory or file name can make two distinct paths flatten to the same string
    /// (<c>a_b/c.png</c> and <c>a/b_c.png</c>). <see cref="Load"/> detects that and fails loud.
    /// </para>
    /// </remarks>
    public static string FlattenToAttachmentName(string assetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetPath);
        return assetPath.Replace('/', '_');
    }

    /// <summary>
    /// The title of the page at <paramref name="pagePath"/> (wiki-root-relative), or
    /// <c>null</c> when no page lives there. This is the link map proper.
    /// </summary>
    public string? TitleFor(string? pagePath) =>
        pagePath is not null && _pagesByPath.TryGetValue(pagePath, out var page) ? page.Title : null;

    /// <summary>
    /// The attachment filename for the asset at <paramref name="assetPath"/>
    /// (wiki-root-relative), or <c>null</c> when no such asset is in scope.
    /// </summary>
    /// <remarks>
    /// Unlike the static <see cref="FlattenToAttachmentName(string)"/> this answers <em>null</em>
    /// for a path that is not in the tree, which is what makes a broken image reference fail
    /// loud in the converter instead of publishing a dangling <c>ri:filename</c>.
    /// </remarks>
    public string? AttachmentNameFor(string? assetPath) =>
        assetPath is not null && _attachmentNames.TryGetValue(assetPath, out var name) ? name : null;

    /// <summary>
    /// The three converter lookups for the page at <paramref name="pagePath"/>.
    /// </summary>
    /// <remarks>
    /// Bound to the page because relative paths are relative to <em>the linking page's
    /// directory</em>: <c>../architecture/overview.md</c> means different things on different
    /// pages. Closing over the page keeps the delegate signatures — and therefore the 27
    /// reviewed goldens — unchanged.
    /// </remarks>
    /// <exception cref="ArgumentException">No page in this tree lives at that path.</exception>
    public PageResolvers ResolversFor(string pagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pagePath);
        if (!_pagesByPath.ContainsKey(pagePath))
        {
            throw new ArgumentException(
                $"No page at '{pagePath}' in the wiki tree at {Root}; resolvers must be bound to a page of this tree.",
                nameof(pagePath));
        }

        return new PageResolvers(
            link => TitleFor(ResolveAgainst(pagePath, link)),
            image => AttachmentNameFor(ResolveAgainst(pagePath, image)),
            DiagramResolver);
    }

    /// <summary>
    /// Resolves <paramref name="reference"/> — a link or image path exactly as the author
    /// wrote it on the page at <paramref name="fromPagePath"/> — to a wiki-root-relative
    /// path, or <c>null</c> when it cannot name a file inside the wiki.
    /// </summary>
    /// <remarks>
    /// Percent-escapes are decoded, so <c>my%20page.md</c> finds <c>my page.md</c>. A
    /// root-absolute reference (<c>/docs/wiki/x.md</c>) returns null: resolving it needs the
    /// repo root, which this layer deliberately does not know, and failing loud beats guessing.
    /// So does a reference that climbs above the wiki root — a page outside the wiki is not
    /// publishable, so there is no title to link to.
    /// </remarks>
    internal static string? ResolveAgainst(string fromPagePath, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.StartsWith('/'))
        {
            return null;
        }

        var slash = fromPagePath.LastIndexOf('/');
        List<string> segments = slash < 0 ? [] : [.. fromPagePath[..slash].Split('/')];

        foreach (var segment in Uri.UnescapeDataString(reference).Replace('\\', '/').Split('/'))
        {
            if (segment.Length == 0 || string.Equals(segment, ".", StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(segment, "..", StringComparison.Ordinal))
            {
                segments.Add(segment);
                continue;
            }

            if (segments.Count == 0)
            {
                return null;
            }

            segments.RemoveAt(segments.Count - 1);
        }

        return segments.Count == 0 ? null : string.Join('/', segments);
    }

    /// <summary>
    /// Every in-scope file as a wiki-root-relative, forward-slash path, ordinal-sorted:
    /// <c>wiki.exclude</c> globs applied, then <c>wiki.extraPages</c> added back (§5.1).
    /// </summary>
    private static List<string> InScope(string wikiRoot, WikiConfig wiki, List<string> errors)
    {
        var all = Directory
            .EnumerateFiles(wikiRoot, "*", SearchOption.AllDirectories)
            .Select(file => RelativePath(wikiRoot, file))
            .ToList();

        var matcher = new Matcher(StringComparison.Ordinal);
        matcher.AddInclude("**/*");

        // Dot-paths are tooling metadata, never wiki content — the same category as the default
        // `_meta/**`, and excluded STRUCTURALLY rather than by default config so that a consumer
        // who overrides `wiki.exclude` for their own reasons cannot lose it silently. Without this
        // a `.claude/` or `.vscode/` directory dropped anywhere under the wiki root publishes as
        // Confluence pages, and — worse — an untitled one fails Load for EVERY page, so a
        // `.github/PULL_REQUEST_TEMPLATE.md` (untitled by design, and in scope whenever wiki.root
        // is the repo root) would stop the whole publish. `wiki.extraPages` still re-includes one
        // deliberately, exactly as it re-includes an excluded `_meta` page.
        matcher.AddExclude("**/.*");
        matcher.AddExclude("**/.*/**");

        foreach (var pattern in wiki.Exclude)
        {
            matcher.AddExclude(pattern);
        }

        var kept = new SortedSet<string>(
            matcher.Match(all).Files.Select(match => match.Path),
            StringComparer.Ordinal);

        var existing = new HashSet<string>(all, StringComparer.Ordinal);
        foreach (var declared in wiki.ExtraPages.Select(extra => extra.Path))
        {
            if (string.IsNullOrWhiteSpace(declared))
            {
                errors.Add("wiki.extraPages contains an entry with no 'path'");
                continue;
            }

            var path = NormalizePath(declared);
            if (!existing.Contains(path))
            {
                errors.Add($"wiki.extraPages path '{declared}' does not exist under the wiki root");
                continue;
            }

            kept.Add(path);
        }

        return [.. kept];
    }

    /// <summary>Path → title map from <c>wiki.extraPages</c>; empty when none declare a title.</summary>
    private static Dictionary<string, string> TitleOverrides(WikiConfig wiki, List<string> errors)
    {
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var extra in wiki.ExtraPages)
        {
            if (string.IsNullOrWhiteSpace(extra.Path) || string.IsNullOrWhiteSpace(extra.Title))
            {
                continue;
            }

            var path = NormalizePath(extra.Path);
            if (!overrides.TryAdd(path, extra.Title))
            {
                errors.Add($"wiki.extraPages declares '{extra.Path}' more than once");
            }
        }

        return overrides;
    }

    private static WikiPage? ReadPage(
        string wikiRoot,
        string path,
        Dictionary<string, string> titleOverrides,
        List<string> errors)
    {
        var parsed = FrontmatterParser.Parse(File.ReadAllText(Path.Combine(wikiRoot, path.Replace('/', Path.DirectorySeparatorChar))));

        // An explicit extraPages title wins over the file's own: renaming a page for
        // publication is the reason that config exists (§5.1).
        var title = titleOverrides.GetValueOrDefault(path) ?? parsed.Title;
        if (title is null)
        {
            errors.Add($"'{path}' has no title: add a 'title:' to its frontmatter or an H1 heading");
            return null;
        }

        return new WikiPage(path, title, parsed);
    }

    /// <summary>
    /// Asset path → attachment filename, with flatten collisions reported rather than
    /// silently letting one asset overwrite another's upload.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Whole-tree on purpose, which costs an abort on a colliding pair no page references and no
    /// run would ever upload. Scoping it to referenced assets is not available here: references
    /// resolve per page, during conversion, long after <see cref="Load"/>. The only alternative is
    /// deferring the error to the first page that resolves one of the pair.
    /// </para>
    /// <para>
    /// That alternative is worse rather than merely later. The colliding name is kept by whichever
    /// path sorts first, so the losers get no entry here and <see cref="AttachmentNameFor"/>
    /// answers null for them — a file that exists on disk reported as a broken image reference, on
    /// a page whose author did nothing wrong, with the blame decided by an unrelated file's name.
    /// Order-dependence of exactly that kind is what <see cref="FlattenToAttachmentName"/> exists
    /// to prevent. Reported at load it is one accurate line naming both paths, in the same pass as
    /// every other tree problem, and the fix is to rename a file.
    /// </para>
    /// </remarks>
    private static Dictionary<string, string> AttachmentNames(List<string> assets, List<string> errors)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        // Case-insensitive on purpose: two names differing only by case cannot coexist in one
        // directory on macOS or Windows anyway, and Confluence is not reliably case-sensitive
        // about attachment filenames either. Over-reporting is the safe direction.
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var asset in assets)
        {
            var name = FlattenToAttachmentName(asset);
            if (owners.TryGetValue(name, out var owner))
            {
                errors.Add(
                    $"assets '{owner}' and '{asset}' both flatten to the attachment filename " +
                    $"'{name}'; rename one (an '_' in a directory or file name is what collides)");
                continue;
            }

            owners[name] = asset;
            names[asset] = name;
        }

        return names;
    }

    /// <summary>
    /// Confluence page titles are unique per space, so two pages claiming one title is a hard
    /// error (§6.2 step 1) — and it would make the link map ambiguous: <c>ri:content-title</c>
    /// is how a page link names its target.
    /// </summary>
    private static void ValidateUniqueTitles(List<WikiPage> pages, List<string> errors)
    {
        var duplicates = pages
            .GroupBy(page => page.Title, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1);

        foreach (var group in duplicates)
        {
            var paths = string.Join(", ", group.Select(page => $"'{page.Path}'"));
            errors.Add($"title '{group.Key}' is claimed by {group.Count()} pages ({paths}); titles must be unique in a Confluence space");
        }
    }

    private static bool IsMarkdown(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);

    /// <summary>A config-declared path in the tree's own spelling: forward slashes, no leading slash.</summary>
    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static string RelativePath(string wikiRoot, string file) =>
        Path.GetRelativePath(wikiRoot, file).Replace('\\', '/');
}
