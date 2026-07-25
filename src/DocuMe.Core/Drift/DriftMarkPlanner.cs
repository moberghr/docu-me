using DocuMe.Core.State;

namespace DocuMe.Core.Drift;

/// <summary>
/// What <c>drift --mark</c> would write: the affected pages that can be labelled <c>stale</c>, the ones
/// already marked, and the ones there is no page to label (PLAN.md §6.4).
/// </summary>
/// <remarks>
/// Shaped like <see cref="Sync.LabelSyncPlan"/> for the same reason: a <c>--dry-run</c> prints this and a
/// real run applies exactly it, so what a human approves is what happens.
/// </remarks>
public sealed record DriftMarkPlan
{
    /// <summary>Pages to add the <c>stale</c> label to, in the report's order.</summary>
    public IReadOnlyList<DriftMark> ToLabel { get; init; } = [];

    /// <summary>
    /// Affected pages state already records as stale. Skipped rather than re-labelled — see
    /// <see cref="DriftMarkPlanner"/> on why the state flag is trusted here.
    /// </summary>
    public IReadOnlyList<DriftMark> AlreadyMarked { get; init; } = [];

    /// <summary>Affected pages with no Confluence page to label, with the reason each one has none.</summary>
    public IReadOnlyList<UnmarkablePage> Unmarkable { get; init; } = [];

    /// <summary>Label writes this plan would make.</summary>
    public int ChangeCount => ToLabel.Count;

    /// <summary>Whether the plan writes anything at all.</summary>
    public bool HasChanges => ToLabel.Count > 0;
}

/// <summary>One affected page and the Confluence page that carries it.</summary>
/// <param name="Path">Wiki-relative markdown path — the key <c>state.json</c> uses (§5.3).</param>
/// <param name="Title">The page title, for a report a human reads.</param>
/// <param name="PageId">The Confluence page id a publish recorded.</param>
public sealed record DriftMark(string Path, string Title, string PageId);

/// <summary>An affected page that cannot be labelled, and why.</summary>
/// <param name="Path">Wiki-relative markdown path.</param>
/// <param name="Title">The page title as the tree resolved it.</param>
/// <param name="Reason">
/// A sentence naming what is missing. Carried rather than an enum because the only consumer is a human
/// reading a report, and both reasons come out of the same absent publish.
/// </param>
public sealed record UnmarkablePage(string Path, string Title, string Reason);

/// <summary>
/// Joins a <see cref="DriftReport"/> onto <see cref="DocumeState"/> to decide which pages
/// <c>drift --mark</c> can label (PLAN.md §6.4). Pure: no client, no clock, no file.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is where state enters §6.4.</strong> <see cref="DriftPlanner"/> is deliberately pure over
/// (changed files, tree pages) and carries only the wiki-relative path, because matching globs has nothing
/// to do with what was published. The label write does: it needs a page id, and only state knows one. So
/// the join lives with the writer, and the matcher stays testable without a publish behind it.
/// </para>
/// <para>
/// <strong>A page state has never seen is reported, not an error.</strong> Declaring <c>sources</c> on a
/// page nobody has published yet is ordinary — a new domain page lands in the repo before its first
/// publish — and failing the run over it would make an advisory check fail for a reason that has nothing
/// to do with drift. There is simply no page to put a label on, so it is named and skipped.
/// </para>
/// <para>
/// <strong>The state flag is trusted for the skip.</strong> <c>sync --labels</c> reconciles
/// <see cref="PageState.Stale"/> from the live labels (§6.3), so a page state calls stale is a page
/// Confluence last showed as labelled; re-adding the label would be a request that changes nothing. If the
/// two do diverge — a reviewer takes the label off by hand — <c>sync</c> is what notices, clears the flag,
/// and lets the next <c>--mark</c> re-label. Marking cannot fix that divergence itself without reading
/// every affected page's labels back, which is a request per page to learn what §6.3 already knows.
/// </para>
/// </remarks>
public static class DriftMarkPlanner
{
    /// <summary>Why an affected page state has no entry for cannot be labelled.</summary>
    public const string NeverPublishedReason =
        "not in state.json — the page has never been published, so there is no page to label";

    /// <summary>Why an affected page with a state entry but no page id cannot be labelled.</summary>
    public const string NoPageIdReason =
        "state.json records the page but no pageId, so the last publish never completed for it";

    /// <summary>
    /// Which of <paramref name="report"/>'s affected pages can be marked, given
    /// <paramref name="state"/>.
    /// </summary>
    /// <param name="report">The drift report from this run (<see cref="DriftPlanner.Plan"/>).</param>
    /// <param name="state">Current state — the only source of page ids and the current stale flag.</param>
    public static DriftMarkPlan Plan(DriftReport report, DocumeState state)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(state);

        var toLabel = new List<DriftMark>();
        var alreadyMarked = new List<DriftMark>();
        var unmarkable = new List<UnmarkablePage>();

        // The report's order, which is path order (DriftPlanner): the dry-run listing, the terminal log
        // and the request sequence all read the same way twice.
        foreach (var page in report.Pages)
        {
            if (!state.Pages.TryGetValue(page.Path, out var recorded))
            {
                unmarkable.Add(new UnmarkablePage(page.Path, page.Title, NeverPublishedReason));

                continue;
            }

            if (recorded.PageId is not { Length: > 0 } pageId)
            {
                unmarkable.Add(new UnmarkablePage(page.Path, page.Title, NoPageIdReason));

                continue;
            }

            // The recorded title, not the tree's: the label goes on the page as published, and saying so
            // in the log is how a rename shows up as the two different names it really is.
            var mark = new DriftMark(page.Path, recorded.Title ?? page.Title, pageId);

            if (recorded.Stale)
            {
                alreadyMarked.Add(mark);

                continue;
            }

            toLabel.Add(mark);
        }

        return new DriftMarkPlan
        {
            ToLabel = toLabel,
            AlreadyMarked = alreadyMarked,
            Unmarkable = unmarkable,
        };
    }
}
