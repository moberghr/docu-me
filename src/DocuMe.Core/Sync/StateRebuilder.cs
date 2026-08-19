using System.Net;
using DocuMe.Core.Confluence;
using DocuMe.Core.Publishing;
using DocuMe.Core.State;

namespace DocuMe.Core.Sync;

/// <summary>
/// What one stamped page means for the state file being rebuilt (PLAN.md §6.3 <c>--rebuild-state</c>,
/// docs/specs/2026-08-19-state-rebuild.md).
/// </summary>
public enum RebuildDisposition
{
    /// <summary>
    /// The marker's path names a file this repo has and state makes no claim on it, so an entry was
    /// written: page id, title, marked — and no content hash, so the next publish re-records it.
    /// </summary>
    Adopted,

    /// <summary>State already maps the path to this page. Nothing written.</summary>
    AlreadyTracked,

    /// <summary>
    /// The records disagree: state maps the path to a different page id, or two stamped pages claim
    /// the same path. Nothing written — the note says exactly what disagrees, and a human decides.
    /// </summary>
    Conflicted,

    /// <summary>
    /// The stamped path names no file under the wiki root. Nothing written, which is the safe side:
    /// a prune cannot touch what state does not hold. Listed so a human can decide.
    /// </summary>
    PathMissing,
}

/// <summary>One line of the adoption manifest: a stamped page and what the rebuild made of it.</summary>
/// <param name="Path">The wiki-relative path the marker names — the key state.json uses.</param>
/// <param name="PageId">The Confluence page carrying the marker.</param>
/// <param name="Title">Its title, so a human reading the manifest can recognize the page.</param>
/// <param name="Disposition">The verdict.</param>
/// <param name="Note">What disagrees or what is missing, for the verdicts that need saying; else null.</param>
public sealed record RebuildEntry(
    string Path,
    string PageId,
    string Title,
    RebuildDisposition Disposition,
    string? Note);

/// <summary>
/// What one rebuild walk found (docs/specs/2026-08-19-state-rebuild.md).
/// </summary>
/// <param name="Entries">One entry per stamped page, ordinal by path.</param>
/// <param name="UnstampedCount">
/// Pages with no marker, or a foreign value under the marker's key. Counted and never listed
/// page-by-page: a shared space can hold thousands that are none of this repo's business.
/// </param>
/// <param name="SkippedVanishedCount">
/// Pages that vanished between the listing and the property read — the read's 404. Skipped, because a
/// page that no longer exists has nothing to adopt.
/// </param>
/// <param name="State">State with every adoption applied. The caller persists it.</param>
/// <param name="StateChanged">Whether <paramref name="State"/> differs from what was passed in.</param>
public sealed record RebuildReport(
    IReadOnlyList<RebuildEntry> Entries,
    int UnstampedCount,
    int SkippedVanishedCount,
    DocumeState State,
    bool StateChanged);

/// <summary>
/// Rebuilds <c>state.json</c>'s page map from the managed marker every DocuMe-written page carries
/// (PLAN.md §6.3 <c>--rebuild-state</c>, §6.2; docs/specs/2026-08-19-state-rebuild.md).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The marker is the registry; state is the copy being restored.</strong> state.json is a
/// committed, hand-editable, losable file, and losing it means every page re-creates and Confluence
/// rejects the duplicate titles. The stamp <see cref="ManagedMarker"/> writes at create time is the
/// page's own testimony, so a rebuild asks the space rather than trusting whatever survived: one space
/// listing, then one property read per page.
/// </para>
/// <para>
/// <strong>Adoption is conservative on purpose.</strong> A page adopts only when its stamped path names
/// a file this repo actually has and state makes no competing claim; every disagreement lands in the
/// manifest as <see cref="RebuildDisposition.Conflicted"/> with nothing written, and a stamped path with
/// no file is listed and left out of state, where a prune cannot reach it. The file check is also what
/// disambiguates two repos sharing a space — each adopts only the paths it holds — in practice though
/// not by proof, since the marker carries no tenant field.
/// </para>
/// <para>
/// <strong>Approvals, hashes and staleness are not rebuilt.</strong> Approvals belong to reviewers and
/// hashes to the next publish; an adopted entry carries neither, so the next publish updates the page
/// and re-records them honestly. Nothing here writes a Confluence byte — reads plus a state transform —
/// which is why the §1.4 space write-lock has nothing to refuse in this walk.
/// </para>
/// </remarks>
public sealed class StateRebuilder
{
    private readonly ConfluenceClient _client;

    /// <summary>Initializes a new instance of the <see cref="StateRebuilder"/> class.</summary>
    /// <param name="client">The Confluence client, already carrying credentials and the retry pipeline.</param>
    public StateRebuilder(ConfluenceClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
    }

