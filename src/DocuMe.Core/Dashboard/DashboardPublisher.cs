using DocuMe.Core.Config;
using DocuMe.Core.Confluence;
using DocuMe.Core.Markdown;
using DocuMe.Core.State;
using DocuMe.Core.Status;
using DocuMe.Core.Sync;

namespace DocuMe.Core.Dashboard;

/// <summary>
/// One rendered dashboard, before anything is written (PLAN.md §6.5): the live label read, what it would
/// change in state, and the storage-format body.
/// </summary>
/// <param name="Read">The two CQL searches, for the counts a caller reports.</param>
/// <param name="Plan">
/// What the labels would change in state. Carried so a caller can say the page shows changes the state
/// file does not — <c>sync --labels</c> owns that file (§6.3), and neither of this type's callers writes it.
/// </param>
/// <param name="Reconciled">State with the observed labels applied, in memory only.</param>
/// <param name="Report">The status model the page renders from (§6.6 shares it).</param>
/// <param name="Body">The storage format to publish.</param>
public sealed record DashboardRender(
    LabelReadResult Read,
    LabelSyncPlan Plan,
    DocumeState Reconciled,
    StatusReport Report,
    string Body);

/// <summary>What one dashboard upsert did.</summary>
public enum DashboardUpsertOutcome
{
    /// <summary>No page carried the title, so one was created.</summary>
    Created,

    /// <summary>The page existed and the body differed, so a version was written.</summary>
    Updated,

    /// <summary>The page existed and the rendered body matched it, so no version was spent.</summary>
    Unchanged,

    /// <summary>The space key names nothing this account can see. Nothing was written.</summary>
    SpaceNotFound,
}

/// <summary>The outcome of one upsert, with the page it landed on when there was one.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="PageId">The dashboard page id, or <c>null</c> when nothing was found or created.</param>
/// <param name="Version">The page version after the call, or <c>null</c> when nothing was written.</param>
public sealed record DashboardUpsertResult(
    DashboardUpsertOutcome Outcome,
    string? PageId = null,
    int? Version = null);

/// <summary>
/// The two halves of <c>docume dashboard</c> (PLAN.md §6.5) as callable steps: render the page from state
/// plus the live labels, then find-or-create it by title and overwrite.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two callers, one page.</strong> §6.4 says <c>drift --mark</c> refreshes the dashboard, so the
/// flow cannot live inside the <c>dashboard</c> command: a second implementation of "render and upsert"
/// would drift from the first, and the symptom would be two commands publishing different bodies to the
/// same machine-owned page, each overwriting the other's.
/// </para>
/// <para>
/// <strong>It reports rather than prints.</strong> Both callers are CLI commands with their own voice, and
/// <see cref="DashboardUpsertOutcome.SpaceNotFound"/> is a result rather than an exception for the same
/// reason: a caller in the middle of a larger run (<c>--mark</c> has already written labels by then)
/// decides for itself whether a missing space ends the run.
/// </para>
/// <para>
/// <strong>Neither half writes the state file.</strong> The reconciled labels stay in memory: §6.3 owns
/// <c>state.json</c>, and a second writer of <c>approval</c> running on a different schedule is how two
/// runs end up disagreeing about who approved what.
/// </para>
/// <para>
/// <strong>Which means the unchanged-body skip needs <c>sync --labels</c> to have run.</strong> An
/// <c>approved</c> label state holds no approval for is reconciled fresh on every render and stamped with
/// that run's instant, so its date lands in the table above the provenance line and each run really is a
/// change (measured, iter46: two consecutive runs over a fixture with unrecorded approvals both write a
/// version; over one whose state already holds them the second is
/// <see cref="DashboardUpsertOutcome.Unchanged"/>). That is the design working — the page shows the
/// approval as of now either way — but a repo that never runs <c>sync</c> spends a page version per
/// dashboard run, and the cron pairing in §10 is what keeps it at a fixed point.
/// </para>
/// </remarks>
public static class DashboardPublisher
{
    /// <summary>The version message a dashboard write carries, so the page history says who wrote it.</summary>
    public const string VersionMessage = "docume dashboard";

