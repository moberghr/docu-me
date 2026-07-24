using DocuMe.Core.Markdown;

namespace DocuMe.Core.Acceptance;

/// <summary>
/// The mermaid render pass: renders every diagram a corpus holds and reports which ones
/// <c>beautiful-mermaid</c> rejects (PLAN.md §4, §4.4).
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="ConversionAcceptance"/> and opt-in, for three reasons that are all the
/// same reason: rendering leaves the converter's world. It starts a Node process per diagram (§7
/// forbids the converter shelling out), it needs <c>node_modules</c> present, and it costs a
/// round-trip each — 59 of them on AurServices. Conversion stays a fast, dependency-free pass that
/// answers "does this page convert"; this answers "will its pictures exist".
/// </para>
/// <para>
/// It is still read-only. The rendered SVG is thrown away: what the pass keeps is the count, and
/// the upload belongs to the publish pipeline (§6.2 step 3).
/// </para>
/// </remarks>
public static class MermaidAcceptance
{
    /// <summary>
    /// Renders the diagrams <paramref name="report"/> collected and returns it with the render pass
    /// attached, so one report carries both halves of §4.4 and one bar covers them.
    /// </summary>
    public static async Task<AcceptanceReport> RenderDiagramsAsync(
        AcceptanceReport report,
        MermaidRenderer renderer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        var renders = await RunAsync(report.Diagrams, renderer, cancellationToken).ConfigureAwait(false);
        return report with { Renders = renders };
    }

    /// <summary>Renders every diagram in <paramref name="diagrams"/> and groups what failed.</summary>
    /// <param name="diagrams">
    /// The corpus's diagrams, normally <see cref="AcceptanceReport.Diagrams"/> — collected during
    /// conversion, because the diagram resolver already sees every fence the converter renders.
    /// </param>
    /// <param name="renderer">The renderer to run them through.</param>
    /// <param name="cancellationToken">Cancels between and during renders.</param>
    /// <exception cref="MermaidRenderException">
    /// Never for a diagram the renderer merely rejected — that is a report row. Still thrown for a
    /// failure of the setup rather than of a diagram (Node missing, script missing,
    /// <c>beautiful-mermaid</c> not installed), because those make every row meaningless and a run
    /// reporting "59 diagrams failed" would read as a corpus problem.
    /// </exception>
    public static async Task<DiagramRenderReport> RunAsync(
        IEnumerable<DiagramOccurrence> diagrams,
        MermaidRenderer renderer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diagrams);
        ArgumentNullException.ThrowIfNull(renderer);

        var occurrences = diagrams.ToList();

        // One render per DISTINCT source. The attachment name is a pure function of the source
        // (§8), so two fences with the same body publish one attachment, and a second Node
        // round-trip could not report anything the first did not.
        var sources = occurrences
            .Select(occurrence => occurrence.Source)
            .Distinct(StringComparer.Ordinal);

        var reasons = new Dictionary<string, string>(StringComparer.Ordinal);

        // Sequential on purpose: a bulk render is not the bottleneck of a docs pipeline, and
        // fanning out N Node processes buys seconds at the cost of a deterministic, readable run.
        foreach (var source in sources)
        {
            var reason = await RejectionReasonAsync(renderer, source, cancellationToken).ConfigureAwait(false);
            if (reason is not null)
            {
                reasons[source] = reason;
            }
        }

        return DiagramRenderReport.From(occurrences, reasons);
    }

    /// <summary>
    /// Renders one diagram and returns why it was rejected, or <c>null</c> when it rendered.
    /// </summary>
    private static async Task<string?> RejectionReasonAsync(
        MermaidRenderer renderer,
        string source,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await renderer.RenderAsync(source, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (MermaidRenderException ex) when (ex.Fault == MermaidRenderFault.Diagram)
        {
            // beautiful-mermaid quotes the header it refused ("Invalid mermaid header:
            // \"graph TD;\""), so normalizing the quotes collapses every rejected header into one
            // reason bucket while DiagramOccurrence.Dialect keeps the spellings apart.
            return QuotedTokens.Normalize(ex.Message, '"').Normalized;
        }
    }
}
