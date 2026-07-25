using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocuMe.Core.Scaffolding;

/// <summary>
/// A legacy "page path → Confluence page id" map, as read by <c>docume init --adopt</c>
/// (PLAN.md §6.1; §14's M7 row names AurServices' <c>_meta/confluence-map.json</c>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>More than one shape is accepted, because the file is not DocuMe's.</strong> Whatever
/// published the wiki before wrote it, so this reader takes a flat <c>{ "&lt;path&gt;": "&lt;id&gt;" }</c>,
/// an entry object carrying <c>pageId</c> or <c>id</c>, and either of those wrapped in a <c>pages</c>
/// member — which is what makes a previous DocuMe <c>state.json</c> a valid map, the one such file
/// anyone other than AurServices is likely to have. Ids are read from JSON strings and numbers alike:
/// a page id looks numeric, and a hand-written map spells it either way.
/// </para>
/// <para>
/// <strong>Keys are normalized, not guessed at.</strong> Backslashes, a leading <c>./</c> or <c>/</c>,
/// and a repo-root-relative spelling that carries the wiki root as a prefix (<c>docs/wiki/x.md</c> for
/// the page <c>x.md</c>) all resolve; anything else is reported unmatched rather than approximated. An
/// unmatched key costs a page its id, and a page adopted without its id collides on its title at the
/// first publish instead of updating — so silence is the one thing this must not do.
/// </para>
/// </remarks>
internal sealed class LegacyPageMap
{
    private readonly Dictionary<string, string> _idsByKey;
    private readonly Dictionary<string, string> _keysByCandidate;
    private readonly HashSet<string> _matched = new(StringComparer.Ordinal);

    private LegacyPageMap(
        Dictionary<string, string> idsByKey,
        Dictionary<string, string> keysByCandidate,
        IReadOnlyList<string> unusable)
    {
        _idsByKey = idsByKey;
        _keysByCandidate = keysByCandidate;
        Unusable = unusable;
    }

    /// <summary>Keys whose value was not a usable page id, in file order.</summary>
    public IReadOnlyList<string> Unusable { get; }

    /// <summary>
    /// Keys that carried an id no page in the tree claimed, ordinal-sorted. Only meaningful after the
    /// caller has looked up every page — this is the residue of <see cref="IdFor"/>.
    /// </summary>
    public IReadOnlyList<string> Unmatched =>
        [.. _idsByKey.Keys.Where(key => !_matched.Contains(key)).Order(StringComparer.Ordinal)];

    /// <summary>An empty map — what a run that named no legacy map file adopts against.</summary>
    public static LegacyPageMap None() => new([], [], []);

    /// <summary>
    /// Reads the map at <paramref name="fullPath"/>, or hands back an empty one when the run named
    /// none. Returns <see langword="false"/> with a one-line <paramref name="failure"/> when the file
    /// was named but cannot be used — the caller refuses the whole adoption then, because a skeleton
    /// silently missing its ids is worse than no skeleton at all.
    /// </summary>
    /// <param name="fullPath">Path to the map file, or null/empty when the run named none.</param>
    /// <param name="label">How to name the file in messages; falls back to <paramref name="fullPath"/>.</param>
    /// <param name="wikiRootLabel">
    /// The wiki root as <c>docume.json</c> spells it, e.g. <c>docs/wiki</c>. Used to strip a
    /// repo-root-relative prefix off map keys.
    /// </param>
    public static bool TryRead(
        string? fullPath,
        string? label,
        string wikiRootLabel,
        out LegacyPageMap map,
        out string? failure)
    {
        map = None();
        failure = null;

        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return true;
        }

        var shown = string.IsNullOrWhiteSpace(label) ? fullPath : label;

        if (!File.Exists(fullPath))
        {
            failure = $"the legacy map '{shown}' does not exist, so nothing was adopted. Seeding no ids "
                + "would leave every already-published page to be created again.";

            return false;
        }

