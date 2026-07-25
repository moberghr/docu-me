using System.Globalization;
using DocuMe.Core.Config;
using DocuMe.Core.Confluence;
using DocuMe.Core.Publishing;
using DocuMe.Core.State;

namespace DocuMe.Core.Sync;

/// <summary>
/// One label read (PLAN.md §6.3): what the two CQL searches saw, ready for
/// <see cref="LabelSyncPlanner.Plan"/>.
/// </summary>
/// <param name="Observation">The reconciler's input.</param>
/// <param name="TitlesByPageId">
/// Page id → title, from the search results. Carried alongside rather than inside
/// <see cref="LabelObservation"/> because the reconciler must not key on titles (see that type); this
/// is for naming an unmanaged page in a report rather than for matching one.
/// </param>
/// <param name="ApprovedCount">Pages the <c>approved</c> label search returned.</param>
/// <param name="StaleCount">Pages the <c>stale</c> label search returned.</param>
public sealed record LabelReadResult(
    LabelObservation Observation,
    IReadOnlyDictionary<string, string> TitlesByPageId,
    int ApprovedCount,
    int StaleCount);

/// <summary>
/// Reads the <c>approved</c> and <c>stale</c> labels out of a space (PLAN.md §6.3): two CQL searches,
/// plus a version read for each approved page whose search hit answered no version.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Shared by <c>sync</c> and <c>dashboard</c> on purpose.</strong> §6.5 renders the status page
/// "from state + live labels", which is the same observation §6.3 reconciles; two readers would
/// eventually observe differently, and the disagreement would show up as a dashboard contradicting the
/// state file it was rendered from.
/// </para>
/// <para>
/// <strong>It writes nothing</strong> — not to Confluence, not to disk. Deciding what an observation
/// means is <see cref="LabelSyncPlanner"/>'s job, and committing the result is the caller's (§6.3).
/// </para>
/// </remarks>
public static class LabelReader
{
    /// <summary>
    /// The <c>approvedAt</c> shape: ISO-8601 UTC to the second. No fractional part, because this string
    /// lands in a committed file that humans read in PR diffs.
    /// </summary>
    public const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    /// <summary>Runs the read.</summary>
    /// <param name="client">A Confluence client for the target site.</param>
    /// <param name="config">The loaded <c>docume.json</c> — <c>labels.approved</c> and <c>labels.stale</c> (§5.1).</param>
    /// <param name="state">Current state, for deciding which pages are worth a version request.</param>
    /// <param name="spaceKey">The space to search. A label search is scoped to one (§6.3).</param>
    /// <param name="observedAt">When the read happened; becomes <c>approvedAt</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<LabelReadResult> ReadAsync(
        ConfluenceClient client,
        DocumeConfig config,
        DocumeState state,
        string spaceKey,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(spaceKey);

        var approved = await client
            .SearchPagesByLabelAsync(spaceKey, config.Labels.Approved, cancellationToken)
            .ConfigureAwait(false);

        var stale = await client
            .SearchPagesByLabelAsync(spaceKey, config.Labels.Stale, cancellationToken)
            .ConfigureAwait(false);

        var versions = await VersionsAsync(client, approved, state, cancellationToken).ConfigureAwait(false);

        var observation = new LabelObservation(
            approved.Select(page => page.Id).ToArray(),
            stale.Select(page => page.Id).ToArray(),
            versions,
            observedAt.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture));

        return new LabelReadResult(observation, Titles(approved, stale), approved.Count, stale.Count);
    }

    /// <summary>
    /// Page id → the version current at observation time (§8), for the pages an approval may be recorded
    /// against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The search is asked for <c>expand=version</c>, which is where nearly every version should come
    /// from: one request per label rather than one per page. A hit that answered no version is read by
    /// id, and only if state manages it — a labelled page DocuMe does not publish is reported and
    /// skipped, so paying a request to learn its version would be paying for nothing.
    /// </para>
    /// <para>
    /// <strong>Not <c>state.publishedVersion</c> as a fallback.</strong> The two differ exactly when a
    /// human edited the page in a browser, which is the case §8's "version current at observation time"
    /// exists for; a page whose version cannot be established is left out of the map, and the reconciler
    /// then declines to restamp rather than recording a version nobody observed.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyDictionary<string, int>> VersionsAsync(
        ConfluenceClient client,
        IReadOnlyList<ConfluenceLabelledPage> approved,
        DocumeState state,
        CancellationToken cancellationToken)
    {
        var managed = PageHierarchy.PathsByPageId(state);
        var versions = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var page in approved)
        {
            if (page.Version is { } version)
            {
                versions[page.Id] = version;
                continue;
            }

            if (!managed.ContainsKey(page.Id))
            {
                continue;
            }

            var read = await client.FindPageByIdAsync(page.Id, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (read?.Version is { } current)
            {
                versions[page.Id] = current;
            }
        }

        return versions;
    }

    /// <summary>
    /// Page id → title, from the search results, so an unmanaged page can be named rather than only
    /// numbered. The reconciler carries no titles by design (see <see cref="UnmanagedLabelledPage"/>).
    /// </summary>
    private static Dictionary<string, string> Titles(
        IReadOnlyList<ConfluenceLabelledPage> approved,
        IReadOnlyList<ConfluenceLabelledPage> stale)
    {
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var page in approved.Concat(stale))
        {
            titles[page.Id] = page.Title;
        }

        return titles;
    }
}
