using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocuMe.Core.Confluence;

/// <summary>
/// Thin client over the Confluence Cloud REST API (PLAN.md §4): the page reads, the page upsert, the
/// attachment upsert and the label add/remove the publish and approval pipelines are built on
/// (§6.2 step 5, §6.3, §8).
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
/// Endpoints and response shapes are taken from Atlassian's published OpenAPI documents rather than
/// from memory: <c>GET /api/v2/spaces</c> filters by <c>keys</c>, <c>GET /api/v2/pages</c> by
/// <c>space-id</c> + <c>title</c> + <c>status</c>, both answering
/// <c>{"results": [...], "_links": {...}}</c>, and <c>body-format=storage</c> is what populates
/// <c>body.storage.value</c>.
/// </para>
/// <para>
/// Attachments and labels are the "v1 only where v2 lacks" case §4 anticipates, and the only one so
/// far. It is not a preference: in the v2 document every attachment path is a <c>GET</c> apart from
/// <c>DELETE /attachments/{id}</c>, and every label path is a <c>GET</c>. So the upload and the label
/// write go to <c>/rest/api/content/{id}/…</c>, off the same <c>/wiki/</c> base address as the v2
/// calls. Both carry <c>X-Atlassian-Token: nocheck</c>, which v1 documents as mandatory for the
/// multipart upload — it accepts <c>multipart/form-data</c> and would otherwise be blocked as
/// suspected XSRF. The label write is not documented as needing it; it is sent there too because the
/// header is inert on a JSON body and the alternative is finding out from a blocked bulk publish.
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

    /// <summary>Content type for every JSON write body.</summary>
    private const string JsonMediaType = "application/json";

    /// <summary>
    /// The header v1 requires on a <c>multipart/form-data</c> write, without which Confluence blocks
    /// the request as suspected XSRF. Spelled <c>nocheck</c>, not <c>no-check</c>.
    /// </summary>
    private const string XsrfHeader = "X-Atlassian-Token";

    private const string XsrfHeaderValue = "nocheck";

    /// <summary>The multipart part name v1 requires for the file itself.</summary>
    private const string FilePart = "file";

    /// <summary>
    /// Required by the v1 schema alongside the file. Always <c>true</c>: an ~80-page publish uploading
    /// 59 rendered diagrams would otherwise raise 59 notifications and activity-stream entries, which
    /// is the machine churn the sandbox space exists to keep off people's feeds.
    /// </summary>
    private const string MinorEditPart = "minorEdit";

    private const string MinorEditValue = "true";

    private const string CommentPart = "comment";

    /// <summary>The namespace an ordinary, human-visible Confluence label lives in.</summary>
    private const string GlobalLabelPrefix = "global";

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
    /// Which REST surface a request goes to. It decides exactly one thing — whether the XSRF opt-out
    /// header rides along — but it is spelled out at every call site rather than inferred from the
    /// path, because "which requests are v1" is the kind of fact that should be stated, not parsed.
    /// </summary>
    private enum ApiSurface
    {
        /// <summary>REST v2: spaces and pages.</summary>
        V2,

        /// <summary>REST v1: attachments and labels, which v2 exposes read-only.</summary>
        V1,
    }

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
                ApiSurface.V2,
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
                ApiSurface.V2,
                cancellationToken)
            .ConfigureAwait(false);

        return MapPage(page, path);
    }

    /// <summary>
    /// Uploads a file to a page, replacing whatever is already attached under that name (PLAN.md §6.2
    /// step 5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>PUT</c>, not <c>POST</c>, and the difference is the whole point. v1 offers both on the same
    /// path: <c>POST</c> is "Create attachment", documented to answer <b>400 if the content already has
    /// an attachment with the same filename</b>, while <c>PUT</c> is "Create or update attachment",
    /// which stores a new version of an existing name. §6.2 uploads only the attachments whose hash
    /// changed, so by construction almost every upload DocuMe performs is to a name that already
    /// exists — the create-only verb would fail exactly the requests that matter and succeed only on
    /// first publish.
    /// </para>
    /// <para>
    /// That makes this the same shape of upsert as the page write, with one difference worth knowing:
    /// there is no version to send and so no optimistic lock. A concurrent edit of an attachment
    /// cannot be detected here the way <see cref="ConfluenceConflictException"/> detects one on a page.
    /// Attachments are machine-owned (rendered diagrams and repo images), so nothing is expected to be
    /// racing — but nothing would report it if something were.
    /// </para>
    /// </remarks>
    /// <exception cref="ConfluenceAuthenticationException">401/403: token expired, or attachments are disabled.</exception>
    /// <exception cref="ConfluenceApiException">
    /// Any other non-success status, including a page the account cannot see (404) and a file over the
    /// space's attachment size limit (404, per v1's own documentation).
    /// </exception>
    /// <exception cref="ConfluenceProtocolException">The response body is not the documented shape.</exception>
    public async Task<ConfluenceAttachment> UploadAttachmentAsync(
        ConfluenceAttachmentUpload upload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        Validate(upload, nameof(upload));

        var path = $"rest/api/content/{Uri.EscapeDataString(upload.PageId)}/child/attachment";
        var operation = $"uploading attachment '{upload.FileName}' to page {upload.PageId}";

        using var content = BuildUpload(upload);
        var response = await SendContentAsync<MultiEntityResult<ContentBulk>>(
                HttpMethod.Put,
                path,
                content,
                operation,
                ApiSurface.V1,
                cancellationToken)
            .ConfigureAwait(false);

        var attachments = RequireResults(response, path);
        if (attachments.Count == 0)
        {
            throw new ConfluenceProtocolException(
                path,
                $"Confluence accepted the upload of '{upload.FileName}' but returned no attachment");
        }

        return MapAttachment(attachments[0], path);
    }

    /// <summary>
    /// Adds labels to a page without touching the ones already on it, and returns what came back.
    /// This is the machine half of the approval gesture (PLAN.md §8) and how <c>drift --mark</c> marks
    /// a page stale (§6.4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Labels rather than page-body edits is a product invariant, not an implementation choice: a body
    /// edit bumps the page version, which invalidates the approval it was trying to record
    /// (.claude/rules/project-specific.md §9.3).
    /// </para>
    /// <para>
    /// v1 documents this as "Does not modify the existing labels", so re-adding a label a page already
    /// carries is not an error — which is what lets a re-run of <c>drift --mark</c> be safe. Whether
    /// the response lists only the added labels or every label on the page is not stated; the caller
    /// gets what Confluence returned rather than an assumption about which.
    /// </para>
    /// </remarks>
    /// <exception cref="ConfluenceAuthenticationException">401/403: token expired, or read-but-not-edit permission.</exception>
    /// <exception cref="ConfluenceApiException">
    /// Any other non-success status, including a label with characters Confluence rejects (400).
    /// </exception>
    /// <exception cref="ConfluenceProtocolException">The response body is not the documented shape.</exception>
    public async Task<IReadOnlyList<ConfluenceLabel>> AddLabelsAsync(
        string pageId,
        IReadOnlyList<string> labels,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        ArgumentNullException.ThrowIfNull(labels);
        ValidateLabels(labels, nameof(labels));

        var path = $"rest/api/content/{Uri.EscapeDataString(pageId)}/label";
        var request = labels
            .Select(label => new LabelCreate(GlobalLabelPrefix, label))
            .ToArray();

        var response = await WriteAsync<IReadOnlyList<LabelCreate>, MultiEntityResult<LabelBulk>>(
                HttpMethod.Post,
                path,
                request,
                $"adding label(s) {Quote(labels)} to page {pageId}",
                ApiSurface.V1,
                cancellationToken)
            .ConfigureAwait(false);

        return RequireResults(response, path)
            .Select(label => MapLabel(label, path))
            .ToArray();
    }

    /// <summary>
    /// Removes one label from a page. Approval invalidation (PLAN.md §8): once a republish changes
    /// <c>contentHash</c>, the <c>approved</c> label asserts something that is no longer true, and its
    /// absence is the re-approval trigger.
    /// </summary>
    /// <remarks>
    /// The <c>?name=</c> spelling rather than the <c>/label/{label}</c> path segment, which Atlassian
    /// recommends for exactly the reason it matters here: a label containing a <c>/</c> is
    /// unrepresentable in the path form. DocuMe's own labels are configurable
    /// (<c>docume.json → labels</c>, §5.1), so the name is not one this client gets to assume.
    /// </remarks>
    /// <exception cref="ConfluenceAuthenticationException">401/403: token expired, or read-but-not-edit permission.</exception>
    /// <exception cref="ConfluenceApiException">Any other non-success status, including an unknown page (404).</exception>
    public async Task RemoveLabelAsync(
        string pageId,
        string label,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        var path = $"rest/api/content/{Uri.EscapeDataString(pageId)}/label"
            + $"?name={Uri.EscapeDataString(label)}";

        var (statusCode, body) = await SendAsync(
                HttpMethod.Delete,
                path,
                content: null,
                ApiSurface.V1,
                cancellationToken)
            .ConfigureAwait(false);

        // 204 with an empty body is the documented success, and it needs no deserialization.
        ThrowIfFailed(path, statusCode, body, $"removing label '{label}' from page {pageId}");
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

    /// <summary>
    /// Builds the <c>multipart/form-data</c> body v1 requires.
    /// </summary>
    /// <remarks>
    /// Every part is a <see cref="ByteArrayContent"/> or a <see cref="StringContent"/>, which is what
    /// keeps the whole request replayable: those serialize from a buffer, so the resilience handler can
    /// re-send the same message after a 429. A <see cref="StreamContent"/> part would be drained by the
    /// first attempt and write nothing on the second, and the upload would "succeed" as an empty file.
    /// The text parts are UTF-8 <c>text/plain</c>, which is the charset RFC 7578 asks for and
    /// Atlassian's own note repeats.
    /// </remarks>
    private static MultipartFormDataContent BuildUpload(ConfluenceAttachmentUpload upload)
    {
        var content = new MultipartFormDataContent();

        var file = new ByteArrayContent(upload.Content.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue(upload.ContentType);
        content.Add(file, FilePart, upload.FileName);
        content.Add(new StringContent(MinorEditValue, Encoding.UTF8), MinorEditPart);

        if (upload.Comment is not null)
        {
            content.Add(new StringContent(upload.Comment, Encoding.UTF8), CommentPart);
        }

        return content;
    }

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

    private static void Validate(ConfluenceAttachmentUpload upload, string parameterName)
    {
        RequireField(upload.PageId, "page id", parameterName);
        RequireField(upload.FileName, "attachment file name", parameterName);
        RequireField(upload.ContentType, "attachment content type", parameterName);

        // Unlike a page body, an empty attachment has no legitimate meaning: DocuMe only ever uploads
        // rendered diagrams and repo images, so zero bytes means the producer failed. Uploading it
        // would not fail — it would store a new version of a working attachment containing nothing.
        if (upload.Content.IsEmpty)
        {
            throw new ArgumentException(
                $"The attachment '{upload.FileName}' is empty. DocuMe uploads rendered diagrams and "
                + "repo images, so zero bytes means whatever produced the file failed; publishing it "
                + "would replace a working attachment with a broken one.",
                parameterName);
        }
    }

    private static void ValidateLabels(IReadOnlyList<string> labels, string parameterName)
    {
        if (labels.Count == 0)
        {
            throw new ArgumentException("Adding no labels is a request worth not sending.", parameterName);
        }

        if (labels.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("A label cannot be null or blank.", parameterName);
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
        var (statusCode, body) = await SendAsync(
                HttpMethod.Get,
                path,
                content: null,
                ApiSurface.V2,
                cancellationToken)
            .ConfigureAwait(false);

        ThrowIfFailed(path, statusCode, body);

        return Deserialize<T>(path, body);
    }

    private async Task<T?> ReadOrNullWhenMissingAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        var (statusCode, body) = await SendAsync(
                HttpMethod.Get,
                path,
                content: null,
                ApiSurface.V2,
                cancellationToken)
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
        ApiSurface surface,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        var payload = JsonSerializer.Serialize(request, WriteSerializerOptions);

        // StringContent buffers, so the resilience handler can replay this request on a 429 or 5xx.
        using var content = new StringContent(payload, Encoding.UTF8, JsonMediaType);

        return await SendContentAsync<TResponse>(method, path, content, operation, surface, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<TResponse> SendContentAsync<TResponse>(
        HttpMethod method,
        string path,
        HttpContent content,
        string operation,
        ApiSurface surface,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        var (statusCode, body) = await SendAsync(method, path, content, surface, cancellationToken)
            .ConfigureAwait(false);

        ThrowIfFailed(path, statusCode, body, operation);

        return Deserialize<TResponse>(path, body);
    }

    private async Task<(HttpStatusCode StatusCode, string Body)> SendAsync(
        HttpMethod method,
        string path,
        HttpContent? content,
        ApiSurface surface,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative))
        {
            Content = content,
        };

        if (surface == ApiSurface.V1)
        {
            request.Headers.Add(XsrfHeader, XsrfHeaderValue);
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

    private static ConfluenceAttachment MapAttachment(ContentBulk attachment, string path)
        => new(
            Require(attachment.Id, "id", path),
            Require(attachment.Title, "title", path),
            attachment.Version?.Number);

    private static ConfluenceLabel MapLabel(LabelBulk label, string path)
        => new(
            Require(label.Name, "name", path),
            Require(label.Prefix, "prefix", path));

    private static string Quote(IReadOnlyList<string> labels)
        => string.Join(", ", labels.Select(label => $"'{label}'"));

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