    /// <summary>
    /// Walks every page of <paramref name="spaceId"/> and builds the adoption manifest.
    /// </summary>
    /// <param name="spaceId">The space to list, as an id — the v2 listing does not take a key.</param>
    /// <param name="state">State as loaded. Never mutated: the report carries the new value.</param>
    /// <param name="fileExists">
    /// Whether a wiki-relative path names a file in this repo. Injected so the walk stays pure over the
    /// filesystem — tests need no wiki tree on disk, and the caller decides what "under wiki.root" means.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// A property read that 404s mid-walk means the page vanished between the listing and the read; it
    /// is skipped and counted, because there is nothing left to adopt. Every other failure propagates —
    /// auth in particular stops the run dead (rule §1.2), never worked around.
    /// </remarks>
    public async Task<RebuildReport> RebuildAsync(
        string spaceId,
        DocumeState state,
        Func<string, bool> fileExists,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(spaceId);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(fileExists);

        var pages = await _client.ListSpacePagesAsync(spaceId, cancellationToken).ConfigureAwait(false);

        var unstamped = 0;
        var vanished = 0;
        var stamped = new List<(string Path, ConfluencePage Page)>();

        foreach (var page in pages)
        {
            ConfluencePageProperty? marker;
            try
            {
                marker = await _client
                    .FindPagePropertyAsync(page.Id, ManagedMarker.Key, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ConfluenceApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // A page gone in the seconds between the listing and this read, usually. Confluence
                // also answers 404 for content the token may not read, so a permission-scoped page
                // lands in this count too; either way there is nothing this run can adopt, and
                // skipping is safer than failing a whole recovery over one page.
                vanished++;
                continue;
            }

            if (marker is null || !ManagedMarker.TryReadPath(marker.RawValue, out var path))
            {
                // No marker, a foreign value under the same key, or a marker with no path: not
                // provably this tool's page, so not this rebuild's business. Counted, never listed.
                unstamped++;
                continue;
            }

            stamped.Add((path, page));
        }

        var original = state;
        var entries = new List<RebuildEntry>();

        foreach (var claim in stamped.GroupBy(entry => entry.Path, StringComparer.Ordinal))
        {
            // De-duplicated by page id first: cursor pagination can answer the same page twice when
            // the space is edited mid-walk, and a page repeated by the listing is one claimant, not a
            // conflict with itself.
            var claimants = claim
                .Select(entry => entry.Page)
                .DistinctBy(page => page.Id, StringComparer.Ordinal)
                .ToList();

            if (claimants.Count > 1)
            {
                // Two stamped pages claiming one path is a fact only a human can settle — a copied
                // page, a restored trash entry, or another repo's page whose path this repo happens to
                // have. Both are reported, each naming the others, and neither adopts.
                foreach (var page in claimants)
                {
                    var others = claimants
                        .Where(other => !string.Equals(other.Id, page.Id, StringComparison.Ordinal))
                        .Select(other => $"page {other.Id} ('{other.Title}')");

                    entries.Add(new RebuildEntry(
                        claim.Key,
                        page.Id,
                        page.Title,
                        RebuildDisposition.Conflicted,
                        $"the same path is stamped on {string.Join(" and ", others)}; neither adopts"));
                }

                continue;
            }

            var (entry, adopted) = Classify(claim.Key, claimants[0], state, fileExists);

            entries.Add(entry);
            state = adopted;
        }

        var ordered = entries
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ThenBy(entry => entry.PageId, StringComparer.Ordinal)
            .ToList();

        // Record equality here still compares the Pages dictionary by reference, so this is honest
        // only because every non-adopting verdict hands back the same instance (AdoptPage's no-op
        // contract, the same reasoning StateUpdates documents for its own no-op returns): a change is
        // reported exactly when something adopted. A transform that returned an equal-valued fresh
        // instance would flip this true and re-open the empty-PR problem — keep no-ops as identity.
        return new RebuildReport(ordered, unstamped, vanished, state, state != original);
    }

    /// <summary>
    /// The wiki files a marker path is allowed to name, spelled exactly as the tree walk spells them:
    /// wiki-root-relative, forward slashes, ordinal. This set is the whole traversal defense for the
    /// rebuild, and it works by construction rather than by inspection — a stamped path is adopted
    /// only when it is byte-for-byte a path this enumeration produced, so <c>../escape.md</c>,
    /// <c>./alias.md</c>, an absolute path, a case variant on a case-insensitive filesystem, and a
    /// string no filesystem accepts all miss the set without any of them ever touching the disk
    /// (rule §1.3, CLAUDE.md §0.2: the path rides in on a Confluence page property, which is
    /// untrusted input).
    /// </summary>
    public static HashSet<string> WikiFilePaths(string wikiRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wikiRoot);

        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(wikiRoot, "*.md", SearchOption.AllDirectories))
        {
            paths.Add(Path.GetRelativePath(wikiRoot, file).Replace(Path.DirectorySeparatorChar, '/'));
        }

        return paths;
    }

    /// <summary>
    /// The verdict for a path exactly one stamped page claims, and the state that results. State's
    /// claim is checked before the file's: a disagreement over the id is the sharper fact, and saying
    /// "no file" about a path state maps elsewhere would bury it.
    /// </summary>
    private static (RebuildEntry Entry, DocumeState State) Classify(
        string path,
        ConfluencePage page,
        DocumeState state,
        Func<string, bool> fileExists)
    {
        if (state.Pages.TryGetValue(path, out var existing)
            && existing.PageId is { Length: > 0 } trackedId)
        {
            if (string.Equals(trackedId, page.Id, StringComparison.Ordinal))
            {
                return (new RebuildEntry(path, page.Id, page.Title, RebuildDisposition.AlreadyTracked, null), state);
            }

            return (new RebuildEntry(
                path,
                page.Id,
                page.Title,
                RebuildDisposition.Conflicted,
                $"state maps this path to page {trackedId}; nothing was written"), state);
        }

        if (!fileExists(path))
        {
            return (new RebuildEntry(
                path,
                page.Id,
                page.Title,
                RebuildDisposition.PathMissing,
                "no file at this path under wiki.root; left out of state, where a prune cannot reach it"), state);
        }

        return (
            new RebuildEntry(path, page.Id, page.Title, RebuildDisposition.Adopted, null),
            StateUpdates.AdoptPage(state, path, page.Id, page.Title));
    }
}
