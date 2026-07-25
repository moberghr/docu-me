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
    /// <param name="pages">Every page in the tree. Pages declaring no <c>sources</c> are counted and skipped.</param>
    public static DriftReport Plan(
        string baseline,
        string head,
        IReadOnlyCollection<string> changedFiles,
        IReadOnlyCollection<WikiPage> pages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseline);
        ArgumentException.ThrowIfNullOrWhiteSpace(head);
        ArgumentNullException.ThrowIfNull(changedFiles);
        ArgumentNullException.ThrowIfNull(pages);

        var files = Normalize(changedFiles);
        var withSources = pages.Where(page => page.Parsed.Frontmatter.Sources.Count > 0).ToList();
        var matchesByPattern = MatchesByPattern(withSources, files);

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
            PageCount = pages.Count,
            PagesWithSourcesCount = withSources.Count,
            Pages = affected,
        };
    }

    /// <summary>
    /// Pattern → the files it matched, for every distinct pattern that matched at least one. Built once
    /// per run rather than per page: two pages documenting one subsystem share a glob, and one
    /// <see cref="Matcher"/> per pattern is what makes per-pattern attribution possible at all
    /// (<see cref="Matcher.Match(IEnumerable{string})"/> reports which files matched, never which
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
            var matcher = new Matcher(StringComparison.Ordinal);
            matcher.AddInclude(NormalizePattern(pattern));

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
    /// The two glob spellings <see cref="Matcher"/> would silently match nothing for, straightened out.
    /// </summary>
    /// <remarks>
    /// A trailing slash (<c>src/Loans/</c>) unambiguously means a directory, and a leading slash
    /// (<c>/src/Loans/**</c>) is the gitignore habit of anchoring to the repo root — which is where
    /// these patterns are anchored anyway. Left as written, either one matches no file ever, and a glob
    /// that can never fire is the one failure mode of an advisory check that gets believed: nobody
    /// investigates a green run. Nothing else is rewritten — a pattern with no wildcard names one file,
    /// per glob semantics, and guessing otherwise would make <c>sources</c> mean something different
    /// from <c>wiki.exclude</c>.
    /// </remarks>
    private static string NormalizePattern(string pattern)
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
