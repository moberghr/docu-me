using DocuMe.Core.Config;
using DocuMe.Core.Markdown;
using DocuMe.Core.State;

namespace DocuMe.Core.Scaffolding;

/// <summary>What an adoption run was pointed at (PLAN.md §6.1's <c>--adopt</c>).</summary>
/// <param name="WikiRoot">Full path to the wiki root directory the repo already has.</param>
/// <param name="Label">
/// How to name that root in messages: the repo-relative spelling from <c>docume.json</c>, because
/// <c>wiki.root</c> is the value a consumer edits when it is wrong.
/// </param>
/// <param name="Wiki">
/// The <c>wiki</c> config section. Its <c>exclude</c> globs and <c>extraPages</c> decide which files
/// are pages, so the adopted skeleton lists exactly what a publish would publish.
/// </param>
/// <param name="Existing">The state file as it is on disk, or an empty state when there is none.</param>
public sealed record AdoptionRequest(
    string WikiRoot,
    string Label,
    WikiConfig Wiki,
    DocumeState Existing)
{
    /// <summary>Full path to a legacy page-id map, or null when the run named none.</summary>
    public string? LegacyMapPath { get; init; }

    /// <summary>How to name that map in messages; falls back to <see cref="LegacyMapPath"/>.</summary>
    public string? LegacyMapLabel { get; init; }
}

/// <summary>The outcome of an adoption.</summary>
/// <param name="State">
/// The state to save, or <c>null</c> when nothing was adopted. Null is the refusal: the caller leaves
/// the file exactly as it found it and reports <paramref name="Note"/>.
/// </param>
/// <param name="Note">
/// One line saying what was adopted, or why nothing was. Always present — an adoption that seeded no
/// ids and an adoption that was refused leave a similar-looking file behind, so both have to speak.
/// </param>
public sealed record AdoptionResult(DocumeState? State, string Note);

/// <summary>
/// The <c>--adopt</c> half of <c>docume init</c> (PLAN.md §6.1): builds a <c>_meta/state.json</c>
/// skeleton for a repo whose markdown wiki already exists — one entry per page (§5.3), with
/// <c>pageId</c>s seeded from page frontmatter (§5.2) and from a legacy map file when the run names one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The ids are the load-bearing half.</strong> A wiki that something else already published has
/// pages in Confluence, and Confluence titles are unique per space — so publishing it without ids does
/// not quietly duplicate the tree, it collides on the titles. Seeding is what turns the first run into
/// 79 updates instead of 79 collisions, which is also why an unreadable map refuses the whole adoption
/// rather than falling back to an unseeded skeleton.
/// </para>
/// <para>
/// <strong>The entries without ids are still worth writing.</strong> An entry with no <c>pageId</c>
/// plans a create exactly as a missing entry does (<see cref="PublishPlanner.PlanPage"/>), so that half
/// of the skeleton changes no publish decision. What it changes is what can be seen before the first
/// publish: <c>docume status</c> and <c>docume dashboard</c> list the inventory, and a human has one
/// file in which to hand-seed an id they looked up. A seeded entry needs nothing else — the publish path
/// re-reads the page's current version from Confluence rather than trusting state
/// (<see cref="Publishing.PublishExecutor"/>), so <c>publishedVersion: 0</c> here is harmless.
/// </para>
/// <para>
/// <strong>It never overwrites what a publish knows.</strong> A state file that already lists pages is
/// left as it is (rule §9.4): its ids, hashes and approvals are the only record of what is published,
/// and an "adoption" that replaced them would re-create every page and revoke every approval. A file
/// that lists none — the one a plain <c>docume init</c> writes — is filled in, and its
/// <c>baselineSha</c>/<c>lastPublishedSha</c> are carried through.
/// </para>
/// </remarks>
public static class WikiAdopter
{
    /// <summary>How many unmatched keys or paths a note lists before summarizing the rest.</summary>
    /// <remarks>
    /// The point of listing them is to show the spelling that did not resolve, which the first few do
    /// as well as all of them; a map whose every key is spelled wrong would otherwise print a line as
    /// long as the wiki.
    /// </remarks>
    private const int ListLimit = 10;

    /// <summary>Builds the skeleton, or refuses and says why.</summary>
    public static AdoptionResult Adopt(AdoptionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Existing.Pages.Count > 0)
        {
            var listed = Plural(request.Existing.Pages.Count, "page", "pages");

            return Refused($"already lists {listed} and was left untouched. Those entries are the only "
                + "record of what is published — replacing them would re-create every page and revoke "
                + "every approval (rule §9.4). Adopt into a state file that lists no pages.");
        }

        if (!Directory.Exists(request.WikiRoot))
        {
            return Refused($"there is no wiki at '{request.Label}' to adopt, so no page entries were "
                + "written. --adopt reads a markdown wiki this repo already has: point wiki.root at "
                + "yours, or run init without --adopt to start a new one.");
        }

