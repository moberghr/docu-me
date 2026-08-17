using DocuMe.Core.Markdown;
using Microsoft.Extensions.FileSystemGlobbing;

namespace DocuMe.Core.Drift;

/// <summary>
/// Matches a diff's changed files against every page's <c>sources</c> globs (PLAN.md §6.4): a pure
/// function from two lists to a <see cref="DriftReport"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No git in here.</strong> The changed files arrive as a list, exactly as
/// <see cref="Sync.LabelSyncPlanner"/> takes an observation rather than a client, so the whole matcher
/// is testable without a repository, a commit, or a process launch. <see cref="Git.GitRepository"/>
/// answers the one question this needs and the CLI hands the answer over.
/// </para>
/// <para>
/// <strong>The globbing is <see cref="Matcher"/>, the same engine <c>wiki.exclude</c> uses</strong>
/// (<see cref="WikiTree"/>). A second glob implementation would eventually disagree with the first
/// about what <c>**</c> means, and a page's <c>sources</c> and the tree's <c>exclude</c> are written by
/// the same hand in the same file. Ordinal, so matching is case-sensitive: git reports paths as it
/// stores them, and a case-folding match would fire on a repo where two paths differ only by case.
/// </para>
/// </remarks>
public static class DriftPlanner
{
    /// <summary>
    /// Which of <paramref name="pages"/> derive from something in <paramref name="changedFiles"/>.
    /// </summary>
    /// <param name="baseline">The revision the diff started from; carried into the report.</param>
    /// <param name="head">The revision the diff ended at; carried into the report.</param>
    /// <param name="changedFiles">
    /// Changed files as forward-slash paths relative to the repo root <c>sources</c> globs are written
    /// against — the directory holding <c>docume.json</c> (§5.1).
    /// </param>
    /// <param name="pages">
    /// Every page in the tree. Drafts (<c>publish: false</c>, §5.2) are invisible to every number in
    /// the report; of the rest, pages declaring no <c>sources</c> are counted and skipped.
    /// </param>
    /// <param name="exemptions">
    /// The parsed <c>_meta/drift-ignore</c>, or null when the wiki has none. An exempted file is
    /// invisible to every page's globs but stays in <see cref="DriftReport.ChangedFileCount"/>: the
    /// count keeps reporting the diff as git answered it, and <see cref="DriftReport.Exempted"/>
    /// accounts for the subset held out.
    /// </param>
    public static DriftReport Plan(
        string baseline,
        string head,
        IReadOnlyCollection<string> changedFiles,
        IReadOnlyCollection<WikiPage> pages,
        DriftExemptions? exemptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseline);
        ArgumentException.ThrowIfNullOrWhiteSpace(head);
        ArgumentNullException.ThrowIfNull(changedFiles);
        ArgumentNullException.ThrowIfNull(pages);

        var files = Normalize(changedFiles);
        var (matchable, exempted) = ApplyExemptions(files, exemptions);

        // Drafts drop out on the first line (§5.2): a draft is not published, so nothing a reader
        // sees can be stale. They are held out of the counts too, not just the matching, because the
        // two denominators print as one ratio and the undeclared-sources nag hangs off the second: a
        // draft with no sources is not a page missing them.
        var visible = pages.Where(page => page.Parsed.Frontmatter.Publish).ToList();
        var withSources = visible.Where(page => page.Parsed.Frontmatter.Sources.Count > 0).ToList();
        var matchesByPattern = MatchesByPattern(withSources, matchable);

        var affected = new List<DriftedPage>();

        // Path order, so the table, the PR comment and the JSON all read the same way twice — and so a
        // bot editing one comment in place produces no diff when the answer has not moved.
        foreach (var page in withSources.OrderBy(page => page.Path, StringComparer.Ordinal))
        {
            var matches = page.Parsed.Frontmatter.Sources
                .Where(pattern => matchesByPattern.ContainsKey(pattern))
                .Distinct(StringComparer.Ordinal)
                .Select(pattern => new SourceMatch(pattern, matchesByPattern[pattern]))
                .ToList();

            if (matches.Count > 0)
            {
                affected.Add(new DriftedPage(page.Path, page.Title, matches));
            }
        }

