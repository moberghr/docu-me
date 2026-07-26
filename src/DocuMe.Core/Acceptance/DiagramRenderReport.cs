namespace DocuMe.Core.Acceptance;

/// <summary>One <c>```mermaid</c> fence: the page that holds it and its source verbatim.</summary>
/// <param name="Path">The page's wiki-root-relative path.</param>
/// <param name="Source">The fence body exactly as the author wrote it.</param>
public sealed record DiagramOccurrence(string Path, string Source)
{
    /// <summary>
    /// The diagram's first non-empty line, trimmed — <c>graph TD</c>, <c>sequenceDiagram</c>,
    /// <c>pie title Pets</c>. This is the "by dialect" axis of a render pass.
    /// </summary>
    /// <remarks>
    /// The whole line, not its first word, because the line is where the dialect gap lives:
    /// <c>beautiful-mermaid</c> 1.1.3 renders <c>graph TD</c> and rejects <c>graph TD;</c>, which
    /// mermaid.js and GitHub both accept, so a key that dropped the trailing semicolon would hide
    /// the one distinction the pass exists to count. The cost is that a header carrying a title
    /// (<c>pie title Pets</c>) buckets per title instead of per diagram type — visible in the
    /// report as several buckets of one, which still reads as "pie is unsupported".
    /// </remarks>
    public string Dialect => DialectOf(Source);

    internal static string DialectOf(string source) =>
        source
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0)
        ?? string.Empty;
}

/// <summary>One diagram that did not render.</summary>
/// <param name="Path">The page holding it.</param>
/// <param name="Dialect">Its <see cref="DiagramOccurrence.Dialect"/>.</param>
public sealed record DiagramFailure(string Path, string Dialect);

/// <summary>Every diagram the renderer rejected for the same reason.</summary>
/// <param name="Reason">
/// What the renderer said, with the quoted diagram header normalized out (see
/// <see cref="QuotedTokens"/>) so one rejected construct is one bucket regardless of dialect.
/// A grouping <em>key</em>, not prose — print <see cref="Detail"/> instead.
/// </param>
/// <param name="Detail">
/// The same message with only the quoted runs that actually differ across the group elided (see
/// <see cref="QuotedTokens.Common"/>). <see cref="Reason"/> has to elide every quoted run to stay a
/// stable key, which also erases quoted prose the group agrees on — and for the renderer that prose
/// is the list of headers it <em>does</em> accept, the one part of the message a reader can act on.
/// </param>
/// <param name="Occurrences">The rejected diagrams, in the order the pass saw them.</param>
public sealed record DiagramFailureGroup(
    string Reason,
    string Detail,
    IReadOnlyList<DiagramFailure> Occurrences)
{
    /// <summary>How many diagrams failed this way.</summary>
    public int Count => Occurrences.Count;

    /// <summary>How many distinct pages hold one.</summary>
    public int PageCount =>
        Occurrences.DistinctBy(occurrence => occurrence.Path, StringComparer.Ordinal).Count();

    /// <summary>The dialects that triggered it, most frequent first then ordinal.</summary>
    public IReadOnlyList<ConstructCount> ByDialect =>
        FailureGroup.Counts(Occurrences.Select(occurrence => occurrence.Dialect));
}