        WikiTree tree;
        try
        {
            tree = WikiTree.Load(request.WikiRoot, request.Wiki);
        }
        catch (WikiTreeException exception)
        {
            var problems = Plural(exception.Errors.Count, "problem", "problems");

            return Refused($"the wiki at '{request.Label}' cannot be published as it stands, so nothing "
                + $"was adopted ({problems}, first: {exception.Errors[0]}). Run "
                + $"`docume convert {request.Label}` for the whole list.");
        }
        catch (DirectoryNotFoundException)
        {
            return Refused($"the wiki at '{request.Label}' vanished while it was being read, so nothing "
                + "was adopted.");
        }

        if (tree.Pages.Count == 0)
        {
            return Refused($"there are no pages under '{request.Label}' to adopt, so nothing was "
                + "written. Check wiki.root and wiki.exclude — every file under the root is excluded, "
                + "or there is no markdown there.");
        }

        if (!LegacyPageMap.TryRead(
            request.LegacyMapPath,
            request.LegacyMapLabel,
            request.Label,
            out var map,
            out var mapFailure))
        {
            return Refused(mapFailure!);
        }

        return Build(request, tree, map);
    }

    private static AdoptionResult Build(AdoptionRequest request, WikiTree tree, LegacyPageMap map)
    {
        var pages = new Dictionary<string, PageState>(StringComparer.Ordinal);
        var conflicts = new List<string>();
        var fromFrontmatter = 0;
        var fromMap = 0;

        // In tree order, which is ordinal by path: state.json is committed and read in diffs, so the
        // skeleton is written sorted rather than in whatever order the filesystem enumerated.
        foreach (var page in tree.Pages)
        {
            var declared = Trimmed(page.Parsed.Frontmatter.PageId);
            var mapped = map.IdFor(page.Path);

            // Frontmatter wins: it is a per-page annotation someone wrote deliberately, while the map
            // is a bulk artifact of whatever published the wiki last. A disagreement is still named —
            // one of the two is stale and only a human can say which.
            if (declared is not null && mapped is not null && !string.Equals(declared, mapped, StringComparison.Ordinal))
            {
                conflicts.Add(page.Path);
            }

            if (declared is not null)
            {
                fromFrontmatter++;
            }
            else if (mapped is not null)
            {
                fromMap++;
            }

            pages[page.Path] = new PageState
            {
                PageId = declared ?? mapped,
                Title = page.Title,
            };
        }

        var state = request.Existing with { Pages = pages };
        var note = Summarize(request, tree.Pages.Count, fromFrontmatter, fromMap, map, conflicts);

        return new AdoptionResult(state, note);
    }

    private static string Summarize(
        AdoptionRequest request,
        int pageCount,
        int fromFrontmatter,
        int fromMap,
        LegacyPageMap map,
        List<string> conflicts)
    {
        List<string> parts = [$"adopted {Plural(pageCount, "page", "pages")} from '{request.Label}'"];
        var seeded = fromFrontmatter + fromMap;

        if (seeded == 0)
        {
            parts.Add("no pageIds were seeded, so the first publish CREATES every one of them — if these "
                + "pages already exist in Confluence, seed their ids first (frontmatter 'pageId:' or "
                + "--legacy-map) or the publish will collide on their titles");
        }
        else
        {
            parts.Add($"seeded {Plural(seeded, "pageId", "pageIds")} ({Sources(fromFrontmatter, fromMap, request)})");
        }

        if (seeded > 0 && seeded < pageCount)
        {
            parts.Add($"the other {pageCount - seeded} will be created on the first publish");
        }

        if (map.Unmatched.Count > 0)
        {
            parts.Add($"{Plural(map.Unmatched.Count, "map entry", "map entries")} matched no page in the "
                + $"tree and seeded nothing: {Listed(map.Unmatched)}");
        }

        if (map.Unusable.Count > 0)
        {
            parts.Add($"{Plural(map.Unusable.Count, "map entry", "map entries")} carried no readable "
                + $"page id: {Listed(map.Unusable)}");
        }

        if (conflicts.Count > 0)
        {
            parts.Add($"frontmatter and the map disagree on {Plural(conflicts.Count, "page", "pages")} "
                + $"({Listed(conflicts)}); the frontmatter id won");
        }

        return string.Join("; ", parts) + ".";
    }

    private static string Sources(int fromFrontmatter, int fromMap, AdoptionRequest request)
    {
        var mapName = request.LegacyMapLabel ?? request.LegacyMapPath ?? "the legacy map";

        if (fromFrontmatter == 0)
        {
            return $"from {mapName}";
        }

        return fromMap == 0
            ? "from page frontmatter"
            : $"{fromFrontmatter} from page frontmatter, {fromMap} from {mapName}";
    }

    private static string Listed(IReadOnlyList<string> items)
    {
        var shown = string.Join(", ", items.Take(ListLimit).Select(item => $"'{item}'"));

        return items.Count <= ListLimit ? shown : $"{shown} (+{items.Count - ListLimit} more)";
    }

    private static string Plural(int count, string singular, string plural) =>
        count == 1 ? $"1 {singular}" : $"{count} {plural}";

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static AdoptionResult Refused(string note) => new(null, note);
}
