using System.Globalization;
using System.Net;
using System.Text.Json;

namespace DocuMe.Core.Confluence;

/// <summary>
/// Thin client over the Confluence Cloud REST v2 API (PLAN.md §4). Read paths only so far; the
/// write paths arrive with the publish pipeline (§6.2).
/// </summary>
/// <remarks>
/// <para>
/// Hand-written rather than <c>Dapplo.Confluence</c>, which settles spike S1 (§13). Dapplo is
/// alive (1.0.41, January 2026, with a net10.0 target) but hard-wires its API root to
/// <c>/rest/api</c> — the v1 content API — and reaches Atlassian through
/// <c>Dapplo.HttpExtensions</c> + Newtonsoft. §4 asks for v2 with v1 only where v2 lacks (label
/// add, CQL search), and for <c>Microsoft.Extensions.Http.Resilience</c> on the transport, so the
/// package would have to be worked around on both counts for the ~12 endpoints DocuMe needs.
/// </para>
/// <para>
/// Endpoints and response shapes are taken from Atlassian's published OpenAPI document rather than
/// from memory: <c>GET /api/v2/spaces</c> filters by <c>keys</c>, <c>GET /api/v2/pages</c> by
/// <c>space-id</c> + <c>title</c> + <c>status</c>, both answering
/// <c>{"results": [...], "_links": {...}}</c>, and <c>body-format=storage</c> is what populates
/// <c>body.storage.value</c>.
/// </para>
/// <para>
/// Everything here is a read, so nothing in this type can change a Confluence page. That is what
/// makes the slice verifiable against WireMock alone (.claude/rules/testing.md §4.2) while the
/// sandbox space (CLAUDE.md §0.1) is still being set up.
/// </para>
/// </remarks>
public sealed class ConfluenceClient : IDisposable
{
    /// <summary>How much of an unexpected response body an exception message quotes.</summary>
    private const int ResponseExcerptLimit = 400;

    /// <summary>
    /// Why a version-less page is fatal: an update sends the current version incremented by one, so
    /// a page read without it cannot be republished at all.
    /// </summary>
    private const string MissingVersionDetail =
        "the page has no 'version.number', which an update has to increment";

    /// <summary>
    /// Web defaults: camelCase, case-insensitive, and a number readable from a JSON string. The
    /// last one is deliberate slack — a version arriving as <c>"3"</c> instead of <c>3</c> is not
    /// worth failing a publish over.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    /// <summary>
    /// Wraps a caller-owned <see cref="HttpClient"/>, which must already carry the base address and
    /// the <c>Authorization</c> header. Disposing this client leaves it open.
    /// </summary>
    public ConfluenceClient(HttpClient httpClient)
        : this(httpClient, ownsHttpClient: false)
    {
    }

    private ConfluenceClient(HttpClient httpClient, bool ownsHttpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
    }

    /// <summary>
    /// Builds a client with the retry pipeline and basic-auth header configured, owning the
    /// <see cref="HttpClient"/> it creates. One instance per run: a bulk publish reuses the
    /// connection pool instead of opening ~80 of them.
    /// </summary>
    public static ConfluenceClient Create(ConfluenceClientOptions options, ConfluenceCredentials credentials)
        => new(ConfluenceHttp.CreateClient(options, credentials), ownsHttpClient: true);

    /// <summary>
    /// Looks up a space by its key, e.g. <c>DOCUMESBX</c>. Returns <c>null</c> when the space does
    /// not exist or the account cannot see it — the two are indistinguishable over this endpoint,
    /// which answers an empty <c>results</c> either way.
    /// </summary>
    /// <exception cref="ConfluenceAuthenticationException">401/403: token expired or revoked.</exception>
    /// <exception cref="ConfluenceApiException">Any other non-success status.</exception>
    /// <exception cref="ConfluenceProtocolException">The response body is not the documented shape.</exception>
    public async Task<ConfluenceSpace?> FindSpaceByKeyAsync(
        string spaceKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spaceKey);

        var path = $"api/v2/spaces?keys={Uri.EscapeDataString(spaceKey)}&limit=1";
        var response = await ReadAsync<MultiEntityResult<SpaceBulk>>(path, cancellationToken)
            .ConfigureAwait(false);