        JsonNode? root;
        try
        {
            // Read leniently: this file is only ever read, never rewritten, so tolerating comments
            // and trailing commas costs a consumer nothing (unlike .config/dotnet-tools.json).
            root = JsonNode.Parse(
                File.ReadAllText(fullPath),
                documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
        }
        catch (JsonException exception)
        {
            failure = $"the legacy map '{shown}' is not valid JSON "
                + $"({exception.Message.ReplaceLineEndings(" ")}), so nothing was adopted.";

            return false;
        }

        if (Entries(root) is not { } entries)
        {
            failure = $"the legacy map '{shown}' is not a JSON object of '<page path>': '<page id>' "
                + "entries, so nothing was adopted.";

            return false;
        }

        map = Build(entries, Prefix(wikiRootLabel));

        if (map._idsByKey.Count == 0)
        {
            failure = $"the legacy map '{shown}' holds no usable '<page path>': '<page id>' entries "
                + $"({entries.Count} read), so nothing was adopted.";

            return false;
        }

        return true;
    }

    /// <summary>
    /// The id this map holds for the page at <paramref name="pagePath"/> (wiki-root-relative), or
    /// <c>null</c> when it holds none. Records the hit, so <see cref="Unmatched"/> can report the
    /// entries no page ever claimed.
    /// </summary>
    public string? IdFor(string pagePath)
    {
        if (!_keysByCandidate.TryGetValue(pagePath, out var key))
        {
            return null;
        }

        _matched.Add(key);
        return _idsByKey[key];
    }

    private static LegacyPageMap Build(JsonObject entries, string prefix)
    {
        var idsByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        var keysByCandidate = new Dictionary<string, string>(StringComparer.Ordinal);
        var unusable = new List<string>();

        foreach (var (key, value) in entries)
        {
            if (PageId(value) is not { } id)
            {
                unusable.Add(key);
                continue;
            }

            idsByKey[key] = id;

            // The key's own spelling first, so an exact match always wins the candidate slot over
            // another key's prefix-stripped form.
            foreach (var candidate in Candidates(key, prefix))
            {
                keysByCandidate.TryAdd(candidate, key);
            }
        }

        return new LegacyPageMap(idsByKey, keysByCandidate, unusable);
    }

    /// <summary>
    /// The object holding the entries: the root, or its <c>pages</c> member when it has one (a DocuMe
    /// <c>state.json</c>). Null when the root is not an object at all.
    /// </summary>
    private static JsonObject? Entries(JsonNode? root)
    {
        if (root is not JsonObject obj)
        {
            return null;
        }

        return obj["pages"] as JsonObject ?? obj;
    }

    /// <summary>
    /// The id in <paramref name="value"/>: the value itself when it is a string or a number, else the
    /// <c>pageId</c>/<c>id</c> member of an entry object. Null when there is nothing usable.
    /// </summary>
    private static string? PageId(JsonNode? value)
    {
        if (value is JsonObject entry)
        {
            return PageId(entry["pageId"]) ?? PageId(entry["id"]);
        }

        if (value is not JsonValue scalar)
        {
            return null;
        }

        if (scalar.TryGetValue<string>(out var text))
        {
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        return scalar.TryGetValue<long>(out var number)
            ? number.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    private static IEnumerable<string> Candidates(string key, string prefix)
    {
        var normalized = Normalize(key);
        if (normalized.Length == 0)
        {
            yield break;
        }

        yield return normalized;

        if (prefix.Length > 0
            && normalized.Length > prefix.Length
            && normalized.StartsWith(prefix, StringComparison.Ordinal))
        {
            yield return normalized[prefix.Length..];
        }
    }

    private static string Normalize(string key)
    {
        var path = key.Replace('\\', '/').Trim();

        while (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        return path.TrimStart('/');
    }

    private static string Prefix(string wikiRootLabel)
    {
        var root = Normalize(wikiRootLabel).TrimEnd('/');

        return root.Length == 0 ? string.Empty : root + "/";
    }
}
