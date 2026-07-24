using DocuMe.Core.Markdown;

namespace DocuMe.Core.Acceptance;

/// <summary>
/// What one page's conversion did: it either failed loud, or converted while applying
/// <paramref name="Diagnostics"/> deliberate degradations.
/// </summary>
/// <param name="Path">The page's wiki-root-relative path, as <see cref="WikiPage.Path"/> spells it.</param>
/// <param name="Failure">The fail-loud reason, or <c>null</c> when the page converted.</param>
/// <param name="Diagnostics">
/// The degradations reported while rendering, in render order. Non-empty on a failed page too:
/// the converter keeps the diagnostics it reported before the throw, so one pass tells a reader
/// "this page failed on X <em>and</em> degrades Y".
/// </param>
public sealed record PageConversionResult(
    string Path,
    ConversionFailure? Failure,
    IReadOnlyList<ConversionDiagnostic> Diagnostics)
{
    /// <summary>Whether the page produced storage format at all.</summary>
    public bool Succeeded => Failure is null;
}

/// <summary>
/// One page's fail-loud reason, split into a grouping key and the specific token that triggered it.
/// </summary>
/// <param name="Kind">
/// The failure's construct: its message with every quoted token replaced by <c>'…'</c>, so the
/// same rejected construct groups into one bucket regardless of which dialect, attribute or node
/// type appeared in it.
/// </param>
/// <param name="Token">
/// The first quoted token of the message — the fence dialect, the node type, the attribute name —
/// or <c>null</c> when the message quotes nothing. This is the "by dialect" axis of a §4.4 report.
/// </param>
/// <param name="Message">The full message, so nothing the renderer said is lost to grouping.</param>
/// <remarks>
/// Deriving both from the message is deliberate for this slice: the renderer's ~15 fail-loud
/// sites throw <see cref="NotSupportedException"/> with prose, and giving each a stable code
/// would be a converter change (and a re-review of its tests) rather than a reporting one. The
/// quoted token is already the thing every one of those messages interpolates, so it doubles as
/// the grouping key the report needs. If the grouping ever proves too coarse on a real corpus,
/// a structured failure code is the follow-up, and <see cref="Kind"/> is the only thing it moves.
/// </remarks>
public sealed record ConversionFailure(string Kind, string? Token, string Message);

/// <summary>How often one construct — a fence dialect, an anchor, a node type — occurred.</summary>
public sealed record ConstructCount(string Construct, int Count);

/// <summary>One page's occurrence inside a <see cref="FailureGroup"/>.</summary>
public sealed record FailureOccurrence(string Path, string? Token, string Message);

/// <summary>Every page that failed for the same reason, with the tokens that triggered it.</summary>
/// <param name="Kind">The shared <see cref="ConversionFailure.Kind"/>.</param>
/// <param name="Occurrences">The failing pages, ordered by path (ordinal).</param>
public sealed record FailureGroup(string Kind, IReadOnlyList<FailureOccurrence> Occurrences)
{
    /// <summary>How many pages failed this way.</summary>
    public int Count => Occurrences.Count;

    /// <summary>
    /// The distinct tokens that triggered it, most frequent first then ordinal. Empty when the
    /// message quotes nothing.
    /// </summary>
    public IReadOnlyList<ConstructCount> ByToken =>
        Counts(Occurrences.Select(occurrence => occurrence.Token));

    internal static IReadOnlyList<ConstructCount> Counts(IEnumerable<string?> constructs) =>
    [
        .. constructs
            .Where(construct => !string.IsNullOrEmpty(construct))
            .GroupBy(construct => construct!, StringComparer.Ordinal)
            .Select(group => new ConstructCount(group.Key, group.Count()))
            .OrderByDescending(count => count.Count)
            .ThenBy(count => count.Construct, StringComparer.Ordinal),
    ];
}

/// <summary>One page's occurrence inside a <see cref="DiagnosticGroup"/>.</summary>
public sealed record DiagnosticOccurrence(string Path, string Construct, string Message);