        return new DriftReport
        {
            Baseline = baseline,
            Head = head,
            ChangedFileCount = files.Count,
            PageCount = visible.Count,
            PagesWithSourcesCount = withSources.Count,
            Pages = affected,
            Exempted = exempted,
        };
    }

    /// <summary>
    /// Splits the diff into the files pages may match and the files <c>drift-ignore</c> holds out,
    /// each claimed by its first matching pattern as <see cref="DriftExemptions.Match"/> promises.
    /// The exemption comes off before any page looks, so a page can never drift on an exempted
    /// file, whichever of its globs would have claimed it. The exempted side is ordinal by path for
    /// the same reason the affected pages are: the list ends up in a PR comment a bot rewrites in
    /// place, so it has to be a function of the diff and nothing else.
    /// </summary>
    private static (List<string> Matchable, List<ExemptedChange> Exempted) ApplyExemptions(
        List<string> files,
        DriftExemptions? exemptions)
    {
        if (exemptions is null)
        {
            return (files, []);
        }

        var matchable = new List<string>();
        var exempted = new List<ExemptedChange>();

        foreach (var file in files)
        {
            if (exemptions.Match(file) is { } change)
            {
                exempted.Add(change);
            }
            else
            {
                matchable.Add(file);
            }
        }

        return (matchable, [.. exempted.OrderBy(change => change.Path, StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Pattern → the files it matched, for every distinct pattern that matched at least one. Built once
    /// per run rather than per page: two pages documenting one subsystem share a glob, and one
    /// <see cref="Matcher"/> per pattern is what makes per-pattern attribution possible at all
    /// (<see cref="MatcherExtensions.Match(Matcher, IEnumerable{string})"/> reports which files matched, never which
    /// pattern matched them).
    /// </summary>
    private static Dictionary<string, List<string>> MatchesByPattern(
        List<WikiPage> pages,
        List<string> files)
    {
        var matches = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        if (files.Count == 0)
        {
            return matches;
        }

        var patterns = pages
            .SelectMany(page => page.Parsed.Frontmatter.Sources)
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .Distinct(StringComparer.Ordinal);

        foreach (var pattern in patterns)
        {
            var matcher = BuildMatcher(pattern);

            // Ordinal-ordered rather than trusting Matcher's traversal order, which is not part of its
            // contract: this list ends up in a PR comment a bot rewrites in place, so it has to be a
            // function of the diff and nothing else.
            var matched = matcher.Match(files).Files
                .Select(file => file.Path)
                .Order(StringComparer.Ordinal)
                .ToList();
            if (matched.Count > 0)
            {
                matches[pattern] = matched;
            }
        }

        return matches;
    }

    /// <summary>
    /// One matcher, built the only way drift builds one for a repo-relative glob.
    /// <see cref="DriftExemptions"/> comes through here too: an exemption exists to cancel a
    /// <c>sources</c> match, and a second construction would eventually disagree with this one
    /// about which files that is.
    /// </summary>
    internal static Matcher BuildMatcher(string pattern)
    {
        var matcher = new Matcher(StringComparison.Ordinal);
        matcher.AddInclude(NormalizePattern(pattern));
        return matcher;
    }

    /// <summary>
    /// The two glob spellings <see cref="Matcher"/> would silently match nothing for, straightened out.
    /// </summary>
    /// <remarks>
    /// A trailing slash (<c>src/Loans/</c>) unambiguously means a directory, and a leading slash
    /// (<c>/src/Loans/**</c>) is the gitignore habit of anchoring to the repo root — which is where
    /// these patterns are anchored anyway. Left as written, either one matches no file ever, and a glob
    /// that can never fire is the one failure mode of an advisory check that gets believed: nobody
    /// investigates a green run. Nothing else is rewritten — a pattern with no wildcard names one file,
    /// per glob semantics, and guessing otherwise would make <c>sources</c> mean something different
    /// from <c>wiki.exclude</c>. Internal rather than private because <see cref="DriftExemptions.Parse"/>
    /// refuses, naming the line, any pattern this leaves empty.
    /// </remarks>
    internal static string NormalizePattern(string pattern)
    {
        var trimmed = pattern.Trim().Replace('\\', '/').TrimStart('/');

        return trimmed.EndsWith('/') ? trimmed + "**" : trimmed;
    }

    /// <summary>
    /// Changed files as the matcher wants them: forward slashes, no blanks, no duplicates. A rename
    /// shows up as two paths and both are kept — deleting a documented file is drift too.
    /// </summary>
    private static List<string> Normalize(IReadOnlyCollection<string> changedFiles) =>
    [
        .. changedFiles
            .Where(file => !string.IsNullOrWhiteSpace(file))
            .Select(file => file.Trim().Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal),
    ];
}
