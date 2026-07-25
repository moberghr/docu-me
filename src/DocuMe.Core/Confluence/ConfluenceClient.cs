using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocuMe.Core.Confluence;

/// <summary>
/// Thin client over the Confluence Cloud REST v2 API (PLAN.md §4): the page reads and the page
/// upsert the publish pipeline is built on (§6.2 step 5). Attachments and labels are still to come,
/// on v1 endpoints v2 has no equivalent for.
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
/// The writes go through the same retry pipeline as the reads, which is worth defending because a
/// retried <c>POST</c> is not obviously safe. A 429 is a rejection — Confluence never applied the
/// request — so replaying it is exactly right, and PLAN.md §13 S5 expects a bulk publish to lean on
/// it. A 5xx is the ambiguous one: the create may have landed. What bounds it is Confluence's own
/// constraint that page titles are unique within a space (the one PLAN.md §6.2 step 1 validates
/// against): a replayed create whose first attempt actually applied comes back as a loud duplicate
/// title, not as a silent second page. A 409 and a 400 are never retried at all.
/// </para>
/// <para>
/// Both halves are verifiable against WireMock alone (.claude/rules/testing.md §4.2) — including the
/// write paths, which is why the sandbox space (CLAUDE.md §0.1) gates M2's acceptance rather than its
/// construction.
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

    /// <summary>The one page status DocuMe ever publishes: never a draft, never an archive.</summary>
    private const string CurrentStatus = "current";

    /// <summary>The body representation §7's renderer produces.</summary>
    private const string StorageRepresentation = "storage";

    /// <summary>Spelled as a segment rather than a path so the endpoint constants carry no separator.</summary>
    private const string PagesSegment = "pages";

    /// <summary>Content type for every write body.</summary>
    private const string JsonMediaType = "application/json";

    /// <summary>
    /// Web defaults: camelCase, case-insensitive, and a number readable from a JSON string. The
    /// last one is deliberate slack — a version arriving as <c>"3"</c> instead of <c>3</c> is not
    /// worth failing a publish over.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The same web defaults, plus null-omission. Omission is what lets a nullable member mean "leave
    /// this alone" instead of "clear it" (see <see cref="PageCreateRequest"/>).
    /// </summary>
    private static readonly JsonSerializerOptions WriteSerializerOptions =
        new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

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

    /// <summary>
    /// Creates a page and returns it as Confluence stored it — most importantly the id that lands in
    /// <c>_meta/state.json</c> and the version a later update increments (PLAN.md §6.2 step 5).
    /// </summary>
    /// <remarks>
    /// A duplicate title is the loud failure this half is designed around: because
    /// <see cref="FindPageByTitleAsync"/> re-checks the title it got back, a lookup that misses ends
    /// here, and Confluence's per-space title uniqueness rejects the create rather than letting
    /// DocuMe overwrite a page it never identified. The rejection arrives as a
    /// <see cref="ConfluenceApiException"/> naming the title.
    /// </remarks>
    /// <exception cref="ConfluenceAuthenticationException">401/403: token expired or revoked.</exception>
    /// <exception cref="ConfluenceConflictException">409: Confluence reports the write as conflicting.</exception>
    /// <exception cref="ConfluenceApiException">
    /// Any other non-success status, including a duplicate title (400), a parent the account cannot
    /// see (404) and a body over the endpoint's 5 MB limit (413).
    /// </exception>
    /// <exception cref="ConfluenceProtocolException">The response body is not the documented shape.</exception>
    public async Task<ConfluencePage> CreatePageAsync(
        ConfluencePageDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        Validate(draft, nameof(draft));

        var path = $"api/v2/{PagesSegment}";
        var request = new PageCreateRequest(
            draft.SpaceId,
            CurrentStatus,
            draft.Title,
            draft.ParentId,
            StorageBody(draft.Storage));

        var page = await WriteAsync<PageCreateRequest, PageBulk>(
                HttpMethod.Post,
                path,
                request,
                $"creating page '{draft.Title}' in space {draft.SpaceId}",
                cancellationToken)
            .ConfigureAwait(false);

        return MapPage(page, path);
    }

    /// <summary>
    /// Overwrites a page with a new revision, sending
    /// <see cref="ConfluencePageRevision.CurrentVersion"/> incremented by one — the optimistic lock
    /// Confluence requires, and the reason a page read without a version fails loud.
    /// </summary>
    /// <remarks>
    /// A stale version is reported, never resolved here: see
    /// <see cref="ConfluenceConflictException"/> for why re-reading and pushing again is the wrong
    /// default.
    /// </remarks>
    /// <exception cref="ConfluenceAuthenticationException">401/403: token expired or revoked.</exception>
    /// <exception cref="ConfluenceConflictException">409: the page changed under us.</exception>
    /// <exception cref="ConfluenceApiException">Any other non-success status.</exception>
    /// <exception cref="ConfluenceProtocolException">The response body is not the documented shape.</exception>
    public async Task<ConfluencePage> UpdatePageAsync(
        ConfluencePageRevision revision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revision);
        Validate(revision, nameof(revision));

        var nextVersion = revision.CurrentVersion + 1;
        var path = $"api/v2/{PagesSegment}/{Uri.EscapeDataString(revision.PageId)}";
        var request = new PageUpdateRequest(
            revision.PageId,
            CurrentStatus,
            revision.Title,
            revision.ParentId,
            StorageBody(revision.Storage),
            new VersionWrite(nextVersion, revision.VersionMessage));

        var page = await WriteAsync<PageUpdateRequest, PageBulk>(
                HttpMethod.Put,
                path,
                request,
                $"updating page {revision.PageId} '{revision.Title}' to version {nextVersion}",
                cancellationToken)
            .ConfigureAwait(false);

        return MapPage(page, path);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static PageNestedBodyWrite StorageBody(string storage)
        => new(new PageBodyWrite(StorageRepresentation, storage));

    private static void Validate(ConfluencePageDraft draft, string parameterName)
    {
        RequireField(draft.SpaceId, "space id", parameterName);
        RequireField(draft.Title, "page title", parameterName);
        RequireBody(draft.Storage, parameterName);
    }

    private static void Validate(ConfluencePageRevision revision, string parameterName)
    {
        RequireField(revision.PageId, "page id", parameterName);
        RequireField(revision.Title, "page title", parameterName);
        RequireBody(revision.Storage, parameterName);

        if (revision.CurrentVersion < 1)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                revision.CurrentVersion,
                "A page version read from Confluence is at least 1, and an update sends it incremented by one.");
        }
    }

    /// <summary>
    /// Guards a field of a write input. Spelled out rather than delegated to
    /// <c>ArgumentException.ThrowIfNullOrWhiteSpace</c> because the caller passes a whole record: the
    /// parameter name can only ever be that record, so the field that was missing goes in the message.
    /// </summary>
    private static void RequireField(string? value, string field, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"A publish needs a {field}, but it was null or blank.", parameterName);
        }
    }

    /// <summary>
    /// A body may be empty — a page with no content is legitimate — but it may not be absent, which
    /// would publish the JSON literal null into a page nobody meant to blank.
    /// </summary>
    private static void RequireBody(string? storage, string parameterName)
    {
        if (storage is null)
        {
            throw new ArgumentException(
                "A publish needs a rendered body; an empty one is fine, a missing one is not.",
                parameterName);
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
        var (statusCode, body) = await SendAsync(HttpMethod.Get, path, payload: null, cancellationToken)
            .ConfigureAwait(false);

        ThrowIfFailed(path, statusCode, body);

        return Deserialize<T>(path, body);
    }

    private async Task<T?> ReadOrNullWhenMissingAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        var (statusCode, body) = await SendAsync(HttpMethod.Get, path, payload: null, cancellationToken)
            .ConfigureAwait(false);

        if (statusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        ThrowIfFailed(path, statusCode, body);

        return Deserialize<T>(path, body);
    }

    private async Task<TResponse> WriteAsync<TRequest, TResponse>(
        HttpMethod method,
        string path,
        TRequest request,
        string operation,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        var payload = JsonSerializer.Serialize(request, WriteSerializerOptions);
        var (statusCode, body) = await SendAsync(method, path, payload, cancellationToken)
            .ConfigureAwait(false);

        ThrowIfFailed(path, statusCode, body, operation);

        return Deserialize<TResponse>(path, body);
    }

    private async Task<(HttpStatusCode StatusCode, string Body)> SendAsync(
        HttpMethod method,
        string path,
        string? payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));

        // StringContent buffers, so the resilience handler can replay this request on a 429 or 5xx.
        // A streamed body would be consumed by the first attempt and silently send nothing on the
        // second.
        if (payload is not null)
        {
            request.Content = new StringContent(payload, Encoding.UTF8, JsonMediaType);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // Read the body unconditionally: it is both the payload on success and the only detail an
        // error message can quote. Responses here are single pages, not exports.
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return (response.StatusCode, body);
    }

    /// <summary>
    /// Turns a failing status into the right exception. 401/403 get their own type because they are
    /// the one case where the whole run must stop rather than the page being retried or reported
    /// (.claude/rules/security.md §1.2); the retry pipeline has already refused to retry them. 409
    /// gets its own for the opposite reason: it is the one case where a single page failed while the
    /// run should carry on (<see cref="ConfluenceConflictException"/>).
    /// </summary>
    private static void ThrowIfFailed(
        string path,
        HttpStatusCode statusCode,
        string body,
        string? operation = null)
    {
        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new ConfluenceAuthenticationException(statusCode, path);
        }

        if (statusCode == HttpStatusCode.Conflict && operation is not null)
        {
            throw new ConfluenceConflictException(path, operation, Excerpt(body));
        }

        if ((int)statusCode is < 200 or > 299)
        {
            throw new ConfluenceApiException(statusCode, path, Excerpt(body), operation);
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