/// <summary>Every degradation reported under one diagnostic code, across the whole run.</summary>
/// <param name="Code">The shared <see cref="ConversionDiagnostic.Code"/>.</param>
/// <param name="Severity">What the run's <see cref="AcceptancePolicy"/> makes of that code.</param>
/// <param name="Occurrences">The occurrences, ordered by page path then render order.</param>
public sealed record DiagnosticGroup(
    string Code,
    DiagnosticSeverity Severity,
    IReadOnlyList<DiagnosticOccurrence> Occurrences)
{
    /// <summary>How many times this code fired.</summary>
    public int Count => Occurrences.Count;

    /// <summary>How many distinct pages it fired on.</summary>
    public int PageCount => Occurrences.DistinctBy(occurrence => occurrence.Path, StringComparer.Ordinal).Count();

    /// <summary>
    /// The distinct source spellings that triggered it, most frequent first then ordinal — the
    /// "by dialect" axis: which fence languages, which anchors, which list types.
    /// </summary>
    public IReadOnlyList<ConstructCount> ByConstruct =>
        FailureGroup.Counts(Occurrences.Select(occurrence => occurrence.Construct));
}

/// <summary>
/// The result of a PLAN.md §4.4 acceptance run: every page's outcome, plus the two grouped views
/// the milestone's acceptance question actually needs — which constructs fail loud and on how
/// many pages, and which dialects degrade and how often.
/// </summary>
/// <param name="Pages">Every page converted, ordered by path (ordinal).</param>
/// <param name="Failures">Failed pages grouped by construct, most pages first.</param>
/// <param name="Diagnostics">Degradations grouped by code, warnings first then most frequent.</param>
/// <param name="Policy">The policy that assigned the severities.</param>
public sealed record AcceptanceReport(
    IReadOnlyList<PageConversionResult> Pages,
    IReadOnlyList<FailureGroup> Failures,
    IReadOnlyList<DiagnosticGroup> Diagnostics,
    AcceptancePolicy Policy)
{
    /// <summary>How many pages the run converted.</summary>
    public int PageCount => Pages.Count;

    /// <summary>How many of them failed loud.</summary>
    public int FailedPageCount => Pages.Count(page => !page.Succeeded);

    /// <summary>How many degradations were reported in total.</summary>
    public int DiagnosticCount => Diagnostics.Sum(group => group.Count);

    /// <summary>How many of them the policy counts against the bar.</summary>
    public int WarningCount =>
        Diagnostics.Where(group => group.Severity == DiagnosticSeverity.Warning).Sum(group => group.Count);

    /// <summary>
    /// PLAN.md §4.4: every page converted with zero errors and zero unaccepted degradations.
    /// A run over an empty corpus does not clear the bar — there is nothing to accept.
    /// </summary>
    public bool MeetsAcceptanceBar => PageCount > 0 && FailedPageCount == 0 && WarningCount == 0;

    /// <summary>Groups <paramref name="pages"/> into the report's two views.</summary>
    public static AcceptanceReport From(IReadOnlyList<PageConversionResult> pages, AcceptancePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(policy);

        var ordered = pages.OrderBy(page => page.Path, StringComparer.Ordinal).ToList();

        List<FailureGroup> failures =
        [
            .. ordered
                .Where(page => page.Failure is not null)
                .GroupBy(page => page.Failure!.Kind, StringComparer.Ordinal)
                .Select(group => new FailureGroup(
                    group.Key,
                    [.. group.Select(page => new FailureOccurrence(page.Path, page.Failure!.Token, page.Failure!.Message))]))
                .OrderByDescending(group => group.Count)
                .ThenBy(group => group.Kind, StringComparer.Ordinal),
        ];

        List<DiagnosticGroup> diagnostics =
        [
            .. ordered
                .SelectMany(page => page.Diagnostics.Select(
                    diagnostic => new { page.Path, Diagnostic = diagnostic }))
                .GroupBy(entry => entry.Diagnostic.Code, StringComparer.Ordinal)
                .Select(group => new DiagnosticGroup(
                    group.Key,
                    policy.SeverityOf(group.Key),
                    [.. group.Select(entry => new DiagnosticOccurrence(
                        entry.Path, entry.Diagnostic.Construct, entry.Diagnostic.Message))]))
                .OrderBy(group => group.Severity)
                .ThenByDescending(group => group.Count)
                .ThenBy(group => group.Code, StringComparer.Ordinal),
        ];

        return new AcceptanceReport(ordered, failures, diagnostics, policy);
    }
}
