using System.Text.Json;
using DocuMe.Core.Json;

namespace DocuMe.Core.Drift;

/// <summary>
/// What one <c>docume drift</c> run found: which wiki pages derive from code this diff touched
/// (PLAN.md §6.4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Self-describing on purpose.</strong> The revisions and the two denominators
/// (<see cref="PageCount"/>, <see cref="PagesWithSourcesCount"/>) travel with the answer, because the
/// answer is usually "nothing drifted" and that sentence means something different in a wiki where
/// forty pages declare <c>sources</c> than in one where none do. A report that said only "0 affected"
/// would read as a clean bill of health in the one case where it is really a switched-off feature.
/// </para>
/// <para>
/// The changed-file list itself is deliberately not carried: a CI job already has the PR's file list,
/// and a diff of five thousand files would bloat every consumer's copy of it. What each affected page
/// matched <em>is</em> carried, per pattern, because that is the part a reviewer cannot reconstruct.
/// </para>
/// </remarks>
public sealed record DriftReport
{
    /// <summary>The revision the diff started from — <c>--baseline</c>, else <c>state.baselineSha</c>.</summary>
    public required string Baseline { get; init; }

    /// <summary>The revision the diff ended at — <c>--head</c>, else <c>HEAD</c>.</summary>
    public required string Head { get; init; }

    /// <summary>How many changed files the diff answered with, before any glob was applied.</summary>
    public required int ChangedFileCount { get; init; }

    /// <summary>
    /// Pages drift can see, whether or not they declare <c>sources</c>. A draft
    /// (<c>publish: false</c>, §5.2) is not published, so nothing a reader sees can be stale, and it
    /// counts toward no number in this report.
    /// </summary>
    public required int PageCount { get; init; }

    /// <summary>
    /// Pages declaring at least one <c>sources</c> glob (§5.2) — the only pages that can ever drift.
    /// </summary>
    public required int PagesWithSourcesCount { get; init; }

    /// <summary>The affected pages, ordered by wiki-relative path.</summary>
    public IReadOnlyList<DriftedPage> Pages { get; init; } = [];

    /// <summary>Affected pages.</summary>
    public int AffectedCount => Pages.Count;

    /// <summary>Whether any page is affected. What <c>--fail-on-drift</c> keys off.</summary>
    public bool HasDrift => Pages.Count > 0;

    /// <summary>
    /// Whether no page in the tree declares <c>sources</c> at all, which makes a zero result a
    /// statement about the frontmatter rather than about the diff. A tree with no visible pages,
    /// because it is empty or because every page is a draft, has no frontmatter to complain about,
    /// so it does not raise this.
    /// </summary>
    public bool SourcesUndeclared => PageCount > 0 && PagesWithSourcesCount == 0;

    /// <summary>
    /// The report as JSON (<c>--format json</c>) — the shape a CI step parses, so
    /// <see cref="DocumeJson.Options"/> as everywhere else: camelCase, indented, nulls dropped.
    /// </summary>
    public string ToJson() => JsonSerializer.Serialize(this, DocumeJson.Options);
}

/// <summary>One wiki page whose declared sources the diff touched.</summary>
/// <param name="Path">Wiki-root-relative markdown path — the key <c>state.json</c> uses (§5.3).</param>
/// <param name="Title">The page's resolved Confluence title, for a report a human reads.</param>
/// <param name="Matches">
/// Every pattern of this page's <c>sources</c> that matched something, with what it matched. Patterns
/// that matched nothing are absent: §6.4 asks for the matched patterns, and listing the misses would
/// bury the hit under the four globs that happened not to fire.
/// </param>
public sealed record DriftedPage(
    string Path,
    string Title,
    IReadOnlyList<SourceMatch> Matches)
{
    /// <summary>
    /// Distinct files behind this page's matches. De-duplicated because two of a page's globs can
    /// legitimately claim the same file, and a count that said "3 files" for two would be wrong in the
    /// direction that makes a change look bigger than it is.
    /// </summary>
    public int MatchedFileCount => Matches
        .SelectMany(match => match.Files)
        .Distinct(StringComparer.Ordinal)
        .Count();
}

/// <summary>One <c>sources</c> glob and the changed files it matched.</summary>
/// <param name="Pattern">The glob exactly as the page's frontmatter spells it, not as normalized.</param>
/// <param name="Files">The changed files it matched, ordinal-ordered by path.</param>
public sealed record SourceMatch(string Pattern, IReadOnlyList<string> Files);
