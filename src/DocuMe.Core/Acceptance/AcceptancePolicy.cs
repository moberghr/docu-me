namespace DocuMe.Core.Acceptance;

/// <summary>
/// How much one reported degradation counts against PLAN.md §4.4's acceptance bar
/// ("all 79 AurServices pages convert with zero errors and zero unknown-construct warnings").
/// </summary>
public enum DiagnosticSeverity
{
    /// <summary>An unaccepted loss: it counts against the bar and a run reporting one fails.</summary>
    Warning,

    /// <summary>
    /// A loss this run has decided to accept. Still counted and printed — the count is the
    /// point — but it does not break the bar.
    /// </summary>
    Note,
}

/// <summary>
/// Which <see cref="Markdown.ConversionDiagnostic"/> codes an acceptance run treats as accepted
/// losses rather than warnings (PLAN.md §4.4).
/// </summary>
/// <remarks>
/// <para>
/// The severity lives here, on the run's policy, and deliberately not on the diagnostic itself:
/// the renderer knows <em>what</em> was lost, but whether losing it is acceptable is a decision
/// about a particular corpus and a particular release. Keeping it out of the converter means a
/// later decision to accept a loss is one entry in a policy, not a change to the converter, its
/// tests, or the shape of this report.
/// </para>
/// <para>
/// It exists from the runner's first version even though every code is a warning today, because
/// the converter has accepted losses that report nothing yet (table alignment dropped,
/// ordered-list start offset dropped, NOTE and IMPORTANT both mapping to <c>info</c>). Those
/// are real degradations worth counting, and some of them will be common enough that counting
/// them as warnings would make §4.4's second clause unreachable without new features nobody has
/// asked for. Demoting a code to <see cref="DiagnosticSeverity.Note"/> is how that gets decided
/// in the open, with the count still visible, instead of by not reporting the loss at all.
/// </para>
/// </remarks>
public sealed class AcceptancePolicy
{
    private readonly HashSet<string> _accepted;

    /// <param name="acceptedCodes">
    /// Diagnostic codes to report as notes (see <see cref="Markdown.ConversionDiagnosticCodes"/>).
    /// Unknown codes are allowed: accepting a code the current corpus never triggers is not an
    /// error, and rejecting it would make a policy brittle against renderer changes.
    /// </param>
    public AcceptancePolicy(IEnumerable<string> acceptedCodes)
    {
        ArgumentNullException.ThrowIfNull(acceptedCodes);
        _accepted = new HashSet<string>(acceptedCodes, StringComparer.Ordinal);
    }

    /// <summary>Accepts nothing: every reported degradation is a warning. The §4.4 default.</summary>
    public static AcceptancePolicy Strict { get; } = new([]);

    /// <summary>The accepted codes, ordinal-sorted so a report prints them deterministically.</summary>
    public IReadOnlyList<string> AcceptedCodes => [.. _accepted.Order(StringComparer.Ordinal)];

    /// <summary>The severity this policy assigns to <paramref name="code"/>.</summary>
    public DiagnosticSeverity SeverityOf(string code) =>
        _accepted.Contains(code) ? DiagnosticSeverity.Note : DiagnosticSeverity.Warning;
}
