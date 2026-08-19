namespace DocuMe.Core.Drift;

/// <summary>
/// The computed exemption: a pure pass over a <see cref="DriftReport"/> that holds out every reported
/// page whose source bytes are provably identical to the ones its live body was published against
/// (docs/specs/2026-08-19-sealed-source-verdicts.md §3.3).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why it is a second pass rather than a branch inside the matcher.</strong>
/// <see cref="DriftPlanner"/>'s doc comment leads with "No git in here", and the same discipline covers
/// the file system: a fingerprint is bytes read off a working tree, so a planner that computed one would
/// stop being testable without a repository behind it. The narrowing is therefore stamped on afterwards
/// by the caller that did the reading, exactly as <see cref="DriftReport.IgnoredCommitCount"/> is
/// (<c>src/DocuMe.Cli/Commands/DriftCommand.cs</c>, where the report is already post-processed).
/// </para>
/// <para>
/// <strong>A sealed page leaves <see cref="DriftReport.Pages"/>.</strong> It is not flagged in place,
/// because <see cref="DriftReport.HasDrift"/>, <see cref="DriftReport.AffectedCount"/>,
/// <c>--fail-on-drift</c> and <see cref="DriftMarkPlanner.Plan"/> all read that one list and so inherit
/// the exclusion by construction. A flag on <see cref="DriftedPage"/> would need those four readers to
/// agree with each other forever, and the first one that forgot would put a <c>stale</c> label on a page
/// the report in front of it calls sealed.
/// </para>
/// <para>
/// <strong>Missing is never sealed, and neither is the empty set.</strong> A path absent from either map
/// — no seal, or no current fingerprint because the sources could not be read — stays in
/// <see cref="DriftReport.Pages"/>, and so does a path whose recorded seal is
/// <see cref="SourcesFingerprint.EmptySet"/>. That is the safe direction and the whole of SC9: an
/// unreadable or unresolvable source tree must not suppress a drift report, since the one thing nobody
/// can verify is the report that was never printed.
/// </para>
/// <para>
/// <strong>What the seal claims.</strong> Only that these were the source bytes when the live body was
/// published. It is not an approval (§8, untouched), it says nothing about whether the prose is right,
/// and it is only as honest as the publish that wrote it — a publish from a dirty tree seals uncommitted
/// bytes, which is what <see cref="State.SealedVerdict.RepoSha"/> exists to make auditable after the
/// fact. Narrowed inputs are always disclosed, never silent, so the pages this pass removes are carried
/// on <see cref="DriftReport.Sealed"/> and rendered by every format
/// (<see cref="DriftComment"/>, <c>DriftCommand.RenderSealed</c>), the way the two declared exemptions
/// are (§6.4).
/// </para>
/// </remarks>
public static class SealedVerdicts
{
    /// <summary>
    /// Moves every page of <paramref name="report"/> whose current fingerprint equals its seal out of
    /// <see cref="DriftReport.Pages"/> and into <see cref="DriftReport.Sealed"/>.
    /// </summary>
    /// <param name="report">
    /// The report as <see cref="DriftPlanner.Plan"/> left it. Everything but the two page lists is
    /// carried through: the revisions and the two denominators describe the diff and the tree, which no
    /// seal changes, and <see cref="DriftReport.ChangedFileCount"/> keeps reporting the diff as git
    /// answered it for the reason <see cref="DriftReport.Exempted"/> does not decrement it either.
    /// </param>
    /// <param name="sealsByPath">
    /// Wiki-relative page path → the <see cref="State.SealedVerdict.SourcesHash"/> a publish recorded
    /// for it. Built from state, so it holds every page ever published and not only the reported ones;
    /// the extras are ignored.
    /// </param>
    /// <param name="currentByPath">
    /// Wiki-relative page path → the fingerprint of that page's <c>sources</c> as they are now
    /// (<see cref="SourcesFingerprint.Compute"/>). A page the caller could not fingerprint is left out
    /// rather than mapped to a sentinel: absence is the one spelling of "unknown" that cannot be
    /// mistaken for a value, and this pass treats it as "not sealed".
    /// </param>
    /// <param name="sealedAtByPath">
    /// Wiki-relative page path → when the seal was taken
    /// (<see cref="State.SealedVerdict.SealedAt"/>), for the disclosure. Optional, and trailing and
    /// defaulted for the reason <see cref="DriftPlanner.Plan"/>'s exemptions are: it narrows nothing and
    /// decides nothing, so a caller that omits it gets the same pages held out, each reported without a
    /// date. Forgetting it costs a reader the age of a verdict; it can never seal or unseal a page.
    /// </param>
    public static DriftReport Apply(
        DriftReport report,
        IReadOnlyDictionary<string, string> sealsByPath,
        IReadOnlyDictionary<string, string> currentByPath,
        IReadOnlyDictionary<string, string>? sealedAtByPath = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(sealsByPath);
        ArgumentNullException.ThrowIfNull(currentByPath);

        if (sealsByPath.Count == 0 || currentByPath.Count == 0)
        {
            // The common case, and the one this feature must cost nothing in: a wiki that has never
            // published under it seals nothing, so the report is the object the planner returned rather
            // than a copy of it.
            return report;
        }

        var reported = new List<DriftedPage>();
        var held = new List<SealedPage>();

        foreach (var page in report.Pages)
        {
            if (IsSealed(page.Path, sealsByPath, currentByPath))
            {
                held.Add(new SealedPage(page.Path, page.Title, Date(page.Path, sealedAtByPath)));

                continue;
            }

            reported.Add(page);
        }

        if (held.Count == 0)
        {
            return report;
        }

        return report with
        {
            Pages = reported,

            // Ordinal by path rather than trusting the order the pages arrived in, the way
            // DriftPlanner.ApplyExemptions orders the list it splits off: this one ends up in a PR
            // comment a bot rewrites in place, so it has to be a function of the inputs and nothing else.
            Sealed = [.. held.OrderBy(page => page.Path, StringComparer.Ordinal)],
        };
    }