        var spaces = RequireResults(response, path);
        return spaces.Count == 0 ? null : MapSpace(spaces[0], path);
    }

    /// <summary>
    /// Finds the current page with an exact title in a space, or <c>null</c> when there is none.
    /// This is how the publish pipeline decides create-vs-update for a page whose id is not in
    /// <c>_meta/state.json</c> yet (PLAN.md §6.2 step 5).
    /// </summary>
    /// <param name="spaceId">
    /// The numeric space id from <see cref="FindSpaceByKeyAsync"/>. The v2 API filters by id, not
    /// by key.
    /// </param>
    /// <param name="title">The exact title to match.</param>
    /// <param name="includeBody">Whether to fetch the storage-format body too.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// <para>
    /// Two deliberate narrowings. <c>status=current</c> is sent explicitly because the endpoint
    /// otherwise defaults to <c>current,archived</c>, and an archived namesake would shadow the
    /// live page. And the title of whatever comes back is compared to
    /// <paramref name="title"/> here, rather than trusting the server-side filter to be exact:
    /// Atlassian's schema documents the parameter only as "filter the results to pages based on
    /// their title". If it turns out to match loosely, this guard makes a near-miss read as "no
    /// such page" — which ends in a create that Confluence rejects loudly for a duplicate title,
    /// not in DocuMe overwriting somebody else's page.
    /// </para>
    /// </remarks>
    public async Task<ConfluencePage?> FindPageByTitleAsync(
        string spaceId,
        string title,
        bool includeBody = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var path = $"api/v2/pages?space-id={Uri.EscapeDataString(spaceId)}"
            + $"&title={Uri.EscapeDataString(title)}&status=current&limit=1{BodyFormat(includeBody)}";

        var response = await ReadAsync<MultiEntityResult<PageBulk>>(path, cancellationToken)
            .ConfigureAwait(false);

        var pages = RequireResults(response, path);
        var match = pages.FirstOrDefault(page => string.Equals(page.Title, title, StringComparison.Ordinal));

        return match is null ? null : MapPage(match, path);
    }

    /// <summary>
    /// Reads a page by id, or <c>null</c> when Confluence no longer has it.
    /// </summary>
    /// <remarks>
    /// A 404 here is not an error: a page id recorded in <c>_meta/state.json</c> whose page a human
    /// deleted in Confluence is the orphan case the publish pipeline reports (PLAN.md §6.2), so the
    /// caller decides what it means.
    /// </remarks>
    public async Task<ConfluencePage?> FindPageByIdAsync(
        string pageId,
        bool includeBody = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);

        var path = $"api/v2/pages/{Uri.EscapeDataString(pageId)}{BodyFormat(includeBody, first: true)}";
        var page = await ReadOrNullWhenMissingAsync<PageBulk>(path, cancellationToken).ConfigureAwait(false);

        return page is null ? null : MapPage(page, path);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static string BodyFormat(bool includeBody, bool first = false)
    {
        if (!includeBody)
        {
            return string.Empty;
        }

        return first ? "?body-format=storage" : "&body-format=storage";
    }

    private async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        var (statusCode, body) = await SendAsync(path, cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(path, statusCode, body);

        return Deserialize<T>(path, body);
    }

    private async Task<T?> ReadOrNullWhenMissingAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        var (statusCode, body) = await SendAsync(path, cancellationToken).ConfigureAwait(false);
        if (statusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        ThrowIfFailed(path, statusCode, body);

        return Deserialize<T>(path, body);
    }

    private async Task<(HttpStatusCode StatusCode, string Body)> SendAsync(
        string path,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient
            .GetAsync(new Uri(path, UriKind.Relative), cancellationToken)
            .ConfigureAwait(false);

        // Read the body unconditionally: it is both the payload on success and the only detail an
        // error message can quote. Responses here are single pages, not exports.
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return (response.StatusCode, body);
    }

    /// <summary>
    /// Turns a failing status into the right exception. 401/403 get their own type because they are
    /// the one case where the whole run must stop rather than the page being retried or reported
    /// (.claude/rules/security.md §1.2); the retry pipeline has already refused to retry them.
    /// </summary>
    private static void ThrowIfFailed(string path, HttpStatusCode statusCode, string body)
    {
        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new ConfluenceAuthenticationException(statusCode, path);
        }

        if ((int)statusCode is < 200 or > 299)
        {
            throw new ConfluenceApiException(statusCode, path, Excerpt(body));
        }
    }

    private static T Deserialize<T>(string path, string body)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, SerializerOptions)
                ?? throw new ConfluenceProtocolException(path, "the body was the JSON literal null");
        }
        catch (JsonException ex)
        {
            throw new ConfluenceProtocolException(
                path,
                $"the body is not valid JSON ({ex.Message}): {Excerpt(body)}",
                ex);
        }
    }

    private static IReadOnlyList<T> RequireResults<T>(MultiEntityResult<T> response, string path)
        => response.Results
            ?? throw new ConfluenceProtocolException(path, "the body has no 'results' array");

    private static ConfluenceSpace MapSpace(SpaceBulk space, string path)
        => new(
            Require(space.Id, "id", path),
            Require(space.Key, "key", path),
            space.Name ?? string.Empty);

    private static ConfluencePage MapPage(PageBulk page, string path)
        => new(
            Require(page.Id, "id", path),
            Require(page.Title, "title", path),
            Require(page.SpaceId, "spaceId", path),
            page.ParentId,
            page.Version?.Number ?? throw new ConfluenceProtocolException(path, MissingVersionDetail),
            page.Body?.Storage?.Value);

    private static string Require(string? value, string field, string path)
        => string.IsNullOrEmpty(value)
            ? throw new ConfluenceProtocolException(path, $"an entity in it has no '{field}'")
            : value;

    private static string Excerpt(string body)
    {
        var trimmed = body.Trim();
        if (trimmed.Length == 0)
        {
            return "(empty body)";
        }

        var text = trimmed.Length <= ResponseExcerptLimit
            ? trimmed
            : string.Concat(trimmed.AsSpan(0, ResponseExcerptLimit), "… (truncated)");

        return string.Create(CultureInfo.InvariantCulture, $"'{text}'");
    }
}