    /// <summary>
    /// Reads the live labels, reconciles them in memory, and renders the page body (§6.5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A caller that just wrote a label knows more than the search does.</strong> The reconciler
    /// takes the live labels as the truth and clears a <see cref="PageState.Stale"/> flag the search does
    /// not confirm, which is exactly right for <c>sync --labels</c> — a reviewer taking a label off by hand
    /// is the case §6.3 exists to notice. It is wrong for <c>drift --mark</c>, which is itself the writer:
    /// the CQL search is index-backed, a label added moments earlier need not be in it yet, and reconciling
    /// against it would discard a fact the run holds a 200 for. The symptom was a dashboard reporting zero
    /// stale pages in the same run that labelled two and saved both to <c>state.json</c> — one command, two
    /// surfaces, disagreeing (§9.3 makes the dashboard the staleness surface).
    /// </para>
    /// <para>
    /// So <paramref name="justLabelledStale"/> is unioned in rather than the reconciliation being weakened.
    /// It carries only ids this run wrote and Confluence accepted; an already-marked page it skipped is
    /// deliberately left out, because there the search and the state flag disagreeing is the genuine
    /// divergence <c>sync</c> is meant to resolve.
    /// </para>
    /// </remarks>
    /// <param name="client">A Confluence client for the target site.</param>
    /// <param name="config">The loaded <c>docume.json</c>.</param>
    /// <param name="paths">Where the config, wiki and state live, for the page's provenance block.</param>
    /// <param name="tree">The wiki tree, which supplies the row per page.</param>
    /// <param name="state">State as loaded — or as a caller has just changed it in memory.</param>
    /// <param name="spaceKey">The space whose labels are read. A label search is scoped to one (§6.3).</param>
    /// <param name="observedAt">
    /// When the read happened. Supplied by the caller so the body is a function of its inputs, which is
    /// what makes the unchanged-body comparison in <see cref="UpsertAsync"/> possible at all.
    /// </param>
    /// <param name="justLabelledStale">
    /// Page ids this run has just added the <c>stale</c> label to, which the caller knows carry it because
    /// Confluence accepted the write moments ago. Unioned into the search's result before reconciling — see
    /// the remarks on why the search alone is not enough for a caller that is also the writer.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<DashboardRender> RenderAsync(
        ConfluenceClient client,
        DocumeConfig config,
        StatusPaths paths,
        WikiTree tree,
        DocumeState state,
        string spaceKey,
        DateTimeOffset observedAt,
        IReadOnlyCollection<string>? justLabelledStale = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(spaceKey);

        var read = await LabelReader
            .ReadAsync(client, config, state, spaceKey, observedAt, cancellationToken)
            .ConfigureAwait(false);

        var plan = LabelSyncPlanner.Plan(state, Observed(read.Observation, justLabelledStale));
        var reconciled = LabelSyncPlanner.Apply(state, plan);
        var report = StatusModel.Build(paths, config, tree, reconciled);
        var body = new DashboardPage { Report = report, GeneratedAt = observedAt }.Render();