    /// <summary>
    /// Whether both maps answer for <paramref name="path"/> with the same fingerprint, and that
    /// fingerprint is one a page can be held out on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ordinal, because the spelling is pinned as <c>sha256:</c> plus lowercase hex
    /// (<see cref="SourcesFingerprint"/>): a case-insensitive compare could only ever paper over a value
    /// this codebase did not write, and papering over it would seal a page against bytes nobody hashed.
    /// </para>
    /// <para>
    /// <strong><see cref="SourcesFingerprint.EmptySet"/> is never a match</strong>, even when both sides
    /// carry it (spec §3.1 as revised 2026-08-19). Publish no longer writes that value, but an older
    /// state file can carry one, and it is the one fingerprint that equals itself for a structural
    /// reason rather than an evidential one: a glob that matches nothing matches nothing again, and a
    /// sparse checkout scoped away from <c>src/</c> reproduces it exactly. Honouring it would hold a
    /// page out of <see cref="DriftReport.Pages"/> on the strength of bytes nobody read, which is the
    /// direction this whole pass exists to refuse. A page carrying one keeps the range-based answer and
    /// re-seals on its next publish.
    /// </para>
    /// </remarks>
    private static bool IsSealed(
        string path,
        IReadOnlyDictionary<string, string> sealsByPath,
        IReadOnlyDictionary<string, string> currentByPath) =>
        sealsByPath.TryGetValue(path, out var recorded)
        && currentByPath.TryGetValue(path, out var current)
        && !SourcesFingerprint.IsEmptySet(recorded)
        && string.Equals(recorded, current, StringComparison.Ordinal);

    private static string? Date(string path, IReadOnlyDictionary<string, string>? sealedAtByPath) =>
        sealedAtByPath?.GetValueOrDefault(path);
}