/// <summary>
/// The result of the mermaid render pass: which of a corpus's diagrams <c>beautiful-mermaid</c>
/// actually renders, grouped by reason and by dialect.
/// </summary>
/// <remarks>
/// <para>
/// This answers a question conversion structurally cannot. A page's diagram attachment name is a
/// pure function of the diagram source (§8), so <see cref="Markdown.WikiTree.DiagramResolver"/>
/// never fails and a page holding an unrenderable diagram converts perfectly clean — then fails at
/// publish. PLAN.md §4 line 128 calls the mermaid mechanism "Proven on AurServices (59 diagrams)";
/// this pass is what turns that claim into a count.
/// </para>
/// </remarks>
/// <param name="Diagrams">Every diagram the corpus holds, in the order the pass saw them.</param>
/// <param name="Failures">The rejected ones grouped by reason, most diagrams first.</param>
public sealed record DiagramRenderReport(
    IReadOnlyList<DiagramOccurrence> Diagrams,
    IReadOnlyList<DiagramFailureGroup> Failures)
{
    /// <summary>How many diagram fences the corpus holds.</summary>
    public int Count => Diagrams.Count;

    /// <summary>
    /// How many distinct diagram sources it holds — how many renders the pass actually ran, since
    /// two fences with identical source publish one attachment.
    /// </summary>
    public int DistinctCount =>
        Diagrams.Select(diagram => diagram.Source).Distinct(StringComparer.Ordinal).Count();

    /// <summary>How many diagram fences did not render.</summary>
    public int FailedCount => Failures.Sum(group => group.Count);

    /// <summary>How many pages hold at least one diagram that did not render.</summary>
    public int FailedPageCount =>
        Failures
            .SelectMany(group => group.Occurrences)
            .Select(occurrence => occurrence.Path)
            .Distinct(StringComparer.Ordinal)
            .Count();

    /// <summary>
    /// Every dialect in the corpus with its count, most frequent first — the census that turns 59
    /// diagrams into a handful of buckets, failures and successes alike.
    /// </summary>
    public IReadOnlyList<ConstructCount> ByDialect =>
        FailureGroup.Counts(Diagrams.Select(diagram => diagram.Dialect));

    /// <summary>
    /// Whether every diagram rendered. True for a corpus with no diagrams at all: nothing was
    /// rejected. (Contrast <see cref="AcceptanceReport.MeetsAcceptanceBar"/>, where an empty corpus
    /// deliberately does not clear the bar — there, pages are the subject.)
    /// </summary>
    public bool AllRendered => Failures.Count == 0;

    /// <summary>Groups <paramref name="diagrams"/> by the reason each was rejected, if it was.</summary>
    /// <param name="diagrams">Every diagram the corpus holds.</param>
    /// <param name="messagesBySource">
    /// The renderer's rejection message <em>verbatim</em> per rejected diagram source; a source
    /// absent from it rendered. Verbatim rather than pre-normalized so grouping and display can
    /// disagree about how much to elide: normalizing here is what makes one construct one bucket,
    /// and keeping the original is what lets <see cref="DiagramFailureGroup.Detail"/> hold on to the
    /// quoted prose the whole group shares. A caller that normalized first could not recover it.
    /// </param>
    public static DiagramRenderReport From(
        IReadOnlyList<DiagramOccurrence> diagrams,
        IReadOnlyDictionary<string, string> messagesBySource)
    {
        ArgumentNullException.ThrowIfNull(diagrams);
        ArgumentNullException.ThrowIfNull(messagesBySource);

        List<DiagramFailureGroup> failures =
        [
            .. diagrams
                .Where(diagram => messagesBySource.ContainsKey(diagram.Source))
                .GroupBy(
                    diagram => QuotedTokens.Normalize(messagesBySource[diagram.Source], RendererQuote).Normalized,
                    StringComparer.Ordinal)
                .Select(group => new DiagramFailureGroup(
                    group.Key,
                    Detail(group.Select(diagram => messagesBySource[diagram.Source])),
                    [.. group.Select(diagram => new DiagramFailure(diagram.Path, diagram.Dialect))]))
                .OrderByDescending(group => group.Count)
                .ThenBy(group => group.Reason, StringComparer.Ordinal),
        ];

        return new DiagramRenderReport(diagrams, failures);
    }

    /// <summary>
    /// The quote character <c>beautiful-mermaid</c> puts around the header it refused and around
    /// each header it accepts.
    /// </summary>
    private const char RendererQuote = '"';

    private static string Detail(IEnumerable<string> messages) =>
        QuotedTokens.Common([.. messages.Distinct(StringComparer.Ordinal)], RendererQuote);
}