        return new DashboardRender(read, plan, reconciled, report, body);
    }

    /// <summary>
    /// Find-or-create by title, then overwrite (§6.5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The page is found by title on every run rather than recorded in <c>state.pages</c>: it is
    /// machine-owned and has no markdown source, so a state entry would make it an orphan on the next
    /// publish, and <c>publish --prune</c> deletes orphans (rule §9.6).
    /// </para>
    /// <para>
    /// §6.5 says "full overwrite each run", and it is — with one deviation. The read asks for the body so
    /// an unchanged page can be left alone, because §6.4 refreshes this page on every <c>drift --mark</c>
    /// and a version per run would bury the page's real history under no-op revisions. The comparison is
    /// ordinal over <see cref="DashboardPage.WithoutProvenance"/>: exact bytes, minus the one line carrying
    /// the run's own timestamp. A body Confluence did not return compares against the empty string and so
    /// counts as changed.
    /// </para>
    /// </remarks>
    /// <param name="client">A Confluence client for the target site.</param>
    /// <param name="confluence">The <c>confluence</c> config section — <c>spaceId</c> and <c>rootPageId</c>.</param>
    /// <param name="spaceKey">The space the page lives in.</param>
    /// <param name="pageTitle">The dashboard page title (<c>dashboard.title</c>, §5.1).</param>
    /// <param name="body">The storage format from <see cref="RenderAsync"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<DashboardUpsertResult> UpsertAsync(
        ConfluenceClient client,
        ConfluenceConfig confluence,
        string spaceKey,
        string pageTitle,
        string body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(confluence);
        ArgumentException.ThrowIfNullOrWhiteSpace(spaceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(pageTitle);
        ArgumentNullException.ThrowIfNull(body);

        var spaceId = await ResolveSpaceIdAsync(client, confluence, spaceKey, cancellationToken)
            .ConfigureAwait(false);

        if (spaceId is null)
        {
            return new DashboardUpsertResult(DashboardUpsertOutcome.SpaceNotFound);
        }

        var existing = await client
            .FindPageByTitleAsync(spaceId, pageTitle, includeBody: true, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            var draft = new ConfluencePageDraft(spaceId, pageTitle, body, confluence.RootPageId);
            var created = await client.CreatePageAsync(draft, cancellationToken).ConfigureAwait(false);

            return new DashboardUpsertResult(DashboardUpsertOutcome.Created, created.Id, created.Version);
        }

        if (string.Equals(
                DashboardPage.WithoutProvenance(existing.Storage ?? string.Empty),
                DashboardPage.WithoutProvenance(body),
                StringComparison.Ordinal))
        {
            return new DashboardUpsertResult(
                DashboardUpsertOutcome.Unchanged,
                existing.Id,
                existing.Version);
        }

        var revision = new ConfluencePageRevision(
            existing.Id,
            pageTitle,
            body,
            existing.Version,
            VersionMessage: VersionMessage);

        var updated = await client.UpdatePageAsync(revision, cancellationToken).ConfigureAwait(false);

        return new DashboardUpsertResult(DashboardUpsertOutcome.Updated, updated.Id, updated.Version);
    }

    /// <summary>
    /// The search's observation, plus the pages the caller has just labelled itself.
    /// </summary>
    /// <remarks>
    /// Only the stale set moves: nothing writes an <c>approved</c> label, so there is never a caller-known
    /// approval the search could be behind on.
    /// </remarks>
    private static LabelObservation Observed(
        LabelObservation observation,
        IReadOnlyCollection<string>? justLabelledStale)
    {
        if (justLabelledStale is not { Count: > 0 })
        {
            return observation;
        }

        // Appended rather than merged in sort order: the search's own order is what an unmodified run
        // reconciles in. Distinct keeps first occurrences, so a page the search already returned is not
        // counted twice.
        var stale = observation.StalePageIds
            .Concat(justLabelledStale)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return observation with { StalePageIds = stale };
    }

    /// <summary>
    /// The numeric space id the v2 page endpoints want. A configured <c>confluence.spaceId</c> is trusted
    /// for the same reason <c>publish</c> trusts it: the config is committed and reviewed, and confirming
    /// it would cost a request per run to learn nothing.
    /// </summary>
    private static async Task<string?> ResolveSpaceIdAsync(
        ConfluenceClient client,
        ConfluenceConfig confluence,
        string spaceKey,
        CancellationToken cancellationToken)
    {
        if (confluence.SpaceId is { Length: > 0 } configured)
        {
            return configured;
        }

        var space = await client.FindSpaceByKeyAsync(spaceKey, cancellationToken).ConfigureAwait(false);

        return space?.Id;
    }
}
