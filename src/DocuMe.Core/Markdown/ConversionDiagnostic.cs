namespace DocuMe.Core.Markdown;

/// <summary>
/// One <em>deliberate degradation</em> the converter applied while rendering a page: the
/// construct converted, but something the author would see on GitHub is not in the published
/// body. Existing so PLAN.md §4.4's acceptance bar ("all 79 AurServices pages convert with
/// zero errors and zero unknown-construct warnings") is measurable at all — without this
/// channel a page either throws or reports success, with no way to say "converted, but N
/// constructs silently degraded".
/// </summary>
/// <param name="Code">
/// Stable kebab-case identifier of the degradation kind, from
/// <see cref="ConversionDiagnosticCodes"/>. Stable because the §4.4 report groups by it and
/// decisions get recorded against it.
/// </param>
/// <param name="Construct">
/// The specific source spelling that triggered the degradation — the fence language token,
/// the anchor target, the degraded list's element name. This is the second grouping key a
/// §4.4 run needs: "how many pages, and which dialects/anchors".
/// </param>
/// <param name="Message">What was lost, and why, in one sentence an author can act on.</param>
/// <remarks>
/// A diagnostic is <strong>not</strong> a fail-loud site. Every construct that reports one
/// here converts today, on purpose, and was decided that way with a test pinning the emitted
/// XML — see the class remarks on <see cref="ConfluenceStorageRenderer"/>. Nor is every
/// accepted loss reported: a construct that degrades exactly the way GitHub itself renders it
/// (a nested GitHub alert keeping its visible marker, an unrecognized character reference
/// staying literal text, a non-root <c>[TOC]</c>) loses nothing relative to the source and
/// would only dilute the report.
/// </remarks>
public sealed record ConversionDiagnostic(string Code, string Construct, string Message);

/// <summary>
/// The <see cref="ConversionDiagnostic.Code"/> values the converter emits. Kebab-case
/// constants rather than an enum because these codes are report output and decision-log keys:
/// they are meant to be read in a §4.4 run's grouped output and grepped for afterwards.
/// </summary>
public static class ConversionDiagnosticCodes
{
    /// <summary>
    /// A fence language outside the renderer's brush map, so the <c>code</c> macro is emitted
    /// with no <c>language</c> parameter and the block publishes unhighlighted. The one purely
    /// cosmetic loss in the set, and the literal "unknown construct" PLAN.md §4.4 has in mind.
    /// </summary>
    public const string UnknownFenceLanguage = "unknown-fence-language";

    /// <summary>
    /// A list mixing task and plain items, which degrades to <c>&lt;ul&gt;</c>/<c>&lt;ol&gt;</c>
    /// with literal <c>[x]</c>/<c>[ ]</c> markers instead of a native
    /// <c>&lt;ac:task-list&gt;</c> — completion state stays readable but stops being tracked.
    /// </summary>
    public const string MixedTaskList = "mixed-task-list";

    /// <summary>
    /// A same-page <c>#anchor</c> link, which publishes as its link text with no link at all
    /// (spike S2's default). The largest loss in the set — a destination disappears — and the
    /// count that decides S2's recorded fallback ("rewrite anchor links to plain page links").
    /// </summary>
    public const string SamePageAnchorLink = "same-page-anchor-link";
}
